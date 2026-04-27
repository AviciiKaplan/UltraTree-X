using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace UltraTree;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void TreeNavGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeNavGrid.SelectedItem is not ResultRow row)
            return;

        if (Directory.Exists(row.Path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{row.Path}\"",
                UseShellExecute = true
            });
        }
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ResultRow row)
            return;

        if (File.Exists(row.Path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{row.Path}\"",
                UseShellExecute = true
            });
        }
    }
}