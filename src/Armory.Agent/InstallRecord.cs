using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armory.Agent;

// Which version of Armory runs on this computer and since when (0.4.3, Mr. Pina's "it doesn't
// have a visible update or version anywhere in the app"). %LOCALAPPDATA%\IDEA Armory\install.json
// is read and written once at each start: a version different from the one recorded is an
// update, said once in one line, and Settings and the tray say the version and its date. The
// record also remembers that the one taskbar sentence (TaskbarPins) was said, so it is said once.
internal sealed record InstallRecord(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("previous")] string? Previous,
    [property: JsonPropertyName("updated")] bool Updated,
    [property: JsonPropertyName("pinNoticeShown")] bool PinNoticeShown = false)
{
    internal const string FileName = "install.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // This start's record, and the one line to say when it is an update. usedBefore: Armory ran
    // on this account before (its settings file is there), so a missing record is an update from
    // a version that kept none (0.4.2 and older), not a first install.
    internal static (InstallRecord Record, string? Notice) Advance(InstallRecord? stored, string current, DateTimeOffset now, bool usedBefore)
    {
        if (stored is null)
            return (new InstallRecord(current, now, null, usedBefore), usedBefore ? UpdatedWords(null, current) : null);
        if (string.Equals(stored.Version, current, StringComparison.Ordinal)) return (stored, null);
        return (stored with { Version = current, Since = now, Previous = stored.Version, Updated = true }, UpdatedWords(stored.Version, current));
    }

    internal static string UpdatedWords(string? from, string to) =>
        from is null ? $"Armory updated to {to}." : $"Armory updated from {from} to {to}.";

    // "Oct 23, 2026", in this computer's own day.
    internal static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    // Settings' line: "Version 0.4.3, updated Oct 23, 2026" (or "installed" for a first install).
    internal string Words => $"Version {Version}, {(Updated ? "updated" : "installed")} {Day(Since)}";

    // The tray's second line, short: "Armory 0.4.3, updated Oct 23".
    internal string ShortWords => $"Armory {Version}, {(Updated ? "updated" : "installed")} {Since.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)}";

    internal static InstallRecord? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var record = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllBytes(file), Json);
            return record is null || string.IsNullOrWhiteSpace(record.Version) ? null : record;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    // Written beside, then moved over, so a start cut off halfway leaves the old record whole.
    internal static bool Write(string file, InstallRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // At start, before the settings are read: the record for this version, written when it
    // changed, and the update line to say once (null when there is none).
    internal static (InstallRecord Record, string? Notice) Start(AgentPaths paths, string current, DateTimeOffset now)
    {
        var file = Path.Combine(paths.DataFolder, FileName);
        var stored = Read(file);
        var (record, notice) = Advance(stored, current, now, File.Exists(paths.SettingsFile));
        if (!Equals(record, stored)) Write(file, record);
        return (record, notice);
    }

    // The taskbar sentence was said: written at once, so it is never said twice.
    internal static InstallRecord MarkPinNotice(AgentPaths paths, InstallRecord record)
    {
        var marked = record with { PinNoticeShown = true };
        Write(Path.Combine(paths.DataFolder, FileName), marked);
        return marked;
    }
}
