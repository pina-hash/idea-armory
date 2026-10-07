using System.Text;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Upgrading 0.1.0 to 0.2.0 keeps the vault (decision D15): the state document migrates, the
// files on disk and their history stay, and the first pass applies the v2 read-only rule. A lock
// 0.1.0 held (its ~$ marker took it) is a check out now, never let go by itself.
public sealed class UpgradeTests
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    // What 0.1.0 left behind: schema 1, the ownership it applied (Free was writable then),
    // the roles in its own words, no project folder, nothing v2 records, and both files
    // writable on disk.
    private static void LeaveAsZeroOneZero(Computer c)
    {
        var json = JsonNode.Parse(c.State.Load()!)!.AsObject();
        json["schema"] = 1;
        json.Remove("revivals");
        foreach (var (path, file) in json["files"]!.AsObject())
        {
            file!["appliedOwnership"] = path.EndsWith("Bracket.SLDPRT", StringComparison.Ordinal) ? 1 : 0;
            file.AsObject().Remove("holder");
        }
        foreach (var (_, project) in json["projects"]!.AsObject())
        {
            project!.AsObject().Remove("folder");
            project["role"] = "Student";
        }
        c.State.Save(Encoding.UTF8.GetBytes(json.ToJsonString()));
        c.Disk.ClearReadOnly(Plate);
        c.Disk.ClearReadOnly(Bracket);
    }

    [PostgresFact]
    public async Task An_upgrade_from_0_1_0_makes_every_file_read_only_unless_it_is_checked_out()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        var bracket = await t.FileId("Bracket.SLDPRT");
        LeaveAsZeroOneZero(t.A);
        var replaces = t.A.Disk.Replaces;
        t.A.Restart(); // the 0.2.0 engine starts on the same vault and the same sign-in
        await t.A.SyncAsync();
        Assert.Equal(Connections.SignedIn, t.A.Engine.View.Connection);
        Assert.True(t.A.Disk.IsReadOnly(Plate)); // nobody has it checked out
        Assert.False(t.A.Disk.IsReadOnly(Bracket)); // still Alex's: a check out now
        Assert.Equal("Checked out by you", Assert.Single(t.A.Engine.View.MyFiles).Checkout.Label);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", bracket)));
        // The vault was kept: nothing downloaded, moved aside or sent again.
        Assert.Equal(replaces, t.A.Disk.Replaces);
        Assert.Empty(t.A.Disk.Recovered);
        Assert.Equal(("plate", "bracket"), (t.A.Text(Plate), t.A.Text(Bracket)));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_versions"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions"));
        Assert.Empty(t.A.Engine.View.Notices);
        // The check out stays until Alex checks it in.
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", bracket)));
        Assert.True((await t.A.CheckInAsync(Bracket)).Ok);
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
    }

    // The first 0.2.0 start is offline (a laptop opened at home): the v2 rule holds from the
    // first pass all the same, from what 0.1.0 last knew of who holds each file, and the window
    // lists both files with who has them.
    [PostgresFact]
    public async Task An_upgrade_that_starts_offline_still_makes_every_file_read_only_unless_it_is_checked_out()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        var bracket = await t.FileId("Bracket.SLDPRT");
        LeaveAsZeroOneZero(t.A);
        t.A.Offline = true;
        t.A.Restart();
        await t.A.SyncTimesAsync(2);
        Assert.Equal(SyncStates.Offline, t.A.Engine.View.Sync.State);
        Assert.True(t.A.Disk.IsReadOnly(Plate)); // nobody has it checked out
        Assert.False(t.A.Disk.IsReadOnly(Bracket)); // Alex's check out
        Assert.Throws<IOException>(() => t.A.Save(Plate, "saved over a file nobody checked out"));
        Assert.Equal(("Available", "Checked out by you"), (t.A.Row(Plate).Checkout.Label, t.A.Row(Bracket).Checkout.Label));
        Assert.Equal(Bracket, Assert.Single(t.A.Engine.View.MyFiles).Path);
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.False(t.A.Disk.IsReadOnly(Bracket));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", bracket)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions"));
    }
}
