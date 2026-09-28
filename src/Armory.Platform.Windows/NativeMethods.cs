using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Armory.Platform.Windows;

internal static class NativeMethods
{
    internal const int ReplaceExisting = 1;
    internal const int WriteThrough = 8;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileExW(string existing, string replacement, int flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
        internal uint VolumeSerial;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint Links;
        internal uint IndexHigh;
        internal uint IndexLow;
    }
    internal static string FileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.VolumeSerial:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
    }
    internal static void Move(string source, string destination, bool replace)
    {
        if (!MoveFileExW(source, destination, WriteThrough | (replace ? ReplaceExisting : 0)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
