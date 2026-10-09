using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Armory.SolidWorks;

// The link's one thread: a single-threaded apartment with its own message loop, because
// SolidWorks' events arrive as COM calls into this apartment and are dispatched by this loop.
// Work from any other thread is posted here; timers run here too. Every call into SolidWorks
// is made from this thread, under the message filter that retries a busy SolidWorks.
internal sealed class StaThread : IDisposable
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly AutoResetEvent posted = new(false);
    private readonly List<(long Due, TimeSpan Every, Action Work)> timers = [];
    private readonly Action<string>? log;
    private readonly Thread thread;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;

    internal StaThread(string name, Action<string>? log, Action? onStart = null, Action? onStop = null)
    {
        this.log = log;
        OnStart = onStart;
        OnStop = onStop;
        thread = new Thread(Loop) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private Action? OnStart { get; }
    private Action? OnStop { get; }
    internal bool IsCurrent => Thread.CurrentThread == thread;
    internal Task Started => started.Task;

    internal void Post(Action work)
    {
        if (stopping) return;
        queue.Enqueue(work);
        posted.Set();
    }

    // Work on this thread, its result handed back; it runs inline when already on it.
    internal Task<T> InvokeAsync<T>(Func<T> work)
    {
        if (IsCurrent)
        {
            try { return Task.FromResult(work()); }
            catch (Exception error) { return Task.FromException<T>(error); }
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopping) { done.TrySetCanceled(); return done.Task; }
        Post(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception error) { done.TrySetException(error); }
        });
        return done.Task;
    }

    // Runs work every period on this thread, the first time after one period.
    internal void Every(TimeSpan period, Action work)
        => Post(() => timers.Add((Environment.TickCount64 + (long)period.TotalMilliseconds, period, work)));

    // Runs work once, after delay, on this thread.
    internal void After(TimeSpan delay, Action work)
        => Post(() => timers.Add((Environment.TickCount64 + (long)delay.TotalMilliseconds, TimeSpan.Zero, work)));

    private void Loop()
    {
        try { OnStart?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { log?.Invoke("solidworks: thread start: " + error.Message); }
        started.TrySetResult();
        var handles = new[] { posted.SafeWaitHandle.DangerousGetHandle() };
        while (!stopping)
        {
            RunQueued();
            RunTimers();
            if (stopping) break;
            var wait = NextWait();
            Native.MsgWaitForMultipleObjectsEx(1, handles, wait, Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
            Pump();
        }
        // The last posted work (releasing SolidWorks' references) still runs.
        RunQueued();
        try { OnStop?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { log?.Invoke("solidworks: thread stop: " + error.Message); }
    }

    private void RunQueued()
    {
        // Only what was queued before this round: work that posts more work never starves the loop.
        for (var n = queue.Count; n > 0 && queue.TryDequeue(out var work); n--)
        {
            Run(work);
            Pump();
        }
    }

    private void RunTimers()
    {
        if (timers.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var timer in timers.Where(t => t.Due <= now).ToList())
        {
            timers.Remove(timer);
            if (timer.Every > TimeSpan.Zero) timers.Add((now + (long)timer.Every.TotalMilliseconds, timer.Every, timer.Work));
            Run(timer.Work);
        }
    }

    private uint NextWait()
    {
        if (!queue.IsEmpty) return 0;
        if (timers.Count == 0) return 1000;
        var wait = timers.Min(t => t.Due) - Environment.TickCount64;
        return (uint)Math.Clamp(wait, 0, 1000);
    }

    // Window messages, COM's among them: SolidWorks' events are dispatched here.
    private static void Pump()
    {
        while (Native.PeekMessageW(out var message, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
    }

    private void Run(Action work)
    {
        try { work(); }
        catch (Exception error) when (error is not OutOfMemoryException) { log?.Invoke("solidworks: " + error.GetType().Name + ": " + error.Message); }
    }

    // Stops the loop after the work posted so far, and waits for it (at most timeout).
    internal bool Stop(TimeSpan timeout)
    {
        if (IsCurrent) { stopping = true; return true; }
        Post(() => stopping = true);
        posted.Set();
        return thread.Join(timeout);
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(10));
        posted.Dispose();
    }
}
