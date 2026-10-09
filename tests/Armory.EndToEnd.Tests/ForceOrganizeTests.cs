using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Note N5 (Abraham, IDEA-06, 0.3.1): "Allow other people to organize files while other people
// have those files checked out, and create a checkout override for the instructor." The server
// moves a file only for its lock's holder and refuses to rename or delete a folder over anyone
// else's check out, with no role allowed past. So a mentor or CAD lead renames or deletes with
// force: the check outs in the way are force checked in first, in the same action, and their
// holders' work that wasn't checked in is kept as their own copy. A student is told who can.
public sealed class ForceOrganizeTests
{
    private const string Gearbox = "Robot 2027/Drivetrain/Gearbox";

    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    private static async Task<(Team T, Computer Mentor)> MentorTeamAsync()
    {
        var t = await TeamAsync();
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        return (t, mentor);
    }

    [PostgresFact]
    public async Task A_lead_force_checks_in_and_renames_a_folder_in_one_action()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        t.A.Write($"{Gearbox}/Housing.SLDPRT", "housing");
        t.A.Write($"{Gearbox}/Gear.SLDPRT", "gear");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Gear.SLDPRT")).Ok);
        t.B.Save($"{Gearbox}/Gear.SLDPRT", "Maria's unfinished gear");
        await mentor.SyncAsync();
        await t.A.SyncAsync();

        // A student is told who can help; a lead is told they can force check it in.
        var student = await t.A.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gearbox v2");
        Assert.Equal((false, "Gearbox can't be renamed now: Maria Lopez has 1 of its files checked out. Ask them to check it in, or ask a mentor or CAD lead to force check it in."),
            (student.Ok, student.Message));
        Assert.Equal("Only a mentor or CAD lead can force a check in.", (await t.A.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gearbox v2", force: true)).Message);
        Assert.Equal("Gearbox can't be renamed now: Maria Lopez has 1 of its files checked out. Ask them to check it in, or force check it in.",
            (await mentor.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gearbox v2")).Message);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_locks where broken_at is null"));

        var renamed = await mentor.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gearbox v2", force: true);
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("Force checked in 1 file from Maria Lopez, then renamed Gearbox to Gearbox v2. Anything Maria hadn't checked in is kept as Maria's own copy.", renamed.Message);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks where broken_at is null"));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where folder='Drivetrain/Gearbox v2' and deleted_at is null"));

        // Maria's computer keeps her unfinished work as her own copy and follows the rename.
        await t.B.SyncTimesAsync(2);
        var gear = await t.FileId("Gear.SLDPRT");
        Assert.Contains(Maria + "|lock broken", await t.SideAuthors(gear));
        Assert.True(t.World.HashOnServer(Hash("Maria's unfinished gear")));
        Assert.Equal("gear", t.B.Text("Robot 2027/Drivetrain/Gearbox v2/Gear.SLDPRT"));
        Assert.True(t.B.Disk.IsReadOnly("Robot 2027/Drivetrain/Gearbox v2/Gear.SLDPRT"));
        var notice = t.B.Card(NoticeKinds.TakenBack)!;
        Assert.Equal("Gear.SLDPRT was force checked in", notice.Title);
        Assert.Equal("Pina force checked in Gear.SLDPRT. Your changes that weren't checked in are kept as your own copy in its history.", notice.Detail);

        // Delete folder the same way.
        Assert.True((await t.B.CheckOutAsync("Robot 2027/Drivetrain/Gearbox v2/Housing.SLDPRT")).Ok);
        await mentor.SyncAsync();
        var deleted = await mentor.Engine.DeleteFolderAsync(t.Project, "Drivetrain/Gearbox v2", force: true);
        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal("Force checked in 1 file from Maria Lopez, then deleted Gearbox v2 and its 2 files. Their history is kept. Anything Maria hadn't checked in is kept as Maria's own copy.",
            deleted.Message);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where deleted_at is null"));
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.B.Read("Robot 2027/Drivetrain/Gearbox v2/Housing.SLDPRT"));
        NoViolations(t.A, t.B, mentor);
    }

    [PostgresFact]
    public async Task A_lead_force_checks_in_and_renames_a_file_and_the_holder_is_told_who()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex's unfinished plate");
        await mentor.SyncAsync();
        var renamed = await mentor.Engine.RenameFileAsync(Plate, "Plate v2.SLDPRT", force: true);
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("Force checked in Plate.SLDPRT from Alex Kim, then renamed it to Plate v2.SLDPRT. Anything Alex hadn't checked in is kept as Alex's own copy.", renamed.Message);
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Plate v2.SLDPRT");
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal("plate", t.A.Text("Robot 2027/Drivetrain/Plate v2.SLDPRT"));
        Assert.Null(t.A.Read(Plate));
        Assert.Equal("Pina force checked in Plate v2.SLDPRT. Your changes that weren't checked in are kept as your own copy in its history.",
            t.A.Card(NoticeKinds.TakenBack)!.Detail);

        // A holder with nothing new, the file still open in SolidWorks: who did it, and how to keep
        // working, never "your changes".
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Drivetrain/Plate v2.SLDPRT")).Ok);
        await t.A.Engine.DismissNoticeAsync(t.A.Card(NoticeKinds.TakenBack)!.Key);
        t.A.Open("Robot 2027/Drivetrain/Plate v2.SLDPRT");
        await t.A.SyncAsync();
        await mentor.SyncAsync();
        Assert.True((await mentor.TakeBackAsync(file)).Ok);
        await t.A.SyncTimesAsync(2);
        var card = t.A.Card(NoticeKinds.TakenBack)!;
        Assert.Equal("Pina force checked in Plate v2.SLDPRT. If it's still open in SolidWorks, use Save As to keep working on a copy.", card.Detail);
        Assert.Equal(1, await t.Versions(file));
        NoViolations(t.A, mentor);
    }
}
