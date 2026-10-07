using System.Runtime.ExceptionServices;

namespace Armory.Agent.Engine;

// The engine's one thread (v2-design.md 4.1): a SynchronizationContext with its own queue. Every
// public SyncEngine method marshals onto it, and every await inside the engine comes back to it,
// so engine state is only ever touched by this thread (transfers run as interleaved async tasks
// on it) and the caller, the window's UI thread among them, never scans, hashes or waits on a
// pass. The thread ends after a while with nothing to do and starts again on the next post, so an
// engine that is dropped (a test's restart) never keeps a thread alive.
internal sealed class EngineThread(Action<Exception>? unhandled, TimeSpan? idleExit = null) : SynchronizationContext
{
    private readonly TimeSpan IdleExit = idleExit ?? TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
    private Thread? thread;

    // True on the engine thread (and only there).
    internal bool IsCurrent => Current == this;
    // Threads started so far (one at a time; a new one after the last went idle).
    internal int Started { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (gate)
        {
            queue.Enqueue((d, state));
            if (thread is null)
            {
                thread = new Thread(Pump) { IsBackground = true, Name = "Armory engine" };
                Started++;
                thread.Start();
            }
            else Monitor.Pulse(gate);
        }
    }

    // Runs d on the engine thread and waits for it (only the constructor uses this, off the UI thread).
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsCurrent) { d(state); return; }
        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? error = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            finally { done.Set(); }
        }, null);
        done.Wait();
        error?.Throw();
    }

    public override SynchronizationContext CreateCopy() => this;

    // Work posted from any thread: runs on the engine thread, its result handed back to the caller.
    internal Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        if (IsCurrent) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => _ = CompleteAsync(work, done), null);
        return done.Task;
    }

    internal void Enqueue(Action work)
    {
        if (IsCurrent) { work(); return; }
        Post(_ =>
        {
            try { work(); }
            catch (Exception e) { unhandled?.Invoke(e); }
        }, null);
    }

    private static async Task CompleteAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> done)
    {
        try { done.TrySetResult(await work()); }
        catch (OperationCanceledException e) { done.TrySetCanceled(e.CancellationToken); }
        catch (Exception e) { done.TrySetException(e); }
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (gate)
            {
                while (queue.Count == 0)
                {
                    if (!Monitor.Wait(gate, IdleExit) && queue.Count == 0)
                    {
                        thread = null;
                        return;
                    }
                }
                item = queue.Dequeue();
            }
            try { item.Callback(item.State); }
            catch (Exception error) { unhandled?.Invoke(error); }
        }
    }
}
