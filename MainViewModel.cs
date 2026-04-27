using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace UltraTree;

public sealed class MainViewModel : INotifyPropertyChanged
{
    // --- Data Collections ---
    public ObservableCollection<string> Drives { get; } = new();
    public ObservableCollection<TreeNode> DriveTree { get; } = new();
    public ObservableCollection<ResultRow> TopFiles { get; } = new();

    // --- State Properties ---
    private string? _selectedDrive;
    public string? SelectedDrive
    {
        get => _selectedDrive;
        set 
        { 
            _selectedDrive = value; 
            OnPropertyChanged(); 
            UpdateDriveStats(); 
            OnPropertyChanged(nameof(SelectionText)); 
        }
    }

    private string _statusText = "Ready to scan.";
    public string StatusText 
    { 
        get => _statusText; 
        set { _statusText = value; OnPropertyChanged(); } 
    }

    private double _progressPercent;
    public double ProgressPercent 
    { 
        get => _progressPercent; 
        set { _progressPercent = value; OnPropertyChanged(); } 
    }

    private TreeNode? _selectedFolder;
    public TreeNode? SelectedFolder
    {
        get => _selectedFolder;
        set 
        { 
            _selectedFolder = value; 
            if (value is not null) _selectedFile = null; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(SelectionText)); 
        }
    }

    private ResultRow? _selectedFile;
    public ResultRow? SelectedFile
    {
        get => _selectedFile;
        set 
        { 
            _selectedFile = value; 
            if (value is not null) _selectedFolder = null; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(SelectedFolder)); 
            OnPropertyChanged(nameof(SelectionText)); 
        }
    }

    public string SelectionText
    {
        get
        {
            if (SelectedFile is not null) return SelectedFile.Path;
            if (SelectedFolder is not null) return SelectedFolder.Path;
            return SelectedDrive ?? "No Drive Selected";
        }
    }

    // --- UI/Theme Properties ---
    private SolidColorPaint _legendTextPaint = new SolidColorPaint(SKColors.White);
    public SolidColorPaint LegendTextPaint 
    { 
        get => _legendTextPaint; 
        set { _legendTextPaint = value; OnPropertyChanged(); } 
    }

    private string _totalSpaceText = "--";
    public string TotalSpaceText { get => _totalSpaceText; set { _totalSpaceText = value; OnPropertyChanged(); } }

    private string _spaceUsedText = "--";
    public string SpaceUsedText { get => _spaceUsedText; set { _spaceUsedText = value; OnPropertyChanged(); } }

    private string _spaceFreeText = "--";
    public string SpaceFreeText { get => _spaceFreeText; set { _spaceFreeText = value; OnPropertyChanged(); } }

    private ISeries[] _folderPieSeries = Array.Empty<ISeries>();
    public ISeries[] FolderPieSeries { get => _folderPieSeries; set { _folderPieSeries = value; OnPropertyChanged(); } }

    private ISeries[] _filePieSeries = Array.Empty<ISeries>();
    public ISeries[] FilePieSeries { get => _filePieSeries; set { _filePieSeries = value; OnPropertyChanged(); } }

    // --- Commands ---
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenPathCommand { get; }
    public ICommand CopyPathCommand { get; }

    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        // Load Drives
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady).Select(x => x.Name.TrimEnd('\\')))
            Drives.Add(d);

        SelectedDrive = Drives.FirstOrDefault();

        // Commands
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => _cts is null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts is not null);

        OpenPathCommand = new RelayCommand(param => 
        {
            if (param is string path && !string.IsNullOrWhiteSpace(path))
            {
                try 
                { 
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); 
                }
                catch (Exception ex) 
                { 
                    StatusText = "Error opening file: " + ex.Message; 
                }
            }
        });

        CopyPathCommand = new RelayCommand(param => 
        {
            if (param is string path) Clipboard.SetText(path);
        });
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedDrive)) return;

        _cts = new CancellationTokenSource();
        RaiseCanExecuteChanged();

        DriveTree.Clear(); 
        TopFiles.Clear();
        SelectedFolder = null; 
        SelectedFile = null;
        FolderPieSeries = Array.Empty<ISeries>(); 
        FilePieSeries = Array.Empty<ISeries>();
        
        ProgressPercent = 0; 
        StatusText = "Accessing NTFS MFT...";

        try
        {
            UpdateDriveStats();
            var progress = new Progress<ScanProgress>(p => 
            { 
                ProgressPercent = p.Percent; 
                StatusText = p.Message; 
            });

            // Call the MFT Scanner
            var result = await Task.Run(() => 
                NtfsMftScanner.ScanDrive(SelectedDrive, progress, _cts.Token), _cts.Token);

            long driveBytes = result.TotalBytes;
            uint clusterSize = GetClusterSize(SelectedDrive);

            // Enrich result data
            var enrichedFiles = await Task.Run(() => 
                result.TopFiles.Select(r => EnrichFileRow(r, driveBytes, clusterSize)).ToList(), _cts.Token);

            foreach (var rootNode in result.RootNodes) DriveTree.Add(rootNode);
            foreach (var r in enrichedFiles) TopFiles.Add(r);

            BuildPieCharts();
            UpdateDriveStats();

            StatusText = $"Scan complete. Indexed {result.FileCount:n0} files.";
            ProgressPercent = 100;
        }
        catch (OperationCanceledException) { StatusText = "Scan was cancelled."; }
        catch (Exception ex) { StatusText = "Fatal Error: " + ex.Message; }
        finally 
        { 
            _cts?.Dispose(); 
            _cts = null; 
            RaiseCanExecuteChanged(); 
        }
    }

    private ResultRow EnrichFileRow(ResultRow row, long driveBytes, uint clusterSize)
    {
        DateTime modified = DateTime.MinValue;
        try { modified = File.GetLastWriteTime(row.Path); } catch { }
        
        return new ResultRow(row.Path, row.Bytes)
        {
            DisplayName = row.Path,
            AllocatedBytes = RoundUpToCluster(row.Bytes, clusterSize),
            PercentOfDrive = driveBytes > 0 ? (double)row.Bytes / driveBytes * 100.0 : 0,
            Modified = modified
        };
    }

    private void BuildPieCharts()
    {
        var topFolders = DriveTree.SelectMany(root => root.Children)
                                  .OrderByDescending(x => x.SizeBytes)
                                  .Take(6).ToList();
        
        FolderPieSeries = BuildProfessionalSeries(topFolders.Select(f => new ChartData(f.Name, f.SizeBytes)).ToList());

        var topFiles = TopFiles.Where(x => x.Bytes > 0 && !x.Path.Contains("$"))
                               .OrderByDescending(x => x.Bytes)
                               .Take(6).ToList();
        
        FilePieSeries = BuildProfessionalSeries(topFiles.Select(f => new ChartData(Path.GetFileName(f.Path), f.Bytes)).ToList());
    }

    private ISeries[] BuildProfessionalSeries(List<ChartData> data)
{
    // Vibrant Rainbow Palette - High Contrast for Presentations
    var rainbowPalette = new[] { 
        new SKColor(239, 68, 68),   // Red
        new SKColor(249, 115, 22),  // Orange
        new SKColor(234, 179, 8),   // Yellow/Gold
        new SKColor(34, 197, 94),   // Green
        new SKColor(59, 130, 246),  // Blue
        new SKColor(168, 85, 247),  // Purple
        new SKColor(236, 72, 153)   // Pink
    };

    var series = new List<ISeries>();

    for (int i = 0; i < data.Count; i++)
    {
        var item = data[i];
        series.Add(new PieSeries<double> { 
            Values = new[] { (double)item.Value }, 
            Name = item.Label, 
            Fill = new SolidColorPaint(rainbowPalette[i % rainbowPalette.Length]), 
            // Cutout stroke matches your app background #0F1115
            Stroke = new SolidColorPaint(new SKColor(15, 17, 21), 3), 
            InnerRadius = 80, 
            Pushout = 4, 
            HoverPushout = 15,
            ToolTipLabelFormatter = (point) => $"{item.Label}: {Utils.FormatBytes((long)point.Model)}" 
        });
    }
    return series.ToArray();
}
    private void UpdateDriveStats()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedDrive)) return;
            var drive = new DriveInfo(SelectedDrive);
            if (!drive.IsReady) return;
            
            long total = drive.TotalSize; 
            long free = drive.AvailableFreeSpace; 
            long used = total - free;
            
            TotalSpaceText = Utils.FormatBytes(total);
            SpaceUsedText = $"{Utils.FormatBytes(used)} ({(total > 0 ? (double)used * 100 / total : 0):F1}%)";
            SpaceFreeText = $"{Utils.FormatBytes(free)} ({(total > 0 ? (double)free * 100 / total : 0):F1}%)";
        }
        catch { }
    }

    private static long RoundUpToCluster(long bytes, uint clusterSize) => 
        clusterSize == 0 ? bytes : ((bytes + clusterSize - 1) / clusterSize) * clusterSize;

    private static uint GetClusterSize(string driveRoot)
    {
        try 
        { 
            if (GetDiskFreeSpace(driveRoot, out uint spc, out uint bps, out _, out _)) 
                return spc * bps; 
        } 
        catch { }
        return 4096;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetDiskFreeSpace(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => 
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    
    private void RaiseCanExecuteChanged() 
    { 
        (ScanCommand as RelayCommand)?.RaiseCanExecuteChanged(); 
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged(); 
    }

    private record ChartData(string Label, long Value);
}

// --- HELPER CLASSES ---

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) 
    { 
        _execute = execute; _canExecute = canExecute; 
    }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ResultRow
{
    public ResultRow(string path, long bytes) { Path = path; Bytes = bytes; DisplayName = path; }
    public string Path { get; set; }
    public string DisplayName { get; set; }
    public long Bytes { get; set; }
    public long AllocatedBytes { get; set; }
    public double PercentOfDrive { get; set; }
    public DateTime Modified { get; set; }

    public string SizeHuman => Utils.FormatBytes(Bytes);
    public string AllocatedHuman => Utils.FormatBytes(AllocatedBytes);
    public string PercentText => $"{PercentOfDrive:0.0}%";
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
}

public sealed record ScanProgress(double Percent, string Message);

public static class Utils
{
    public static string FormatBytes(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }
}