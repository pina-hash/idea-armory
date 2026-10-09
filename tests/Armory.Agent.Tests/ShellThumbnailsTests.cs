using System.Buffers.Binary;

namespace Armory.Agent.Tests;

// File Explorer's own pictures: a picture file has one (Windows' image thumbnail handler), a
// text file has none (never the file type's icon), and a path outside the vault is never read.
public sealed class ShellThumbnailsTests
{
    [WindowsFact]
    public async Task A_picture_has_a_thumbnail_and_a_text_file_has_none()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot 2027"));
        File.WriteAllBytes(vault.File("Robot 2027/Render.bmp"), Bitmap24(160, 100));
        File.WriteAllText(vault.File("Robot 2027/Notes.txt"), "torque numbers");

        var png = ShellThumbnails.Make(vault.File("Robot 2027/Render.bmp"), 96);
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var (width, height) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.InRange(Math.Max(width, height), 16, 160);
        Assert.Null(ShellThumbnails.Make(vault.File("Robot 2027/Notes.txt"), 96));
        Assert.Null(ShellThumbnails.Make(vault.File("Robot 2027/Missing.bmp"), 96));

        // Through the vault: inside it, the picture; outside it or in .armory, nothing is read.
        using var files = new WindowsVaultFileSystem(vault.Root);
        using var thumbnails = new ShellThumbnails();
        Assert.Equal(vault.File("Robot 2027/Render.bmp"), files.ExistingFile("Robot 2027/Render.bmp"));
        Assert.NotNull(await thumbnails.GetAsync(files.ExistingFile("Robot 2027/Render.bmp")!));
        Assert.Null(files.ExistingFile("../Render.bmp"));
        Assert.Null(files.ExistingFile(".armory/settings.json"));
        Assert.Null(files.ExistingFile("Robot 2027/Missing.bmp"));
    }

    // The PNG is written here, nothing else loaded: its chunks carry their CRCs and its pixels
    // come back exactly, a row at a time, each with no filter.
    [Fact]
    public void The_png_written_carries_the_pixels_exactly()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
        var png = ShellThumbnails.Encode(3, 2, rgba);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var at = 8;
        var chunks = new List<(string Type, byte[] Data)>();
        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(at + 4, 4 + length)), crc);
            chunks.Add((type, data));
            at += 12 + length;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0, 0, 0, 2, 8, 6, 0, 0, 0 }, chunks[0].Data);
        using var inflate = new System.IO.Compression.ZLibStream(new MemoryStream(chunks[1].Data), System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        Assert.Equal([0, .. rgba[..12], 0, .. rgba[12..]], raw.ToArray());
    }

    // A handler is someone else's code (N15): the window never waits on one for long. These run
    // anywhere, with a stand-in for Windows' handlers and short waits.
    private static readonly byte[] Picture = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    private static string Part(TempFolder folder, string name, int length = 16)
    {
        var file = folder.File(name);
        File.WriteAllBytes(file, new byte[length]);
        return file;
    }

    [Fact]
    public async Task A_hung_handler_answers_null_in_time()
    {
        using var folder = new TempFolder();
        using var never = new ManualResetEventSlim();
        using var thumbnails = new ShellThumbnails(make: (_, _) => { never.Wait(TimeSpan.FromSeconds(10)); return Picture; },
            answerWithin: TimeSpan.FromMilliseconds(200), stuckAfter: TimeSpan.FromSeconds(30));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await thumbnails.GetAsync(Part(folder, "Gearbox.SLDASM")));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
        never.Set();
    }

    [Fact]
    public async Task A_stuck_handler_is_left_behind_and_a_new_thread_makes_the_next_pictures()
    {
        using var folder = new TempFolder();
        var log = new List<string>();
        var stuck = new List<string>();
        using var release = new ManualResetEventSlim();
        var gear = Part(folder, "Gear.SLDPRT");
        var plate = Part(folder, "Plate.SLDPRT");
        using var thumbnails = new ShellThumbnails(line => { lock (log) log.Add(line); }, file => { lock (stuck) stuck.Add(file); },
            make: (file, _) => { if (file == gear) release.Wait(TimeSpan.FromSeconds(20)); return Picture; },
            answerWithin: TimeSpan.FromMilliseconds(150), stuckAfter: TimeSpan.FromMilliseconds(400));
        Assert.Null(await thumbnails.GetAsync(gear));
        // The plate waits behind the gear until the gear's thread is left behind; then a new one makes it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        byte[]? png = null;
        while (png is null && DateTime.UtcNow < deadline) png = await thumbnails.GetAsync(plate);
        Assert.Equal(Picture, png);
        Assert.Equal(1, thumbnails.StuckThreads);
        lock (stuck) Assert.Equal([gear], stuck);
        lock (log) Assert.Contains(log, l => l.StartsWith("thumbnail Gear.SLDPRT: Windows' thumbnail handler gave no answer", StringComparison.Ordinal));
        // The stuck file is not given to the new thread again right away.
        Assert.Null(await thumbnails.GetAsync(gear));
        release.Set();
    }

    [Fact]
    public async Task Only_the_newest_64_pictures_wait_and_an_older_one_answers_null_at_once()
    {
        using var folder = new TempFolder();
        using var hold = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var made = 0;
        using var thumbnails = new ShellThumbnails(make: (_, _) => { started.Set(); hold.Wait(TimeSpan.FromSeconds(10)); Interlocked.Increment(ref made); return Picture; },
            answerWithin: TimeSpan.FromSeconds(10), stuckAfter: TimeSpan.FromSeconds(30));
        // The first is being made; the next 70 wait, and only the newest 64 of them stay.
        var first = thumbnails.GetAsync(Part(folder, "Row-0000.SLDPRT"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var rows = Enumerable.Range(1, 70).Select(i => thumbnails.GetAsync(Part(folder, $"Row-{i:0000}.SLDPRT"))).ToList();
        var dropped = await Task.WhenAll(rows.Take(6)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(dropped, Assert.Null);
        Assert.All(rows.Skip(6), r => Assert.False(r.IsCompleted));
        hold.Set();
        Assert.Equal(Picture, await first);
        Assert.All(await Task.WhenAll(rows.Skip(6)), png => Assert.Equal(Picture, png));
        Assert.Equal(65, made);
    }

    [Fact]
    public async Task Two_asks_for_one_file_make_one_picture()
    {
        using var folder = new TempFolder();
        using var hold = new ManualResetEventSlim();
        var made = 0;
        using var thumbnails = new ShellThumbnails(make: (_, _) => { hold.Wait(TimeSpan.FromSeconds(10)); Interlocked.Increment(ref made); return Picture; },
            answerWithin: TimeSpan.FromSeconds(10), stuckAfter: TimeSpan.FromSeconds(30));
        var file = Part(folder, "Bracket.SLDPRT");
        var one = thumbnails.GetAsync(file);
        var two = thumbnails.GetAsync(file);
        hold.Set();
        Assert.Equal(Picture, await one);
        Assert.Equal(Picture, await two);
        Assert.Equal(Picture, await thumbnails.GetAsync(file));
        Assert.Equal(1, made);
    }

    [Fact]
    public async Task A_missing_picture_is_asked_again_after_a_minute_and_a_picture_is_kept()
    {
        using var folder = new TempFolder();
        var clock = new SetClock();
        var made = new List<string>();
        using var thumbnails = new ShellThumbnails(make: (file, _) => { lock (made) made.Add(Path.GetFileName(file)); return file.EndsWith("Plate.SLDPRT", StringComparison.Ordinal) ? Picture : null; },
            clock: clock, answerWithin: TimeSpan.FromSeconds(10), stuckAfter: TimeSpan.FromSeconds(30));
        var open = Part(folder, "Open-in-SolidWorks.SLDPRT");
        var plate = Part(folder, "Plate.SLDPRT");
        Assert.Null(await thumbnails.GetAsync(open));
        Assert.Equal(Picture, await thumbnails.GetAsync(plate));
        clock.Forward(TimeSpan.FromSeconds(59));
        Assert.Null(await thumbnails.GetAsync(open));
        Assert.Equal(Picture, await thumbnails.GetAsync(plate));
        lock (made) Assert.Equal(["Open-in-SolidWorks.SLDPRT", "Plate.SLDPRT"], made);
        clock.Forward(TimeSpan.FromSeconds(2));
        Assert.Null(await thumbnails.GetAsync(open));
        Assert.Equal(Picture, await thumbnails.GetAsync(plate));
        lock (made) Assert.Equal(["Open-in-SolidWorks.SLDPRT", "Plate.SLDPRT", "Open-in-SolidWorks.SLDPRT"], made);
    }

    [Fact]
    public async Task A_picture_is_kept_under_the_bytes_it_was_asked_for()
    {
        using var folder = new TempFolder();
        var file = Part(folder, "Plate.SLDPRT", 16);
        var made = 0;
        // The file is saved again while its picture is being made: the picture belongs to the old
        // bytes, so the new bytes are asked for again.
        using var thumbnails = new ShellThumbnails(make: (f, _) =>
            {
                if (Interlocked.Increment(ref made) == 1) File.WriteAllBytes(f, new byte[48]);
                return Picture;
            }, answerWithin: TimeSpan.FromSeconds(10), stuckAfter: TimeSpan.FromSeconds(30));
        Assert.Equal(Picture, await thumbnails.GetAsync(file));
        Assert.Equal(Picture, await thumbnails.GetAsync(file));
        Assert.Equal(2, made);
        Assert.Equal(Picture, await thumbnails.GetAsync(file));
        Assert.Equal(2, made);
    }

    // The wall clock, moved by hand (timers still run on real time).
    private sealed class SetClock : TimeProvider
    {
        private TimeSpan ahead;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
        internal void Forward(TimeSpan by) => ahead += by;
    }

    // PNG's CRC-32, a bit at a time (the encoder uses a table).
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    // A plain 24-bit bitmap, a blue gradient.
    private static byte[] Bitmap24(int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + stride * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = 54 + y * stride + x * 3;
                bytes[at] = 200;
                bytes[at + 1] = (byte)(x * 255 / width);
                bytes[at + 2] = (byte)(y * 255 / height);
            }
        return bytes;
    }
}
