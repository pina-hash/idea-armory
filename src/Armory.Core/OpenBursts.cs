namespace Armory.Core;

// Opens that belong together, so one open of many files asks one question, not one per file
// (docs/agent/ENGINE.md, "SolidWorks opened a file"). With the SolidWorks link, documents the
// student opened within 1.5 seconds of the first (a multi-select open from File Explorer) are
// one group. Without it, SolidWorks' "~$" markers that appear within 3 seconds of the
// previous one are one burst, for at most 60 seconds (opening an assembly makes a marker for
// every part in it).
public sealed record OpenBurst(IReadOnlyList<string> Paths, DateTimeOffset First, DateTimeOffset Last);

public static class OpenBursts
{
    public static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan MarkerGap = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MarkerMaxSpan = TimeSpan.FromSeconds(60);

    // Opens in time order (ties by path): an open joins the burst being built when it comes at
    // most gap after the previous open and at most maxSpan after the burst's first; otherwise it
    // starts a new burst. Each path is in one burst, at its earliest open.
    public static IReadOnlyList<OpenBurst> Group(IEnumerable<(string Path, DateTimeOffset At)> opens, TimeSpan gap, TimeSpan maxSpan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = opens.OrderBy(o => o.At).ThenBy(o => o.Path, StringComparer.OrdinalIgnoreCase).Where(o => seen.Add(o.Path)).ToList();
        List<OpenBurst> bursts = [];
        List<string>? paths = null;
        DateTimeOffset first = default, last = default;
        foreach (var (path, at) in ordered)
        {
            if (paths is not null && at - last <= gap && at - first <= maxSpan)
            {
                paths.Add(path);
                last = at;
                continue;
            }
            if (paths is not null) bursts.Add(new OpenBurst(paths, first, last));
            paths = [path];
            first = last = at;
        }
        if (paths is not null) bursts.Add(new OpenBurst(paths, first, last));
        return bursts;
    }

    // The link's grouping: within 1.5 seconds of the group's first open.
    public static IReadOnlyList<OpenBurst> LinkGroups(IEnumerable<(string Path, DateTimeOffset At)> opens) => Group(opens, LinkWindow, LinkWindow);

    // The markers' grouping: within 3 seconds of the previous one, for at most 60 seconds.
    public static IReadOnlyList<OpenBurst> MarkerBursts(IEnumerable<(string Path, DateTimeOffset At)> opens) => Group(opens, MarkerGap, MarkerMaxSpan);
}
