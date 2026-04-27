using System.IO;
using System.Diagnostics;

namespace UltraTree;

public static class ShellFileActions
{
    /// <summary>Open a file/folder using the default Windows shell handler.</summary>
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    /// <summary>Open a folder in Explorer.</summary>
    public static void OpenFolder(string folderPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        });
    }

    /// <summary>Reveal a file in Explorer (highlights it).</summary>
    public static void RevealInExplorer(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Delete to Recycle Bin with Windows shell semantics.
    /// This is the closest to "right-click -> Delete" behavior.
    /// </summary>
    public static void DeleteToRecycleBin(IEnumerable<string> paths)
    {
        // This uses the Windows shell / VB FileIO layer to send items to Recycle Bin.
        // Add reference: project already can use this without extra NuGet.
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    p,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (File.Exists(p))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    p,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
    }
}
