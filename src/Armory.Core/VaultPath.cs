using System.Text;

namespace Armory.Core;

public readonly struct VaultPath : IEquatable<VaultPath>, IComparable<VaultPath>
{
    private readonly string? value;
    private VaultPath(string value) => this.value = value;
    public string Value => value ?? string.Empty;
    public bool IsValid => value is not null;
    public string Name => Value[(Value.LastIndexOf('/') + 1)..];
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static bool TryCreate(string? relative, out VaultPath path, out string? problem,
        string root = @"C:\IDEA\Armory", int maxWindowsPathLength = 240)
    {
        path = default;
        problem = null;
        if (string.IsNullOrEmpty(relative)) { problem = "A vault-relative path is required."; return false; }
        if (string.IsNullOrEmpty(root) || maxWindowsPathLength < 1)
        { problem = "A root and positive Windows path limit are required."; return false; }
        // Accept Windows separators at the edge, but validate every resulting segment.
        string canonical;
        try { canonical = relative.Replace('\\', '/').Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { problem = "The name contains an unpaired Unicode surrogate."; return false; }
        foreach (var segment in canonical.Split('/'))
        {
            if (!TryValidateName(segment, out problem)) return false;
        }
        if (root.TrimEnd('\\', '/').Length + 1 + canonical.Length > maxWindowsPathLength)
        { problem = $"The full Windows path exceeds {maxWindowsPathLength} UTF-16 characters."; return false; }
        path = new VaultPath(canonical);
        return true;
    }

    public static bool TryValidateName(string? name, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        { problem = "Empty names, '.' and '..' are not allowed."; return false; }
        try { _ = name.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { problem = "The name contains an unpaired Unicode surrogate."; return false; }
        if (name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
        { problem = "The name contains a control character or a Windows-reserved character."; return false; }
        if (name.EndsWith('.') || name.EndsWith(' '))
        { problem = "A name cannot end with a dot or space."; return false; }
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3])))
        { problem = $"'{name}' is a reserved Windows device name."; return false; }
        return true;
    }

    public static bool TryFromSegments(IEnumerable<string> segments, out VaultPath path, out string? problem,
        string root = @"C:\IDEA\Armory", int maxWindowsPathLength = 240)
    {
        path = default;
        var names = segments.ToArray();
        foreach (var name in names) if (!TryValidateName(name, out problem)) return false;
        return TryCreate(string.Join('/', names), out path, out problem, root, maxWindowsPathLength);
    }

    public bool TryToWindowsPath(out string? windowsPath, out string? problem,
        string root = @"C:\IDEA\Armory", int maxWindowsPathLength = 240)
    {
        windowsPath = null;
        if (!TryCreate(value, out _, out problem, root, maxWindowsPathLength)) return false;
        windowsPath = root.TrimEnd('\\', '/') + "\\" + Value.Replace('/', '\\');
        return true;
    }
    public bool Equals(VaultPath other) => Comparer.Equals(value, other.value);
    public override bool Equals(object? obj) => obj is VaultPath path && Equals(path);
    public override int GetHashCode() => value is null ? 0 : Comparer.GetHashCode(value);
    public int CompareTo(VaultPath other) => Comparer.Compare(value, other.value);
    public override string ToString() => Value;
    public static bool operator ==(VaultPath left, VaultPath right) => left.Equals(right);
    public static bool operator !=(VaultPath left, VaultPath right) => !left.Equals(right);
}
