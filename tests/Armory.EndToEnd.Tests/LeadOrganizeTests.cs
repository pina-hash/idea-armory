using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.4.1 (the item 0.4.0 left unbuilt, idea-app docs/history/armory-040-field-fixes-0423.md): a
// mentor, CAD lead or instructor organizes (renames, moves) files and folders over someone else's
// check out without force checking it in first. The server already allows it (idea-app 0236,
// ArmoryV3StandIn.ApplyOrganizeAsync applies its bodies as they are): the check out stays the
// holder's, and the holder's computer moves its writable copy itself, only once nothing has it open
// (SolidWorks lets go), with every save kept, before and after the move.
public sealed class LeadOrganizeTests
{
    private const string Gearbox = "Robot 2027/Drivetrain/Gearbox";

    private static async Task<(Team T, Computer Mentor)> OrganizeTeamAsync()
    {
        var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyOrganizeAsync(t.World.Database);
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        return (t, mentor);
    }

    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    private static bool Logged(Computer c, string line) => c.Engine.ActivityNow.Log.Any(l => l.Line == line);

    [PostgresFact]
    public async Task A_lead_renames_a_file_someone_has_checked_out_and_it_stays_theirs_with_their_changes()
    {
        var (t, mentor) = await OrganizeTeamAsync();
        await using var _ = t;
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        // Maria's change, not sent yet.
        t.B.Save(Plate, "Maria's plate");
        await mentor.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");

        // A student is still refused; a lead renames it at once, no force check in.
        await t.A.SyncAsync();
        var student = await t.A.Engine.RenameFileAsync(Plate, "Plate v2.SLDPRT");
        Assert.False(student.Ok);
        Assert.Equal("Maria Lopez on student B lab PC has Plate.SLDPRT checked out, so it can't be renamed now. Ask them to check it in, or ask a mentor or CAD lead to force check it in.",
            student.Message);
        var renamed = await mentor.Engine.RenameFileAsync(Plate, "Plate v2.SLDPRT");
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("Renamed Plate.SLDPRT to Plate v2.SLDPRT. Maria Lopez on student B lab PC still has it checked out.", renamed.Message);
        Assert.Equal(Maria, await t.Holder(file));
        Assert.Empty(await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where name='Plate v2.SLDPRT' and folder='Drivetrain'"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved' and payload->>'checked_out_by'=@m", ("m", Maria)));
        const string Renamed = "Robot 2027/Drivetrain/Plate v2.SLDPRT";
        Assert.Equal("plate", mentor.Text(Renamed));
        Assert.Null(mentor.Read(Plate));
        Assert.True(mentor.Disk.IsReadOnly(Renamed));

        // Maria's computer moves her writable copy, with her change in it, and she is told who did it.
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.B.Read(Plate));
        Assert.Equal("Maria's plate", t.B.Text(Renamed));
        Assert.False(t.B.Disk.IsReadOnly(Renamed));
        Assert.True(Logged(t.B, "Pina renamed Plate.SLDPRT to Plate v2.SLDPRT. It's still checked out to you."));
        Assert.Equal(Maria, await t.Holder(file));

        // She goes on working, and her check in lands on the renamed file.
        t.B.Save(Renamed, "Maria's plate, finished");
        Assert.True((await t.B.CheckInAsync(Renamed)).Ok);
        Assert.Equal(Hash("Maria's plate, finished"), await t.CurrentHash(file));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.True(t.B.Disk.IsReadOnly(Renamed));
        await mentor.SyncAsync();
        await t.A.SyncTimesAsync(2);
        Assert.Equal("Maria's plate, finished", mentor.Text(Renamed));
        Assert.Equal("Maria's plate, finished", t.A.Text(Renamed));
        Assert.Null(t.A.Read(Plate));
        NoViolations(t.A, t.B, mentor);
    }

    [PostgresFact]
    public async Task A_lead_renames_a_folder_over_a_file_open_in_solidworks_and_the_holders_computer_waits_for_it_to_close()
    {
        var (t, mentor) = await OrganizeTeamAsync();
        await using var _ = t;
        t.A.Write($"{Gearbox}/Housing.SLDPRT", "housing");
        t.A.Write($"{Gearbox}/Gear.SLDPRT", "gear");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        const string Gear = Gearbox + "/Gear.SLDPRT";
        Assert.True((await t.B.CheckOutAsync(Gear)).Ok);
        // Open in SolidWorks on Maria's computer, saving through its own handle.
        t.B.Disk.Hold(Gear);
        t.B.Save(Gear, "Maria's gear 1");
        await t.B.SyncAsync();
        await mentor.SyncAsync();
        var gear = await t.FileId("Gear.SLDPRT");

        var renamed = await mentor.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gearbox v2");
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("Renamed Gearbox to Gearbox v2. Check outs stay as they were: Maria Lopez has 1 of its files checked out.", renamed.Message);
        Assert.Equal(Maria, await t.Holder(gear));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where folder='Drivetrain/Gearbox v2' and deleted_at is null"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where kind='folder_renamed' and (payload->>'over_checkouts')::int = 1"));
        Assert.Equal("housing", mentor.Text("Robot 2027/Drivetrain/Gearbox v2/Housing.SLDPRT"));

        // While SolidWorks has it, Maria's Gear stays where SolidWorks has it, and every save there
        // is kept; Housing (closed, not hers) moves at once.
        const string Moved = "Robot 2027/Drivetrain/Gearbox v2/Gear.SLDPRT";
        await t.B.SyncTimesAsync(2);
        Assert.True(Logged(t.B, "Pina renamed the folder Gearbox to Gearbox v2. Your 1 file in it is still checked out to you."));
        Assert.Equal("housing", t.B.Text("Robot 2027/Drivetrain/Gearbox v2/Housing.SLDPRT"));
        Assert.Equal("Maria's gear 1", t.B.Text(Gear));
        Assert.Null(t.B.Read(Moved));
        Assert.Contains(t.B.Engine.View.Notices, n => n.Kind == NoticeKinds.NewerWaiting && n.Detail.Contains("Close Gear.SLDPRT to finish moving it.", StringComparison.Ordinal));
        t.B.Save(Gear, "Maria's gear 2");
        await t.B.SyncTimesAsync(2);
        Assert.Equal("Maria's gear 2", t.B.Text(Gear));
        // Never read while SolidWorks holds it (feedback N4), so it goes up once it lets go.
        Assert.False(t.World.HashOnServer(Hash("Maria's gear 2")));

        // SolidWorks lets go: the file moves with its last save, still hers, still writable.
        t.B.Disk.Unhold(Gear);
        await t.B.SyncTimesAsync(2);
        Assert.Null(t.B.Read(Gear));
        Assert.Equal("Maria's gear 2", t.B.Text(Moved));
        Assert.False(t.B.Disk.IsReadOnly(Moved));
        Assert.True(t.World.HashOnServer(Hash("Maria's gear 2")));
        Assert.Equal(Maria, await t.Holder(gear));
        Assert.True((await t.B.CheckInAsync(Moved)).Ok);
        Assert.Equal(Hash("Maria's gear 2"), await t.CurrentHash(gear));
        Assert.Empty(await t.SideAuthors(gear).ContinueWith(s => s.Result.Where(a => a.Contains("lock broken", StringComparison.Ordinal)).ToList()));
        await mentor.SyncAsync();
        Assert.Equal("Maria's gear 2", mentor.Text(Moved));
        NoViolations(t.A, t.B, mentor);
    }

    [PostgresFact]
    public async Task A_lead_moves_a_file_held_on_their_own_other_computer_and_a_student_still_cannot()
    {
        var (t, mentor) = await OrganizeTeamAsync();
        await using var _ = t;
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        await t.A.SyncAsync();
        // A student's rename in Explorer of a file someone else has is put back, as before.
        const string Elsewhere = "Robot 2027/Drivetrain/Plate old.SLDPRT";
        t.A.Disk.ClearReadOnly(Plate);
        File.Move(t.A.Disk.Full(Plate), t.A.Disk.Full(Elsewhere));
        await t.A.SyncTimesAsync(2);
        Assert.Equal("plate", t.A.Text(Plate));
        Assert.Null(t.A.Read(Elsewhere));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where name='Plate.SLDPRT'"));

        // The lead's MoveAsync goes over the check out, no force needed.
        await mentor.SyncAsync();
        const string Target = "Robot 2027/Drivetrain/Plates/Plate.SLDPRT";
        Assert.True(await mentor.Engine.MoveAsync(PortableVaultFileSystem.P(Plate), PortableVaultFileSystem.P(Target)));
        Assert.Equal(Maria, await t.Holder(await t.FileId("Plate.SLDPRT")));
        await t.B.SyncTimesAsync(2);
        Assert.Equal("plate", t.B.Text(Target));
        Assert.True(Logged(t.B, "Pina moved Plate.SLDPRT to Robot 2027 › Drivetrain › Plates. It's still checked out to you."));
        NoViolations(t.A, t.B, mentor);
    }
}
