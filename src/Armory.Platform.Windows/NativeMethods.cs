using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Armory.Platform.Windows;

internal static class NativeMethods
{
    internal const int ReplaceExisting = 1;
    internal const int WriteThrough = 8;
    internal const uint FileAttributeReadOnly = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    internal static string FileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return FileId(info);
    }
    internal static string FileId(ByHandleFileInformation info) => $"{info.VolumeSerial:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
    // A directory's NTFS identity, or null when it cannot be opened right now. Opened for
    // attributes only and sharing read, write and delete, so a student's Explorer rename or
    // delete of the folder is never blocked by the scan. FILE_FLAG_BACKUP_SEMANTICS is what
    // lets CreateFile open a directory; FILE_FLAG_OPEN_REPARSE_POINT never follows a link.
    internal static string? DirectoryId(string directory)
    {
        using var handle = CreateFileW(WindowsPaths.Extended(directory), FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return GetFileInformationByHandle(handle, out var info) ? FileId(info) : null;
    }
    internal static void Move(string source, string destination, bool replace)
    {
        if (!MoveFileExW(source, destination, WriteThrough | (replace ? ReplaceExisting : 0)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
