using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
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
            // Whatever a handler throws, this thread goes on to the next file (one that ended would
            // leave every later picture waiting).
            catch (Exception error) when (error is not OutOfMemoryException)
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

    // The handler's bitmap as PNG, its transparency kept: GDI gives its pixels as 32-bit BGRA,
    // top row first, whatever kind of bitmap it is; a picture with no alpha at all is opaque.
    // Written here (zlib from .NET, a CRC table), so nothing outside the runtime is loaded.
    private static byte[]? Png(IntPtr hbitmap)
    {
        var bitmap = new BitmapInfo();
        if (GetObject(hbitmap, Marshal.SizeOf<BitmapInfo>(), ref bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height == 0) return null;
        var width = bitmap.Width;
        var height = Math.Abs(bitmap.Height);
        var request = new DibRequest { Header = new BitmapHeader { Size = Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
        var pixels = new byte[width * height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, hbitmap, 0, (uint)height, pixels, ref request, 0) == 0) return null;
        }
        finally { _ = ReleaseDC(IntPtr.Zero, screen); }
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        var rgba = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = anyAlpha ? pixels[i + 3] : 255;
            // Premultiplied by alpha, as Windows hands thumbnails over: back to straight color.
            if (a is > 0 and < 255)
            {
                r = Math.Min(255, r * 255 / a);
                g = Math.Min(255, g * 255 / a);
                b = Math.Min(255, b * 255 / a);
            }
            rgba[i] = (byte)r;
            rgba[i + 1] = (byte)g;
            rgba[i + 2] = (byte)b;
            rgba[i + 3] = (byte)a;
        }
        return Encode(width, height, rgba);
    }

    internal static byte[] Encode(int width, int height, byte[] rgba)
    {
        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        Chunk(file, "IHDR", header);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = width * 4;
            for (var row = 0; row < height; row++)
            {
                zlib.WriteByte(0); // no filter
                zlib.Write(rgba, row * stride, stride);
            }
        }
        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    private static void Chunk(Stream to, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        to.Write(four);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        to.Write(name);
        to.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        to.Write(four);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, byte[] bytes)
    {
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
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

    // BITMAPINFO for 32-bit pixels: the header, and room for the three masks GDI may write.
    [StructLayout(LayoutKind.Sequential)]
    private struct DibRequest
    {
        public BitmapHeader Header;
        public uint Mask0, Mask1, Mask2;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref BitmapInfo bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref DibRequest info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
