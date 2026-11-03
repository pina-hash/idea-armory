using System.Security.Cryptography;

namespace Armory.Agent.Previews;

// Called only by ShellThumbnails' bounded background queue. Results flow into its picture
// cache and the existing armory-pictures.local host. Content reuse survives renamed/copied
// files and metadata changes; no Core pass, UI thread, or shell extension parses these files.
internal sealed class ContentPreviews
{
    internal const int CacheBytes = 24 * 1024 * 1024;
    private readonly Dictionary<string, byte[]> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly object gate = new();
    private int bytes;
    internal int Rendered { get; private set; }
    internal int Fallbacks { get; private set; }

    internal static bool Supports(string file) => Path.GetExtension(file).ToLowerInvariant() is
        ".stl" or ".step" or ".stp" or ".dxf" or ".gcode" or ".nc" or ".tap" or ".lbrn" or ".lbrn2" or ".svg";

    internal byte[]? Make(string file, int size)
    {
        if (!Supports(file) || size != ShellThumbnails.Size) return null;
        // Serializes content dedup even if the shell watchdog starts a replacement worker.
        lock (gate)
        {
            try
            {
                var budget = new PreviewBudget();
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                if (input.Length > PreviewBudget.MaxFileBytes) return null;
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var hash = Hash(input, budget);
                var key = hash + ":" + extension + ":" + size;
                // A STEP tile intentionally carries the filename, rather than invented geometry.
                if (extension is ".step" or ".stp") key += ":" + Path.GetFileName(file);
                if (cache.TryGetValue(key, out var known)) return known;
                input.Position = 0;
                var raster = new PreviewRaster(size, budget);
                try
                {
                    if (extension is ".step" or ".stp") raster.Tile("STEP", file, input.Length, "NO GEOMETRY PREVIEW");
                    else
                    {
                        var geometry = new Geometry(budget);
                        switch (extension)
                        {
                            case ".stl": StlReader.Read(input, geometry); break;
                            case ".dxf": DxfReader.Read(input, geometry); break;
                            case ".gcode": case ".nc": case ".tap": GCodeReader.Read(input, geometry); break;
                            case ".svg": ShapeReader.Svg(input, geometry); break;
                            case ".lbrn": case ".lbrn2": ShapeReader.LightBurn(input, geometry); break;
                        }
                        raster.Draw(geometry, extension is ".svg" or ".lbrn" or ".lbrn2");
                    }
                }
                catch (Exception error) when (error is PreviewLimitException or FormatException or System.Xml.XmlException or OverflowException)
                {
                    Fallbacks++;
                    // Do not misrepresent unsupported/malformed input as a successful drawing.
                    // These tiles have no filename, so equal content still shares one result.
                    raster.Tile(extension.TrimStart('.').ToUpperInvariant(), "", input.Length,
                        error is PreviewLimitException ? "PREVIEW LIMIT" : "PREVIEW UNAVAILABLE");
                }
                // The same open handle is write-excluding on Windows. Rehash as well, so a Unix
                // test or a writer ignoring sharing never publishes a picture under another version.
                input.Position = 0;
                if (hash != Hash(input, new PreviewBudget())) return null;
                var png = ShellThumbnails.Encode(size, size, raster.Pixels);
                Rendered++;
                cache.Add(key, png);
                order.Enqueue(key);
                bytes += png.Length;
                while ((cache.Count > ShellThumbnails.Kept || bytes > CacheBytes) && order.TryDequeue(out var old))
                {
                    bytes -= cache[old].Length;
                    cache.Remove(old);
                }
                return png;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PreviewLimitException or ArgumentException or NotSupportedException)
            { return null; }
        }
    }

    private static string Hash(Stream stream, PreviewBudget budget)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long read = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            budget.Check(count / 64);
            read += count;
            if (read > PreviewBudget.MaxFileBytes) throw new PreviewLimitException();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
