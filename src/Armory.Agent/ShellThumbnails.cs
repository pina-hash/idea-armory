using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Armory.Agent;

// A file's picture as File Explorer shows it, from Windows' own thumbnail handlers (SolidWorks
// installs one for parts, assemblies and drawings), as PNG bytes, or null when Windows has no
// picture for it. Only a real thumbnail, never the file type's icon (SIIGBF_THUMBNAILONLY):
// the window keeps its own glyph for those. The handlers run on one STA thread of their own,
// as some require, one file at a time, never on the window's thread; recent answers are kept
// by path, size and time written, so a list scrolled back and forth asks Windows once.
internal sealed class ShellThumbnails : IDisposable
{
    internal const int Size = 192;
    private const int Kept = 600;
    private readonly BlockingCollection<(string File, TaskCompletionSource<byte[]?> Done)> queue = new();
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly Dictionary<(string File, long Length, long Written), byte[]?> cache = [];
    private readonly Queue<(string File, long Length, long Written)> order = new();
    private readonly Action<string>? log;

    internal ShellThumbnails(Action<string>? log = null)
    {
        this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = "Armory thumbnails" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal Task<byte[]?> GetAsync(string file)
    {
        var key = KeyOf(file);
        if (key is null) return Task.FromResult<byte[]?>(null);
        lock (gate) if (cache.TryGetValue(key.Value, out var known)) return Task.FromResult(known);
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { queue.Add((file, done)); }
        catch (InvalidOperationException) { done.TrySetResult(null); } // disposed
        return done.Task;
    }

    private static (string, long, long)? KeyOf(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? (info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private void Run()
    {
        foreach (var (file, done) in queue.GetConsumingEnumerable())
        {
            byte[]? png = null;
            try { png = Make(file, Size); }
            catch (Exception error) when (error is COMException or ExternalException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { log?.Invoke($"thumbnail {Path.GetFileName(file)}: {error.GetType().Name}: {error.Message}"); }
            if (KeyOf(file) is { } key)
                lock (gate)
                {
                    if (cache.TryAdd(key, png)) order.Enqueue(key);
                    while (order.Count > Kept) cache.Remove(order.Dequeue());
                }
            done.TrySetResult(png);
        }
    }

    // One file's thumbnail at most size pixels on its longer side, as PNG; null when there is none.
    internal static byte[]? Make(string file, int size)
    {
        if (!File.Exists(file)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out var item) != 0 || item is null) return null;
        try
        {
            var factory = (IShellItemImageFactory)item;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, ThumbnailOnly | BiggerSizeOk, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            try { return Png(bitmap); }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    // The handler's bitmap with its transparency (a 32-bit DIB section, premultiplied), else as
    // Windows draws it opaque.
    private static byte[] Png(IntPtr hbitmap)
    {
        var section = new DibSection();
        using var image = GetObject(hbitmap, Marshal.SizeOf<DibSection>(), ref section) == Marshal.SizeOf<DibSection>()
            && section.Bitmap.BitsPixel == 32 && section.Bitmap.Bits != IntPtr.Zero
            ? FromSection(section)
            : Image.FromHbitmap(hbitmap);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Bitmap FromSection(DibSection section)
    {
        var width = section.Bitmap.Width;
        var height = section.Bitmap.Height;
        var stride = section.Bitmap.WidthBytes;
        // A positive height in the header is a bottom-up DIB: its first row is the picture's last.
        var bottomUp = section.Header.Height > 0;
        var pixels = new byte[stride * height];
        Marshal.Copy(section.Bitmap.Bits, pixels, 0, pixels.Length);
        // A handler that leaves alpha at zero everywhere drew an opaque picture.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        if (!anyAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(pixels, (bottomUp ? height - 1 - row : row) * stride, data.Scan0 + row * data.Stride, Math.Min(stride, data.Stride));
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    public void Dispose()
    {
        queue.CompleteAdding();
        while (queue.TryTake(out var left)) left.Done.TrySetResult(null);
    }

    private const int ThumbnailOnly = 0x08, BiggerSizeOk = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public BitmapInfo Bitmap;
        public BitmapHeader Header;
        public uint BitField0, BitField1, BitField2;
        public IntPtr Section;
        public uint Offset;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref DibSection section);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
