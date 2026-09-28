using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace Armory.Probe;

internal static class CompoundInspection
{
    internal static string Inspect(string file)
    {
        Span<byte> signature = stackalloc byte[8];
        using (var input = File.OpenRead(file)) input.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            return $"Header signature `{Convert.ToHexString(signature)}` is not the OLE compound-file signature. Standard compound streams and OLE SummaryInformation cannot be read by this method. No saved release was inferred.\n";
        var opened = StgOpenStorage(file, null, 0x20, IntPtr.Zero, 0, out var storage);
        if (opened < 0) throw new InvalidDataException($"StgOpenStorage failed: 0x{opened:X8}.");
        var report = new StringBuilder();
        try
        {
            storage.EnumElements(0, IntPtr.Zero, 0, out var iterator);
            try
            {
                var item = new STATSTG[1];
                while (iterator.Next(1, item, out var fetched) == 0 && fetched == 1)
                {
                    var name = item[0].pwcsName;
                    report.AppendLine($"- Stream/storage `{Escape(name)}`: {item[0].cbSize} bytes.");
                    if (item[0].type != 2 || item[0].cbSize > 1024 * 1024 ||
                        !(name.Contains("Summary", StringComparison.OrdinalIgnoreCase) || name.Contains("Version", StringComparison.OrdinalIgnoreCase) || name.Equals("Header", StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        storage.OpenStream(name, IntPtr.Zero, 0x10, 0, out var stream);
                        try
                        {
                            var bytes = new byte[checked((int)item[0].cbSize)];
                            stream.Read(bytes, bytes.Length, IntPtr.Zero);
                            var text = Encoding.Latin1.GetString(bytes) + "\n" + Encoding.Unicode.GetString(bytes);
                            var tokens = Regex.Matches(text, @"(?i)SOLIDWORKS[\x20-\x7e]{0,35}|\b20[0-9]{2}\b").Select(m => m.Value).Distinct().Take(12);
                            report.AppendLine($"  Candidate text (not a validated release): `{string.Join(" | ", tokens)}`.");
                        }
                        finally { Marshal.ReleaseComObject(stream); }
                    }
                    catch (COMException error) { report.AppendLine($"  Could not read stream: HRESULT 0x{error.HResult:X8}."); }
                }
            }
            finally { Marshal.ReleaseComObject(iterator); }
        }
        finally { Marshal.ReleaseComObject(storage); }
        return report.ToString();
    }
    private static string Escape(string value) => string.Concat(value.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorage(string name, IStorage? priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);
    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStorage storage);
        void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage? priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);
        void CopyTo(uint count, IntPtr excludeIds, IntPtr excludeNames, IStorage destination);
        void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);
        void Commit(uint flags);
        void Revert();
        void EnumElements(uint reserved1, IntPtr reserved2, uint reserved3, out IEnumStatStg enumerator);
    }
    [ComImport, Guid("0000000D-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumStatStg
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] STATSTG[] elements, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumStatStg clone);
    }
}
