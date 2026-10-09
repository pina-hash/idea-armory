using System.Globalization;

namespace Armory.Core;

public readonly record struct SolidWorksRelease(int Year)
{
    // The release a running SolidWorks is, from ISldWorks.RevisionNumber ("33.5.0" is 2025 SP5,
    // "34.4.1" is 2026 SP4.1): null unless it is a revision (SolidWorksRevision.Parse).
    public static SolidWorksRelease? FromRevisionNumber(string? revision) => SolidWorksRevision.Parse(revision)?.Release;
}

// ISldWorks.RevisionNumber, "major.minor.hotfix": 2005 is 13.0.0, each release adds 1 to the
// major and each service pack 1 to the minor, so the year is major + 1992. A pre-release build
// has a negative minor ("23.-3.0" is a 2015 beta). Anything else is not a revision.
public readonly record struct SolidWorksRevision(int Major, int Minor, int Hotfix)
{
    public int Year => Major + 1992;
    public SolidWorksRelease Release => new(Year);

    // How SolidWorks names itself in Help > About: "SolidWorks 2026 SP4.1" for 34.4.1,
    // "SolidWorks 2026 SP0" for the first release, "SolidWorks 2015 beta" for a pre-release.
    public string DisplayName => Minor < 0 ? $"SolidWorks {Year} beta"
        : Hotfix == 0 ? $"SolidWorks {Year} SP{Minor}" : $"SolidWorks {Year} SP{Minor}.{Hotfix}";

    public static SolidWorksRevision? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('.');
        if (parts.Length != 3) return null;
        Span<int> values = stackalloc int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])) return null;
        // 1995 (major 3) is the oldest year the gate takes; a major that big would be no year.
        if (values[0] is < 3 or > 1000) return null;
        return new SolidWorksRevision(values[0], values[1], values[2]);
    }
}

// ISldWorks.VersionHistory(path), which SolidWorks reads from the file on disk: one entry per
// release the file was saved in, "<major code>[<build date>,...]" ("13000[2020/296]",
// "19000[2026/225]"), the last one the release that wrote it. The bracket is a build date, never
// the release (research section 1.1).
public static class VersionHistory
{
    // The year of the last entry's major code (SolidWorksFileRelease.YearOf), or null.
    public static int? LastYear(IReadOnlyList<string>? entries)
    {
        if (entries is not { Count: > 0 }) return null;
        var last = entries[^1]?.Trim() ?? "";
        var digits = 0;
        while (digits < last.Length && char.IsAsciiDigit(last[digits])) digits++;
        if (digits == 0 || digits > 9 || (digits < last.Length && last[digits] != '[')) return null;
        return SolidWorksFileRelease.YearOf(int.Parse(last.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture));
    }
}

public interface ISavedReleaseReader
{
    ValueTask<SolidWorksRelease?> ReadAsync(Stream content, CancellationToken cancellationToken = default);
}

// What the SolidWorks link recorded right after a save it watched (research section 2): the
// bytes' SHA-256, the year SolidWorks itself reads in them (null when that disagreed with the
// release the link meant to save in), the SolidWorks that wrote them, and the year Save to
// Version was set to save in (null when it was off).
public sealed record ReleaseStamp(string Sha256, int? Year, string? WriterRevision, int? WriterYear, int? SaveToVersionYear, DateTimeOffset At);

// Resolution of a SolidWorks file's saved release from the link's stamp for that exact content
// hash and the file reader (docs/core/solidworks-version-gate.md, research section 1.7).
public static class SavedReleaseRule
{
    // known Y and known Y: Y. One known, the other unknown: the known one. Two known that
    // differ: unknown (Disagree says so, for a telemetry record). Neither: unknown.
    public static SolidWorksRelease? Combine(ReleaseStamp? stamp, SolidWorksRelease? parsed)
    {
        var stamped = Known(stamp?.Year);
        var read = Known(parsed?.Year);
        if (stamped is { } a && read is { } b) return a == b ? new SolidWorksRelease(a) : null;
        return (stamped ?? read) is { } year ? new SolidWorksRelease(year) : null;
    }

    public static bool Disagree(ReleaseStamp? stamp, SolidWorksRelease? parsed)
        => Known(stamp?.Year) is { } a && Known(parsed?.Year) is { } b && a != b;

    // The link meant to save these bytes down (Save to Version was on for that save) and
    // SolidWorks' own reading of them did not confirm the year it was set to: they may well be
    // the newer release. They are kept on this computer in both gate modes, so a save that
    // couldn't be written in the pinned year is never uploaded as the newer one, not even
    // "release not checked" in Warn.
    public static bool UnverifiedSaveDown(ReleaseStamp? stamp) => stamp is { SaveToVersionYear: not null } && Known(stamp.Year) is null;

    // The year a stamp records (research section 2, step 4): what SolidWorks read in the bytes
    // on disk (the last major code of VersionHistory), when it is the year the link meant to
    // save in, or when the link meant nothing (a stamp on open). Otherwise unknown.
    public static int? StampYear(int? fromHistory, int? intended)
    {
        if (Known(fromHistory) is not { } year) return null;
        return intended is null || intended == year ? year : null;
    }

    // A second stamp for the same bytes: the newer one, unless the two name different known
    // years, which makes the year unknown (the bytes can only have one).
    public static ReleaseStamp Merge(ReleaseStamp? existing, ReleaseStamp incoming)
    {
        if (existing is null || !string.Equals(existing.Sha256, incoming.Sha256, StringComparison.Ordinal)) return incoming;
        var newer = incoming.At >= existing.At ? incoming : existing;
        return Known(existing.Year) is { } a && Known(incoming.Year) is { } b && a != b ? newer with { Year = null }
            : newer with { Year = Known(newer.Year) ?? Known(existing.Year) ?? Known(incoming.Year) };
    }

    private static int? Known(int? year) => year is >= 1995 ? year : null;
}

// What saving down looks like on a computer whose SolidWorks is newer than the project's
// pinned release (research section 3): the in-place "Save to Version" option of SolidWorks
// 2026 SP3 and later, one release back (Penultimate) or two (Antepenultimate), or why not.
public enum SaveDownPlan
{
    // The running SolidWorks is the pinned release or older: its saves are already allowed.
    NotNeeded,
    Penultimate,
    Antepenultimate,
    // SolidWorks 2026 before Service Pack 3, which has no Save to Version option.
    OldServicePack,
    // More than two releases apart: SolidWorks saves back two releases at most.
    TooFarApart,
    // A release older than 2026 (or no valid pin): there is no in-place save down at all.
    Unsupported,
}

public static class SaveDown
{
    public static SaveDownPlan Plan(int runningYear, bool hasSaveToVersion, int pinnedYear)
    {
        if (pinnedYear < 1995 || runningYear < 1995) return SaveDownPlan.Unsupported;
        var apart = runningYear - pinnedYear;
        if (apart <= 0) return SaveDownPlan.NotNeeded;
        if (apart > 2) return SaveDownPlan.TooFarApart;
        if (runningYear < 2026) return SaveDownPlan.Unsupported;
        if (!hasSaveToVersion) return SaveDownPlan.OldServicePack;
        return apart == 1 ? SaveDownPlan.Penultimate : SaveDownPlan.Antepenultimate;
    }

    // The same from ISldWorks.RevisionNumber: 2026 needs Service Pack 3 (34.3) or later. A later
    // release is taken to keep the option from its first build (reasoned: it shipped in 2026
    // SP3; to confirm on the first 2027 computer).
    public static SaveDownPlan Plan(SolidWorksRevision running, int pinnedYear) => Plan(running.Year, HasSaveToVersion(running), pinnedYear);

    public static bool HasSaveToVersion(SolidWorksRevision running) => running.Year > 2026 || (running.Year == 2026 && running.Minor >= 3);

    public static bool CanSave(this SaveDownPlan plan) => plan is SaveDownPlan.Penultimate or SaveDownPlan.Antepenultimate;

    // swSaveToVersion_e: 1 is the release before the running one, 2 the one before that.
    public static int? SaveToVersionValue(this SaveDownPlan plan) => plan switch
    {
        SaveDownPlan.Penultimate => 1,
        SaveDownPlan.Antepenultimate => 2,
        _ => null,
    };

    // Whether the running SolidWorks can save down in place, as the link found it at attach
    // (research section 3.5). enumsKnown: the numbers of the swEnableSaveToVersion and
    // swSaveToVersion preferences are known on this computer (they are not printed in the API
    // help; lab step L1). readBack: setting each preference to its own value read it back
    // unchanged. saveDownFailed: a save made with the option on still wrote this release (no
    // license for it, B4). Anything but Available means no save down at all.
    public static SaveToVersionSupport Support(SolidWorksRevision running, bool enumsKnown, bool readBack, bool saveDownFailed)
    {
        if (running.Year < 2026) return SaveToVersionSupport.Unsupported;
        if (!HasSaveToVersion(running)) return SaveToVersionSupport.OldServicePack;
        if (!enumsKnown) return SaveToVersionSupport.NotConfigured;
        if (!readBack || saveDownFailed) return SaveToVersionSupport.NotLicensed;
        return SaveToVersionSupport.Available;
    }

    // What Save to Version should be while a document is SolidWorks' active one (research 3.4
    // step 1). Armory touches the option only where it can use it (optionUsable: Support is
    // Available) and only for a document in the vault: there, a project pinned one or two
    // releases back saves down, unless the document can't go back (blocked: SolidWorks' own
    // compatibility check lists something the pinned release doesn't have) or the student chose
    // to keep it on this computer only; then the option is off, so the save writes this release
    // and the file stays a private draft. Everything else keeps the student's own setting.
    public static SaveToVersionChoice Choose(SaveDownPlan plan, bool optionUsable, bool vaultDocument, bool blocked, bool keepLocal)
    {
        if (!optionUsable || !vaultDocument || !plan.CanSave()) return SaveToVersionChoice.StudentOwn;
        return blocked || keepLocal ? SaveToVersionChoice.Off : SaveToVersionChoice.SaveDown;
    }
}

public enum SaveToVersionSupport
{
    Available,
    // SolidWorks 2026 before Service Pack 3: no Save to Version option.
    OldServicePack,
    // The option's preference numbers are unknown on this computer: Armory never guesses one.
    NotConfigured,
    // The option did not take (it didn't read back, or a save with it on still wrote this
    // release): no license for it, as far as Armory can tell.
    NotLicensed,
    // A release before 2026: there is no in-place save down.
    Unsupported,
}

public enum SaveToVersionChoice
{
    // The student's own Save to Version setting, as it was before Armory attached.
    StudentOwn,
    // On, one or two releases back (SaveDownPlan.SaveToVersionValue).
    SaveDown,
    // Off: this save writes the running release, and the file stays on this computer.
    Off,
}
public sealed record InstallationGap(string Installation, int ReleasesBehind, bool WarnNextSeason, bool ExceedsBackSaveRange);
// Per-project gate. Enforce refuses a SolidWorks file whose saved release cannot be read;
// Warn uploads it marked "release not checked". A release known to be newer than the pin
// is refused in both modes. Enforce is the library default; projects default to Warn.
public enum ReleaseGateMode { Enforce, Warn }
public sealed record ReleaseGateDecision(bool Allowed, bool ReleaseNotChecked, string? Problem);

public static class SolidWorksVersionGate
{
    public static string? UploadProblem(SolidWorksRelease? saved, SolidWorksRelease pinned)
        => pinned.Year < 1995 ? "The pinned SolidWorks release is invalid."
        : saved is null || saved.Value.Year < 1995 ? "The saved SolidWorks release is unknown; keep the local draft until it can be read."
        : saved.Value.Year > pinned.Year ? $"SolidWorks {saved.Value.Year} cannot upload to a vault pinned to {pinned.Year}. Keep this private draft or save to {pinned.Year}."
        : null;

    public static ReleaseGateDecision Decide(SolidWorksRelease? saved, SolidWorksRelease? pinned, ReleaseGateMode mode)
    {
        if (pinned is null) return new(false, false, "The vault has no pinned SolidWorks release; upload is unsafe.");
        var problem = UploadProblem(saved, pinned.Value);
        if (problem is null) return new(true, false, null);
        var unknown = pinned.Value.Year >= 1995 && (saved is null || saved.Value.Year < 1995);
        return unknown && mode == ReleaseGateMode.Warn ? new(true, true, null) : new(false, false, problem);
    }

    public static bool TryRaise(SolidWorksRelease current, SolidWorksRelease next, out string? problem)
    {
        problem = current.Year < 1995 || next.Year <= current.Year ? "The pinned release must increase from a valid current release." : null;
        return problem is null;
    }

    public static IReadOnlyList<InstallationGap> RolloverGaps(IReadOnlyDictionary<string, SolidWorksRelease> installations)
    {
        if (installations.Count == 0) return [];
        var newest = installations.Values.Max(r => r.Year);
        return installations.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new InstallationGap(p.Key, newest - p.Value.Year, newest - p.Value.Year >= 2, newest - p.Value.Year > 2)).ToArray();
    }
}
