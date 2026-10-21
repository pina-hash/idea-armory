namespace Armory.Agent.Engine;

// The pass watch (0.4.0). 0.3.3 sent slowPass incidents of loop passes that moved nothing and
// took 60 seconds to 10 hours (IDEA-06, IDEA-20, DESKTOP-PFH54TK, IDEA-00, DESKTOP-QH30N35), and
// nothing ever ended such a pass: the loop waited behind it and the window said "Checking for
// changes" all the while. Every step of a pass marks progress (a phase ending, a step starting,
// a file done, a count going up); a loop pass with no progress for StallLimit is ended, the log
// and the flight say what it was doing, and the loop starts the next pass at once. A window
// action's pass is never ended here (its student is waiting for its answer, and its own budgets
// end its questions).
//
// The watch also tells a stall from a computer that slept or a process Windows paused: two looks
// of the watch more than FrozenAfter apart mean no time ran here, so the pass is not slow (a power
// event in the flight, which the slowPass rule leaves out) and its stall clock starts again.
public sealed partial class SyncEngine
{
    private long lastProgress;
    private string passDoing = "looking at this computer";
    private CancellationTokenSource? stalledPass;
    private Timer? watch;
    // Environment.TickCount64 at the last look: it runs on while the computer sleeps, and while
    // Windows pauses the process, so a long gap between looks is one of those.
    private long lastWatch;
    private readonly object watchGate = new();

    // A step of the pass moved on. doing: what it does now, when that changed.
    internal void Progress(string? doing)
    {
        Volatile.Write(ref lastProgress, deps.Clock.GetTimestamp());
        if (doing is not null) passDoing = doing;
    }

    private void StartWatch()
    {
        lock (watchGate)
        {
            if (watch is not null) return;
            lastWatch = Environment.TickCount64;
            watch = new Timer(_ => Look(), null, options.WatchEvery, options.WatchEvery);
        }
    }

    private void StopWatch()
    {
        lock (watchGate)
        {
            watch?.Dispose();
            watch = null;
        }
    }

    // One look, from a timer thread.
    internal void Look()
    {
        var now = Environment.TickCount64;
        TimeSpan gap;
        lock (watchGate)
        {
            gap = TimeSpan.FromMilliseconds(now - lastWatch);
            lastWatch = now;
        }
        if (gap > options.FrozenAfter)
        {
            // Asleep, or paused by Windows: no time ran here.
            flight?.Power("frozen");
            deps.Log?.Invoke($"the computer slept or Armory was paused by Windows for {gap.TotalSeconds:F0} s");
            Progress(null);
            return;
        }
        if (stalledPass is not { IsCancellationRequested: false } pass || !inPass) return;
        var still = deps.Clock.GetElapsedTime(Volatile.Read(ref lastProgress));
        if (still < options.StallLimit) return;
        var step = currentStep is { Over: false } s ? ": " + s.Line : "";
        deps.Log?.Invoke($"pass: no progress for {still.TotalSeconds:F0} s while {passDoing}{step}; starting it again");
        flight?.Note("passStalled", passDoing + step);
        stalls++;
        try { pass.Cancel(); }
        catch (AggregateException error) { deps.Log?.Invoke("engine: ending a stalled pass: " + error.InnerException?.Message); }
        catch (ObjectDisposedException) { }
    }

    // How many passes the watch ended (tests, and the incident snapshot).
    private int stalls;
    public int StalledPasses => Volatile.Read(ref stalls);
}
