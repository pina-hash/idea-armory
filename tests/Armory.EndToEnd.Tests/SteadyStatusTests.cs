using System.Collections.Concurrent;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Feedback b32e6726 (0.3.3): "the updating and not saved back and forth status every second
// constantly twitches the screen". Each live update rejoin started a pass that only read the
// server, and every one flipped the status to Updating and back. A look that moves nothing keeps
// the settled status; the now sentence is always there and says what Armory is doing.
public sealed class SteadyStatusTests
{
    [PostgresFact]
    public async Task A_quick_look_at_the_server_never_flips_the_status()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncTimesAsync(2);
        Assert.Equal(SyncStates.Synced, t.A.Engine.View.Sync.State);
        Assert.Equal("Watching your folder for saves.", t.A.Engine.View.Sync.Now);
        var seen = new ConcurrentQueue<SyncView>();
        void Heard(AgentView view) => seen.Enqueue(view.Sync);
        t.A.Engine.ViewChanged += Heard;
        try { await t.A.SyncTimesAsync(5); }
        finally { t.A.Engine.ViewChanged -= Heard; }
        // Nothing changed, so nothing was drawn again; and nothing said Updating.
        Assert.DoesNotContain(seen, s => s.State != SyncStates.Synced);
        Assert.All(seen, s => Assert.False(string.IsNullOrWhiteSpace(s.Now)));
        // The detail keeps its place: "Last checked ..." throughout.
        Assert.All(seen, s => Assert.StartsWith("Last checked", s.Detail));
    }

    // The now sentence says what Armory waits for: a check in waiting for SolidWorks to close.
    [PostgresFact]
    public async Task Now_says_a_check_in_waits_for_SolidWorks_to_close_the_file()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Disk.Hold(Plate);
        t.A.Save(Plate, "edit");
        await t.A.CheckInAsync(Plate);
        await t.A.SyncAsync();
        Assert.Equal("Waiting for you to close 1 file in SolidWorks to check it in.", t.A.Engine.View.Sync.Now);
        t.A.Disk.Unhold(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal("Watching your folder for saves.", t.A.Engine.View.Sync.Now);
    }
}
