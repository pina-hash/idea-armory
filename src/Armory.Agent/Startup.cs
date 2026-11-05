using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Armory.Telemetry;

namespace Armory.Agent;

// 0.5.8: Armory always either opens or says why (docs/agent/INSTALL-OWNERSHIP.md, "0.5.8: never
// a silent launch"). 0.5.5 to 0.5.7 could end a launch with no window and no word: the install
// cleanup ran before the window and ended the launch whenever it could not finish, a second
// launch that could not reach the running copy left quietly, and an exception before the crash
// handlers ended the process without a trace. The decisions are here, without Windows, so the
// tests run them on every host; Program wires them to the real guard, processes and dialogs.

// The running Armory a second launch talks to (SingleInstance).
internal interface IRunningCopy
{
    // False when an elevated copy (a setup run as administrator) owns names this launch may not open.
    bool Reachable { get; }
    // True when the running copy is 0.5.8 or later: it says when its tray listens (ready) and when
    // its window came up for a Show (shown). An older copy only takes the Show signal.
    bool Acknowledges { get; }
    void AllowForeground();
    void SignalShow();
    bool WaitReady(TimeSpan wait);
    bool WaitShown(TimeSpan wait);
    // True when the running copy took the Show signal within wait (any version).
    bool ShowTaken(TimeSpan wait);
}

internal interface IInstance : IRunningCopy, IDisposable
{
    bool IsFirst { get; }
}

internal enum HandoffResult { Shown, NotReady, NoAnswer, NotTaken, Unreachable }

internal sealed record HandoffPatience(TimeSpan Starting, TimeSpan Show, TimeSpan Older)
{
    // Starting: a copy still starting (its tray not listening yet) on a slow lab computer. Show: a
    // listening copy's window. Older: a 0.5.7 or older copy taking the signal.
    internal static readonly HandoffPatience Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
}

internal static class Handoff
{
    internal static HandoffResult Show(IRunningCopy copy, HandoffPatience patience)
    {
        if (!copy.Reachable) return HandoffResult.Unreachable;
        copy.AllowForeground();
        // An answer left over from an earlier launch is not this launch's answer.
        if (copy.Acknowledges) copy.WaitShown(TimeSpan.Zero);
        copy.SignalShow();
        if (!copy.Acknowledges) return copy.ShowTaken(patience.Older) ? HandoffResult.Shown : HandoffResult.NotTaken;
        if (!copy.WaitReady(patience.Starting)) return HandoffResult.NotReady;
        return copy.WaitShown(patience.Show) ? HandoffResult.Shown : HandoffResult.NoAnswer;
    }

    internal static string Detail(HandoffResult result, HandoffPatience patience) => result switch
    {
        HandoffResult.Unreachable => "the running copy owns names this launch may not open (started as administrator)",
        HandoffResult.NotReady => "the running copy did not finish starting within " + Seconds(patience.Starting),
        HandoffResult.NoAnswer => "the running copy did not bring up its window within " + Seconds(patience.Show),
        HandoffResult.NotTaken => "an older running copy did not take the open signal within " + Seconds(patience.Older),
        _ => "shown",
    };

    private static string Seconds(TimeSpan wait) => ((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
}

// A second launch: the guard is owned and this launch is not the one that runs.
internal static class SecondLaunch
{
    // blocked: the words for the person and the detail for the incident. The link and the sign-in
    // start (--background) behave exactly as before 0.5.8.
    internal static int Run(AgentCommandLine command, IRunningCopy copy, Func<string, bool> forwardLink, Func<IReadOnlyList<int>> running,
        Action<string, string> blocked, HandoffPatience? patience = null)
    {
        // A notification's link goes to the running Armory, which acts on it; if it can't be
        // reached, its window still comes up.
        if (command.Link is { } link)
        {
            if (forwardLink(link)) return 0;
            copy.SignalShow();
            return 1;
        }
        // The sign-in start entry while Armory already runs: nothing to show.
        if (command.Background) return 0;
        patience ??= HandoffPatience.Default;
        var result = Handoff.Show(copy, patience);
        if (result == HandoffResult.Shown) return 0;
        var ids = running();
        var words = result == HandoffResult.Unreachable ? StartupWords.RunningAsAdministrator(ids) : StartupWords.NotResponding(ids);
        blocked(words, Handoff.Detail(result, patience) + (ids.Count == 0 ? "" : "; " + StartupWords.Processes(ids)));
        return 1;
    }
}

// What a person reads when Armory cannot open. Plain, one action, no jargon beyond Task Manager.
internal static class StartupWords
{
    internal const string Title = "IDEA Armory";
    private const string WhatToDo = "Restart the computer, or end IdeaArmory in Task Manager, then open Armory again.";

    internal static string NotResponding(IReadOnlyList<int> ids)
        => "Armory is already running in the background but not responding" + InParentheses(ids) + ". " + WhatToDo;

    internal static string RunningAsAdministrator(IReadOnlyList<int> ids)
        => "Armory is already running as administrator" + InParentheses(ids) + ", so it can't be opened from here. " + WhatToDo;

    internal static string CouldNotStart(Exception error, string crashFile)
        => "Armory couldn't start: " + Summary(error) + "\n\nThe details are saved in " + crashFile +
           ". Restart the computer and open Armory again. If it keeps happening, show this message to your teacher.";

    // The error's one-line summary: its type and the first line of its message, scrubbed.
    internal static string Summary(Exception error)
    {
        var message = (error.Message ?? "").ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var line = error.GetType().Name + (message.Length == 0 ? "" : ": " + message);
        line = Redactor.Scrub(line);
        return line.Length <= 300 ? line : line[..297] + "...";
    }

    internal static string Processes(IReadOnlyList<int> ids)
    {
        var numbers = ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        return numbers.Count switch
        {
            0 => "",
            1 => "process " + numbers[0],
            _ => "processes " + string.Join(", ", numbers.Take(numbers.Count - 1)) + " and " + numbers[^1],
        };
    }

    private static string InParentheses(IReadOnlyList<int> ids) => ids.Count == 0 ? "" : " (" + Processes(ids) + ")";
}

// Other IdeaArmory.exe processes in this Windows session.
internal sealed record RunningArmory(int Id, string? Path);

internal static class RunningArmories
{
    // True when one of them runs from another install than ownExe (paths compared by long name,
    // any case). One whose path can't be read is not counted: that is the elevated case.
    internal static bool AnotherInstall(string? ownExe, IEnumerable<RunningArmory> running, Func<string, string> longName)
    {
        if (string.IsNullOrEmpty(ownExe)) return false;
        var own = longName(ownExe);
        return running.Any(r => !string.IsNullOrEmpty(r.Path) && !string.Equals(longName(r.Path), own, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<RunningArmory> InThisSession()
    {
        var found = new List<RunningArmory>();
        if (!OperatingSystem.IsWindows()) return found;
        try
        {
            using var self = Process.GetCurrentProcess();
            var session = self.SessionId;
            foreach (var process in Process.GetProcessesByName("IdeaArmory"))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self.Id || process.SessionId != session) continue;
                        found.Add(new(process.Id, LinkForwarder.ImagePath((uint)process.Id)));
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return found;
    }
}

// The order of a launch that is not --check, --quit or the post-update worker.
internal sealed class LaunchSteps
{
    // Takes the single-instance guard, waiting up to the given time for a copy that is letting go.
    public required Func<TimeSpan, IInstance> Acquire { get; init; }
    // Another install's Armory runs and owns the guard (only asked when this launch is not first).
    public required Func<bool> AnotherInstallOwnsTheGuard { get; init; }
    // The install cleanup, now, before handing off: only when another install owns the guard.
    public required Func<RetireOutcome> RetireNow { get; init; }
    public required Action<RetireOutcome> RetireFailed { get; init; }
    public required Func<IInstance, int> SecondLaunch { get; init; }
    // Runs the app and returns at quit. The bool: start the install cleanup once the window is up,
    // off the window's thread (false when it already ran in this launch).
    public required Func<IInstance, bool, int> RunFirst { get; init; }
}

internal static class LaunchFlow
{
    internal static readonly TimeSpan ReacquireWait = TimeSpan.FromSeconds(10);

    internal static int Run(LaunchSteps steps)
    {
        var instance = steps.Acquire(TimeSpan.Zero);
        try
        {
            var retiredNow = false;
            // An older install's Armory owns the guard (a leftover copy started at sign-in): the
            // cleanup that retires it runs now, as it did before 0.5.8, because only that copy can
            // let go of the guard. If it can't finish, the running copy's window comes up instead.
            if (!instance.IsFirst && instance.Reachable && steps.AnotherInstallOwnsTheGuard())
            {
                var outcome = InstallRetirement.RunAndReport(steps.RetireNow, _ => { }, _ => null);
                if (outcome.Result == RetireResult.Done)
                {
                    instance.Dispose();
                    instance = steps.Acquire(ReacquireWait);
                    retiredNow = instance.IsFirst;
                }
                else if (!outcome.Ok) steps.RetireFailed(outcome);
            }
            if (!instance.IsFirst) return steps.SecondLaunch(instance);
            return steps.RunFirst(instance, !retiredNow);
        }
        finally { instance.Dispose(); }
    }
}

// Every step of Main before the app runs is inside this: an exception there writes crash.log
// and an incident and, unless nobody is looking (--background, an installer step), says why in a
// message box. It never throws.
internal static class StartupGuard
{
    internal static int Run(Func<int> body, bool quiet, Action<string> show, Func<AgentPaths>? paths = null)
    {
        try { return body(); }
        catch (Exception error)
        {
            return Report(error, quiet, show, paths ?? Resolve);
        }
    }

    internal static int Report(Exception error, bool quiet, Action<string> show, Func<AgentPaths> paths)
    {
        var crashFile = "the Armory logs folder";
        try
        {
            var where = paths();
            crashFile = where.CrashFile;
            var log = new AgentLog(where.LogFile, where.CrashFile);
            log.Crash("startup", error);
            StartupIncidents.Crash(where, log, error);
        }
        catch (Exception) { }
        if (!quiet)
        {
            try { show(StartupWords.CouldNotStart(error, crashFile)); }
            catch (Exception) { }
        }
        return 1;
    }

    // The usual data folder, or a folder under TEMP when even that can't be worked out.
    internal static AgentPaths Resolve()
    {
        try { return AgentPaths.Resolve(); }
        catch (Exception) { return new AgentPaths(Path.Combine(Path.GetTempPath(), "IDEA Armory"), false); }
    }
}

// Incidents from a launch that has no telemetry of its own (a crash before it, or a second
// launch): the same incidents folder, the same files and throttle, uploaded by the next Armory
// that runs. Never touches the last flight, which belongs to the running copy.
internal static class StartupIncidents
{
    internal static IncidentReporter Reporter(AgentPaths paths, AgentLog? log) => new(new FlightRecorder(capacity: 64), new IncidentStore(paths.IncidentsFolder), new IncidentSources
    {
        Header = AgentTelemetry.HeaderWithoutSession,
        LogTail = log is null ? null : log.TailWithKept,
        Scrubber = new Scrubber(Redactor.Scrub),
    });

    internal static string? Crash(AgentPaths paths, AgentLog? log, Exception error)
    {
        try { return Reporter(paths, log).CrashNow("startup", error); }
        catch (Exception) { return null; }
    }

    internal static string? Save(AgentPaths paths, AgentLog? log, Glitch glitch)
    {
        try { return Reporter(paths, log).Save(glitch); }
        catch (Exception) { return null; }
    }
}

// logs\launch.log: what a launch that did not become the running Armory saw. Its own file, so
// the running copy's agent.log (and how its end is read at the next start) is never touched.
internal static class LaunchLog
{
    internal static string File(AgentPaths paths) => Path.Combine(paths.LogFolder, "launch.log");

    internal static void Write(AgentPaths paths, string line)
    {
        try
        {
            Directory.CreateDirectory(paths.LogFolder);
            var file = File(paths);
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 256 * 1024) System.IO.File.Delete(file);
            System.IO.File.AppendAllText(file, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + Redactor.Scrub(line) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

// The one Windows Forms piece: the message box. Its own class, called only on Windows, so the
// tests (which run without the Windows Desktop runtime) never load it.
internal static class StartupNotice
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Show(string words)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Application.EnableVisualStyles(); }
        catch (InvalidOperationException) { }
        // DefaultDesktopOnly keeps it in front: this launch has no window of its own to sit on.
        MessageBox.Show(words, StartupWords.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
    }
}

internal static class ForegroundRights
{
    private const int Anyone = -1;

    // Lets the running copy bring its window to the front for this launch's Show.
    internal static void AllowAnyone()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _ = AllowSetForegroundWindow(Anyone); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
