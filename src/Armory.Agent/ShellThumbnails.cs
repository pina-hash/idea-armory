using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Armory.Agent;

// A file's picture as File Explorer shows it, from Windows' own thumbnail handlers (SolidWorks
// installs one for parts, assemblies and drawings), as PNG bytes, or null when Windows has no
// picture for it. Only a real thumbnail, never the file type's icon (SIIGBF_THUMBNAILONLY):
// the window keeps its own glyph for those. The handlers run on an STA thread of their own,
// as some require, one file at a time, never on the window's thread.
//
// A handler is someone else's code, so nothing here waits on one for long (0.3.3, N15):
//   - the window has its answer within AnswerWithin (5 s), null when the picture is late;
//   - a handler stuck on one file for StuckAfter (20 s) is left behind on its thread, a new
//     thread makes the next pictures, and the file is logged and counted (stuck: the flight
//     recorder's thumbnailStuck); after MaxStuck such threads no more pictures are made until
//     Armory starts again;
//   - at most Waiting (64) pictures wait, the newest (the rows in view): an older one answers
//     null at once, and two asks for one file share one picture;
//   - answers are kept by path, size and time written, read before the picture is made (so a
//     picture is never kept under bytes it wasn't made from), the last Kept of them; a file
//     with no picture is asked again after NoPictureFor (60 s), since SolidWorks may have had
//     it open, or Windows may still have been making it.
internal sealed class ShellThumbnails : IDisposable
{
    internal const int Size = 192;
    internal const int Kept = 600;
    internal const int Waiting = 64;
    internal const int MaxStuck = 3;
    internal static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan NoPictureFor = TimeSpan.FromSeconds(60);

    private readonly object gate = new();
    private readonly Dictionary<Key, Answer> cache = [];
    private readonly Queue<Key> order = new();
    private readonly LinkedList<Job> waiting = new();
    // Every picture asked for and not answered yet (waiting, or being made): another ask for
    // the same file joins it.
    private readonly Dictionary<Key, Job> asked = [];
    private readonly Func<string, int, byte[]?> make;
    private readonly Action<string>? log;
    private readonly Action<string>? stuck;
    private readonly TimeProvider clock;
    private readonly TimeSpan answerWithin;
    private readonly TimeSpan stuckAfter;
    private readonly ITimer watchdog;
    private Worker worker;
    private int stuckThreads;
    private bool disposed;

    // log: a line for agent.log; stuck: the file a handler got stuck on (the flight recorder).
    // Tests pass their own make, clock and waits; the app uses Windows' handlers (Make).
    internal ShellThumbnails(Action<string>? log = null, Action<string>? stuck = null, Func<string, int, byte[]?>? make = null,
        TimeProvider? clock = null, TimeSpan? answerWithin = null, TimeSpan? stuckAfter = null)
    {
        this.log = log;
        this.stuck = stuck;
        this.make = make ?? Make;
        this.clock = clock ?? TimeProvider.System;
        this.answerWithin = answerWithin ?? AnswerWithin;
        this.stuckAfter = stuckAfter ?? StuckAfter;
        worker = StartWorker();
        var look = this.stuckAfter / 4;
        watchdog = this.clock.CreateTimer(_ => Watch(), null, look, look);
    }

    // How many handler threads were left behind, stuck.
    internal int StuckThreads { get { lock (gate) return stuckThreads; } }

    internal Task<byte[]?> GetAsync(string file)
    {
        // The key first: a picture is kept under the bytes it was asked for.
        var key = KeyOf(file);
        if (key is null) return Task.FromResult<byte[]?>(null);
        Job? job;
        Job? dropped = null;
        lock (gate)
        {
            if (disposed || stuckThreads >= MaxStuck) return Task.FromResult<byte[]?>(null);
            if (cache.TryGetValue(key.Value, out var known) && (known.Until is not { } until || clock.GetUtcNow() < until)) return Task.FromResult(known.Png);
            if (!asked.TryGetValue(key.Value, out job))
            {
                job = new Job(file, key.Value);
                asked[key.Value] = job;
                waiting.AddLast(job);
                // The newest wait (the rows in view); the oldest gives way.
                if (waiting.Count > Waiting)
                {
                    dropped = waiting.First!.Value;
                    waiting.RemoveFirst();
                    asked.Remove(dropped.Key);
                }
                Monitor.PulseAll(gate);
            }
        }
        dropped?.Done.TrySetResult(null);
        return InTime(job.Done.Task);
    }

    private async Task<byte[]?> InTime(Task<byte[]?> made)
    {
        try { return await made.WaitAsync(answerWithin, clock); }
        catch (TimeoutException) { return null; }
    }

    private static Key? KeyOf(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? new Key(info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private Worker StartWorker()
    {
        var next = new Worker();
        var thread = new Thread(() => Run(next)) { IsBackground = true, Name = "Armory thumbnails" };
        // The shell's thumbnails are COM, Windows only (a test host elsewhere gets none).
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return next;
    }

    private void Run(Worker me)
    {
        while (true)
        {
            Job job;
            lock (gate)
            {
                while (!disposed && !me.LeftBehind && waiting.Count == 0) Monitor.Wait(gate);
                if (disposed || me.LeftBehind) return;
                job = waiting.First!.Value;
                waiting.RemoveFirst();
                me.Current = job;
                me.Since = clock.GetTimestamp();
            }
            byte[]? png = null;
            try { png = make(job.File, Size); }
            // Whatever a handler throws, this thread goes on to the next file (one that ended would
            // leave every later picture waiting).
            catch (Exception error) when (error is not OutOfMemoryException)
            { log?.Invoke($"thumbnail {Path.GetFileName(job.File)}: {error.GetType().Name}: {error.Message}"); }
            lock (gate)
            {
                // A thread left behind that answers after all: its picture is still good.
                if (!disposed) Remember(job.Key, png);
                if (asked.TryGetValue(job.Key, out var same) && same == job) asked.Remove(job.Key);
                me.Current = null;
            }
            job.Done.TrySetResult(png);
        }
    }

    // A handler stuck on one file is left behind with its thread; a new thread takes the rest.
    private void Watch()
    {
        Job? job;
        int count;
        lock (gate)
        {
            job = worker.Current;
            if (disposed || job is null || worker.LeftBehind || clock.GetElapsedTime(worker.Since) < stuckAfter) return;
            worker.LeftBehind = true;
            if (asked.TryGetValue(job.Key, out var same) && same == job) asked.Remove(job.Key);
            // Not that file again for a while: the next thread would likely stick on it too.
            Remember(job.Key, null);
            count = ++stuckThreads;
            if (count < MaxStuck) worker = StartWorker();
            else
            {
                foreach (var left in waiting) left.Done.TrySetResult(null);
                waiting.Clear();
                asked.Clear();
            }
            Monitor.PulseAll(gate);
        }
        job.Done.TrySetResult(null);
        var name = Path.GetFileName(job.File);
        var seconds = stuckAfter.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        log?.Invoke(count < MaxStuck
            ? $"thumbnail {name}: Windows' thumbnail handler gave no answer in {seconds} s; a new thread makes the next pictures ({count} left behind)"
            : $"thumbnail {name}: Windows' thumbnail handler gave no answer in {seconds} s, {count} times now; no more pictures until Armory starts again");
        stuck?.Invoke(job.File);
    }

    // Under gate. A missing picture stands for NoPictureFor only; a picture until its file changes.
    private void Remember(Key key, byte[]? png)
    {
        if (!cache.ContainsKey(key)) order.Enqueue(key);
        cache[key] = new Answer(png, png is null ? clock.GetUtcNow() + NoPictureFor : null);
        while (cache.Count > Kept && order.TryDequeue(out var old)) cache.Remove(old);
    }

    private readonly record struct Key(string File, long Length, long Written);

    private sealed record Answer(byte[]? Png, DateTimeOffset? Until);

    private sealed class Job(string file, Key key)
    {
        internal string File { get; } = file;
        internal Key Key { get; } = key;
        internal TaskCompletionSource<byte[]?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // One handler thread: the picture it is making, since when, and whether it was left behind.
    private sealed class Worker
    {
        internal Job? Current;
        internal long Since;
        internal bool LeftBehind;
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
        List<Job> left;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            left = [.. waiting];
            waiting.Clear();
            asked.Clear();
            Monitor.PulseAll(gate);
        }
        watchdog.Dispose();
        foreach (var job in left) job.Done.TrySetResult(null);
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
