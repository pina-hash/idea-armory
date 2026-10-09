namespace Armory.Agent.Engine;

// Thrown by a window action, a pass or a look at the folder asked of an engine that is stopping
// for good (or has stopped): nothing was done. The host answers it with one plain sentence
// ("Armory is switching students. Try again in a moment."). Not a cancellation: the engine
// thread hands a cancellation back as a bare TaskCanceledException, and this must arrive as itself.
public sealed class EngineStoppedException() : InvalidOperationException("This sync engine has stopped.");

// The stop guarantee (docs/agent/ENGINE.md, "Stopping for good"). Two runtimes never write one
// folder at once: on a shared computer the next student's engine starts on the folder the last
// one's engine used, and a vault folder change starts one where another was. StopAsync stops only
// the loop, and before 0.3.3 that was all the host waited for: a window action's pass (a check in,
// a download to open) ran on the caller's token and could still write to the disk, the state
// document and the journal after it returned, and the runtime then closed its files under it.
//
// StopForGoodAsync returns only when the loop has ended, every window action has ended, and none
// can start again: every pass (the loop's and every action's) runs on a token linked to halting,
// so it stops at its next step; an action waiting for the pass gate is refused; the gate is then
// taken and never given back; and what the passes changed is saved. Every disk write the engine
// makes is inside a pass or an action holding the gate, so once it returns nothing of this engine
// writes again. Anything asked of it afterwards throws EngineStoppedException.
public sealed partial class SyncEngine
{
    private readonly CancellationTokenSource halting = new();
    private volatile bool halted;
    private Task<bool>? stoppedForGood;

    // True from the moment StopForGoodAsync begins.
    public bool IsStopping => halted;

    public Task StopForGoodAsync() => engineThread.InvokeAsync(() => stoppedForGood ??= HaltAsync());

    private async Task<bool> HaltAsync()
    {
        halted = true;
        await halting.CancelAsync();
        await StopLoopAsync();
        // The action holding the gate (its pass stops at its next step) gives it back, and the
        // engine keeps it: no pass and no action runs here again.
        await passGate.WaitAsync();
        await SettleAsync();
        StopActivity();
        deps.Log?.Invoke("engine stopped for good");
        return true;
    }

    // The pass gate, for a pass or an action. Refused once the engine is stopping for good, and
    // given up when it starts to while this waits.
    private async Task WaitGateAsync(CancellationToken ct)
    {
        if (halted) throw new EngineStoppedException();
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, halting.Token);
        try { await passGate.WaitAsync(either.Token); }
        catch (OperationCanceledException) when (halted && !ct.IsCancellationRequested) { throw new EngineStoppedException(); }
        if (halted)
        {
            // Got the gate in the same moment the stop began: the stop takes it.
            passGate.Release();
            throw new EngineStoppedException();
        }
    }
}
