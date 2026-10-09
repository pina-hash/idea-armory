using System.Runtime.ExceptionServices;
using Armory.Core;

namespace Armory.Agent.Engine;

// The transfer queue (0.3.3, feedback N3, docs/agent/ENGINE.md "The transfer queue"). Until 0.3.3
// a loop pass started no file after PassSlice, waited for every file in flight, ran its phase D,
// and only then did the next pass scan, read the server and plan: a 1,429-file download stopped
// 18 times for 3 to 12 seconds (no download ran for 41% of it), and one straggling 31.5 MB file
// held its pass, and five idle lanes, for 77 seconds. Now a loop pass's units run in a queue the
// engine owns. At the slice's end (or when a click waits for the gate, or Armory is paused) the
// pass starts nothing more and does not wait for the units in flight: they are carried, and go on
// while the next pass scans, reads the server and plans, and its units fill the lanes as carried
// ones end. A carried unit's files are left alone by every step of every pass whose scan began
// before it ended (Fenced: never captured, adopted, planned, finished, moved or made read-only
// there), so its bytes are taken in by a pass that saw them land, and a carried unit waits for a
// scan in progress to be taken in before it writes into the vault. A failure that is not one
// file's, in any unit, stops every unit, carried or not, before anything more is saved, and is
// thrown by the pass then running (or the next one). An action's pass first waits for the
// carried units holding its files, a whole pass for every one, and stopping the engine for all.
public sealed partial class SyncEngine
{
    // One unit in the queue: its files' plans and the task running them (Watched: until its end
    // was read on the engine thread). Carried: its pass ended while it ran.
    private sealed class QueuedUnit(List<Planned> unit)
    {
        internal readonly List<Planned> Unit = unit;
        internal Task Task = Task.CompletedTask;
        internal Task Watched = Task.CompletedTask;
        internal bool Carried;
    }

    private readonly List<QueuedUnit> queue = [];
    // Every queued unit's token: the engine stopping, or a failure in any of them, cancels it.
    private CancellationTokenSource? queueStop;
    // The first failure a queued unit threw that is not one file's, until a pass throws it.
    private ExceptionDispatchInfo? queueFatal;
    // Completed whenever a queued unit ends, a live event comes or a click waits for the gate: the
    // loop pass's phase C looks again (a lane to fill, or a reason to stop starting files).
    private TaskCompletionSource queueChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The records and paths of the carried units this pass found in flight at its start, and of
    // those it carried itself: this pass leaves them alone, even once they have ended.
    private readonly HashSet<FileState> fencedStates = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> fencedPaths = new(StringComparer.OrdinalIgnoreCase);
    // Their units' keys (project folder and NameKey): a file sharing a name with a carried one is
    // not planned beside it (files sharing a name are one unit, so which gets it never depends
    // on timing).
    private readonly HashSet<(string Top, string Name)> fencedUnits = [];
    // Completed once this pass's scan is in `local` (always, by its end): a carried unit writes
    // into the vault only after that, so a scan never reports a file half taken in and the
    // engine's own picture of a file it just wrote is never replaced by the scan's older one.
    private Task scanApplied = Task.CompletedTask;
    private TaskCompletionSource? scanApplying;

    private int CarriedCount
    {
        get
        {
            var count = 0;
            foreach (var entry in queue) if (entry.Carried) count++;
            return count;
        }
    }

    private CancellationToken QueueToken => (queueStop ??= CancellationTokenSource.CreateLinkedTokenSource(stopping.Token)).Token;

    private void QueueChanged()
    {
        var changed = queueChanged;
        queueChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    // Left alone by this pass: a carried unit holds it (or held it when this pass began).
    private bool Fenced(FileState st) => fencedStates.Count > 0 && (fencedStates.Contains(st) || fencedPaths.Contains(st.Path));
    private bool Fenced(string path) => fencedPaths.Count > 0 && fencedPaths.Contains(path);
    private bool FencedUnit(string path) => fencedUnits.Count > 0 && fencedUnits.Contains(UnitOf(path));
    // A folder with a carried unit's file in it: not moved, removed or tidied by this pass.
    private bool FencedUnder(string folder)
    {
        if (fencedPaths.Count == 0) return false;
        foreach (var path in fencedPaths) if (Inside(path, folder)) return true;
        return false;
    }

    // A carried unit still in flight has a file in this folder (now, not as this pass began).
    private bool CarriedUnder(string folder)
    {
        foreach (var entry in queue)
            if (entry.Carried)
                foreach (var planned in entry.Unit)
                    if (Inside(planned.Key, folder) || Inside(planned.State.Path, folder)) return true;
        return false;
    }

    private void RebuildFence()
    {
        fencedStates.Clear();
        fencedPaths.Clear();
        fencedUnits.Clear();
        foreach (var entry in queue) if (entry.Carried) Fence(entry);
    }

    private void Fence(QueuedUnit entry)
    {
        foreach (var planned in entry.Unit)
        {
            fencedStates.Add(planned.State);
            fencedPaths.Add(planned.Key);
            fencedUnits.Add(UnitOf(planned.Key));
        }
    }

    // Phase C of a loop pass. Starts units while lanes are free (the carried ones hold lanes too)
    // until its slice is over, a click waits for the gate or Armory is paused, then carries the
    // units it started that are still in flight. Returns how many units started; the rest wait for
    // the next pass, which the loop starts at once, and how many it carried. Offline it waits for
    // its own units (they stop at their next server call) and plans the rest offline, as before.
    private async Task<(int Begun, int Carried)> RunQueuedAsync(List<List<Planned>> run, CancellationToken ct)
    {
        var concurrency = Math.Max(1, options.TransferConcurrency);
        var started = deps.Clock.GetTimestamp();
        var events = liveEvents;
        // With live updates the team's changes arrive as they happen: the slice is longer, and a
        // live event ends it once PassSlice has gone by.
        var live = deps.Live is { } feed && feed.Joined.Count > 0;
        List<QueuedUnit> mine = [];
        var next = 0;
        var carried = 0;
        Task? sliceTimer = null;
        while (true)
        {
            while (queueFatal is null && !failing && online == true && next < run.Count && queue.Count < concurrency && !Cut())
                if (StartQueued(run[next++]) is { } entry) mine.Add(entry);
            mine.RemoveAll(entry => entry.Watched.IsCompleted);
            if (queueFatal is not null || failing)
            {
                await StopQueueAsync();
                TakeQueueFatal()?.Throw();
                throw new OperationCanceledException("The pass failed elsewhere.");
            }
            ct.ThrowIfCancellationRequested();
            if (mine.Count == 0 && (next >= run.Count || online != true || Cut())) break;
            if (online == true && Cut())
            {
                foreach (var entry in mine) Carry(entry);
                carried = mine.Count;
                // The slice is over: the downloads it planned and did not start go on starting as
                // lanes free while the next pass scans and reads the server (Feed).
                if (actionsWaiting == 0 && !paused)
                {
                    for (var i = next; i < run.Count; i++)
                        if (run[i].All(p => p.Plan.Actions.All(a => a.Kind is SyncActionKind.None or SyncActionKind.Download)) &&
                            run[i].Any(p => p.Plan.Actions.Any(a => a.Kind == SyncActionKind.Download))) pending.Enqueue(run[i]);
                    feeding = pending.Count > 0;
                }
                break;
            }
            // A unit ends (a lane to fill), a reason to stop starting comes, or the slice may be
            // over. Offline, only the units still stopping are waited for.
            var changed = queueChanged.Task;
            if (online != true) await changed;
            else
            {
                if (sliceTimer is not { IsCompleted: false }) sliceTimer = Task.Delay(SliceLeft(started, live), CancellationToken.None);
                await Task.WhenAny(changed, sliceTimer);
            }
        }
        // Offline: what was not started is planned offline, its intents journaled.
        if (online != true)
            for (var i = next; i < run.Count; i++)
                foreach (var planned in run[i]) JournalOffline(Reconciler.Plan(planned.Input with { IsOnline = false }), planned.State);
        return (next, carried);

        bool Cut() => actionsWaiting > 0 || paused || SliceOver(started, events, live);
    }

    // The downloads the last slice planned and did not start (only downloads: their write rechecks
    // that the file is closed and unchanged, and nothing of the team's is sent). The queue starts
    // them itself, carried, as lanes free, until the next loop pass has read the server (its moves
    // and plans come after that): the scan and the read of the server, 0.4 to 1.7 seconds a pass in
    // the field, no longer leave every lane idle. The next pass plans the rest again.
    private readonly Queue<List<Planned>> pending = new();
    private bool feeding, fed;

    private void Feed()
    {
        // A unit that ends at once ends here again (Ended): the loop below goes on from it.
        if (fed) return;
        fed = true;
        try
        {
            var concurrency = Math.Max(1, options.TransferConcurrency);
            while (feeding && pending.Count > 0 && queue.Count < concurrency && online == true && !paused && actionsWaiting == 0 &&
                   queueFatal is null && !failing && !stopping.IsCancellationRequested)
            {
                var unit = pending.Dequeue();
                if (StartQueued(unit) is { } entry) Carry(entry);
            }
            if (pending.Count == 0) feeding = false;
        }
        finally { fed = false; }
    }

    // The next pass has read the server (or another kind of pass starts): what the slice left is
    // its to plan again.
    private void StopFeeding()
    {
        feeding = false;
        pending.Clear();
    }

    // A loop pass's slice: PassSlice, or with live updates until a live event after PassSlice and
    // at most LivePassSlice.
    private bool SliceOver(long started, long events, bool live)
    {
        var elapsed = deps.Clock.GetElapsedTime(started);
        if (elapsed < options.PassSlice) return false;
        return !live || liveEvents != events || elapsed >= options.LivePassSlice;
    }

    // How long until the slice may be over (at least a millisecond, so the timer really waits).
    private TimeSpan SliceLeft(long started, bool live)
    {
        var elapsed = deps.Clock.GetElapsedTime(started);
        var end = elapsed < options.PassSlice || !live ? options.PassSlice : options.LivePassSlice;
        return TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling((end - elapsed).TotalMilliseconds)));
    }

    // A unit starts in the queue (most units end at once: nothing to wait for).
    private QueuedUnit? StartQueued(List<Planned> unit)
    {
        // The folder each of its files was planned into as this pass's scan found it on disk: a
        // download carried past a later scan still never writes into a folder the student moved
        // or removed since (FolderMovedAway).
        foreach (var planned in unit) plannedFolders[planned.State] = KnownFolderOf(planned.Key);
        var entry = new QueuedUnit(unit) { Task = RunUnitAsync(unit, QueueToken) };
        if (entry.Task.IsCompleted)
        {
            Ended(entry);
            return null;
        }
        queue.Add(entry);
        entry.Watched = WatchAsync(entry);
        return entry;
    }

    // By record: the nearest folder above each queued file that was on disk when it was planned
    // (null: none, a folder new to this computer).
    private readonly Dictionary<FileState, string?> plannedFolders = new(ReferenceEqualityComparer.Instance);

    private string? KnownFolderOf(string path)
    {
        for (var folder = Parent(path); folder is not null; folder = Parent(folder))
            if (localFolders.Contains(folder)) return folder;
        return null;
    }

    // On the engine thread once the unit ended, whatever it ended with.
    private async Task WatchAsync(QueuedUnit entry)
    {
        await Task.WhenAny(entry.Task);
        queue.Remove(entry);
        Ended(entry);
    }

    private void Ended(QueuedUnit entry)
    {
        foreach (var planned in entry.Unit) plannedFolders.Remove(planned.State);
        // The failure that started it: the others only stopped because of it (canceled).
        if (entry.Task.Exception?.InnerException is { } error and not OperationCanceledException && queueFatal is null)
        {
            queueFatal = ExceptionDispatchInfo.Capture(error);
            try { queueStop?.Cancel(); }
            catch (AggregateException cancel) { deps.Log?.Invoke("engine: stopping the queue: " + cancel.InnerException?.Message); }
        }
        if (entry.Carried) Landed(entry);
        QueueChanged();
        Feed();
    }

    // The pass that started it ended while it ran: it goes on, and this pass and the next leave
    // its files alone.
    private void Carry(QueuedUnit entry)
    {
        entry.Carried = true;
        Fence(entry);
    }

    // A carried unit ended. A write it made is read again with its project by the next pass (a
    // pass that read the server while it was in flight may not have seen it). When it was the
    // last one, the loop runs a pass now: that pass finishes its files (phase D) and ends the run.
    private void Landed(QueuedUnit entry)
    {
        foreach (var planned in entry.Unit)
            if (planned.Plan.Actions.Any(a => a.Kind is not (SyncActionKind.None or SyncActionKind.Download))) projectsWritten.Add(planned.Project.Id);
        if (!inPass && CarriedCount == 0 && !stopping.IsCancellationRequested) wake.Release();
        viewWanted = true;
        PublishSoon();
    }

    // Waits for the carried units that match (all of them by default) to end.
    private async Task AwaitCarriedAsync(Func<QueuedUnit, bool>? which = null)
    {
        var waiting = queue.Where(entry => entry.Carried && (which is null || which(entry))).Select(entry => entry.Watched).ToArray();
        if (waiting.Length > 0) await Task.WhenAll(waiting);
    }

    // Every queued unit stops at its next step and has ended; the next unit gets a new token.
    private async Task StopQueueAsync()
    {
        StopFeeding();
        if (queue.Count > 0)
        {
            try { queueStop?.Cancel(); }
            catch (AggregateException error) { deps.Log?.Invoke("engine: stopping the queue: " + error.InnerException?.Message); }
            while (queue.Count > 0) await Task.WhenAll(queue.Select(entry => entry.Watched).ToArray());
        }
        if (queueStop is { IsCancellationRequested: true } stopped)
        {
            stopped.Dispose();
            queueStop = null;
        }
    }

    private ExceptionDispatchInfo? TakeQueueFatal()
    {
        var fatal = queueFatal;
        queueFatal = null;
        return fatal;
    }

    // A carried unit failed with what is not one file's while no pass ran (a crash, in tests):
    // before another pass starts, every unit has stopped and what was serialized before the
    // failure is on disk, and the failure is thrown.
    private async Task SettleQueueFailureAsync()
    {
        if (queueFatal is null && !(failing && queue.Count > 0)) return;
        await StopQueueAsync();
        await DrainAsync();
        failing = false;
        TakeQueueFatal()?.Throw();
    }

    // A queued unit writes into the vault (a download put in place, a copy moved aside) only once
    // the scan of the pass now running is taken in.
    private async Task AfterScanAsync(CancellationToken ct)
    {
        if (scanApplied.IsCompleted) return;
        await scanApplied;
        Proceed(ct);
    }

    private void ScanStarting()
    {
        scanApplying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanApplied = scanApplying.Task;
    }

    private void ScanTakenIn() => scanApplying?.TrySetResult();
}
