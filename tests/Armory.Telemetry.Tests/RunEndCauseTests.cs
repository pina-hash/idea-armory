namespace Armory.Telemetry.Tests;

// 0.4.0: a run that ended without a word is a crash incident only when Windows saw Armory crash
// or has no record of why; a restart, a shut down, a power loss or sleep is not (IDEA-06 sent 26
// "crashes" in a week of 0.3.3, mostly overnight).
public sealed class RunEndCauseTests
{
    private static readonly DateTimeOffset Last = DateTimeOffset.Parse("2026-10-21T04:51:03Z", System.Globalization.CultureInfo.InvariantCulture);

    private static WindowsEvent E(string log, string provider, int id, int minutesAfter, string? text = null) => new(log, provider, id, Last.AddMinutes(minutesAfter), text);

    [Fact]
    public void A_windows_restart_is_not_a_crash()
    {
        var verdict = RunEndCause.Explain(Last, asleep: false, [E("System", "User32", 1074, 20, "The process C:\\Windows\\system32\\svchost.exe has initiated the restart of computer IDEA-06 on behalf of user NT AUTHORITY\\SYSTEM for the following reason: Operating System: Upgrade (Planned)")]);
        Assert.False(verdict.Crash);
        Assert.StartsWith("Windows restarted or shut down at 2026-10-21 05:11 UTC: The process", verdict.Words);
    }

    [Fact]
    public void A_power_loss_is_not_a_crash()
    {
        var verdict = RunEndCause.Explain(Last, false, [E("System", "Microsoft-Windows-Kernel-Power", 41, 30), E("System", "EventLog", 6008, 30)]);
        Assert.False(verdict.Crash);
        Assert.Contains("stopped without shutting down", verdict.Words);
    }

    [Fact]
    public void Armory_crashing_is_a_crash_and_says_what_Windows_saw()
    {
        var verdict = RunEndCause.Explain(Last, false, [
            E("Application", ".NET Runtime", 1026, 2, "Application: IdeaArmory.exe\\nCoreCLR Version: 10.0\\nDescription: The process was terminated due to stack overflow."),
            E("System", "User32", 1074, 20, "restart")]);
        Assert.True(verdict.Crash);
        Assert.StartsWith("Windows recorded Armory crashing at 2026-10-21 04:53 UTC (.NET Runtime 1026): Application: IdeaArmory.exe", verdict.Words);
    }

    // Another program's crash, or Windows' records from before the run's last line, say nothing about it.
    [Fact]
    public void Other_programs_and_older_records_do_not_count()
    {
        var verdict = RunEndCause.Explain(Last, false, [E("Application", "Application Error", 1000, 5, "Faulting application name: SLDWORKS.exe"), E("System", "User32", 1074, -90, "restart")]);
        Assert.True(verdict.Crash);
        Assert.Equal("Windows has no record of why it ended.", verdict.Words);
    }

    [Fact]
    public void Asleep_with_nothing_recorded_is_not_a_crash()
    {
        var verdict = RunEndCause.Explain(Last, asleep: true, []);
        Assert.False(verdict.Crash);
        Assert.Equal("The computer went to sleep and was turned off or restarted before it woke up.", verdict.Words);
    }

    // 0.4.2: a shared lab computer signs the student out, and Armory ends with the session
    // (0.3.3 sent these as crashes from IDEA-06, IDEA-12 and IDEA-16). Only this account's sign out
    // counts: another student's, later on the same computer, says nothing about this run.
    [Fact]
    public void This_account_signed_out_is_not_a_crash_and_another_account_s_sign_out_says_nothing()
    {
        const string Me = "S-1-5-21-1-2-3-1001", Other = "S-1-5-21-1-2-3-1002";
        var signOut = new WindowsEvent("System", "Microsoft-Windows-Winlogon", 7002, Last.AddMinutes(3), "User Logoff Notification for Customer Experience Improvement Program", Me);
        var verdict = RunEndCause.Explain(Last, false, [signOut], Me);
        Assert.False(verdict.Crash);
        Assert.Equal("Windows signed this account out at 2026-10-21 04:54 UTC, and Armory ended with the session.", verdict.Words);
        Assert.True(RunEndCause.Explain(Last, false, [signOut with { User = Other }], Me).Crash);
        Assert.True(RunEndCause.Explain(Last, false, [signOut], null).Crash);
        Assert.True(RunEndCause.Explain(Last, false, [signOut with { At = Last.AddMinutes(-30) }], Me).Crash);
        // Armory's own crash still wins over a sign out after it.
        Assert.True(RunEndCause.Explain(Last, false, [E("Application", "Application Error", 1000, 1, "Faulting application name: IdeaArmory.exe"), signOut], Me).Crash);
        Assert.Contains("7002", RunEndCause.SystemQueryIds.Split(','));
    }

    [Fact]
    public void A_crash_incident_carries_what_Windows_said()
        => Assert.Equal("The previous run ended unexpectedly. Windows has no record of why it ended. Its last log line was: pass: x",
            GlitchRules.PreviousRunEnded("pass: x", "Windows has no record of why it ended.").Summary);
}
