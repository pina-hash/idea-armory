using System.Diagnostics;

namespace Armory.Telemetry.Tests;

// The flight recorder (docs/agent/TELEMETRY.md): a fixed ring, oldest first, and recording so
// cheap the engine never notices it.
public sealed class FlightRecorderTests
{
    [Fact]
    public void The_ring_keeps_the_last_events_oldest_first()
    {
        var recorder = new FlightRecorder(capacity: 16, clock: new ManualClock());
        for (var i = 0; i < 40; i++) recorder.Rpc("armory_project_files", i, 200, null);
        var events = recorder.Snapshot();
        Assert.Equal(16, events.Length);
        Assert.Equal(40, recorder.Recorded);
        Assert.Equal(Enumerable.Range(25, 16).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(Enumerable.Range(24, 16).Select(i => (long)i), events.Select(e => e.Ms));
        Assert.Equal(4, recorder.Snapshot(4).Length);
        Assert.Equal(40, recorder.Snapshot(4)[^1].Sequence);
        Assert.Empty(new FlightRecorder(16).Snapshot());
    }

    [Fact]
    public void Events_carry_their_wall_clock_time_and_their_fields()
    {
        var clock = new ManualClock();
        var recorder = new FlightRecorder(clock: clock);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        recorder.Transfer("download", 4096, 120, ok: false, status: 403, error: "StorageTransferException");
        recorder.WindowAction("checkOut", 3, 250, ok: true);
        var events = recorder.Snapshot();
        Assert.Equal(clock.GetUtcNow(), recorder.UtcAt(events[0].Timestamp));
        var json = FlightJson.Event(events[0], recorder.UtcAt(events[0].Timestamp));
        Assert.Equal("transfer", json["kind"]!.GetValue<string>());
        Assert.Equal(4096, json["bytes"]!.GetValue<long>());
        Assert.Equal(403, json["status"]!.GetValue<int>());
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Equal("2026-10-07T18:00:01.500Z", json["at"]!.GetValue<string>());
        var action = FlightJson.Event(events[1], recorder.UtcAt(events[1].Timestamp));
        Assert.Equal("checkOut", action["action"]!.GetValue<string>());
        Assert.Equal(3, action["targets"]!.GetValue<int>());
    }

    [Fact]
    public void The_observer_sees_what_can_start_an_incident_and_never_the_frequent_calls()
    {
        var recorder = new FlightRecorder(clock: new ManualClock());
        var seen = new Watch();
        recorder.Observer = seen;
        recorder.Rpc("armory_list_changes", 20, 200, null);
        recorder.Transfer("upload", 10, 5, true, 200, null);
        recorder.Notice("cantSend", "Robot/Plate.SLDPRT", "refused");
        recorder.PassStart("loop");
        recorder.PassEnd("loop", false, 61_000, 1, 2, 3, 4);
        recorder.WindowAction("checkIn", 1, 12, true);
        recorder.ReadOnlyBroken("Robot/Plate.SLDPRT");
        Assert.Equal([FlightKind.PassStart, FlightKind.PassEnd, FlightKind.WindowAction, FlightKind.ReadOnlyBroken], seen.Kinds);
    }

    // The engine records every server call and transfer, so recording must cost well under a
    // microsecond and allocate nothing once the ring is warm (GC.GetAllocatedBytesForCurrentThread).
    [Fact]
    public void Recording_costs_well_under_a_microsecond_and_allocates_nothing_per_event()
    {
        var recorder = new FlightRecorder();
        recorder.Observer = new Watch(keep: false);
        const string rpc = "armory_project_files", path = "Robot 2027/Drivetrain/Gearbox.SLDASM";
        void Burst(int n)
        {
            for (var i = 0; i < n; i++)
            {
                switch (i & 3)
                {
                    case 0: recorder.Rpc(rpc, i, 200, null); break;
                    case 1: recorder.Transfer("download", i * 1024L, i, true, 200, null); break;
                    case 2: recorder.PassPhase("plan", i); break;
                    default: recorder.Notice("cantRead", path, null); break;
                }
            }
        }
        Burst(20_000); // warm: the ring, the JIT, the lock
        const int events = 1_000_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        Burst(events);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var nanoseconds = watch.Elapsed.TotalNanoseconds / events;
        Assert.True(nanoseconds < 1000, $"recording took {nanoseconds:F0} ns per event");
        Assert.True(allocated < 1024, $"recording {events:N0} events allocated {allocated:N0} bytes");
        Assert.Equal(events + 20_000, recorder.Recorded);
    }

    private sealed class Watch(bool keep = true) : IFlightObserver
    {
        public List<FlightKind> Kinds { get; } = [];
        public void Observe(in FlightEvent e)
        {
            if (keep) Kinds.Add(e.Kind);
        }
    }
}
