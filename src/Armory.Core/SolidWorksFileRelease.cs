using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace Armory.Core;

// Reads the SolidWorks release that wrote a part, assembly or drawing from its bytes alone
// (docs/core/solidworks-version-gate.md, "The saved-release reader"). Files since SolidWorks
// 2015 are a chunk container: each chunk is a 4-byte value, the marker 14 00 06 00 08 00, a
// header with a 32-bit value, the compressed size, the uncompressed size and the name length,
// then the stream name rotated left per byte by the key at byte 7, then raw DEFLATE data.
// Two fields name the release, and a year is returned only when they agree: the one code in
// every "_MO_VERSION_<code>/" stream name, and the last major code of the
// "_MO_VERSION_<code>/History" stream (the list ISldWorks.VersionHistory reports, one major
// code per release the file was saved in). Anything else is unknown (null), never a guess.
// Pure: a seekable stream in, at most 1 MiB of history and a few small buffers in memory.
public static class SolidWorksFileRelease
{
    private static ReadOnlySpan<byte> Marker => [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
    private static ReadOnlySpan<byte> CompoundFile => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    // The MFC CArchive tag of a new class (FF FF, schema 1, name length 18) and its name.
    private static readonly byte[] HistoryClass = [0xFF, 0xFF, 0x01, 0x00, 0x12, 0x00, .. "moVersionHistory_c"u8];
    // Each entry's major code follows an empty Unicode CString.
    private static ReadOnlySpan<byte> EmptyUnicodeString => [0xFF, 0xFE, 0xFF, 0x00];
    private const string VersionPrefix = "_MO_VERSION_", HistoryName = "/History";
    private const int HeadLength = 64, HeaderLength = 0x1E, MaxName = 512, MaxHistory = 1 << 20, MaxEntries = 1000, InlineFlag = 65536;
    // Deflate never grows 1 MiB by more than a few hundred bytes; anything bigger is not a history.
    private const int MaxHistoryCompressed = MaxHistory + 4096;
    private const long MaxChunk = 64L << 20;
    private const int Window = 1 << 16;

    // The official table (ISldWorks.VersionHistory remarks): 8000 is 2015, each release adds
    // 1000, 18000 is 2025 and 19000 is 2026. A pre-release code (18800, a 2026 beta) belongs to
    // the release it previews, so it rounds up: a 2026 beta can never read as 2025. Below 8000
    // is not a 2015-or-later release code.
    public static int? YearOf(int code)
    {
        if (code < 8000) return null;
        var thousands = code / 1000 + (code % 1000 == 0 ? 0 : 1);
        return 2007 + thousands;
    }

    // The release that saved these bytes, or null when it cannot be known exactly. Never throws
    // on bad bytes; a stream that fails (IOException) or a canceled token still throws.
    public static SolidWorksRelease? Read(Stream content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content is null || !content.CanRead || !content.CanSeek) return null;
        try { return ReadSeekable(content, cancellationToken); }
        catch (Exception error) when (error is not (IOException or OperationCanceledException or OutOfMemoryException)) { return null; }
    }

    private static SolidWorksRelease? ReadSeekable(Stream content, CancellationToken ct)
    {
        var length = content.Length;
        var head = new byte[HeadLength];
        content.Position = 0;
        if (ReadFull(content, head) < HeadLength) return null;
        // Before 2015 a SolidWorks file was an OLE compound file: unknown here.
        if (head.AsSpan(0, CompoundFile.Length).SequenceEqual(CompoundFile)) return null;
        if (head.AsSpan().IndexOf(Marker) < 0) return null;
        var key = head[7] & 7;

        int? code = null;
        var histories = 0;
        (long Offset, uint Compressed, uint Size) history = default;
        var window = new byte[Window];
        var header = new byte[HeaderLength];
        var name = new byte[MaxName];
        long pos = 0;
        while (pos < length)
        {
            ct.ThrowIfCancellationRequested();
            var m = Find(content, pos, window);
            if (m < 0) break;
            if (m < 4) { pos = m + 1; continue; }
            var start = m - 4;
            content.Position = start;
            if (ReadFull(content, header) < HeaderLength) break;
            var flag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x0E));
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x12));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x16));
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1A));
            // Not a chunk header after all (the marker inside other bytes): look on.
            if (nameLength is 0 or > MaxName || compressed > MaxChunk || start + HeaderLength + nameLength > length) { pos = m + 1; continue; }
            if (ReadFull(content, name.AsSpan(0, (int)nameLength)) < nameLength) break;
            if (Decode(name.AsSpan(0, (int)nameLength), key) is not { } text) { pos = m + 1; continue; }
            // A table-of-contents entry names a stream without holding it.
            if (flag < InlineFlag || compressed == 0) { pos = m + Marker.Length; continue; }
            var data = start + HeaderLength + nameLength;
            if (data + compressed > length) { pos = m + 1; continue; }
            if (text.StartsWith(VersionPrefix, StringComparison.Ordinal))
            {
                var slash = text.IndexOf('/', VersionPrefix.Length);
                if (slash < 0 || !int.TryParse(text.AsSpan(VersionPrefix.Length, slash - VersionPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var found))
                    return null;
                if (code is { } seen && seen != found) return null; // two release codes in one file
                code = found;
                if (text.AsSpan(slash).SequenceEqual(HistoryName))
                {
                    if (++histories > 1) return null;
                    history = (data, compressed, size);
                }
            }
            pos = data + compressed;
        }
        if (code is not { } only || histories != 1) return null;
        if (history.Size is 0 or > MaxHistory || history.Compressed > MaxHistoryCompressed) return null;
        ct.ThrowIfCancellationRequested();
        if (LastMajor(content, history.Offset, (int)history.Compressed, (int)history.Size) != only) return null;
        return YearOf(only) is { } year ? new SolidWorksRelease(year) : null;
    }

    // The last major code of the History stream: it inflates to exactly its declared size, is
    // the CArchive object moVersionHistory_c, and its entry count (1 to 1000) is the number of
    // major codes in it. Null otherwise.
    private static int? LastMajor(Stream content, long offset, int compressed, int size)
    {
        var raw = new byte[compressed];
        content.Position = offset;
        if (ReadFull(content, raw) < compressed) return null;
        var text = new byte[size];
        using (var inflate = new DeflateStream(new MemoryStream(raw, writable: false), CompressionMode.Decompress))
        {
            if (ReadFull(inflate, text) < size) return null;
            Span<byte> more = stackalloc byte[1];
            if (inflate.Read(more) != 0) return null; // longer than declared
        }
        var span = text.AsSpan();
        if (span.Length < HistoryClass.Length + 4 || !span.StartsWith(HistoryClass)) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[HistoryClass.Length..]);
        if (count is 0 or > MaxEntries) return null;
        var found = 0;
        var last = 0;
        var rest = span[(HistoryClass.Length + 4)..];
        while (rest.IndexOf(EmptyUnicodeString) is var at and >= 0)
        {
            var value = at + EmptyUnicodeString.Length;
            if (value + 4 > rest.Length) return null;
            last = (int)BinaryPrimitives.ReadUInt32LittleEndian(rest[value..]);
            if (++found > count) return null;
            rest = rest[(value + 4)..];
        }
        return found == count ? last : null;
    }

    // The stream name, rotated back; null unless every character is printable ASCII.
    private static string? Decode(ReadOnlySpan<byte> bytes, int key)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var c = key == 0 ? b : (byte)((b << key) | (b >> (8 - key)));
            if (c is < 0x20 or > 0x7E) return null;
            chars[i] = (char)c;
        }
        return new string(chars);
    }

    // The next marker at or after from, reading a window at a time; -1 when there is none.
    private static long Find(Stream content, long from, byte[] window)
    {
        var at = from;
        while (true)
        {
            content.Position = at;
            var got = ReadFull(content, window);
            if (got < Marker.Length) return -1;
            var i = window.AsSpan(0, got).IndexOf(Marker);
            if (i >= 0) return at + i;
            if (got < window.Length) return -1;
            at += got - (Marker.Length - 1);
        }
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

// The engine's reader (ISavedReleaseReader) over SolidWorksFileRelease.Read.
public sealed class SolidWorksSavedReleaseReader : ISavedReleaseReader
{
    public ValueTask<SolidWorksRelease?> ReadAsync(Stream content, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SolidWorksFileRelease.Read(content, cancellationToken));
}
