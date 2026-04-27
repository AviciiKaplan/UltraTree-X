using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace UltraTree;

public sealed class FileFact
{
    public required string Path { get; init; }
    public required string ParentPath { get; init; }  // folder path ending with "\"
    public required bool IsDir { get; init; }
    public long LogicalSize { get; init; }            // for files; 0 for dirs
    public long AllocatedSize { get; init; }          // for files; 0 for dirs
}

public sealed class NodeMetrics
{
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long SizeBytes { get; set; }
    public long AllocatedBytes { get; set; }
    
    // Updated property names to match the ViewModel and Scanner
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public int ItemCount { get; set; }
    
    public double PercentOfParent { get; set; }
}

public static class MetricsBuilder
{
    public static IReadOnlyDictionary<string, NodeMetrics> BuildTreeMetrics(
        IEnumerable<FileFact> facts,
        string driveRoot, // "C:\"
        CancellationToken ct)
    {
        // Normalize folders to end with '\'
        static string NormFolder(string p) => p.EndsWith("\\") ? p : p + "\\";

        var map = new ConcurrentDictionary<string, NodeMetrics>(StringComparer.OrdinalIgnoreCase);

        // Ensure root exists
        driveRoot = NormFolder(driveRoot);
        map[driveRoot] = new NodeMetrics { Path = driveRoot, IsDir = true, FolderCount = 1, ItemCount = 1 };

        // 1) Seed nodes and add file contributions to parent chains
        foreach (var f in facts)
        {
            ct.ThrowIfCancellationRequested();

            if (f.IsDir)
            {
                var dir = NormFolder(f.Path);
                map.TryAdd(dir, new NodeMetrics { Path = dir, IsDir = true, FolderCount = 1, ItemCount = 1 });
                continue;
            }

            // file node itself (optional to store; usually table shows folder rows)
            map.TryAdd(f.Path, new NodeMetrics { Path = f.Path, IsDir = false, FileCount = 1, ItemCount = 1 });

            // Walk up folder chain and accumulate
            long size = f.LogicalSize;
            long alloc = f.AllocatedSize;

            string cur = NormFolder(f.ParentPath);

            while (!string.IsNullOrEmpty(cur))
            {
                ct.ThrowIfCancellationRequested();

                var node = map.GetOrAdd(cur, p => new NodeMetrics { Path = p, IsDir = true, FolderCount = 1, ItemCount = 1 });

                // update metrics (thread-safe via AddOrUpdate pattern)
                lock (node)
                {
                    node.SizeBytes += size;
                    node.AllocatedBytes += alloc;
                    node.FileCount += 1;
                    node.ItemCount += 1;
                }

                if (cur.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                cur = ParentFolder(cur);
            }
        }

        // 2) After aggregation, compute PercentOfParent for directories
        foreach (var kv in map)
        {
            ct.ThrowIfCancellationRequested();

            var node = kv.Value;
            if (node.Path.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                node.PercentOfParent = 100;
                continue;
            }

            var parent = ParentFolder(node.Path);
            if (string.IsNullOrEmpty(parent) || !map.TryGetValue(parent, out var parentNode))
            {
                node.PercentOfParent = 0;
                continue;
            }

            node.PercentOfParent = parentNode.SizeBytes <= 0
                ? 0
                : (double)node.SizeBytes / parentNode.SizeBytes * 100.0;
        }

        return map;
    }

    public static string ParentFolder(string folderPathWithSlash)
    {
        // expects folder path with trailing "\" (except maybe drive root)
        var p = folderPathWithSlash.TrimEnd('\\');
        int idx = p.LastIndexOf('\\');
        if (idx < 0) return "";
        return p.Substring(0, idx + 1);
    }
}