using System.IO;

namespace UltraTree;

public static class VolumeInfo
{
    public static VolumeStats GetVolumeStats(string driveRootLikeCColonOrSlash)
    {
        var root = driveRootLikeCColonOrSlash.TrimEnd('\\') + "\\";
        var di = new DriveInfo(root);

        return new VolumeStats
        {
            Root = root,
            TotalBytes = di.TotalSize,
            FreeBytes = di.AvailableFreeSpace
        };
    }
}