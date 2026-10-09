namespace Armory.Telemetry.Tests;

// Each glitch rule on its own (docs/agent/TELEMETRY.md, "Triggers"), and the throttle.
public sealed class GlitchRuleTests
{
    private static FlightEvent Last(Action<FlightRecorder> record)
    {
        var recorder = new FlightRecorder(clock: new ManualClock());
        record(recorder);
        return recorder.Snapshot(1)[0];
    }

    [Fact]
    public void An_exception_nothing_handled_is_a_crash_and_a_handled_one_is_not()
    {
        var fatal = Last(r => r.Exception("engine loop", new InvalidOperationException("the state is gone"), fatal: true));
        var glitch = GlitchRules.Crash(fatal);
        Assert.NotNull(glitch);
        Assert.Equal(GlitchKinds.Crash, glitch.Kind);
        Assert.Contains("engine loop", glitch.Summary);
        Assert.Contains("System.InvalidOperationException: the state is gone", glitch.Summary);
        Assert.Null(GlitchRules.Crash(Last(r => r.Exception("file", new IOException("in use")))));
        Assert.Equal(GlitchKinds.Crash, new GlitchDetector().Inspect(fatal)?.Kind);
    }

    [Fact]
    public void The_previous_run_ending_without_a_word_is_a_crash()
    {
        var glitch = GlitchRules.PreviousRunEnded("2026-10-07T18:00:00.000Z pass: moving 12 of 40 files (loop)");
        Assert.Equal(GlitchKinds.Crash, glitch.Kind);
        Assert.Contains("ended unexpectedly", glitch.Summary);
        Assert.Contains("pass: moving 12 of 40 files", glitch.Summary);
        Assert.Null(glitch.Trigger);
    }

    [Fact]
    public void A_window_action_slower_than_ten_seconds_is_a_slow_action()
    {
        Assert.Null(GlitchRules.SlowAction(Last(r => r.WindowAction("checkOut", 2, 10_000, true))));
        var glitch = GlitchRules.SlowAction(Last(r => r.WindowAction("checkOut", 2, 10_001, true)));
        Assert.Equal(GlitchKinds.SlowAction, glitch?.Kind);
        Assert.Equal("The window waited 10.0 s for checkOut (2 targets) to answer.", glitch!.Summary);
        Assert.Null(GlitchRules.SlowAction(Last(r => r.PassEnd("loop", false, 90_000, 0, 0, 0, 0))));
    }

    [Fact]
    public void A_pass_slower_than_sixty_seconds_is_a_slow_pass()
    {
        Assert.Null(GlitchRules.SlowPass(Last(r => r.PassEnd("loop", false, 60_000, 0, 0, 0, 0))));
        var glitch = GlitchRules.SlowPass(Last(r => r.PassEnd("loop", false, 74_000, 12, 3, 1, 0)));
        Assert.Equal(GlitchKinds.SlowPass, glitch?.Kind);
        Assert.Equal("A loop pass took 74.0 s: 12 downloaded, 3 uploaded, 1 kept copies, 0 refused.", glitch!.Summary);
        Assert.Null(GlitchRules.SlowPass(Last(r => r.WindowAction("checkIn", 1, 70_000, true))));
    }

    // The computer slept in the middle of a pass (0.3.3: a 52 minute "pass" on IDEA-06 was the
    // computer asleep): no slowPass. A slow pass that starts after it woke is still one.
    [Fact]
    public void A_pass_the_computer_slept_through_is_not_a_slow_pass()
    {
        var detector = new GlitchDetector();
        var recorder = new FlightRecorder(clock: new ManualClock());
        FlightEvent Next(Action<FlightRecorder> record) { record(recorder); return recorder.Snapshot(1)[0]; }
        Assert.Null(GlitchRules.SlowPass(Next(r => r.PassEnd("loop", true, 3_134_550, 0, 0, 0, 0)), slept: true));
        Assert.Null(detector.Inspect(Next(r => r.PassStart("loop"))));
        Assert.Null(detector.Inspect(Next(r => r.Power("suspend"))));
        Assert.Null(detector.Inspect(Next(r => r.Power("resume"))));
        Assert.Null(detector.Inspect(Next(r => r.PassEnd("loop", true, 3_134_550, 0, 0, 0, 0))));
        Assert.Null(detector.Inspect(Next(r => r.PassStart("loop"))));
        Assert.Equal(GlitchKinds.SlowPass, detector.Inspect(Next(r => r.PassEnd("loop", true, 74_000, 0, 0, 0, 0)))?.Kind);
        var power = FlightJson.Event(Next(r => r.Power("resume")), DateTimeOffset.UnixEpoch);
        Assert.Equal("power", (string?)power["kind"]);
        Assert.Equal("resume", (string?)power["mode"]);
    }

    // One open-files question (0.3.3, feedback N6) in the flight: its time, its files and whether
    // the platform gave up at its budget.
    [Fact]
    public void An_open_files_question_is_one_flight_event()
    {
        var recorder = new FlightRecorder(clock: new ManualClock());
        recorder.OpenFiles(2_004, 1_467, timedOut: true);
        var written = FlightJson.Event(recorder.Snapshot(1)[0], DateTimeOffset.UnixEpoch);
        Assert.Equal("openFiles", (string?)written["kind"]);
        Assert.Equal(2_004, (long?)written["ms"]);
        Assert.Equal(1_467, (int?)written["files"]);
        Assert.Equal(true, (bool?)written["timedOut"]);
        Assert.Null(new GlitchDetector().Inspect(recorder.Snapshot(1)[0]));
    }

    [Fact]
    public void The_same_file_failing_three_times_in_a_row_is_a_repeated_failure()
    {
        var detector = new GlitchDetector();
        var recorder = new FlightRecorder(clock: new ManualClock());
        Glitch? Fail(string path)
        {
            recorder.FileFailed(path, new IOException("The process cannot access the file"));
            return detector.Inspect(recorder.Snapshot(1)[0]);
        }
        Assert.Null(Fail("Robot/Plate.SLDPRT"));
        Assert.Null(Fail("Robot/Plate.SLDPRT"));
        Assert.Null(Fail("Robot/Gear.SLDPRT")); // another file never counts toward this one
        var third = Fail("robot/plate.sldprt"); // paths compare as Windows compares them
        Assert.Equal(GlitchKinds.RepeatedFailure, third?.Kind);
        Assert.Contains("failed 3 times in a row", third!.Summary);
        Assert.Contains("IOException", third.Summary);
        // The count starts again after a glitch, and a success in between breaks the row.
        Assert.Null(Fail("Robot/Plate.SLDPRT"));
        Assert.Null(Fail("Robot/Gear.SLDPRT"));
        recorder.FileRecovered("Robot/Gear.SLDPRT");
        Assert.Null(detector.Inspect(recorder.Snapshot(1)[0]));
        Assert.Null(Fail("Robot/Gear.SLDPRT"));
        Assert.Null(Fail("Robot/Gear.SLDPRT"));
        Assert.NotNull(Fail("Robot/Gear.SLDPRT"));

        var rule = new RepeatedFailureRule();
        Assert.Equal(0, rule.Failed("a"));
        rule.Succeeded("a");
        Assert.Equal(0, rule.Failed("a"));
        Assert.Equal(0, rule.Failed("a"));
        Assert.Equal(3, rule.Failed("a"));
        Assert.Equal(0, rule.Tracked);
    }

    [Fact]
    public void A_repaired_check_out_is_its_own_incident()
    {
        var glitch = GlitchRules.RepairedCheckout(Last(r => r.RepairedCheckout("Robot 2027/Drivetrain/Plate.SLDPRT")));
        Assert.Equal(GlitchKinds.RepairedCheckout, glitch?.Kind);
        Assert.Contains("Robot 2027/Drivetrain/Plate.SLDPRT", glitch!.Summary);
        Assert.Null(GlitchRules.RepairedCheckout(Last(r => r.ReadOnlyBroken("x"))));
    }

    [Fact]
    public void A_file_writable_that_should_be_read_only_is_its_own_incident()
    {
        var glitch = GlitchRules.ReadOnlyBroken(Last(r => r.ReadOnlyBroken("Robot 2027/Drivetrain/Plate.SLDPRT")));
        Assert.Equal(GlitchKinds.ReadOnlyBroken, glitch?.Kind);
        Assert.Contains("should have been read-only", glitch!.Summary);
        Assert.Null(GlitchRules.ReadOnlyBroken(Last(r => r.RepairedCheckout("x"))));
    }

    [Fact]
    public void At_most_one_incident_per_kind_per_ten_minutes_and_a_users_report_is_never_held()
    {
        var throttle = new IncidentThrottle();
        var t0 = new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero);
        Assert.True(throttle.TryAdmit(GlitchKinds.SlowPass, t0));
        Assert.False(throttle.TryAdmit(GlitchKinds.SlowPass, t0.AddMinutes(9.9)));
        Assert.True(throttle.TryAdmit(GlitchKinds.SlowAction, t0.AddMinutes(1)));
        Assert.True(throttle.TryAdmit(GlitchKinds.SlowPass, t0.AddMinutes(10)));
        for (var i = 0; i < 5; i++) Assert.True(throttle.TryAdmit(GlitchKinds.UserReport, t0));
        // An earlier run's files count too.
        var next = new IncidentThrottle();
        next.Seed(GlitchKinds.Crash, t0);
        Assert.False(next.TryAdmit(GlitchKinds.Crash, t0.AddMinutes(5)));
        Assert.True(next.TryAdmit(GlitchKinds.Crash, t0.AddMinutes(11)));
    }

    [Fact]
    public void A_summary_is_one_line_of_at_most_five_hundred_characters()
    {
        var glitch = GlitchRules.UserReport("bug", "It froze\nwhen I pressed Check in. " + new string('x', 900));
        Assert.True(glitch.Summary.Length <= 500);
        Assert.DoesNotContain('\n', glitch.Summary);
        Assert.StartsWith("Reported from the window (bug): It froze when I pressed Check in.", glitch.Summary);
        Assert.Equal(7, GlitchKinds.All.Count);
    }
}
