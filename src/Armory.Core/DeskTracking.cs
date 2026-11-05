namespace Armory.Core;

// Desk tracking (idea-app ledger 0521, migration 0369; docs/agent/TRACKING.md): what this
// computer tells the website about each team file, and the two rules that act on a check out
// the student may have forgotten. Operational data only, never a score: it says a file was
// opened, saved in SolidWorks, closed, or that its person was asked to check it in.
//
// Pure: the queue holds events until the host sends them (armory_file_events), and the rules
// take the clock as an argument. Nothing here touches the network, the disk or SolidWorks.

public enum DeskEventKind
{
    Opened,
    Saved,
    Closed,
    // Armory asked its person, at the end of a class period, to check the file in.
    Nudged,
}

public sealed record DeskEvent(Guid FileId, DeskEventKind Kind, DateTimeOffset At)
{
    // The word armory_file_events reads (0369): opened, saved, closed or nudged.
    public string KindWord => Kind switch
    {
        DeskEventKind.Opened => "opened",
        DeskEventKind.Saved => "saved",
        DeskEventKind.Closed => "closed",
        _ => "nudged",
    };
}

// The events waiting to go to the website, and what this computer last saw of each file. Safe
// from any thread. The same kind for the same file inside Repeat is one event: SolidWorks fires
// a save per document of an assembly, and an open and its reopen within seconds say nothing new.
public sealed class DeskEventQueue
{
    public static readonly TimeSpan Repeat = TimeSpan.FromSeconds(20);
    // At most this many wait; past it the oldest go, because the website shows only the newest.
    public const int Capacity = 1000;
    // armory_file_events takes at most this many in one call.
    public const int BatchLimit = 200;

    private readonly Lock gate = new();
    private readonly List<DeskEvent> waiting = [];
    private readonly Dictionary<(Guid, DeskEventKind), DateTimeOffset> lastQueued = [];
    private readonly Dictionary<Guid, DateTimeOffset> touched = [];
    private readonly Dictionary<Guid, (DateTimeOffset? Opened, DateTimeOffset? Closed)> openClose = [];

    public int Count { get { lock (gate) return waiting.Count; } }

    // True when the event was queued; false for a repeat inside Repeat or an empty file id.
    public bool Add(Guid fileId, DeskEventKind kind, DateTimeOffset at)
    {
        if (fileId == Guid.Empty) return false;
        lock (gate)
        {
            if (kind != DeskEventKind.Nudged)
            {
                if (!touched.TryGetValue(fileId, out var t) || at > t) touched[fileId] = at;
                var (opened, closed) = openClose.GetValueOrDefault(fileId);
                if (kind == DeskEventKind.Opened && (opened is null || at > opened)) openClose[fileId] = (at, closed);
                if (kind == DeskEventKind.Closed && (closed is null || at > closed)) openClose[fileId] = (opened, at);
            }
            if (lastQueued.TryGetValue((fileId, kind), out var last) && at >= last && at - last < Repeat) return false; // MUTATION: never collapse repeats
            lastQueued[(fileId, kind)] = at;
            waiting.Add(new DeskEvent(fileId, kind, at));
            if (waiting.Count > Capacity) waiting.RemoveRange(0, waiting.Count - Capacity);
            return true;
        }
    }

    // Up to BatchLimit of the oldest, removed from the queue; PutBack returns them on a failure.
    public IReadOnlyList<DeskEvent> Take(int max = BatchLimit)
    {
        lock (gate)
        {
            var n = Math.Min(Math.Clamp(max, 0, BatchLimit), waiting.Count);
            var batch = waiting.GetRange(0, n);
            waiting.RemoveRange(0, n);
            return batch;
        }
    }

    // A batch the website did not take goes back in front, in order, within Capacity.
    public void PutBack(IReadOnlyList<DeskEvent> batch)
    {
        lock (gate)
        {
            waiting.InsertRange(0, batch);
            if (waiting.Count > Capacity) waiting.RemoveRange(Capacity, waiting.Count - Capacity);
        }
    }

    // A website that does not track (before 0369) is not sent anything to wait for.
    public void Clear()
    {
        lock (gate) waiting.Clear();
    }

    // When this computer last saw the file opened, saved or closed; null when never.
    public DateTimeOffset? LastTouched(Guid fileId)
    {
        lock (gate) return touched.TryGetValue(fileId, out var t) ? t : null;
    }

    // Opened more recently than closed, as far as this computer saw.
    public bool IsOpen(Guid fileId)
    {
        lock (gate)
        {
            var (opened, closed) = openClose.GetValueOrDefault(fileId);
            return opened is { } o && (closed is not { } c || o > c);
        }
    }
}

// The end of a class period: Armory asks its person to check in what they still have checked
// out. From Lead before the bell until Grace after it, once per bell.
public static class PeriodNudge
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // The bell to nudge for now, or null. lastNudged is the bell the last nudge was for.
    public static DateTimeOffset? Due(IReadOnlyList<DateTimeOffset> ends, DateTimeOffset now, DateTimeOffset? lastNudged)
    {
        DateTimeOffset? due = null;
        foreach (var end in ends)
        {
            if (now < end - Lead || now > end + Grace) continue; // MUTATION: nudge at any time of day
            if (lastNudged is { } done && done >= end) continue; // MUTATION: nudge the same bell again
            if (due is null || end < due) due = end;
        }
        return due;
    }
}

// The site admin's automatic check in (0369's auto_check_in_idle_minutes, off unless set): a
// file checked out here, closed, and untouched for that long is checked in by Armory itself.
// Never while it is open: a check in waits for the file to close anyway, and an open file is
// somebody working.
public static class AutoCheckIn
{
    public static bool Due(int? idleMinutes, DateTimeOffset checkedOutAt, DateTimeOffset? lastTouched, bool isOpen, DateTimeOffset now)
    {
        if (idleMinutes is not { } minutes || minutes <= 0) return false; // MUTATION: on by default
        if (isOpen) return false; // MUTATION: check in an open file
        var since = lastTouched is { } t && t > checkedOutAt ? t : checkedOutAt;
        return now - since >= TimeSpan.FromMinutes(minutes);
    }
}
