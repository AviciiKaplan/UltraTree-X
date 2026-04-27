using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UltraTree;

public sealed class VolumeStats
{
    public string Root { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
}

public sealed class TreeNode : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    
    public long SizeBytes { get; }
    public long AllocatedBytes { get; }
    public int ItemCount { get; }
    public int FileCount { get; }
    public int FolderCount { get; }
    public DateTime Modified { get; }
    
    public double PercentOfParent { get; }
    
    // This holds the nested folders/files!
    public ObservableCollection<TreeNode> Children { get; } = new();

    // Formatting Helpers for the UI
    public string SizeHuman => Utils.FormatBytes(SizeBytes);
    public string AllocatedHuman => Utils.FormatBytes(AllocatedBytes);
    public string PercentOfParentText => $"{PercentOfParent:0.0}%";
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    // UI state for the TreeView
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public TreeNode(string path, string name, bool isDir, long size, long alloc, int items, int files, int folders, DateTime modified, double percentOfParent)
    {
        Path = path;
        Name = name;
        IsDirectory = isDir;
        SizeBytes = size;
        AllocatedBytes = alloc;
        ItemCount = items;
        FileCount = files;
        FolderCount = folders;
        Modified = modified;
        PercentOfParent = percentOfParent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}