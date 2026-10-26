using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// One Armory folder for everyone on a lab computer (0.5.0, ledger 0437; SyncEngine.People.cs).
// The next person gets the folder at once, with nothing downloaded again; what the last person
// left waiting (a check out, a save not sent, a new file) stays theirs, read-only for the next
// person, and goes to Armory under their own sign-in when they are back. Nothing of one person's
// work is ever sent as another's. (Until 0.5.0 the folder stayed with whoever had work in it, and
// the next student waited or used a folder of their own.)
public sealed class AccountTests
{
    private const string Sketch = "Robot 2027/Drivetrain/Sketch.SLDPRT", Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";

    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    private static Task<List<string>> Authors(Team t, Guid file)
        => t.World.QueryAsync("select author_email from armory_versions where file_id=@f order by created_at", r => r.GetString(0), ("f", file));

    private static Task<long> FilesNamed(Team t, string name)
        => t.World.CountAsync("select count(*) from armory_files where name=@n and deleted_at is null", ("n", name));

    // Replaces The_next_student_takes_over_the_folder_only_when_the_last_one_has_nothing_waiting.
    [PostgresFact]
    public async Task The_next_person_takes_the_folder_at_once_and_the_last_ones_waiting_work_stays_theirs()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex, not checked in");
        await t.A.SyncAsync();
        // A sketch of Alex's, saved after his last pass: Armory hasn't seen it yet.
        t.A.Write(Sketch, "Alex's sketch, never added");

        // Maria signs in on Alex's lab computer: the folder is hers at once, nothing comes down
        // again, and nothing of Alex's goes up.
        await t.A.ConnectAsync(Maria);
        var first = await t.A.SyncAsync();
        Assert.Equal((0, 0, 0), (first.Downloaded, first.Uploaded, first.SideVersions));
        Assert.Equal(Connections.SignedIn, t.A.Engine.View.Connection);
        Assert.Equal(Maria, t.A.Engine.View.Account!.Email);
        Assert.Equal(Maria, SyncEngine.OwnerOf(t.A.State));
        Assert.Equal("Alex, not checked in", t.A.Text(Plate));
        Assert.Equal("Alex's sketch, never added", t.A.Text(Sketch));
        Assert.Equal(0, await FilesNamed(t, "Sketch.SLDPRT"));
        // Both are read-only for her, so SolidWorks opens them read-only and her save can't land there.
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Sketch));
        Assert.Throws<IOException>(() => t.A.Save(Plate, "Maria's save over Alex's check out"));
        var held = Assert.Single(t.A.Engine.View.HeldForOthers!);
        Assert.Equal((Alex, "Alex Kim", 2, "2 files"), (held.Email, held.Name, held.Files, held.Words));
        // Her check out of his file is refused here, in words about him, and changes nothing.
        var refused = await t.A.CheckOutAsync(Plate);
        Assert.False(refused.Ok);
        Assert.Equal("Alex Kim's work in Plate.SLDPRT is waiting on this computer for Alex. It opens read-only for you until Alex picks their name here and finishes it.", refused.Message);
        Assert.Equal("This Armory folder is already yours.", (await t.A.Engine.TakeOverFolderAsync()).Message);
        // Passes after the first stay quiet: still nothing of his is captured or sent.
        var again = await t.A.SyncAsync();
        Assert.Equal((0, 0), (again.Downloaded, again.Uploaded));
        Assert.Equal(1, await t.Versions(plate));
        Assert.Equal(new[] { Alex }, await Authors(t, plate));

        // Her own work is hers: a new part, under her name.
        t.A.Write(Gear, "Maria's gear");
        await t.A.SyncAsync();
        Assert.Equal(new[] { Maria }, await Authors(t, await t.FileId("Gear.SLDPRT")));

        // Alex is back: his work is his again, writable, and goes up under his own sign-in.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(t.A.Engine.View.HeldForOthers!);
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        await t.A.SyncAsync();
        Assert.Equal(new[] { Alex, Alex }, await Authors(t, plate));
        Assert.Equal(Hash("Alex, not checked in"), await t.CurrentHash(plate));
        Assert.Equal(new[] { Alex }, await Authors(t, await t.FileId("Sketch.SLDPRT")));
        // Maria's gear came to Alex as the team's version, never as his work.
        Assert.Equal("Maria's gear", t.A.Text(Gear));
        Assert.Equal(new[] { Maria }, await Authors(t, await t.FileId("Gear.SLDPRT")));

        // And back to Maria: nothing waits for anyone now.
        await t.A.ConnectAsync(Maria);
        var last = await t.A.SyncAsync();
        Assert.Equal((0, 0), (last.Downloaded, last.Uploaded));
        Assert.Empty(t.A.Engine.View.HeldForOthers!);
        NoViolations(t.A);
    }

    // Replaces A_save_not_sent_keeps_the_folder_with_its_student: a save Alex made offline, never
    // sent, is his. It waits for him, and only his sign-in sends it.
    [PostgresFact]
    public async Task A_save_not_sent_is_held_for_its_author_and_sent_only_under_their_sign_in()
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
        Assert.Contains(new Armory.Core.OfflineJournal(t.A.Journal).Read().Entries, e => e.Path == "Robot 2027/Drivetrain/Offline.SLDPRT" && e.Author == Alex);

        await t.A.ConnectAsync(Maria);
        var hers = await t.A.SyncAsync();
        await t.A.SyncAsync();
        Assert.Equal(0, hers.Uploaded);
        Assert.Equal(0, await FilesNamed(t, "Offline.SLDPRT"));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Drivetrain/Offline.SLDPRT"));
        Assert.Equal(1, Assert.Single(t.A.Engine.View.HeldForOthers!).Files);
        // What waits for Maria herself is nothing: Alex's save is not hers to finish.
        Assert.False((await t.A.Engine.WaitingAsync()).Any);

        // Alex comes back: his save goes, under his name.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncAsync();
        await t.A.SyncAsync();
        Assert.Equal(Alex, (await t.World.QueryAsync("select v.author_email from armory_files f join armory_versions v on v.id = f.current_version_id where f.name='Offline.SLDPRT'", r => r.GetString(0))).Single());
        await t.A.ConnectAsync(Maria);
        Assert.Equal((0, 0), ((await t.A.SyncAsync()).Downloaded, (await t.A.SyncAsync()).Uploaded));
        NoViolations(t.A);
    }

    // Two people on one project with nothing waiting: the second downloads nothing, and the first
    // is back to a folder that is up to date with what the second did.
    [PostgresFact]
    public async Task Two_people_on_one_project_share_one_folder_and_nothing_downloads_twice()
    {
        await using var t = await TeamAsync();
        for (var i = 0; i < 12; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT", $"part {i}");
        await t.A.SyncTimesAsync(2);
        await t.A.ConnectAsync(Maria);
        var first = await t.A.SyncAsync();
        Assert.Equal((0, 0), (first.Downloaded, first.Uploaded));
        Assert.Empty(t.A.Engine.View.HeldForOthers!);
        Assert.Equal(12, t.A.Engine.View.Projects.Single().Folders.Sum(f => f.Files.Count));
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Bulk/Part-03.SLDPRT")).Ok);
        t.A.Save("Robot 2027/Bulk/Part-03.SLDPRT", "Maria's part 3");
        Assert.Equal("Checked in Part-03.SLDPRT.", (await t.A.CheckInAsync("Robot 2027/Bulk/Part-03.SLDPRT")).Message);
        await t.A.ConnectAsync(Alex);
        var back = await t.A.SyncAsync();
        Assert.Equal((0, 0), (back.Downloaded, back.Uploaded));
        Assert.Equal("Maria's part 3", t.A.Text("Robot 2027/Bulk/Part-03.SLDPRT"));
        NoViolations(t.A);
    }
}
