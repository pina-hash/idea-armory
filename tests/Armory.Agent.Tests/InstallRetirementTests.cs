using Armory.Telemetry;

namespace Armory.Agent.Tests;

public sealed class InstallRetirementTests
{
    private const string Root = @"C:\Users\student\AppData\Local\IDEAArmory";
    private const string Exe = Root + @"\current\IdeaArmory.exe";
    private const string Launcher = Root + @"\IDEA Armory.exe";

    [Fact]
    public void A_test_or_development_copy_cannot_retire_real_installs()
    {
        Assert.False(InstallRetirement.Enabled(new AgentPaths("test", true), Exe, _ => true));
        Assert.False(InstallRetirement.Enabled(new AgentPaths("live", false), @"C:\src\bin\Debug\IdeaArmory.exe", _ => true));
        Assert.True(InstallRetirement.Enabled(new AgentPaths("live", false), Exe, _ => true));
    }

    [Fact]
    public void Callback_removals_reach_the_existing_flight_recorder_on_the_next_start()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        var recorder = new FlightRecorder();
        InstallRetirement.Save(paths, "removed old auto-start");
        InstallRetirement.Save(paths, "ran old uninstaller");
        InstallRetirement.Replay(paths, log, recorder);
        var events = recorder.Snapshot();
        Assert.Equal(2, events.Length);
        Assert.All(events, e => { Assert.Equal(FlightKind.Note, e.Kind); Assert.Equal("install retirement", e.Name); });
        Assert.Contains("removed old auto-start", events[0].Detail);
        Assert.Contains("ran old uninstaller", events[1].Detail);
        InstallRetirement.Replay(paths, log, recorder);
        Assert.Equal(2, recorder.Recorded);
    }

    [Fact]
    public void An_interrupted_audit_does_not_hide_later_removals()
    {
        using var folder = new TempFolder();
        var paths = new AgentPaths(folder.Root, true);
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        var recorder = new FlightRecorder();
        var audit = Path.Combine(paths.LogFolder, "install-retirement.jsonl");
        File.WriteAllText(audit + ".replay", "\"interrupted\n");
        InstallRetirement.Save(paths, "removed old task");
        InstallRetirement.Replay(paths, log, recorder);
        InstallRetirement.Replay(paths, log, recorder);
        Assert.Single(recorder.Snapshot());
        Assert.Contains("removed old task", recorder.Snapshot()[0].Detail);
        Assert.Contains(log.Tail(10), line => line.Contains("incomplete audit line", StringComparison.Ordinal));
    }
}
