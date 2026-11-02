using Armory.Client;

namespace Armory.Agent.Engine;

// The loop's schedule (0.5.3, docs/agent/ENGINE.md "Schedule"). On 2026-11-02 the lab's Armory
// apps made about 324 server requests a minute, three quarters of everything the shared website
// database answered: every app looked every 2 seconds for two minutes after anyone's change and
// every 10 seconds otherwise, hidden window, locked Windows and site outage alike, and re-read
// every project's whole file list every minute. Now:
//
// - 10 seconds only while the window is up and THIS computer did something in the last
//   IdleAfter (a save captured, a file sent, a folder changed, a window action). A teammate's
//   change, or a download of one, no longer puts every computer in the lab on the fast look.
// - 30 seconds while the window is up, 120 seconds while it is hidden or Windows is locked.
// - Live updates joined for every project: the poll is only a safety net, at least 120 seconds.
// - Offline or the site failing (5xx, unreachable, a cut answer): 30, 60, 120, 240, then every
//   300 seconds until an answer comes.
// - Any wake ends a wait at once: a window action, the window shown, Windows unlocked, a disk
//   change (the host's hints), a SolidWorks save, a live event.
//
// A quiet project (its feed has not moved for QuietAfter) is read by a loop pass at most every
// QuietProjectEvery, 120 seconds, in view or not; one that moved is read by every pass.
//
// The project list (armory_my_projects) is read on the first pass, when the window is shown,
// after a "not a member" answer, and otherwise every ProjectListEvery; a loop pass in between
// goes on from the list it has. A whole pass (a test's SyncOnceAsync) and a window action's pass
// always read it.
public sealed partial class SyncEngine
{
    // The window is up (shown, not minimized) and Windows is unlocked. True until the host says
    // otherwise, so an engine without a window (tests, a probe) keeps the 0.5.2 looks.
    private bool looking = true;
    // Loop passes in a row that ended offline (the site unreachable or failing).
    private int offlinePasses;
    // The project list as last read, for whom, and when (Clock timestamp).
    private IReadOnlyList<RemoteProject>? listedProjects;
    private string? listedFor;
    private long listedAt;
    // The next read of the project list goes to the server whatever its age.
    private bool listWanted = true;
    // How many loop passes, and how many read the project list (tests and the incident snapshot).
    internal int LoopPasses { get; private set; }
    internal int ListReads { get; private set; }

    // The host says whether anyone can see the window: shown and not minimized, with Windows
    // unlocked. Coming into view wakes the loop and reads the project list again.
    public void SetAttention(bool lookingNow) => engineThread.Enqueue(() =>
    {
        if (looking == lookingNow) return;
        looking = lookingNow;
        flight?.Note("attention", lookingNow ? "shown" : "hidden");
        if (!lookingNow) return;
        listWanted = true;
        feedsDue = true;
        wake.Release();
    });

    internal bool Looking => looking;

    // How long the loop waits after a pass (any wake ends it sooner).
    internal static TimeSpan NextPoll(EngineOptions options, bool looking, bool? online, int offlinePasses, bool ownWorkRecent, bool liveEverywhere)
    {
        if (online == false && offlinePasses > 0)
        {
            var wait = options.IdlePollInterval;
            for (var i = 1; i < offlinePasses && wait < options.OfflinePollLongest; i++) wait += wait;
            return wait < options.OfflinePollLongest ? wait : options.OfflinePollLongest;
        }
        var delay = !looking ? options.HiddenPollInterval : ownWorkRecent ? options.ActivePollInterval : options.IdlePollInterval;
        if (liveEverywhere && delay < options.LivePollInterval) delay = options.LivePollInterval;
        return delay;
    }

    // The wait after the loop's pass now.
    internal TimeSpan NextPollNow()
    {
        var ownWork = online == true && deps.Clock.GetUtcNow() - lastActivity <= options.IdleAfter;
        return NextPoll(options, looking, online, offlinePasses, ownWork, LiveEverywhere());
    }

    // Live updates joined for every project this computer reads (and at least one).
    private bool LiveEverywhere()
    {
        if (deps.Live is not { } live || remoteProjects.Count == 0) return false;
        var joined = live.Joined;
        return joined.Count > 0 && remoteProjects.Keys.All(joined.Contains);
    }

    // After a loop pass: counts a pass that ended offline, so the next wait grows.
    private void LoopPassEnded()
    {
        LoopPasses++;
        if (startedAt == long.MinValue) startedAt = deps.Clock.GetTimestamp();
        if (online == true && !refreshCut) feedsDue = false;
        offlinePasses = online == false ? offlinePasses + 1 : 0;
    }

    // The project list: from the server, or (a loop pass, within ProjectListEvery of the last
    // read, for the same person) the one read last.
    private async Task<IReadOnlyList<RemoteProject>> ProjectListAsync(CancellationToken ct)
    {
        var who = deps.Sessions.Current?.Email;
        if (loopPass && passScope is null && !listWanted && listedProjects is { } known && who is not null && SameEmail(listedFor ?? "", who) &&
            deps.Clock.GetElapsedTime(listedAt) < options.ProjectListEvery)
            return known;
        var projects = await deps.Api.MyProjectsAsync(ct);
        ListReads++;
        listedProjects = projects;
        listedFor = who;
        listedAt = deps.Clock.GetTimestamp();
        listWanted = false;
        return projects;
    }

    // The project list is read again by the next pass (a "not a member" answer, a project gone).
    private void ListAgain() => listWanted = true;

    // Each project's change feed: when this engine last read it, and when it last moved (Clock
    // timestamps). A project not in feedMoved has not moved since this start.
    private readonly Dictionary<Guid, long> feedReadAt = [];
    private readonly Dictionary<Guid, long> feedMovedAt = [];
    private long startedAt = long.MinValue;
    // Every project's feed is read by the next pass (the window shown).
    private bool feedsDue = true;

    private void FeedRead(Guid project, bool moved)
    {
        var now = deps.Clock.GetTimestamp();
        feedReadAt[project] = now;
        if (moved) feedMovedAt[project] = now;
    }

    // A live event for this project: its feed is read by the next pass.
    private void FeedDue(Guid project) => feedReadAt.Remove(project);

    // A loop pass leaves this project's feed for a later one: it has not moved for QuietAfter
    // (nor since this start, which counts as moving), it was read within QuietProjectEvery, and
    // nothing of this computer's is waiting to be read there. Never for a whole pass or an action.
    private bool QuietForNow(ProjectState ps)
    {
        if (!loopPass || passScope is not null || feedsDue) return false;
        if (!feedReadAt.TryGetValue(ps.Id, out var read) || deps.Clock.GetElapsedTime(read) >= options.QuietProjectEvery) return false;
        if (projectsWritten.Contains(ps.Id) || staleProjects.Contains(ps.Id) || !remoteProjects.ContainsKey(ps.Id)) return false;
        if (startedAt == long.MinValue) startedAt = deps.Clock.GetTimestamp();
        var moved = feedMovedAt.TryGetValue(ps.Id, out var at) ? at : startedAt;
        return deps.Clock.GetElapsedTime(moved) >= options.QuietAfter;
    }
}
