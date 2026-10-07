using System.ComponentModel;
using System.Diagnostics;

namespace Armory.Agent;

// Opens a document in its own program the way a double-click in File Explorer does (decision
// D14 already refused programs and scripts before this is reached).
//
// v0.2.0 called ShellExecute on the engine thread, a background MTA thread. Programs that take
// their documents by DDE (SolidWorks among them) need the shell call on an STA thread: when
// SolidWorks was not running yet, the DDE conversation failed (ERROR_DDE_FAIL, 1156) and Open
// answered "Wait a moment" and opened nothing, and the call held the engine thread meanwhile.
// Now the shell call runs on its own short-lived STA thread. When it fails with 1156 or 1157,
// or has not come back after FallbackAfter, explorer.exe is started with the quoted full path:
// Explorer starts the program itself, exactly as a double-click does. The caller waits at most
// AnswerWithin for an answer; a call still going after that is taken as opening (its thread
// finishes, and falls back, on its own).
internal sealed class ShellOpener(Action<ProcessStartInfo> startShell)
{
    internal TimeSpan AnswerWithin { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan FallbackAfter { get; init; } = TimeSpan.FromSeconds(10);
    // Where a failure of the background call goes (the agent's log).
    internal Action<string>? Log { get; init; }

    // Null when it opened, or is opening; otherwise what stopped it (a Win32Exception, or an
    // InvalidOperationException or IOException from Process.Start).
    internal Exception? Open(string plainPath)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.TrySetResult(OpenHere(plainPath))) { IsBackground = true, Name = "Armory open" };
        thread.Start();
        return done.Task.Wait(AnswerWithin) ? done.Task.Result : null;
    }

    // Off the caller's thread: the shell's own open on an STA thread, then Explorer when the
    // shell's DDE call failed or has not come back.
    private Exception? OpenHere(string plainPath)
    {
        var shell = new ProcessStartInfo(plainPath) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(plainPath) ?? "" };
        var call = Task.Factory.StartNew(() => Try(() => startShell(shell)), CancellationToken.None, TaskCreationOptions.LongRunning, StaScheduler.Instance);
        if (!call.Wait(FallbackAfter))
        {
            Log?.Invoke($"open: the shell did not answer in {FallbackAfter.TotalSeconds:F0} s; asking File Explorer");
            return Explorer(plainPath);
        }
        var error = call.Result;
        if (error is Win32Exception { NativeErrorCode: 1156 or 1157 } dde)
        {
            Log?.Invoke($"open: the shell answered {dde.NativeErrorCode}; asking File Explorer");
            return Explorer(plainPath);
        }
        return error;
    }

    private Exception? Explorer(string plainPath)
        => Try(() => startShell(new ProcessStartInfo("explorer.exe", "\"" + plainPath + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plainPath) ?? "" }));

    private static Exception? Try(Action start)
    {
        try { start(); return null; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException) { return error; }
    }

    // Runs each task on a new STA thread of its own (so a shell call that never returns holds
    // only that thread).
    private sealed class StaScheduler : TaskScheduler
    {
        internal static readonly StaScheduler Instance = new();
        protected override void QueueTask(Task task)
        {
            var thread = new Thread(() => TryExecuteTask(task)) { IsBackground = true, Name = "Armory shell" };
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task>? GetScheduledTasks() => [];
    }
}
