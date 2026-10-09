using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Armory.Platform.Windows;

internal static class NativeMethods
{
    internal const int ReplaceExisting = 1;
    internal const int WriteThrough = 8;
    internal const uint FileAttributeReadOnly = 0x1;
    internal const uint FileAttributeDirectory = 0x10;
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
    // A directory's or file's NTFS identity, or null when it cannot be opened right now.
    // Opened for attributes only and sharing read, write and delete, so a student's Explorer
    // rename or delete is never blocked by the scan, and a file another program holds with no
    // sharing at all can still be identified (sharing applies to data and delete access, not
    // to FILE_READ_ATTRIBUTES). FILE_FLAG_BACKUP_SEMANTICS is what lets CreateFile open a
    // directory; FILE_FLAG_OPEN_REPARSE_POINT never follows a link.
    internal static string? EntryId(string path)
    {
        using var handle = CreateFileW(WindowsPaths.Extended(path), FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return GetFileInformationByHandle(handle, out var info) ? FileId(info) : null;
    }
    internal static string? DirectoryId(string directory) => EntryId(directory);
    // The NTFS id of the file at path, or null when no file is there (nothing, or a folder);
    // any other failure throws.
    internal static string? ExistingFileId(string path)
    {
        using var handle = CreateFileW(WindowsPaths.Extended(path), FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.Attributes & FileAttributeDirectory) != 0 ? null : FileId(info);
    }
    // The NTFS id, last-write time and size of the file at path, read through a handle opened for
    // attributes only (it never conflicts with another program's sharing), or null when no file
    // is there (nothing, or a folder); any other failure throws.
    internal static (string Id, DateTime LastWriteUtc, long Size)? FileStamp(string path)
    {
        using var handle = CreateFileW(WindowsPaths.Extended(path), FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete), IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & FileAttributeDirectory) != 0) return null;
        var written = DateTime.FromFileTimeUtc(((long)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
        return (FileId(info), written, ((long)info.SizeHigh << 32) | info.SizeLow);
    }

    internal static void Move(string source, string destination, bool replace)
    {
        if (!MoveFileExW(source, destination, WriteThrough | (replace ? ReplaceExisting : 0)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenameIgnoreReadOnlyAttribute = 0x40;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    // Renames source over an existing destination WITHOUT clearing the destination's read-only
    // bit first: SetFileInformationByHandle(FileRenameInfoEx) with
    // FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE (Windows 10
    // 1809 and later, NTFS). Like MoveFileEx without POSIX semantics it fails while any program
    // has the destination open, and the source is opened with FILE_FLAG_WRITE_THROUGH as
    // MoveFileEx's MOVEFILE_WRITE_THROUGH does. False (nothing renamed) when this Windows or this
    // volume's file system does not support it (ERROR_INVALID_FUNCTION, ERROR_NOT_SUPPORTED,
    // ERROR_INVALID_PARAMETER); any other failure throws.
    internal static bool TryReplaceIgnoringReadOnly(string source, string destination)
    {
        using var handle = CreateFileW(WindowsPaths.Extended(source), Delete | Synchronize,
            (uint)(FileShare.Read | FileShare.Delete), IntPtr.Zero, OpenExisting, FileFlagWriteThrough, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var name = WindowsPaths.Extended(destination);
        // FILE_RENAME_INFO: a 4-byte Flags union, the RootDirectory handle (pointer aligned),
        // the name length in bytes, then the name itself, NUL terminated.
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var nameBytes = name.Length * 2;
        // sizeof(FILE_RENAME_INFO) already holds one WCHAR, so this leaves room for the NUL.
        var size = (IntPtr.Size == 8 ? 24 : 16) + nameBytes;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(RenameReplaceIfExists | RenameIgnoreReadOnlyAttribute)));
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, nameBytes);
            Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
            if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87) return false;
            throw new Win32Exception(error);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
