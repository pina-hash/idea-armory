namespace Armory.Telemetry.Tests;

// A refusal in the flight (0.3.3, docs/agent/TELEMETRY.md): when a file's refusal starts, changes
// or ends, never again while it stands. IDEA-06's incidents could not name a single one of its
// 148 refused files.
public sealed class RefusalEventTests
{
    private sealed class Watcher : IFlightObserver
    {
        public int Seen;
        public void Observe(in FlightEvent e) => Seen++;
    }

    [Fact]
    public void A_refusal_is_written_with_its_path_its_kind_and_its_namesake()
    {
        var recorder = new FlightRecorder(clock: new ManualClock());
        var watcher = new Watcher();
        recorder.Observer = watcher;
        recorder.Refusal("FRC 2026 Off-Season/Full Assembly/WCP-0563.SLDPRT", "nameTaken", "FRC 2026 Off-Season/COTS/WCP-0563.SLDPRT");
        recorder.Refusal("FRC 2026 Off-Season/Full Assembly/WCP-0563.SLDPRT", null, null);
        var events = recorder.Snapshot();
        var started = FlightJson.Event(events[0], recorder.UtcAt(events[0].Timestamp));
        Assert.Equal("refusal", started["kind"]!.GetValue<string>());
        Assert.Equal("FRC 2026 Off-Season/Full Assembly/WCP-0563.SLDPRT", started["path"]!.GetValue<string>());
        Assert.Equal("nameTaken", started["refusal"]!.GetValue<string>());
        Assert.Equal("FRC 2026 Off-Season/COTS/WCP-0563.SLDPRT", started["namesake"]!.GetValue<string>());
        var ended = FlightJson.Event(events[1], recorder.UtcAt(events[1].Timestamp));
        Assert.Equal("ended", ended["refusal"]!.GetValue<string>());
        Assert.False(ended.ContainsKey("namesake"));
        Assert.True(events[1].Ok);
        // A refusal is never a glitch's trigger: the glitch rules never see it.
        Assert.Equal(0, watcher.Seen);
        Assert.All(events, e => Assert.Null(new GlitchDetector().Inspect(e)));
    }
}
