namespace Armory.Core;

public readonly record struct SolidWorksRelease(int Year);
public interface ISavedReleaseReader
{
    ValueTask<SolidWorksRelease?> ReadAsync(Stream content, CancellationToken cancellationToken = default);
}
public sealed record InstallationGap(string Installation, int ReleasesBehind, bool WarnNextSeason, bool ExceedsBackSaveRange);

public static class SolidWorksVersionGate
{
    public static string? UploadProblem(SolidWorksRelease? saved, SolidWorksRelease pinned)
        => pinned.Year < 1995 ? "The pinned SolidWorks release is invalid."
        : saved is null || saved.Value.Year < 1995 ? "The saved SolidWorks release is unknown; keep the local draft until it can be read."
        : saved.Value.Year > pinned.Year ? $"SolidWorks {saved.Value.Year} cannot upload to a vault pinned to {pinned.Year}. Keep this private draft or save to {pinned.Year}."
        : null;

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
