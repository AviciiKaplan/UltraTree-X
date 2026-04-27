using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UltraTree;

public static class AllocatedSize
{
    // For compressed/sparse files, this returns actual on-disk allocation more accurately than Length.
    public static long GetAllocatedBytesForFile(string path)
    {
        uint high;
        uint low = GetCompressedFileSizeW(path, out high);

        if (low == 0xFFFFFFFF)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 0) // if err==0 then size really is 0xFFFFFFFF low
                throw new Win32Exception(err);
        }

        long size = ((long)high << 32) | low;

        // NOTE: for some cases, this is "compressed size" rather than raw allocation,
        // but it generally matches Explorer/WizTree expectations better than Length.
        return size;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
