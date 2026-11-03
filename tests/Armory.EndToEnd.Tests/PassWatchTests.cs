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
        // The app's own disk hints wake the loop at a save (AgentHost.PollHints); this world has
        // none, so it looks every second (0.5.3's quiet look is 30 seconds).
        t.A.Poll = TimeSpan.FromSeconds(1);
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

    // 0.5.3 (d45da67b, f80cbc57, f9acf13e: right after a start or a switch, a pass "failed after
    // 307 s"): the open-files question about SolidWorks' markers used a token the watch never
    // canceled, so the watch said it was starting the pass again and nothing happened. Now the
    // watch ends the question too, and the next pass runs.
    [PostgresFact]
    public async Task A_loop_pass_stuck_asking_which_files_are_open_is_ended_and_the_next_one_runs()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        // SolidWorks has the part open: every loop pass asks about it before it reads the server.
        t.A.Write("Robot 2027/Drivetrain/~$Plate.SLDPRT", "marker");
        t.A.StallLimit = TimeSpan.FromSeconds(1);
        t.A.WatchEvery = TimeSpan.FromMilliseconds(200);
        t.A.Restart();
        t.A.Disk.OpenAmongHangs = true;
        t.A.Engine.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => t.A.Engine.StalledPasses > 0, TimeSpan.FromSeconds(20)), "the stuck pass was never ended");
            var before = t.A.Engine.LoopPasses;
            Assert.True(SpinWait.SpinUntil(() => t.A.Engine.LoopPasses > before || t.A.Engine.StalledPasses > 1, TimeSpan.FromSeconds(20)), "the stuck question was never ended");
            t.A.Disk.OpenAmongHangs = false;
            var after = t.A.Engine.LoopPasses;
            Assert.True(SpinWait.SpinUntil(() => t.A.Engine.LoopPasses > after, TimeSpan.FromSeconds(20)), "no pass ran after the stall");
            Assert.DoesNotContain(t.A.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.Exception);
        }
        finally
        {
            t.A.Disk.OpenAmongHangs = false;
            await t.A.Engine.StopAsync();
        }
    }
}
