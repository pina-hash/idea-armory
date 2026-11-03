# Read real Windows shortcut and window property stores in installer smoke tests.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class ArmoryPinReader
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public int PropertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    public static string WindowProperty(IntPtr window, int id)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = id };
            PropVariant value;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            try { return (value.Type == 31 || value.Type == 8) && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    public static string Target(string shortcut)
    {
        object link = new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
            var buffer = new System.Text.StringBuilder(32768);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    public static string AppId(string shortcut)
    {
        object link = new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
            PropertyKey key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            PropVariant value;
            if (((IPropertyStore)link).GetValue(ref key, out value) != 0) return null;
            try { return (value.Type == 31 || value.Type == 8) && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
}
'@

