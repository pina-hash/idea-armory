using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.SolidWorks;

// The numbers of SolidWorks' two "Save to Version" preferences (swUserPreferenceToggle_e
// .swEnableSaveToVersion and swUserPreferenceIntegerValue_e.swSaveToVersion). The API help
// does not print them and the public 2026 SP0 interop lacks them (research section 3.1; lab
// step L1 records them). Armory never guesses one: a wrong number would change another of the
// student's preferences.
internal sealed record SaveToVersionIds(int EnableToggle, int VersionValue);

internal static class SaveToVersionEnums
{
    internal const string Namespace = "SolidWorks.Interop.swconst";
    internal const string ToggleEnum = "swUserPreferenceToggle_e", ToggleMember = "swEnableSaveToVersion";
    internal const string IntegerEnum = "swUserPreferenceIntegerValue_e", IntegerMember = "swSaveToVersion";

    // Where every SolidWorks install keeps its API's interop assemblies, beside SLDWORKS.exe.
    internal static string InteropPath(string installFolder) => Path.Combine(installFolder, "api", "redist", "SolidWorks.Interop.swconst.dll");

    // The settings first (a number Mr. Pina's lab step wrote), then the installed SolidWorks'
    // own constants, read as metadata from its interop assembly (never loaded as code). Null:
    // unknown here, so no save down.
    internal static SaveToVersionIds? Resolve(LinkSettings settings, string? executablePath, Action<string>? log)
    {
        if (settings.SaveToVersionIds is { } configured) return configured;
        if (executablePath is null || Path.GetDirectoryName(executablePath) is not { } folder) return null;
        var path = InteropPath(folder);
        try
        {
            var found = FromAssembly(path);
            log?.Invoke(found is null ? $"solidworks: no Save to Version preference numbers in {path}" : $"solidworks: Save to Version preferences are {found.EnableToggle} and {found.VersionValue}");
            return found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            log?.Invoke($"solidworks: couldn't read {path}: {error.Message}");
            return null;
        }
    }

    // The two constants in an assembly's metadata, or null when it doesn't define both.
    internal static SaveToVersionIds? FromAssembly(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;
        var reader = pe.GetMetadataReader();
        int? toggle = null, value = null;
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) != Namespace) continue;
            var name = reader.GetString(type.Name);
            var member = name == ToggleEnum ? ToggleMember : name == IntegerEnum ? IntegerMember : null;
            if (member is null) continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (reader.GetString(field.Name) != member || field.GetDefaultValue().IsNil) continue;
                var constant = reader.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode != ConstantTypeCode.Int32) continue;
                var number = reader.GetBlobReader(constant.Value).ReadInt32();
                if (name == ToggleEnum) toggle = number; else value = number;
            }
        }
        return toggle is { } t && value is { } v ? new SaveToVersionIds(t, v) : null;
    }
}

// %LOCALAPPDATA%\IDEA Armory\solidworks.json: the student's own Save to Version setting per
// SolidWorks release, kept before Armory first changes it (Changed: Armory's value is set now),
// so a crash or a SolidWorks that closed first restores it at the next attach; and the
// preference numbers when a lab step wrote them (no default). Written whole, through a temp
// file renamed over the old one.
internal sealed class LinkSettings
{
    private readonly string? path;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly Dictionary<int, StudentSetting> students = [];

    internal sealed record StudentSetting(bool Enable, int Value, bool Changed);

    private LinkSettings(string? path, Action<string>? log)
    {
        this.path = path;
        this.log = log;
    }

    internal SaveToVersionIds? SaveToVersionIds { get; private set; }

    // A settings file at path (null: in memory only, for tests).
    internal static LinkSettings Load(string? path, Action<string>? log = null)
    {
        var settings = new LinkSettings(path, log);
        if (path is null || !File.Exists(path)) return settings;
        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
            if (root?["saveToVersionIds"] is JsonObject ids && Int(ids["enableToggle"]) is { } t && Int(ids["versionValue"]) is { } v)
                settings.SaveToVersionIds = new SaveToVersionIds(t, v);
            if (root?["student"] is JsonObject student)
                foreach (var (major, node) in student)
                    if (int.TryParse(major, out var m) && node is JsonObject s && s["enable"] is JsonValue e && e.TryGetValue<bool>(out var enable) && Int(s["value"]) is { } value)
                        settings.students[m] = new StudentSetting(enable, value, s["changed"] is JsonValue c && c.TryGetValue<bool>(out var changed) && changed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke("solidworks: couldn't read its settings: " + error.Message);
        }
        return settings;
    }

    private static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    internal StudentSetting? Student(int major)
    {
        lock (gate) return students.GetValueOrDefault(major);
    }

    internal void SetStudent(int major, StudentSetting setting)
    {
        lock (gate)
        {
            students[major] = setting;
            Save();
        }
    }

    internal void SetIds(SaveToVersionIds? ids)
    {
        lock (gate)
        {
            SaveToVersionIds = ids;
            Save();
        }
    }

    private void Save()
    {
        if (path is null) return;
        var student = new JsonObject();
        foreach (var (major, s) in students.OrderBy(s => s.Key))
            student[major.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["enable"] = s.Enable, ["value"] = s.Value, ["changed"] = s.Changed };
        var root = new JsonObject
        {
            ["saveToVersionIds"] = SaveToVersionIds is { } ids ? new JsonObject { ["enableToggle"] = ids.EnableToggle, ["versionValue"] = ids.VersionValue } : null,
            ["student"] = student,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".pending";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, root, new JsonSerializerOptions { WriteIndented = true });
                output.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("solidworks: couldn't save its settings: " + error.Message);
        }
    }
}
