using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// One Armory folder taken in turns on a lab computer (0.3.2, SyncEngine.Accounts.cs): the next
// student takes the folder over only when the last one has nothing waiting in it, and nothing
// of the last one's work ever becomes the next one's.
public sealed class AccountTests
{
    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    private static string Waiting(string what) =>
        $"Alex Kim still has {what} in this folder. Alex can sign in to Armory here to finish them, or you can use a folder of your own.";

    [PostgresFact]
    public async Task The_next_student_takes_over_the_folder_only_when_the_last_one_has_nothing_waiting()
    {
        await using var t = await TeamAsync();
        const string Sketch = "Robot 2027/Drivetrain/Sketch.SLDPRT";
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex, not checked in");
        await t.A.SyncAsync();

        // Maria signs in to Armory on Alex's lab computer: the folder is Alex's, with his check out.
        await t.A.ConnectAsync(Maria);
        await t.A.SyncAsync();
        Assert.Equal(Connections.VaultOwnedByOther, t.A.Engine.View.Connection);
        var refused = await t.A.Engine.TakeOverFolderAsync();
        Assert.Equal((false, Waiting("1 file checked out")), (refused.Ok, refused.Message));
        await t.A.SyncAsync();
        Assert.Equal(Connections.VaultOwnedByOther, t.A.Engine.View.Connection);
        Assert.Equal("Alex, not checked in", t.A.Text(Plate));

        // Alex signs back in and checks in; a sketch of his is still only on this disk.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncAsync();
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        await t.A.ConnectAsync(Maria);
        t.A.Write(Sketch, "Alex's sketch, never added");
        var sketch = await t.A.Engine.TakeOverFolderAsync();
        Assert.Equal((false, Waiting("1 new file not in Armory yet")), (sketch.Ok, sketch.Message));
        // A team file changed on disk while nobody's Armory was watching is Alex's too.
        t.A.Delete(Sketch);
        t.A.ForceWrite(Plate, "changed with Armory signed out");
        Assert.Equal(Waiting("1 file changed and not saved to Armory"), (await t.A.Engine.TakeOverFolderAsync()).Message);
        t.A.ForceWrite(Plate, "Alex, not checked in");

        // Nothing of Alex's is waiting: Maria takes the folder over, and nothing is downloaded again.
        var taken = await t.A.Engine.TakeOverFolderAsync();
        Assert.Equal((true, "This Armory folder is yours now. Alex Kim's files here were all saved to Armory, so nothing of theirs changes."), (taken.Ok, taken.Message));
        var first = await t.A.SyncAsync();
        Assert.Equal((0, 0, 0), (first.Downloaded, first.Uploaded, first.SideVersions));
        Assert.Equal(Connections.SignedIn, t.A.Engine.View.Connection);
        Assert.Equal(Maria, t.A.Engine.View.Account!.Email);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal("This Armory folder is yours now. It was Alex's.", t.A.Engine.View.Activity.Log[^1].Line);

        // Her own work is hers: a check out, a save, a check in under her name.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Maria's change");
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        Assert.Equal(Maria, (await t.World.QueryAsync("select v.author_email from armory_files f join armory_versions v on v.id = f.current_version_id where f.id=@f",
            r => r.GetString(0), ("f", plate))).Single());
        Assert.Equal(new[] { Alex, Alex, Maria }, await t.World.QueryAsync("select author_email from armory_versions where file_id=@f order by created_at", r => r.GetString(0), ("f", plate)));
        Assert.Equal("This Armory folder is already yours.", (await t.A.Engine.TakeOverFolderAsync()).Message);
        NoViolations(t.A);
    }

    // A save Alex made offline, never sent: his, so the folder stays his until it goes.
    [PostgresFact]
    public async Task A_save_not_sent_keeps_the_folder_with_its_student()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        t.A.Offline = true;
        t.A.Write("Robot 2027/Drivetrain/Offline.SLDPRT", "made offline");
        await t.A.SyncAsync();
        t.A.Offline = false;
        await t.A.ConnectAsync(Maria);
        var refused = await t.A.Engine.TakeOverFolderAsync();
        Assert.False(refused.Ok);
        Assert.Equal(Waiting("1 save not sent"), refused.Message);
        // Alex comes back: his save goes, under his name, and then the folder can be handed over.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncAsync();
        await t.A.SyncAsync();
        Assert.Equal(Alex, (await t.World.QueryAsync("select v.author_email from armory_files f join armory_versions v on v.id = f.current_version_id where f.name='Offline.SLDPRT'", r => r.GetString(0))).Single());
        await t.A.ConnectAsync(Maria);
        Assert.True((await t.A.Engine.TakeOverFolderAsync()).Ok);
        NoViolations(t.A);
    }
}
