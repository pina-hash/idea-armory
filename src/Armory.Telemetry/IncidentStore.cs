using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Telemetry;

// The incidents folder (%LOCALAPPDATA%\IDEA Armory\incidents): one "<utc>-<kind>.json.gz" per
// incident, at most MaximumFiles of them (the oldest goes first). A file the site has is renamed
// "<utc>-<kind>.sent.json.gz"; one the site refused is "<utc>-<kind>.held.json.gz", kept for a
// person to hand over. Every write is a temporary file renamed into place.
public sealed class IncidentStore
{
    public const int MaximumFiles = 20;
    public const string Extension = ".json.gz";
    public const string SentMark = ".sent", HeldMark = ".held";
    private const string TimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private readonly object gate = new();

    public IncidentStore(string folder) => Folder = folder;

    public string Folder { get; }
    public string WaitFile => Path.Combine(Folder, "upload-wait.json");

    public static string FileName(DateTimeOffset at, string kind) => at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture) + "-" + kind + Extension;

    // When and of what kind a file's incident is, from its name; false for any other file.
    public static bool TryParseName(string fileName, out DateTimeOffset at, out string kind)
    {
        at = default;
        kind = "";
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = fileName[..^Extension.Length];
        foreach (var mark in new[] { SentMark, HeldMark })
            if (stem.EndsWith(mark, StringComparison.Ordinal)) stem = stem[..^mark.Length];
        var dash = stem.IndexOf('-');
        if (dash < 0 || !DateTime.TryParseExact(stem[..dash], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) return false;
        at = new DateTimeOffset(time, TimeSpan.Zero);
        kind = stem[(dash + 1)..];
        var suffix = kind.LastIndexOf('-');
        if (suffix > 0 && int.TryParse(kind[(suffix + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)) kind = kind[..suffix];
        return kind.Length > 0;
    }

    // Saves one incident file and drops the oldest beyond MaximumFiles. Returns its full path.
    public string Save(DateTimeOffset at, string kind, byte[] gzip)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            var name = FileName(at, kind);
            for (var n = 2; File.Exists(Path.Combine(Folder, name)) || File.Exists(Path.Combine(Folder, Marked(name, SentMark))); n++)
                name = FileName(at, kind)[..^Extension.Length] + "-" + n.ToString(CultureInfo.InvariantCulture) + Extension;
            var path = Path.Combine(Folder, name);
            WriteAtomically(path, gzip);
            Prune();
            return path;
        }
    }

    // Every incident file here, oldest first.
    public IReadOnlyList<string> All()
    {
        if (!Directory.Exists(Folder)) return [];
        try
        {
            return Directory.EnumerateFiles(Folder, "*" + Extension)
                .Where(f => TryParseName(Path.GetFileName(f), out _, out _))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // The files still waiting for the site, oldest first.
    public IReadOnlyList<string> Pending() => All().Where(f => !IsMarked(f, SentMark) && !IsMarked(f, HeldMark)).ToList();

    public static bool IsMarked(string path, string mark) => Path.GetFileName(path).EndsWith(mark + Extension, StringComparison.Ordinal);

    // The kind and time of every incident here (the throttle starts from them).
    public IEnumerable<(string Kind, DateTimeOffset At)> Saved()
    {
        foreach (var file in All())
            if (TryParseName(Path.GetFileName(file), out var at, out var kind)) yield return (kind, at);
    }

    public JsonObject Read(string path) => IncidentDocument.Read(File.ReadAllBytes(path));

    // The incident again, after a field changed (the feedback id it was linked to).
    public void Rewrite(string path, JsonObject incident)
    {
        lock (gate) WriteAtomically(path, IncidentDocument.Gzip(JsonSerializer.SerializeToUtf8Bytes(incident)));
    }

    public string Mark(string path, string mark)
    {
        lock (gate)
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, Marked(Path.GetFileName(path), mark));
            File.Move(path, target, overwrite: true);
            return target;
        }
    }

    // When each RPC that was not on the site yet may be tried again (survives a restart, so a
    // computer that starts often never asks more than every few hours).
    public Dictionary<string, DateTimeOffset> ReadWaits()
    {
        try
        {
            if (!File.Exists(WaitFile)) return new(StringComparer.Ordinal);
            var stored = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllBytes(WaitFile));
            return stored is null ? new(StringComparer.Ordinal) : new(stored, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.Ordinal); }
    }

    public void WriteWaits(IReadOnlyDictionary<string, DateTimeOffset> waits)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Folder);
            WriteAtomically(WaitFile, JsonSerializer.SerializeToUtf8Bytes(waits));
        }
    }

    private void Prune()
    {
        var files = All();
        // Files the site already has go first, then the oldest of the rest.
        var order = files.Where(f => IsMarked(f, SentMark)).Concat(files.Where(f => !IsMarked(f, SentMark))).ToList();
        for (var i = 0; i < files.Count - MaximumFiles; i++)
        {
            try { File.Delete(order[i]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Marked(string name, string mark) => name[..^Extension.Length] + mark + Extension;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
