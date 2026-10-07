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
        // What 0.1.0 left behind: schema 1, the ownership it applied (Free was writable then),
        // the roles in its own words, no project folder, and both files writable on disk.
        var json = JsonNode.Parse(t.A.State.Load()!)!.AsObject();
        json["schema"] = 1;
        foreach (var (path, file) in json["files"]!.AsObject())
            file!["appliedOwnership"] = path.EndsWith("Bracket.SLDPRT", StringComparison.Ordinal) ? 1 : 0;
        foreach (var (_, project) in json["projects"]!.AsObject())
        {
            project!.AsObject().Remove("folder");
            project["role"] = "Student";
        }
        t.A.State.Save(Encoding.UTF8.GetBytes(json.ToJsonString()));
        t.A.Disk.ClearReadOnly(Plate);
        t.A.Disk.ClearReadOnly(Bracket);
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
}
