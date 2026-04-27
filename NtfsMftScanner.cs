using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UltraTree;

public static class NtfsMftScanner
{
    public sealed class ScanResult
    {
        public required VolumeStats Volume { get; init; }
        public required IReadOnlyDictionary<string, NodeMetrics> Metrics { get; init; }
        public long FileCount { get; init; }
        public long TotalBytes { get; init; }
        public List<ResultRow> TopFiles { get; init; } = new();
        public ObservableCollection<TreeNode> RootNodes { get; init; } = new();
    }

    private readonly record struct MftRun(long StartCluster, long ClusterCount);

    private readonly record struct ParsedRecordLite(
        ulong RecordFrn,
        ulong BaseFrn,
        ulong ParentFrn,
        bool IsDir,
        long DataSize,
        bool HadDataAttr,
        bool DataWasResident,
        ushort DataFlags,
        string? Name);

    private sealed class FileAggregate
    {
        public ulong Frn;
        public ulong ParentFrn;
        public string Name = "";
        public bool IsDir;
        public long TotalDataSize;
        public bool HasBaseRecord;
    }

    private sealed class DiagCounters
    {
        public long RawBaseFileCount;
        public long RawBaseSizeBytes;
        public long RawExtCount;
        public long RawExtWithData;

        public long ZeroBaseFiles;
        public long ZeroNoDataAttr;
        public long ZeroResidentZero;
        public long ZeroNonResZero;
        public long ZeroCompressed;
        public long ZeroSparse;

        public readonly List<(bool Resident, ushort Flags, string Name)> ZeroSamples = new(capacity: 30);
    }

    private sealed class Entry
    {
        public ulong Frn { get; }
        public ulong ParentFrn { get; }
        public string Name { get; }
        public bool IsDir { get; }
        public long Size { get; }

        public Entry(ulong frn, ulong parentFrn, string name, bool isDir, long size)
        {
            Frn = frn;
            ParentFrn = parentFrn;
            Name = name;
            IsDir = isDir;
            Size = size;
        }
    }

    public static ScanResult ScanDrive(string driveLetterNoSlash, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string drive = driveLetterNoSlash.TrimEnd('\\');
        string rootPath = drive + "\\";
        var volumeStats = VolumeInfo.GetVolumeStats(rootPath);
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
            throw new InvalidOperationException("Run as Administrator to access MFT.");

        byte[] boot = new byte[512];
        ReadAt(hVol, 0, boot, boot.Length);

        var bs = ParseBootSector(boot);
        if (!bs.IsNtfs)
            throw new InvalidOperationException("Not NTFS.");

        long bytesPerCluster = bs.BytesPerSector * bs.SectorsPerCluster;
        long mftByteOffset = bs.MftCluster * bytesPerCluster;

        byte[] mftRecord0 = new byte[bs.MftRecordSize];
        ReadAt(hVol, mftByteOffset, mftRecord0, mftRecord0.Length);
        ApplyUsaFixup(mftRecord0, bs.BytesPerSector);

        var mftRuns = ParseMftRunList(mftRecord0);
        if (mftRuns.Count == 0)
            throw new InvalidOperationException("Could not parse MFT runlist.");

        long totalMftClusters = 0;
        foreach (var run in mftRuns) totalMftClusters += run.ClusterCount;
        long totalPossibleRecords = (totalMftClusters * bytesPerCluster) / bs.MftRecordSize;

        var aggregates = new Dictionary<ulong, FileAggregate>(capacity: 1_500_000);
        var diag = new DiagCounters();

        long recordIndex = 0;
        int recordSize = bs.MftRecordSize;

        const int MaxBufferSize = 32 * 1024 * 1024; // 32 MB
        foreach (var run in mftRuns)
        {
            ct.ThrowIfCancellationRequested();

            long runByteOffset = run.StartCluster * bytesPerCluster;
            long runByteLength = run.ClusterCount * bytesPerCluster;

            int bufferSize = (int)Math.Min(MaxBufferSize, runByteLength);
            byte[] buffer = new byte[bufferSize];

            for (long offset = 0; offset < runByteLength; offset += bufferSize)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(bufferSize, runByteLength - offset);
                if (!TryReadAt(hVol, runByteOffset + offset, buffer, toRead))
                    break;

                for (int i = 0; i + recordSize <= toRead; i += recordSize)
                {
                    if (recordIndex >= totalPossibleRecords)
                        break;

                    Span<byte> recordSpan = buffer.AsSpan(i, recordSize);

                    if (IsFileRecord(recordSpan))
                    {
                        ApplyUsaFixup(recordSpan, bs.BytesPerSector);

                        if (TryParseRecord(recordSpan, (ulong)recordIndex, out var rec))
                        {
                            ProcessParsedRecord(rec, aggregates, diag);
                        }
                    }

                    recordIndex++;
                }

                progress?.Report(new ScanProgress(
                    (double)recordIndex / Math.Max(1, totalPossibleRecords) * 30.0 + 5.0,
                    "Parsing MFT..."));
            }
        }

        var entries = new Dictionary<ulong, Entry>(aggregates.Count);
        foreach (var agg in aggregates.Values)
        {
            if (!agg.HasBaseRecord) continue;
            if (agg.Frn < 12) continue;

            entries[agg.Frn] = new Entry(
                agg.Frn,
                agg.ParentFrn,
                agg.Name,
                agg.IsDir,
                agg.IsDir ? 0 : agg.TotalDataSize);
        }

        WriteDiagnostic(volumeStats, diag, entries);

        long totalBytes = 0;
        long fileCount = 0;
        foreach (var e in entries.Values)
        {
            if (e.IsDir) continue;
            totalBytes += e.Size;
            fileCount++;
        }

        var pathCache = new Dictionary<ulong, string>(capacity: Math.Min(entries.Count, 300_000));
        pathCache[5] = rootPath;

        string ResolvePath(ulong startFrn)
        {
            if (pathCache.TryGetValue(startFrn, out var cached))
                return cached;

            var chain = new List<ulong>(16);
            ulong cur = startFrn;

            while (true)
            {
                if (pathCache.TryGetValue(cur, out var known))
                {
                    string built = known;
                    for (int j = chain.Count - 1; j >= 0; j--)
                    {
                        ulong frn = chain[j];
                        if (!entries.TryGetValue(frn, out var entry))
                            return "";

                        built = Path.Combine(built, entry.Name);
                        if (entry.IsDir && !built.EndsWith("\\", StringComparison.Ordinal))
                            built += "\\";

                        pathCache[frn] = built;
                    }
                    return built;
                }

                if (!entries.TryGetValue(cur, out var e))
                    return "";

                if (e.ParentFrn == cur)
                    return "";

                chain.Add(cur);
                cur = e.ParentFrn;

                if (chain.Count > 512)
                    return "";
            }
        }

        var facts = new List<FileFact>(entries.Count);
        var topFilesHeap = new FixedSizeMinHeap(1000);

        foreach (var e in entries.Values)
        {
            ct.ThrowIfCancellationRequested();

            string full = ResolvePath(e.Frn);
            if (string.IsNullOrEmpty(full))
                continue;

            long alloc = e.Size == 0
                ? 0
                : e.Size <= 1024
                    ? e.Size
                    : ((e.Size + bytesPerCluster - 1) / bytesPerCluster) * bytesPerCluster;

            string parentPath = Path.GetDirectoryName(full.TrimEnd('\\')) ?? rootPath.TrimEnd('\\');
            if (!parentPath.EndsWith("\\", StringComparison.Ordinal))
                parentPath += "\\";

            facts.Add(new FileFact
            {
                Path = full,
                ParentPath = parentPath,
                IsDir = e.IsDir,
                LogicalSize = e.Size,
                AllocatedSize = alloc
            });

            if (!e.IsDir && e.Size > 0)
                topFilesHeap.Consider(full, e.Size);
        }

        progress?.Report(new ScanProgress(60, "Building metrics..."));

        var metrics = MetricsBuilder.BuildTreeMetrics(facts, rootPath, ct);

        progress?.Report(new ScanProgress(85, "Building tree..."));

        var treeMap = new Dictionary<string, TreeNode>(metrics.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var m in metrics.Values)
        {
            if (!m.IsDir) continue;

            string trimmed = m.Path.TrimEnd('\\');
            int idx = trimmed.LastIndexOf('\\');

            treeMap[m.Path] = new TreeNode(
                m.Path,
                idx < 0 ? m.Path : trimmed[(idx + 1)..],
                true,
                m.SizeBytes,
                m.AllocatedBytes,
                m.ItemCount,
                m.FileCount,
                m.FolderCount,
                DateTime.MinValue,
                m.PercentOfParent);
        }

        var rootNodes = new ObservableCollection<TreeNode>();
        foreach (var node in treeMap.Values.OrderByDescending(x => x.SizeBytes))
        {
            if (node.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                rootNodes.Add(node);
                continue;
            }

            string pPath = MetricsBuilder.ParentFolder(node.Path);
            if (treeMap.TryGetValue(pPath, out var parentNode))
                parentNode.Children.Add(node);
            else
                rootNodes.Add(node);
        }

        progress?.Report(new ScanProgress(100, "Done"));

        return new ScanResult
        {
            Volume = volumeStats,
            Metrics = metrics,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            RootNodes = rootNodes,
            TopFiles = topFilesHeap
                .GetTopDescending(200)
                .Select(x => new ResultRow(x.Path, x.Bytes))
                .ToList()
        };
    }

    private static void ProcessParsedRecord(
        ParsedRecordLite rec,
        Dictionary<ulong, FileAggregate> aggregates,
        DiagCounters diag)
    {
        bool isBase = rec.BaseFrn == 0;
        ulong ownerFrn = isBase ? rec.RecordFrn : rec.BaseFrn;

        if (isBase && !rec.IsDir)
        {
            diag.RawBaseFileCount++;
            diag.RawBaseSizeBytes += rec.DataSize;

            if (rec.DataSize == 0)
            {
                diag.ZeroBaseFiles++;

                if (!rec.HadDataAttr) diag.ZeroNoDataAttr++;
                else if (rec.DataWasResident) diag.ZeroResidentZero++;
                else diag.ZeroNonResZero++;

                if ((rec.DataFlags & 0x0001) != 0) diag.ZeroCompressed++;
                if ((rec.DataFlags & 0x8000) != 0) diag.ZeroSparse++;

                if (rec.HadDataAttr && diag.ZeroSamples.Count < 30 && !string.IsNullOrEmpty(rec.Name))
                {
                    diag.ZeroSamples.Add((rec.DataWasResident, rec.DataFlags, rec.Name!));
                }
            }
        }
        else if (!isBase)
        {
            diag.RawExtCount++;
            if (rec.DataSize > 0) diag.RawExtWithData++;
        }

        if (!aggregates.TryGetValue(ownerFrn, out var agg))
        {
            agg = new FileAggregate { Frn = ownerFrn };
            aggregates.Add(ownerFrn, agg);
        }

        if (isBase)
        {
            agg.ParentFrn = rec.ParentFrn;
            agg.IsDir = rec.IsDir;
            agg.HasBaseRecord = true;

            if (!string.IsNullOrEmpty(rec.Name))
                agg.Name = rec.Name!;
        }

        if (!rec.IsDir && rec.DataSize > 0)
        {
            checked
            {
                agg.TotalDataSize += rec.DataSize;
            }
        }
    }

    private static void WriteDiagnostic(
        VolumeStats volumeStats,
        DiagCounters diag,
        Dictionary<ulong, Entry> entries)
    {
        try
        {
            long GB = 1024L * 1024 * 1024;
            long osUsedBytes = volumeStats.TotalBytes - volumeStats.FreeBytes;

            long entryFileCount = 0;
            long entrySizeBytes = 0;
            long zeroSizeEntries = 0;

            foreach (var e in entries.Values)
            {
                if (e.IsDir) continue;
                entryFileCount++;
                entrySizeBytes += e.Size;
                if (e.Size == 0) zeroSizeEntries++;
            }

            var top20 = entries.Values
                .Where(e => !e.IsDir)
                .OrderByDescending(e => e.Size)
                .Take(20)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("  UltraTree MFT Diagnostic Report v4");
            sb.AppendLine($"  {DateTime.Now}");
            sb.AppendLine("========================================");
            sb.AppendLine();
            sb.AppendLine("── LAYER 0: OS ground truth ─────────────");
            sb.AppendLine($"  OS reported used bytes : {osUsedBytes,20:N0}  ({(double)osUsedBytes / GB:F2} GB)");
            sb.AppendLine();
            sb.AppendLine("── LAYER 1: Raw MFT parse ───────────────");
            sb.AppendLine($"  Base file records      : {diag.RawBaseFileCount,10:N0}");
            sb.AppendLine($"  Base files size sum    : {diag.RawBaseSizeBytes,20:N0}  ({(double)diag.RawBaseSizeBytes / GB:F2} GB)");
            sb.AppendLine($"  Extension records      : {diag.RawExtCount,10:N0}");
            sb.AppendLine($"    with DataSize > 0    : {diag.RawExtWithData,10:N0}");
            sb.AppendLine();
            sb.AppendLine("── LAYER 2: After entry building ────────");
            sb.AppendLine($"  File entries           : {entryFileCount,10:N0}");
            sb.AppendLine($"  File entries size sum  : {entrySizeBytes,20:N0}  ({(double)entrySizeBytes / GB:F2} GB)");
            sb.AppendLine($"  Entries with size == 0 : {zeroSizeEntries,10:N0}");
            sb.AppendLine();
            sb.AppendLine("── GAP ANALYSIS ─────────────────────────");
            long gapRawVsOs = osUsedBytes - diag.RawBaseSizeBytes;
            long gapEntryVsOs = osUsedBytes - entrySizeBytes;
            sb.AppendLine($"  OS - raw base sum      : {gapRawVsOs,20:N0}  ({(double)gapRawVsOs / GB:F2} GB)");
            sb.AppendLine($"  OS - entry sum         : {gapEntryVsOs,20:N0}  ({(double)gapEntryVsOs / GB:F2} GB)");
            sb.AppendLine();
            sb.AppendLine("── ZERO-SIZE BASE FILE BREAKDOWN ────────");
            sb.AppendLine($"  Total zero-size base files    : {diag.ZeroBaseFiles,10:N0}");
            sb.AppendLine($"  → Had NO $DATA attribute      : {diag.ZeroNoDataAttr,10:N0}");
            sb.AppendLine($"  → Had resident $DATA, size=0  : {diag.ZeroResidentZero,10:N0}");
            sb.AppendLine($"  → Had non-resident $DATA,sz=0 : {diag.ZeroNonResZero,10:N0}");
            sb.AppendLine($"  → $DATA flagged COMPRESSED    : {diag.ZeroCompressed,10:N0}");
            sb.AppendLine($"  → $DATA flagged SPARSE        : {diag.ZeroSparse,10:N0}");
            sb.AppendLine();
            sb.AppendLine("── SAMPLE: zero-size files that HAD a $DATA attr ──");
            sb.AppendLine("  Resident   Flags    Name");
            foreach (var sample in diag.ZeroSamples)
                sb.AppendLine($"  {sample.Resident,-10} 0x{sample.Flags:X4}     {sample.Name}");

            sb.AppendLine();
            sb.AppendLine("── TOP 20 ENTRIES BY SIZE ───────────────");
            foreach (var e in top20)
                sb.AppendLine($"  {e.Size,15:N0}  {e.Name}");

            string diagPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ultratree_diag.txt");

            File.WriteAllText(diagPath, sb.ToString());
        }
        catch
        {
        }
    }

    private static List<MftRun> ParseMftRunList(ReadOnlySpan<byte> record)
    {
        var runs = new List<MftRun>(32);

        ushort attrOff = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x14, 2));
        int pos = attrOff;

        while (pos + 8 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(pos, 4));
            if (type == 0xFFFFFFFF)
                break;

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(pos + 4, 4));
            if (len == 0 || pos + len > record.Length)
                break;

            if (type == 0x80 && record[pos + 8] == 1)
            {
                ushort rlOff = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x20, 2));
                ReadOnlySpan<byte> rl = record.Slice(pos + rlOff);

                int rlPos = 0;
                long currentLcn = 0;

                while (rlPos < rl.Length && rl[rlPos] != 0)
                {
                    byte b = rl[rlPos++];
                    int lenS = b & 0x0F;
                    int offS = (b & 0xF0) >> 4;

                    if (rlPos + lenS + offS > rl.Length)
                        break;

                    long runLen = 0;
                    for (int i = 0; i < lenS; i++)
                        runLen |= (long)rl[rlPos++] << (i * 8);

                    long runOff = 0;
                    for (int i = 0; i < offS; i++)
                        runOff |= (long)rl[rlPos++] << (i * 8);

                    if (offS > 0 && (rl[rlPos - 1] & 0x80) != 0)
                    {
                        for (int i = offS; i < 8; i++)
                            runOff |= (long)0xFF << (i * 8);
                    }

                    currentLcn += runOff;

                    if (runLen > 0)
                        runs.Add(new MftRun(currentLcn, runLen));
                }

                return runs;
            }

            pos += (int)len;
        }

        return runs;
    }

    private static bool TryParseRecord(ReadOnlySpan<byte> r, ulong recordFrn, out ParsedRecordLite parsed)
    {
        parsed = default;

        if (r.Length < 0x30)
            return false;

        ushort recordFlags = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x16, 2));
        if ((recordFlags & 0x0001) == 0)
            return false;

        bool isDir = (recordFlags & 0x0002) != 0;
        ulong baseFrn = BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(0x20, 8)) & 0x0000FFFFFFFFFFFFUL;
        ushort attrOff = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(0x14, 2));

        string? name = null;
        ulong parent = 0;
        byte nsRank = byte.MaxValue;
        long totalUnnamedDataSize = 0;
        bool hadData = false;
        bool sawResidentData = false;
        ushort dataFlags = 0;

        int pos = attrOff;
        while (pos + 8 <= r.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(pos, 4));
            if (type == 0xFFFFFFFF)
                break;

            uint len = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(pos + 4, 4));
            if (len == 0 || pos + len > r.Length)
                break;

            if (type == 0x30 && r[pos + 8] == 0)
            {
                ushort voff = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(pos + 20, 2));
                int vpos = pos + voff;

                if (vpos + 0x42 <= r.Length)
                {
                    byte nLen = r[vpos + 0x40];
                    byte ns = r[vpos + 0x41];

                    if (nLen > 0 && vpos + 0x42 + nLen * 2 <= r.Length)
                    {
                        int rank = ns switch
                        {
                            1 => 0,
                            3 => 1,
                            0 => 2,
                            2 => 3,
                            _ => 4
                        };

                        if (rank < nsRank)
                        {
                            nsRank = (byte)rank;
                            name = Encoding.Unicode.GetString(r.Slice(vpos + 0x42, nLen * 2));
                            parent = BinaryPrimitives.ReadUInt64LittleEndian(r.Slice(vpos, 8)) & 0x0000FFFFFFFFFFFFUL;
                        }
                    }
                }
            }
            else if (type == 0x80)
            {
                bool nonResident = r[pos + 8] == 1;
                byte nameLength = r[pos + 9];

                if (nameLength == 0)
                {
                    hadData = true;

                    if (pos + 0x0E <= r.Length)
                        dataFlags = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(pos + 0x0C, 2));

                    if (!nonResident)
                    {
                        sawResidentData = true;
                        if (pos + 0x18 <= r.Length)
                        {
                            uint valueLen = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(pos + 0x10, 4));
                            totalUnnamedDataSize += valueLen;
                        }
                    }
                    else
                    {
                        if (pos + 0x38 <= r.Length)
                        {
                            long realSize = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(pos + 0x30, 8));
                            if (realSize > 0)
                                totalUnnamedDataSize += realSize;
                        }
                    }
                }
            }

            pos += (int)len;
        }

        if (baseFrn == 0)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            parsed = new ParsedRecordLite(
                recordFrn,
                0,
                parent,
                isDir,
                isDir ? 0 : totalUnnamedDataSize,
                hadData,
                sawResidentData,
                dataFlags,
                name);

            return true;
        }

        if (!hadData && totalUnnamedDataSize == 0)
            return false;

        parsed = new ParsedRecordLite(
            recordFrn,
            baseFrn,
            0,
            isDir,
            isDir ? 0 : totalUnnamedDataSize,
            hadData,
            sawResidentData,
            dataFlags,
            null);

        return true;
    }

    private static BootSector ParseBootSector(ReadOnlySpan<byte> boot)
    {
        bool ntfs = boot.Length >= 11 &&
                    boot[3] == 'N' &&
                    boot[4] == 'T' &&
                    boot[5] == 'F' &&
                    boot[6] == 'S';

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(0x0B, 2));
        byte sectorsPerCluster = boot[0x0D];
        long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.Slice(0x30, 8));
        sbyte clustersPerFileRecord = unchecked((sbyte)boot[0x40]);

        int mftRecordSize = clustersPerFileRecord < 0
            ? 1 << (-clustersPerFileRecord)
            : clustersPerFileRecord * bytesPerSector * sectorsPerCluster;

        return new BootSector(ntfs, bytesPerSector, sectorsPerCluster, mftCluster, mftRecordSize);
    }

    private static bool IsFileRecord(ReadOnlySpan<byte> r) =>
        r.Length >= 4 &&
        r[0] == 'F' &&
        r[1] == 'I' &&
        r[2] == 'L' &&
        r[3] == 'E';

    private static void ApplyUsaFixup(Span<byte> r, int bytesPerSector)
    {
        if (r.Length < 8)
            return;

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6, 2));

        if (usaCount < 2 || usaOffset + usaCount * 2 > r.Length)
            return;

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(usaOffset, 2));

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd + 2 <= r.Length &&
                BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(sectorEnd, 2)) == sequence)
            {
                ushort replacement = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(usaOffset + i * 2, 2));
                BinaryPrimitives.WriteUInt16LittleEndian(r.Slice(sectorEnd, 2), replacement);
            }
        }
    }

    private static void ReadAt(SafeFileHandle h, long off, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(h, off, out _, 0))
            throw new IOException("Failed to seek volume.");

        if (!ReadFile(h, buffer, count, out int read, IntPtr.Zero) || read != count)
            throw new IOException("Failed to read volume.");
    }

    private static bool TryReadAt(SafeFileHandle h, long off, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(h, off, out _, 0))
            return false;

        return ReadFile(h, buffer, count, out int read, IntPtr.Zero) && read == count;
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
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    private enum FileAccessFlags : uint
    {
        GENERIC_READ = 0x80000000
    }

    private enum FileShareFlags : uint
    {
        FILE_SHARE_READ = 1,
        FILE_SHARE_WRITE = 2,
        FILE_SHARE_DELETE = 4
    }

    private enum CreationDisposition : uint
    {
        OPEN_EXISTING = 3
    }

    private enum FileFlagsAndAttributes : uint
    {
        FILE_ATTRIBUTE_NORMAL = 0x80
    }

    private readonly record struct BootSector(
        bool IsNtfs,
        ushort BytesPerSector,
        byte SectorsPerCluster,
        long MftCluster,
        int MftRecordSize);
}