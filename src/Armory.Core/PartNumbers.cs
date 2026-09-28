using System.Globalization;

namespace Armory.Core;

public sealed record PartNumberPattern(string Prefix = "5669", int SeasonDigits = 2, int SubsystemDigits = 2, int PartDigits = 2)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Prefix) && Prefix.All(c => char.IsAsciiLetterOrDigit(c)) &&
        SeasonDigits is >= 1 and <= 4 && SubsystemDigits is >= 1 and <= 4 && PartDigits is >= 1 and <= 4;
}
public readonly record struct PartNumber(int Season, int Subsystem, int Part);

public static class PartNumbers
{
    private static int Capacity(int digits) => (int)Math.Pow(10, digits);
    public static bool TryFormat(PartNumber number, PartNumberPattern pattern, out string? text)
    {
        text = null;
        if (!pattern.IsValid || number.Season < 0 || number.Season >= Capacity(pattern.SeasonDigits) ||
            number.Subsystem < 0 || number.Subsystem >= Capacity(pattern.SubsystemDigits) || number.Part < 0 || number.Part >= Capacity(pattern.PartDigits)) return false;
        text = pattern.Prefix + "-" + number.Season.ToString("D" + pattern.SeasonDigits, CultureInfo.InvariantCulture) + "-" +
            number.Subsystem.ToString("D" + pattern.SubsystemDigits, CultureInfo.InvariantCulture) + number.Part.ToString("D" + pattern.PartDigits, CultureInfo.InvariantCulture);
        return true;
    }
    public static bool TryParse(string? text, PartNumberPattern pattern, out PartNumber number)
    {
        number = default;
        if (!pattern.IsValid || text is null) return false;
        var prefix = pattern.Prefix + "-";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length != prefix.Length + pattern.SeasonDigits + 1 + pattern.SubsystemDigits + pattern.PartDigits) return false;
        var rest = text[prefix.Length..];
        if (rest[pattern.SeasonDigits] != '-') return false;
        var season = rest[..pattern.SeasonDigits];
        var subsystem = rest.Substring(pattern.SeasonDigits + 1, pattern.SubsystemDigits);
        var part = rest[(pattern.SeasonDigits + 1 + pattern.SubsystemDigits)..];
        if (!(season + subsystem + part).All(char.IsAsciiDigit)) return false;
        number = new PartNumber(int.Parse(season, CultureInfo.InvariantCulture), int.Parse(subsystem, CultureInfo.InvariantCulture), int.Parse(part, CultureInfo.InvariantCulture));
        return true;
    }
    public static bool TryNext(int season, int subsystem, IEnumerable<string> used, PartNumberPattern pattern, out string? next, out string? problem)
    {
        next = null;
        problem = null;
        if (!TryFormat(new PartNumber(season, subsystem, 0), pattern, out _)) { problem = "Invalid part number pattern, season, or subsystem."; return false; }
        var occupied = used.Select(s => TryParse(s, pattern, out var n) ? n : (PartNumber?)null)
            .Where(n => n.HasValue && n.Value.Season == season && n.Value.Subsystem == subsystem).Select(n => n!.Value.Part).ToHashSet();
        for (var i = 0; i < Capacity(pattern.PartDigits); i++)
            if (!occupied.Contains(i)) return TryFormat(new PartNumber(season, subsystem, i), pattern, out next);
        problem = "This subsystem is full.";
        return false;
    }
}
