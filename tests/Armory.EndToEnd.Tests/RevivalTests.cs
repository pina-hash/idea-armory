using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Contract v2, C4 and D6: adding a file under a removed file's name revives that file, so its
// history goes on. 0232 is live on ideabosco.com, so the agent meets revivals now: the added
// bytes must become the shared version, committed on top of the revived file's current version,
// and the removed bytes must never come back over them (docs/server/contract.md, open point 1,
// is what the 0.1.0 agent does instead).
public sealed class RevivalTests
{
    private const string Intake = "Robot 2027/Intake/Plate.SLDPRT";

    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    // The Pack and Go case: a student unzips a copy into another folder, and one of its parts
    // has the name of a part someone removed from the project.
    [PostgresFact]
    public async Task A_removed_name_added_in_another_folder_revives_its_history_with_the_new_bytes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "old plate");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        var removed = (await t.World.QueryAsync("select current_version_id from armory_files where id=@f", r => r.GetGuid(0), ("f", file))).Single();
        t.A.Delete(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_tombstones where file_id=@f", ("f", file)));

        t.B.Write(Intake, "brand new plate from B");
        await t.B.SyncTimesAsync(3);

        // B's new part stays on B's disk: nothing was downloaded over it, moved aside, or fetched
        // back to the removed file's old folder.
        Assert.Equal("brand new plate from B", t.B.Text(Intake));
        Assert.Equal(0, t.B.Disk.Replaces);
        Assert.Empty(t.B.Disk.Recovered);
        Assert.Null(t.B.Read(Plate));
        // It is the same file, revived in Intake, and B's bytes are its shared version, committed
        // on top of the version that was current when it was removed. Nothing was kept aside.
        Assert.Equal(file, await t.FileId("Plate.SLDPRT"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where id=@f and deleted_at is null and folder='Intake'", ("f", file)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones where file_id=@f", ("f", file)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where entity_id=@f and kind='file_revived'", ("f", file)));
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(0, await t.Sides(file));
        Assert.Equal(Hash("brand new plate from B"), await t.CurrentHash(file));
        Assert.Equal(removed, (await t.World.QueryAsync("select v.parent_version_id from armory_files f join armory_versions v on v.id=f.current_version_id where f.id=@f", r => r.GetGuid(0), ("f", file))).Single());
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file)));

        // A, which removed it, receives B's part in its new folder, and the old path stays gone.
        await t.A.SyncTimesAsync(2);
        Assert.Equal("brand new plate from B", t.A.Text(Intake));
        Assert.Null(t.A.Read(Plate));
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(0, await t.Sides(file));
        Assert.Null(t.B.Read(Plate));
        Assert.DoesNotContain(t.A.Engine.View.NeedsMe, n => n.Kind != AttentionKinds.ReleaseNotChecked);
        Assert.DoesNotContain(t.B.Engine.View.NeedsMe, n => n.Kind != AttentionKinds.ReleaseNotChecked);
        NoViolations(t.A); NoViolations(t.B);
    }

    // The computer that removed the file adds the name again in another folder: its own record of
    // the removed file is that file's past, and the revived file goes on from the removed version.
    [PostgresFact]
    public async Task The_computer_that_removed_a_name_can_add_it_again_in_another_folder()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "old plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Delete(Plate);
        await t.A.SyncTimesAsync(2);
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate)); // B kept its copy in recovery
        t.A.Write(Intake, "Alex's new plate");
        await t.A.SyncTimesAsync(3);
        Assert.Equal("Alex's new plate", t.A.Text(Intake));
        Assert.Null(t.A.Read(Plate));
        Assert.Equal(0, t.A.Disk.Replaces);
        Assert.Equal(file, await t.FileId("Plate.SLDPRT"));
        Assert.Equal((2L, 0L), (await t.Versions(file), await t.Sides(file)));
        Assert.Equal(Hash("Alex's new plate"), await t.CurrentHash(file));
        await t.B.SyncTimesAsync(2);
        Assert.Equal("Alex's new plate", t.B.Text(Intake));
        Assert.Null(t.B.Read(Plate));
        Assert.Equal((2L, 0L), (await t.Versions(file), await t.Sides(file)));
        Assert.DoesNotContain(t.A.Engine.View.NeedsMe, n => n.Kind != AttentionKinds.ReleaseNotChecked);
        Assert.DoesNotContain(t.B.Engine.View.NeedsMe, n => n.Kind != AttentionKinds.ReleaseNotChecked);
        NoViolations(t.A); NoViolations(t.B);
    }
}
