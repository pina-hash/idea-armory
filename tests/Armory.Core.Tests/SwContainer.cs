using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Armory.Core.Tests;

// A synthetic SolidWorks 2015+ container: bytes 0 to 7 (byte 7 is the key that rotates every
// stream name), then chunks of [4-byte value][14 00 06 00 08 00][4 bytes][u32 flag][u32
// compressed size][u32 size][u32 name length][rotated name][raw DEFLATE payload]. A table-of-
// contents entry has a flag below 65536 and no payload. The end-to-end tests use it too (linked).
internal sealed class SwContainer
{
    private readonly List<(string Name, byte[] Stored, uint Flag, uint Size)> chunks = [];
    // Byte 7 of the file; its low three bits rotate the names.
    public byte KeyByte { get; init; } = 0x04;
    // The rotation actually used for the names (null: the key byte's), to build a mismatch.
    public int? NameKey { get; init; }
    // Where the first chunk starts (measured: 9 to 18).
    public int FirstChunkAt { get; init; } = 11;

    // What every real file carries around the release streams.
    public void AddTypicalStreams()
    {
        Add("Contents/3DExperienceExchange2", Encoding.ASCII.GetBytes(new string('e', 300)));
        Add("Contents/CMgr", Encoding.ASCII.GetBytes("configuration manager " + new string('c', 900)));
        Add("docProps/app.xml", Encoding.UTF8.GetBytes("<Properties><Application>SOLIDWORKS</Application><AppVersion>23.0000</AppVersion></Properties>"));
        Add("Header2", [1, 2, 3, 4, 5, 6, 7, 8]);
        Add("PreviewPNG", Enumerable.Range(0, 2000).Select(i => (byte)(i * 7)).ToArray());
    }

    public static SwContainer Typical(int code, int[] majors) => new SwContainer().WithTypical(code, majors);
    public static SwContainer WithHistory(int code, byte[] history, int? declared = null)
    {
        var container = new SwContainer();
        container.AddTypicalStreams();
        container.Add($"_MO_VERSION_{code}/Biography", Encoding.ASCII.GetBytes("moBiography_c Service Pack 2"));
        container.Add($"_MO_VERSION_{code}/History", history, declared);
        return container;
    }

    public SwContainer WithTypical(int code, int[]? majors = null)
    {
        AddTypicalStreams();
        Add($"_MO_VERSION_{code}/Biography", Encoding.ASCII.GetBytes("moBiography_c Service Pack 2"));
        Add($"_MO_VERSION_{code}/History", History(majors ?? [code]));
        Add("ThirdPty/Something", Encoding.ASCII.GetBytes(new string('t', 120)));
        return this;
    }

    public SwContainer With(string name, byte[] payload)
    {
        Add(name, payload);
        return this;
    }

    public void Add(string name, byte[] payload, int? declared = null)
        => chunks.Add((name, Deflate(payload), 0x8C2EFA3Cu ^ (uint)name.Length, (uint)(declared ?? payload.Length)));

    // Stored bytes exactly as given (not deflated), declared as size bytes once inflated.
    public void AddRaw(string name, byte[] stored, int size) => chunks.Add((name, stored, 0x7AE4033Bu, (uint)size));

    public void AddToc(string name) => chunks.Add((name, [], 0x1234, 0));

    public byte[] Build()
    {
        var output = new MemoryStream();
        byte[] start = [0x8D, 0x73, 0x5B, 0x07, 0x00, 0x00, 0x00, KeyByte];
        for (var i = 0; i < FirstChunkAt; i++) output.WriteByte(i < start.Length ? start[i] : (byte)0x45);
        var key = NameKey ?? KeyByte & 7;
        Span<byte> header = stackalloc byte[0x1E];
        foreach (var (name, stored, flag, size) in chunks)
        {
            var rotated = Rotate(name, key);
            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4A5995D6);
            byte[] marker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
            marker.CopyTo(header[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x0A..], 0x7FE83FDF);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x0E..], flag);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x12..], (uint)stored.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x16..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x1A..], (uint)rotated.Length);
            output.Write(header);
            output.Write(rotated);
            output.Write(stored);
        }
        return output.ToArray();
    }

    // The name as stored: each byte rotated right, so rotating left by the key reads it.
    public static byte[] Rotate(string name, int key)
        => Encoding.ASCII.GetBytes(name).Select(b => key == 0 ? b : (byte)((b >> key) | (b << (8 - key)))).ToArray();

    public static byte[] Deflate(byte[] payload)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(payload);
        return output.ToArray();
    }

    // _MO_VERSION_<code>/History as SolidWorks writes it (an MFC CArchive): the moVersionHistory_c
    // class tag, the entry count, then per entry a small prefix, an empty Unicode CString, the
    // major code and a moDateCodeHistory_c of build dates (yyyyddd), then a trailer.
    public static byte[] History(int[] majors, uint? count = null, string className = "moVersionHistory_c", int schema = 1)
    {
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write((ushort)0xFFFF);
        writer.Write((ushort)schema);
        writer.Write((ushort)className.Length);
        writer.Write(Encoding.ASCII.GetBytes(className));
        writer.Write(count ?? (uint)majors.Length);
        for (var i = 0; i < majors.Length; i++)
        {
            if (i == 0) writer.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0x08, 0x10, 0x8C, 0x45 });
            else writer.Write(new byte[] { 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x8C, 0xBA, 0xB0, 0x52 });
            writer.Write(new byte[] { 0xFF, 0xFE, 0xFF, 0x00 });
            writer.Write((uint)majors[i]);
            if (i == 0)
            {
                writer.Write(new byte[] { 0xFF, 0xFF, 0x01, 0x00, 0x13, 0x00 });
                writer.Write(Encoding.ASCII.GetBytes("moDateCodeHistory_c"));
            }
            else writer.Write(new byte[] { 0x03, 0x80 });
            writer.Write(1u);
            writer.Write(0x01000000u | (uint)(2007 + majors[i] / 1000) * 1000 + 261);
        }
        writer.Write(new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Flush();
        return output.ToArray();
    }
}
