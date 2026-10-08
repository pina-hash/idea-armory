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
