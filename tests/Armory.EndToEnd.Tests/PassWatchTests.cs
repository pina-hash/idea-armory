using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.4.0 (slowPass incidents of 0.3.3: loop passes of 60 seconds to 10 hours that moved nothing,
// IDEA-06 3647.8 s, IDEA-20 3035.5 s, DESKTOP-QH30N35 26531.3 s): a loop pass that stops making
// progress is ended, the log says what it was doing, and the next pass runs at once, so a stall
// never holds Armory for longer than the limit.
public sealed class PassWatchTests
{
    [PostgresFact]
    public async Task A_loop_pass_stuck_reading_the_server_is_ended_and_the_next_one_runs()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.StallLimit = TimeSpan.FromSeconds(1);
        t.A.WatchEvery = TimeSpan.FromMilliseconds(200);
        t.A.Restart();
        var stuck = true;
        t.A.Network.RpcDelay = _ => Volatile.Read(ref stuck) ? TimeSpan.FromMinutes(30) : TimeSpan.Zero;
        t.A.Engine.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => t.A.Engine.StalledPasses > 0, TimeSpan.FromSeconds(20)), "the stuck pass was never ended");
            Volatile.Write(ref stuck, false);
            List<string> logged;
            lock (t.A.Logged) logged = [.. t.A.Logged];
            Assert.Contains(logged, l => l.StartsWith("pass: no progress for", StringComparison.Ordinal) && l.Contains("while asking Armory what changed", StringComparison.Ordinal) && l.EndsWith("starting it again", StringComparison.Ordinal));
            Assert.Contains(t.A.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.Note && e.Name == "passStalled");
            // Not a crash: nothing in the flight says the engine loop failed.
            Assert.DoesNotContain(t.A.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.Exception);
            // The loop goes on: a save made now goes up by itself.
            t.A.Save("Robot 2027/Drivetrain/After.SLDPRT", "after the stall");
            var file = Guid.Empty;
            for (var i = 0; i < 100 && file == Guid.Empty; i++)
            {
                await Task.Delay(200);
                try { file = await t.FileId("After.SLDPRT"); } catch (Exception) { }
            }
            Assert.NotEqual(Guid.Empty, file);
        }
        finally
        {
            Volatile.Write(ref stuck, false);
            await t.A.Engine.StopAsync();
        }
    }
}
