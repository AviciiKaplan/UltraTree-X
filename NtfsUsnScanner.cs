using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace UltraTree;

/// <summary>
/// NTFS scanner using the USN Journal to enumerate the file tree quickly.
/// NOTE: USN records do NOT contain file size; we do a parallel sizing pass.
/// </summary>
public static class NtfsUsnScanner
{
    public sealed class ScanResult
    {
        // Drive summary
        public required VolumeStats Volume { get; init; }

        // Tree metrics (folder + file nodes). UI can filter to folders.
        public required IReadOnlyDictionary<string, NodeMetrics> Metrics { get; init; }

        // Convenience lists for quick UI views
        public long FileCount { get; init; }
        public long TotalBytes { get; init; }
        public List<ResultRow> TopFiles { get; init; } = new();
        public List<ResultRow> TopFolders { get; init; } = new();
    }

    public static ScanResult ScanDrive(string driveLetterNoSlash, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        // Expect "C:" or "D:"
        string drive = driveLetterNoSlash.TrimEnd('\\');
        string rootPath = drive + "\\";
        var volumeStats = VolumeInfo.GetVolumeStats(rootPath);

        // 1) Open volume handle: \\.\C:
        string volPath = @"\\.\" + drive;
        using SafeFileHandle hVol = CreateFileW(
            volPath,
            FileAccessFlags.GENERIC_READ,
            FileShareFlags.FILE_SHARE_READ | FileShareFlags.FILE_SHARE_WRITE | FileShareFlags.FILE_SHARE_DELETE,
            IntPtr.Zero,
            CreationDisposition.OPEN_EXISTING,
            FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (hVol.IsInvalid)
            throw new InvalidOperationException("Cannot open volume handle. Run as Administrator and ensure it's an NTFS drive.");

        // 2) Get real root directory FRN (critical for correct path reconstruction)
        ulong rootFrn = GetRootDirectoryFrn(rootPath);

        // 3) Query USN Journal
        USN_JOURNAL_DATA_V0 journal = QueryUsnJournal(hVol);

        // 4) Enumerate USN records (names + parent FRN)
        progress?.Report(new ScanProgress(1, "Enumerating NTFS records (USN Journal)…"));

        var entries = new ConcurrentDictionary<ulong, Entry>(concurrencyLevel: Environment.ProcessorCount, capacity: 1_000_000);
        entries[rootFrn] = new Entry(rootFrn, rootFrn, rootPath.TrimEnd('\\'), IsDir: true, Size: 0, Allocated: 0);

        var enumData = new MFT_ENUM_DATA_V0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = journal.NextUsn
        };

        int enumSize = Marshal.SizeOf<MFT_ENUM_DATA_V0>();
        IntPtr enumPtr = Marshal.AllocHGlobal(enumSize);
        Marshal.StructureToPtr(enumData, enumPtr, false);

        const int OUT_BUF_SIZE = 1024 * 1024; // 1MB
        IntPtr outBuf = Marshal.AllocHGlobal(OUT_BUF_SIZE);

        long approxCount = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool ok = DeviceIoControl(
                    hVol,
                    FSCTL_ENUM_USN_DATA,
                    enumPtr,
                    enumSize,
                    outBuf,
                    OUT_BUF_SIZE,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!ok || bytesReturned <= (int)sizeof(ulong))
                    break;

                // First 8 bytes: next StartFileReferenceNumber
                ulong nextFrn = (ulong)Marshal.ReadInt64(outBuf);
                enumData.StartFileReferenceNumber = nextFrn;
                Marshal.StructureToPtr(enumData, enumPtr, false);

                IntPtr p = outBuf + sizeof(ulong);
                IntPtr end = outBuf + bytesReturned;

                while (p.ToInt64() < end.ToInt64())
                {
                    ct.ThrowIfCancellationRequested();

                    var rec = Marshal.PtrToStructure<USN_RECORD_V2>(p);
                    bool isDir = (rec.FileAttributes & FileAttributesFlags.DIRECTORY) != 0;

                    string name = Marshal.PtrToStringUni(p + rec.FileNameOffset, rec.FileNameLength / 2) ?? "";

                    entries[rec.FileReferenceNumber] = new Entry(
                        rec.FileReferenceNumber,
                        rec.ParentFileReferenceNumber,
                        name,
                        IsDir: isDir,
                        Size: 0,
                        Allocated: 0);

                    approxCount++;
                    if (approxCount % 250_000 == 0)
                        progress?.Report(new ScanProgress(5, $"Enumerated {approxCount:n0} records…"));

                    p += (int)rec.RecordLength;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(enumPtr);
            Marshal.FreeHGlobal(outBuf);
        }

        // 5) Resolve paths with memoization (FRN -> full path)
        progress?.Report(new ScanProgress(10, "Resolving paths…"));

        var pathCache = new ConcurrentDictionary<ulong, string>(concurrencyLevel: Environment.ProcessorCount, capacity: 1_000_000);
        pathCache[rootFrn] = rootPath.TrimEnd('\\'); // no trailing slash in cache

        string ResolvePath(ulong frn)
        {
            if (pathCache.TryGetValue(frn, out var cached))
                return cached;

            if (!entries.TryGetValue(frn, out var ent))
                return "";

            if (ent.ParentFrn == frn)
            {
                pathCache[frn] = rootPath.TrimEnd('\\');
                return pathCache[frn];
            }

            string parent = ResolvePath(ent.ParentFrn);
            if (string.IsNullOrEmpty(parent))
                return "";

            string full = Path.Combine(parent, ent.Name);
            pathCache[frn] = full;
            return full;
        }

        // 6) Size files in parallel + compute Allocated
        var fileFrns = entries.Values.Where(e => !e.IsDir).Select(e => e.Frn).ToArray();
        progress?.Report(new ScanProgress(15, $"Sizing {fileFrns.Length:n0} files (parallel)…"));

        var topFilesHeap = new FixedSizeMinHeap(1000);

        long totalBytes = 0;
        long fileCount = 0;

        // We will build FileFacts for the metrics engine
        var factsBag = new ConcurrentBag<FileFact>();

        Parallel.ForEach(
            fileFrns,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            frn =>
            {
                ct.ThrowIfCancellationRequested();

                if (!entries.TryGetValue(frn, out var ent) || ent.IsDir)
                    return;

                string path = ResolvePath(frn);
                if (string.IsNullOrEmpty(path))
                    return;

                try
                {
                    long len = new FileInfo(path).Length;

                    long alloc;
                    try
                    {
                        alloc = AllocatedSize.GetAllocatedBytesForFile(path);
                        if (alloc < 0) alloc = len;
                    }
                    catch
                    {
                        alloc = len; // fallback
                    }

                    entries[frn] = ent with { Size = len, Allocated = alloc };

                    Interlocked.Add(ref totalBytes, len);
                    Interlocked.Increment(ref fileCount);

                    topFilesHeap.Consider(path, len);

                    // FileFact for tree metrics
                    string parent = Path.GetDirectoryName(path) ?? rootPath.TrimEnd('\\');
                    if (!parent.EndsWith("\\")) parent += "\\";

                    factsBag.Add(new FileFact
                    {
                        Path = path,
                        ParentPath = parent,
                        IsDir = false,
                        LogicalSize = len,
                        AllocatedSize = alloc
                    });
                }
                catch
                {
                    // access denied / path missing / reparse weirdness etc.
                }
            });

        // Optional: include directories as facts so folder counts can be correct
        foreach (var d in entries.Values.Where(x => x.IsDir))
        {
            ct.ThrowIfCancellationRequested();
            var p = ResolvePath(d.Frn);
            if (string.IsNullOrEmpty(p)) continue;
            if (!p.EndsWith("\\")) p += "\\";

            string parent = Path.GetDirectoryName(p.TrimEnd('\\')) ?? rootPath.TrimEnd('\\');
            if (!parent.EndsWith("\\")) parent += "\\";

            factsBag.Add(new FileFact
            {
                Path = p,
                ParentPath = parent,
                IsDir = true,
                LogicalSize = 0,
                AllocatedSize = 0
            });
        }

        progress?.Report(new ScanProgress(60, "Building tree metrics…"));

        var metrics = MetricsBuilder.BuildTreeMetrics(factsBag, rootPath, ct);

        // 7) Build top folders list from metrics (uses folder logical size)
        progress?.Report(new ScanProgress(85, "Building top folders…"));

        var topFoldersHeap = new FixedSizeMinHeap(500);
        foreach (var n in metrics.Values)
        {
            ct.ThrowIfCancellationRequested();

            if (!n.IsDir) continue;
            if (string.Equals(n.Path.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue;

            topFoldersHeap.Consider(n.Path, n.SizeBytes);
        }

        progress?.Report(new ScanProgress(95, "Finalizing…"));

        return new ScanResult
        {
            Volume = volumeStats,
            Metrics = metrics,

            FileCount = fileCount,
            TotalBytes = totalBytes,

            TopFiles = topFilesHeap.GetTopDescending(200).Select(x => new ResultRow(x.Path, x.Bytes)).ToList(),
            TopFolders = topFoldersHeap.GetTopDescending(200).Select(x => new ResultRow(x.Path, x.Bytes)).ToList()
        };
    }

    private sealed record Entry(ulong Frn, ulong ParentFrn, string Name, bool IsDir, long Size, long Allocated);

    // --- Root FRN lookup ---
    private static ulong GetRootDirectoryFrn(string rootPathWithSlash)
    {
        using SafeFileHandle hRoot = CreateFileW(
            rootPathWithSlash,
            FileAccessFlags.GENERIC_READ,
            FileShareFlags.FILE_SHARE_READ | FileShareFlags.FILE_SHARE_WRITE | FileShareFlags.FILE_SHARE_DELETE,
            IntPtr.Zero,
            CreationDisposition.OPEN_EXISTING,
            FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (hRoot.IsInvalid)
            throw new InvalidOperationException("Cannot open root directory handle.");

        if (!GetFileInformationByHandle(hRoot, out BY_HANDLE_FILE_INFORMATION info))
            throw new InvalidOperationException("GetFileInformationByHandle failed for root.");

        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    private static USN_JOURNAL_DATA_V0 QueryUsnJournal(SafeFileHandle hVol)
    {
        int outLen = Marshal.SizeOf<USN_JOURNAL_DATA_V0>();
        IntPtr outBuf = Marshal.AllocHGlobal(outLen);
        try
        {
            bool ok = DeviceIoControl(
                hVol,
                FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero,
                0,
                outBuf,
                outLen,
                out int bytesReturned,
                IntPtr.Zero);

            if (!ok || bytesReturned < outLen)
                throw new InvalidOperationException("FSCTL_QUERY_USN_JOURNAL failed. Is this NTFS?");

            return Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(outBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    // ---------------- P/Invoke ----------------

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900b3;

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_RECORD_V2
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public FileAttributesFlags FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
    }

    [Flags]
    private enum FileAttributesFlags : uint
    {
        DIRECTORY = 0x10,
    }

    [Flags]
    private enum FileAccessFlags : uint
    {
        GENERIC_READ = 0x80000000
    }

    [Flags]
    private enum FileShareFlags : uint
    {
        FILE_SHARE_READ = 0x00000001,
        FILE_SHARE_WRITE = 0x00000002,
        FILE_SHARE_DELETE = 0x00000004
    }

    private enum CreationDisposition : uint
    {
        OPEN_EXISTING = 3
    }

    [Flags]
    private enum FileFlagsAndAttributes : uint
    {
        FILE_ATTRIBUTE_NORMAL = 0x00000080,
        FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        FileAccessFlags dwDesiredAccess,
        FileShareFlags dwShareMode,
        IntPtr lpSecurityAttributes,
        CreationDisposition dwCreationDisposition,
        FileFlagsAndAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}
