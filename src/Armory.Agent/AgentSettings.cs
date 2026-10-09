using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent;

public static class Themes
{
    public const string System = "system", Idea = "idea", SpaceWhite = "spaceWhite";
    public static string Normalize(string? theme) => theme switch
    {
        Idea => Idea,
        SpaceWhite => SpaceWhite,
        _ => System,
    };
    // Windows' app mode decides "system": light apps get Space White, dark apps get IDEA.
    public static string Effective(string theme, bool appsUseLightTheme) => Normalize(theme) switch
    {
        Idea => Idea,
        SpaceWhite => SpaceWhite,
        _ => appsUseLightTheme ? SpaceWhite : Idea,
    };
}

// %LOCALAPPDATA%\IDEA Armory\settings.json holds exactly these values. SharedComputer is "This
// computer is shared by several students" (docs/agent/PROFILES.md), off unless it says true, so
// every settings.json written before 0.3.3 keeps one student per computer; it is written only
// while on, so with it off the file is the same three values as before. With it on, VaultRoot is
// the computer's one shared Armory folder.
public sealed record AgentSettings(
    [property: JsonPropertyName("vaultRoot")] string VaultRoot,
    [property: JsonPropertyName("startAtSignIn")] bool StartAtSignIn,
    [property: JsonPropertyName("theme")] string Theme,
    [property: JsonPropertyName("sharedComputer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SharedComputer = false)
{
    public const string DefaultVaultRoot = @"C:\IDEA\Armory";
    public static AgentSettings Default { get; } = new(DefaultVaultRoot, true, Themes.System);

    public SettingsView ToView() => new(VaultRoot, StartAtSignIn, Theme, SharedComputer);

    // An unknown theme becomes "system"; an unusable vault root becomes the default.
    public AgentSettings Normalize()
        => new(TryNormalizeVaultRoot(VaultRoot, out var root, out _) ? root! : DefaultVaultRoot, StartAtSignIn, Themes.Normalize(Theme), SharedComputer);

    // The vault must be a folder on a local drive letter, below the drive's root, with every
    // folder name valid under Armory.Core's VaultPath rules. A drive root would put the
    // whole drive in the vault, and a network share is not an ordinary local NTFS vault.
    public static bool TryNormalizeVaultRoot(string? candidate, out string? normalized, out string? problem)
    {
        normalized = null;
        problem = null;
        var text = (candidate ?? string.Empty).Trim().Replace('/', '\\');
        if (text.Length < 4 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
        {
            problem = "Choose a folder on this computer, like C:\\IDEA\\Armory.";
            return false;
        }
        var segments = text[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            problem = "Choose a folder inside the drive, not the whole drive.";
            return false;
        }
        foreach (var segment in segments)
        {
            if (!VaultPath.TryValidateName(segment, out var reason))
            {
                problem = $"\"{segment}\" cannot be a folder name: {reason}";
                return false;
            }
        }
        var root = new StringBuilder().Append(char.ToUpperInvariant(text[0])).Append(":\\").AppendJoin('\\', segments).ToString();
        if (root.Length > 120)
        {
            problem = "That folder path is too long. Choose a shorter one so CAD file paths still fit.";
            return false;
        }
        normalized = root;
        return true;
    }
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    // A missing or unreadable file gives the defaults; the file is only written by Save.
    public AgentSettings Load()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(Path); }
        catch (FileNotFoundException) { return AgentSettings.Default; }
        catch (DirectoryNotFoundException) { return AgentSettings.Default; }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return AgentSettings.Default;
            var vault = root.TryGetProperty("vaultRoot", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var start = root.TryGetProperty("startAtSignIn", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? s.GetBoolean() : AgentSettings.Default.StartAtSignIn;
            var theme = root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            // Only a true turns several students on; anything else (missing, false, a word) is one student.
            var shared = root.TryGetProperty("sharedComputer", out var c) && c.ValueKind == JsonValueKind.True;
            return new AgentSettings(vault ?? AgentSettings.DefaultVaultRoot, start, theme ?? Themes.System, shared).Normalize();
        }
        catch (JsonException) { return AgentSettings.Default; }
    }

    // Write-through temp file, flushed, then renamed over the old file.
    public void Save(AgentSettings settings)
    {
        var normalized = settings.Normalize();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".pending";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, normalized, Options);
            output.Flush(true);
        }
        File.Move(temp, Path, overwrite: true);
    }
}
