using System.ComponentModel;
using Armory.Telemetry;

namespace Armory.Agent.Tests;

// 0.5.8: Armory always either opens or says why (docs/agent/INSTALL-OWNERSHIP.md, "0.5.8"). The
// install cleanup never ends a launch, a second launch that can't reach the running copy says so,
// and an exception before the app runs writes crash.log, an incident and a message.
public sealed class StartupTests
{
    private static readonly HandoffPatience Quick = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

    // ---- The install cleanup failing still leads to a normal start ----

    public static TheoryData<string> Failures => ["exit 1", "exit 2", "timeout", "no powershell", "blocked script", "anything else"];

    private static Func<ScriptRun> Failing(string how) => how switch
    {
        "exit 1" => () => new ScriptRun(true, 1, "2026-11-05T17:47:00Z deferred: another retirement owns the inventory\n", ""),
        "exit 2" => () => new ScriptRun(true, 2, "retired while waiting: another install owns this account\n", ""),
        "timeout" => () => new ScriptRun(false, 0, "", ""),
        "no powershell" => () => throw new Win32Exception(2, "The system cannot find the file specified."),
        "blocked script" => () => new ScriptRun(true, 1, "", "File Armory-retire.ps1 cannot be loaded because running scripts is disabled on this system."),
        _ => () => throw new NullReferenceException("something nobody expected"),
    };

    [Fact]
    public void The_reason_a_cleanup_gave_way_comes_from_the_scripts_own_audit_line()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var audit = Path.Combine(paths.LogFolder, "install-retirement.jsonl");
        Directory.CreateDirectory(paths.LogFolder);
        File.WriteAllText(audit, "\"2026-11-04T15:00:00.0000000Z an older line, not this run's reason\"\n");
        // What Retire.ps1's Audit writes: one JSON string, the UTC time, a space, the words.
        var outcome = InstallRetirement.Run(paths, () =>
        {
            File.AppendAllText(audit, "\"2026-11-05T17:47:01.0000000Z retirement failed: Access denied \"\n");
            return new ScriptRun(true, 1, "", "");
        }, () => { });
        Assert.Equal("deferred (exit 1: retirement failed: Access denied)", outcome.Words);
        // A script that never ran (blocked by policy) wrote nothing: the older line is not its reason.
        var blocked = InstallRetirement.Run(paths, () => new ScriptRun(true, 1, "", "running scripts is disabled on this system."), () => { });
        Assert.Equal("deferred (exit 1: running scripts is disabled on this system.)", blocked.Words);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_cleanup_that_cannot_finish_is_an_outcome_and_an_audit_line_never_a_throw(string how)
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var repaired = false;
        var outcome = InstallRetirement.Run(paths, Failing(how), () => repaired = true);
        Assert.False(outcome.Ok);
        Assert.False(repaired);
        Assert.Equal(how switch
        {
            "exit 2" => RetireResult.RetiredWhileWaiting,
            "timeout" => RetireResult.TimedOut,
            "no powershell" or "anything else" => RetireResult.Unavailable,
            _ => RetireResult.Deferred,
        }, outcome.Result);
        var audit = File.ReadAllText(Path.Combine(paths.LogFolder, "install-retirement.jsonl"));
        Assert.Contains("deferred", audit);
        if (how == "blocked script") Assert.Contains("running scripts is disabled", outcome.Words);
        if (how == "exit 1") Assert.Contains("another retirement owns the inventory", outcome.Words);
    }

    [Fact]
    public void A_finished_cleanup_repairs_the_registrations()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var repaired = false;
        var outcome = InstallRetirement.Run(paths, () => new ScriptRun(true, 0, "inventory complete; survivor C:\\a\\IdeaArmory.exe\n", ""), () => repaired = true);
        Assert.Equal(RetireResult.Done, outcome.Result);
        Assert.True(outcome.Ok);
        Assert.True(repaired);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void The_first_copy_starts_normally_and_files_one_incident_when_its_cleanup_fails(string how)
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var lines = new List<string>();
        var instance = new FakeInstance { First = true };
        var ran = false;
        var code = LaunchFlow.Run(new LaunchSteps
        {
            Acquire = _ => instance,
            AnotherInstallOwnsTheGuard = () => throw new InvalidOperationException("only asked of a second launch"),
            RetireNow = () => throw new InvalidOperationException("the first copy cleans up after its window"),
            RetireFailed = _ => throw new InvalidOperationException("not this path"),
            SecondLaunch = _ => throw new InvalidOperationException("this launch is first"),
            RunFirst = (_, retireInBackground) =>
            {
                // What the window's thread does after the window is up: the cleanup, which gives way.
                Assert.True(retireInBackground);
                var outcome = InstallRetirement.RunAndReport(() => InstallRetirement.Run(paths, Failing(how), () => { }), lines.Add,
                    glitch => StartupIncidents.Save(paths, null, glitch));
                Assert.False(outcome.Ok);
                ran = true;
                return 0;
            },
        });
        Assert.Equal(0, code);
        Assert.True(ran);
        Assert.True(instance.Disposed);
        Assert.Contains(lines, line => line.StartsWith("retire: ", StringComparison.Ordinal));
        var store = new IncidentStore(paths.IncidentsFolder);
        var incident = store.Read(Assert.Single(store.All()));
        Assert.Equal(GlitchKinds.RetireFailed, incident["kind"]!.GetValue<string>());
        Assert.StartsWith("Armory opened, but the cleanup of other Armory installs did not finish", incident["summary"]!.GetValue<string>());
    }

    [Fact]
    public void The_background_cleanup_never_throws_even_when_reporting_fails()
    {
        var lines = new List<string>();
        var outcome = InstallRetirement.RunAndReport(() => throw new IOException("temp is full"), lines.Add, _ => throw new UnauthorizedAccessException("incidents"));
        Assert.Equal(RetireResult.Unavailable, outcome.Result);
        Assert.Contains(lines, line => line.Contains("temp is full", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("could not report", StringComparison.Ordinal));
        Assert.Equal(RetireResult.Done, InstallRetirement.RunAndReport(() => RetireOutcome.Done, lines.Add, _ => throw new InvalidOperationException("not reported")).Result);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void When_another_install_owns_the_guard_a_failed_cleanup_hands_off_to_it_and_says_so(string how)
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var failed = new List<RetireOutcome>();
        var handedOff = false;
        var code = LaunchFlow.Run(new LaunchSteps
        {
            Acquire = _ => new FakeInstance { First = false },
            AnotherInstallOwnsTheGuard = () => true,
            RetireNow = () => InstallRetirement.Run(paths, Failing(how), () => { }),
            RetireFailed = failed.Add,
            SecondLaunch = _ => { handedOff = true; return 0; },
            RunFirst = (_, _) => throw new InvalidOperationException("the older copy still runs"),
        });
        Assert.Equal(0, code);
        Assert.True(handedOff);
        Assert.False(Assert.Single(failed).Ok);
    }

    [Fact]
    public void When_the_cleanup_retires_the_other_install_this_launch_takes_the_guard_and_runs()
    {
        var waits = new List<TimeSpan>();
        var first = new FakeInstance { First = false };
        var second = new FakeInstance { First = true };
        var cleanupAfterWindow = (bool?)null;
        var code = LaunchFlow.Run(new LaunchSteps
        {
            Acquire = wait => { waits.Add(wait); return waits.Count == 1 ? first : second; },
            AnotherInstallOwnsTheGuard = () => true,
            RetireNow = () => RetireOutcome.Done,
            RetireFailed = _ => throw new InvalidOperationException("it finished"),
            SecondLaunch = _ => throw new InvalidOperationException("this launch is first now"),
            RunFirst = (_, retire) => { cleanupAfterWindow = retire; return 0; },
        });
        Assert.Equal(0, code);
        Assert.Equal([TimeSpan.Zero, LaunchFlow.ReacquireWait], waits);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(cleanupAfterWindow);
    }

    [Fact]
    public void Another_install_is_a_running_armory_from_a_different_folder()
    {
        const string own = @"C:\Users\ideastudent\AppData\Local\IDEAArmory\current\IdeaArmory.exe";
        Assert.False(RunningArmories.AnotherInstall(own, [], s => s));
        Assert.False(RunningArmories.AnotherInstall(own, [new(4120, own.ToUpperInvariant())], s => s));
        Assert.False(RunningArmories.AnotherInstall(own, [new(4120, null)], s => s));
        Assert.True(RunningArmories.AnotherInstall(own, [new(4120, own), new(5000, @"C:\Users\ideastudent\AppData\Local\Programs\IDEA Armory\IdeaArmory.exe")], s => s));
        Assert.False(RunningArmories.AnotherInstall(own, [new(4120, @"C:\Users\IDEAST~1\AppData\Local\IDEAArmory\current\IdeaArmory.exe")],
            s => s.Replace("IDEAST~1", "ideastudent", StringComparison.Ordinal)));
        Assert.False(RunningArmories.AnotherInstall(null, [new(4120, own)], s => s));
    }

    [Fact]
    public void Velopack_steps_are_quiet_and_its_first_run_is_not()
    {
        foreach (var step in new[] { "--veloapp-install", "--veloapp-updated", "--VELOAPP-OBSOLETE", "--veloapp-uninstall" })
            Assert.True(Updates.IsInstallerStep([step, "0.5.8"]));
        Assert.False(Updates.IsInstallerStep(["--veloapp-firstrun"]));
        Assert.False(Updates.IsInstallerStep([]));
        Assert.False(Updates.IsInstallerStep(["--background"]));
    }

    // ---- A second launch that can't reach the first copy says so ----

    [Theory]
    [InlineData("never ready")]
    [InlineData("ready, no window")]
    [InlineData("older, never takes")]
    public void A_second_launch_with_an_unreachable_first_copy_shows_a_message_with_the_process_id(string how)
    {
        var copy = how switch
        {
            "never ready" => new FakeInstance { Acknowledging = true },
            "ready, no window" => new FakeInstance { Acknowledging = true, Ready = true },
            _ => new FakeInstance { Acknowledging = false },
        };
        var said = new List<(string Words, string Detail)>();
        var code = SecondLaunch.Run(AgentCommandLine.Parse([]), copy, _ => throw new InvalidOperationException("no link"), () => [4120],
            (words, detail) => said.Add((words, detail)), Quick);
        Assert.Equal(1, code);
        Assert.Equal(1, copy.Shows);
        Assert.True(copy.Allowed);
        var (words, detail) = Assert.Single(said);
        Assert.Equal("Armory is already running in the background but not responding (process 4120). " +
            "Restart the computer, or end IdeaArmory in Task Manager, then open Armory again.", words);
        Assert.Contains("process 4120", detail);
        Assert.Contains(how == "older, never takes" ? "older running copy" : how == "never ready" ? "did not finish starting" : "did not bring up its window", detail);
    }

    [Fact]
    public void A_second_launch_beside_a_copy_started_as_administrator_says_so()
    {
        var copy = new FakeInstance { Reachable = false };
        var said = new List<string>();
        var code = SecondLaunch.Run(AgentCommandLine.Parse([]), copy, _ => false, () => [4120, 5000], (words, _) => said.Add(words), Quick);
        Assert.Equal(1, code);
        Assert.Equal("Armory is already running as administrator (processes 4120 and 5000), so it can't be opened from here. " +
            "Restart the computer, or end IdeaArmory in Task Manager, then open Armory again.", Assert.Single(said));
    }

    [Fact]
    public void A_message_without_a_known_process_still_says_what_to_do()
    {
        var said = new List<string>();
        SecondLaunch.Run(AgentCommandLine.Parse([]), new FakeInstance { Acknowledging = true }, _ => false, () => [], (words, _) => said.Add(words), Quick);
        Assert.Equal("Armory is already running in the background but not responding. Restart the computer, or end IdeaArmory in Task Manager, then open Armory again.",
            Assert.Single(said));
        Assert.Equal("processes 1, 2 and 3", StartupWords.Processes([1, 2, 3]));
    }

    [Fact]
    public void A_second_launch_the_first_copy_answers_leaves_quietly()
    {
        var newer = new FakeInstance { Acknowledging = true, Ready = true, AnswersShow = true };
        var older = new FakeInstance { Acknowledging = false, TakesShow = true };
        foreach (var copy in new[] { newer, older })
        {
            var code = SecondLaunch.Run(AgentCommandLine.Parse([]), copy, _ => false, () => [4120], (_, _) => throw new InvalidOperationException("answered"), Quick);
            Assert.Equal(0, code);
            Assert.Equal(1, copy.Shows);
        }
    }

    [Fact]
    public void The_sign_in_start_and_a_notification_link_behave_as_before()
    {
        // --background beside a running (or unreachable) copy: quiet, exit 0, nothing signaled.
        foreach (var copy in new[] { new FakeInstance { Acknowledging = true }, new FakeInstance { Reachable = false } })
        {
            Assert.Equal(0, SecondLaunch.Run(AgentCommandLine.Parse(["--background"]), copy, _ => false, () => [4120], (_, _) => throw new InvalidOperationException("quiet"), Quick));
            Assert.Equal(0, copy.Shows);
        }
        const string link = "idea-armory:act?t=abc&a=show";
        var forwarded = new FakeInstance();
        Assert.Equal(0, SecondLaunch.Run(AgentCommandLine.Parse([link]), forwarded, l => l == link, () => [], (_, _) => throw new InvalidOperationException("quiet"), Quick));
        Assert.Equal(0, forwarded.Shows);
        var notForwarded = new FakeInstance();
        Assert.Equal(1, SecondLaunch.Run(AgentCommandLine.Parse([link]), notForwarded, _ => false, () => [], (_, _) => throw new InvalidOperationException("quiet"), Quick));
        Assert.Equal(1, notForwarded.Shows);
    }

    [WindowsFact]
    public void The_real_guard_tells_a_second_launch_whether_the_first_answered()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        using var first = SingleInstance.Acquire(paths);
        Assert.True(first.IsFirst);
        // A mutex is owned per thread: the second launch is another thread here.
        (bool First, bool Reachable, bool Acknowledges, HandoffResult Result) OnAnotherThread()
        {
            var seen = (false, false, false, HandoffResult.Unreachable);
            var thread = new Thread(() =>
            {
                using var second = SingleInstance.Acquire(paths);
                seen = (second.IsFirst, second.Reachable, second.Acknowledges,
                    second.IsFirst ? HandoffResult.Unreachable : Handoff.Show(second, Quick with { Starting = TimeSpan.FromSeconds(2), Show = TimeSpan.FromSeconds(2) }));
            });
            thread.Start();
            thread.Join();
            return seen;
        }
        Assert.Equal((false, true, true, HandoffResult.NotReady), OnAnotherThread());
        first.MarkReady();
        Assert.Equal((false, true, true, HandoffResult.NoAnswer), OnAnotherThread());
        var answering = ThreadPool.RegisterWaitForSingleObject(first.ShowSignal, (_, _) => first.Acknowledge(), null, Timeout.Infinite, executeOnlyOnce: false);
        try { Assert.Equal((false, true, true, HandoffResult.Shown), OnAnotherThread()); }
        finally { answering.Unregister(null); }
    }

    // ---- An exception before the app runs: crash.log, an incident, and a message ----

    [Fact]
    public void An_early_exception_writes_the_crash_file_and_an_incident_and_shows_its_one_line_summary()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var shown = new List<string>();
        var code = StartupGuard.Run(() => throw new InvalidOperationException("The Armory icon armory.ico is missing from the app.\n   at somewhere"),
            quiet: false, shown.Add, () => paths);
        Assert.Equal(1, code);
        var crash = File.ReadAllText(paths.CrashFile);
        Assert.Contains("crash in startup", crash);
        Assert.Contains("armory.ico is missing", crash);
        Assert.Contains("crash in startup (details in crash.log): InvalidOperationException", File.ReadAllText(paths.LogFile));
        var words = Assert.Single(shown);
        Assert.StartsWith("Armory couldn't start: InvalidOperationException: The Armory icon armory.ico is missing from the app.\n\nThe details are saved in " + paths.CrashFile, words);
        Assert.DoesNotContain("at somewhere", words);
        var store = new IncidentStore(paths.IncidentsFolder);
        var incident = store.Read(Assert.Single(store.All()));
        Assert.Equal(GlitchKinds.Crash, incident["kind"]!.GetValue<string>());
        Assert.Contains("startup", incident["summary"]!.GetValue<string>());
        Assert.Null(incident["email"]?.GetValue<string>());
    }

    [Fact]
    public void A_quiet_start_writes_it_down_and_shows_nothing()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var code = StartupGuard.Run(() => throw new UnauthorizedAccessException("Access to the path is denied."), quiet: true,
            _ => throw new InvalidOperationException("a sign-in start shows nothing"), () => paths);
        Assert.Equal(1, code);
        Assert.Contains("Access to the path is denied.", File.ReadAllText(paths.CrashFile));
        Assert.Single(new IncidentStore(paths.IncidentsFolder).All());
    }

    [Fact]
    public void The_guard_still_says_why_when_nothing_can_be_written_and_never_throws()
    {
        var shown = new List<string>();
        Assert.Equal(1, StartupGuard.Run(() => throw new IOException("disk"), quiet: false, shown.Add, () => throw new IOException("no data folder")));
        Assert.Contains("Armory couldn't start: IOException: disk", Assert.Single(shown));
        Assert.Contains("the Armory logs folder", shown[0]);
        Assert.Equal(1, StartupGuard.Run(() => throw new IOException("disk"), quiet: false, _ => throw new InvalidOperationException("no desktop"), () => throw new IOException("none")));
        Assert.Equal(7, StartupGuard.Run(() => 7, quiet: false, _ => throw new InvalidOperationException("nothing to say")));
    }

    [Fact]
    public void A_summary_is_one_scrubbed_line()
    {
        var token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.abcdefghijklmnopqrstuvwxyz0123456789";
        var summary = StartupWords.Summary(new InvalidOperationException("Refused " + token + "\nsecond line"));
        Assert.StartsWith("InvalidOperationException: Refused ", summary);
        Assert.DoesNotContain(token, summary);
        Assert.DoesNotContain("second line", summary);
    }

    private sealed class FakeInstance : IInstance
    {
        public bool First { get; init; }
        public bool Reachable { get; init; } = true;
        public bool Acknowledging { get; init; }
        public bool Ready { get; init; }
        public bool AnswersShow { get; init; }
        public bool TakesShow { get; init; }
        public int Shows { get; private set; }
        public bool Allowed { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsFirst => First;
        public bool Acknowledges => Acknowledging;
        public void AllowForeground() => Allowed = true;
        public void SignalShow() => Shows++;
        public bool WaitReady(TimeSpan wait) => Ready || Sleep(wait);
        public bool WaitShown(TimeSpan wait) => AnswersShow || Sleep(wait);
        public bool ShowTaken(TimeSpan wait) => TakesShow || Sleep(wait);
        public void Dispose() => Disposed = true;

        private static bool Sleep(TimeSpan wait)
        {
            Thread.Sleep(wait);
            return false;
        }
    }
}
