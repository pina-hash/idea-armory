using System.Text;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The window's v2 actions end to end (docs/agent/ENGINE.md, "Check out"): what each one does
// on the server and the disk, and the one plain sentence it answers with.
public sealed class CheckOutTests
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    private static void NoViolations(params Computer[] computers)
    {
        foreach (var c in computers)
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }

    // Undo check out: refused while open; then the unsent bytes are kept as a kept copy, the
    // shared version comes back, the file is read-only and the lock is let go.
    [PostgresFact]
    public async Task Undo_check_out_keeps_the_changes_and_puts_back_the_checked_in_version()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Open(Plate);
        t.A.Save(Plate, "an idea that did not work");
        var refused = await t.A.UndoCheckOutAsync(Plate);
        Assert.False(refused.Ok);
        Assert.Equal("Close Plate.SLDPRT in SolidWorks first.", refused.Message);
        t.A.Close(Plate);
        var undone = await t.A.UndoCheckOutAsync(Plate);
        Assert.True(undone.Ok);
        Assert.Equal("Undid the check out of Plate.SLDPRT. Your changes are kept as your own copy.", undone.Message);
        Assert.Equal("v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h and reason='kept when the check out was undone'",
            ("f", file), ("h", Hash("an idea that did not work"))));
        var history = (await t.A.Engine.GetFileDetailAsync(file))!.History;
        Assert.Contains(history, h => h.Kind == HistoryKinds.KeptCopy && h.Note == "Kept when the check out was undone");
        Assert.Empty(t.A.Engine.View.MyFiles);
        // Nothing changed: the undo only lets it go.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        Assert.Equal("Undid the check out of Plate.SLDPRT.", (await t.A.UndoCheckOutAsync(Plate)).Message);
        Assert.Equal(1, await t.Sides(file));
        NoViolations(t.A);
    }

    // Check out of a copy that is behind downloads the current version first (D18); of a copy
    // with bytes saved without a check out keeps them as a kept copy and puts the shared
    // version back first; the lock is only ever taken over the shared version.
    [PostgresFact]
    public async Task A_check_out_brings_the_copy_up_to_date_before_it_takes_the_lock()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        // B has not synced since: its copy is behind.
        Assert.Equal("v1", t.B.Text(Plate));
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.Equal("v2", t.B.Text(Plate));
        Assert.False(t.B.Disk.IsReadOnly(Plate));
        Assert.True((await t.B.UndoCheckOutAsync(Plate)).Ok);
        // Saved without a check out: kept, the checked-in version back, then checked out.
        t.B.ForceWrite(Plate, "Maria without a check out");
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.Equal("v2", t.B.Text(Plate));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Maria without a check out"))));
        Assert.Equal(1, await t.LiveLocks(file));
        Assert.Equal(Hash("v2"), await t.CurrentHash(file));
        // Open in SolidWorks with such bytes: it is not checked out until the file is closed.
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        t.B.Open(Plate);
        t.B.ForceWrite(Plate, "Maria again");
        var answer = await t.B.CheckOutAsync(Plate);
        Assert.False(answer.Ok);
        Assert.Equal("Plate.SLDPRT was changed without a check out. Close it in SolidWorks first, then check it out.", answer.Message);
        Assert.Equal(0, await t.LiveLocks(file));
        NoViolations(t.A, t.B);
    }

    // A folder means every file in it; files someone else has stay theirs, and the answer
    // says who has how many.
    [PostgresFact]
    public async Task A_folder_check_out_says_who_has_the_rest()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        t.A.Write("Robot 2027/Intake/Roller.SLDPRT", "roller");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Bracket)).Ok);
        var partial = await t.A.CheckOutAsync("Robot 2027/Drivetrain");
        Assert.True(partial.Ok);
        Assert.Equal("Checked out 1 of 2 files. Maria Lopez has 1 of them checked out.", partial.Message);
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Intake/Roller.SLDPRT"));
        var held = await t.A.CheckOutAsync(Bracket);
        Assert.False(held.Ok);
        Assert.Equal("Bracket.SLDPRT is checked out by Maria Lopez on student B lab PC.", held.Message);
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync("Robot 2027")).Message);
        Assert.Equal("Nothing there is checked out by you.", (await t.A.CheckInAsync("Robot 2027/Intake")).Message);
        Assert.Equal("Checked in Bracket.SLDPRT.", (await t.B.CheckInAsync("Robot 2027/Drivetrain")).Message);
    }

    // Take back (a mentor or CAD lead): the holder's bytes not checked in are kept in the
    // history, the holder sees one notice, and a student can't take a file back.
    [PostgresFact]
    public async Task A_mentor_takes_back_a_check_out_and_nothing_is_lost()
    {
        await using var t = await TeamAsync();
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex unfinished");
        Assert.Equal("Only a mentor or CAD lead can take back a file.", (await t.B.TakeBackAsync(file)).Message);
        await mentor.SyncAsync();
        var taken = await mentor.TakeBackAsync(file);
        Assert.True(taken.Ok);
        Assert.Equal("Took back Plate.SLDPRT from Alex Kim. Anything not checked in is kept in its history.", taken.Message);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal("Plate.SLDPRT isn't checked out.", (await mentor.TakeBackAsync(file)).Message);
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal("v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        var notice = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.TakenBack, notice.Kind);
        Assert.Equal("Plate.SLDPRT was taken back", notice.Title);
        Assert.Equal(1, await t.Versions(file));
        // The notice's OK dismisses it.
        t.A.Engine.DismissNotice(notice.Key);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.Notices);
        NoViolations(t.A);
    }

    // The quiet question: one per open, dismissed for that open only; a file opened again
    // asks again. Check out and open refuses to open a file SolidWorks still has open.
    [PostgresFact]
    public async Task The_check_out_question_asks_once_per_open()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        await t.A.SyncAsync();
        t.A.Clock.Advance(TimeSpan.FromSeconds(5));
        t.A.Open(Bracket);
        await t.A.SyncAsync();
        var newest = Assert.IsType<PromptView>(t.A.Engine.View.Prompt);
        Assert.Equal(Bracket, newest.Path); // the most recent open asks first
        Assert.StartsWith("prompt:" + Bracket + ":", newest.Key, StringComparison.Ordinal);
        Assert.Equal([Bracket, Plate], t.A.Engine.OpenWithoutCheckOut);
        t.A.Engine.DismissNotice(newest.Key);
        await t.A.SyncAsync();
        Assert.Equal(Plate, t.A.Engine.View.Prompt!.Path);
        // Check out and reopen while SolidWorks still has it open: checked out, not opened.
        var answer = await t.A.CheckOutAndOpenAsync(Plate);
        Assert.True(answer.Ok);
        Assert.Equal("Checked out Plate.SLDPRT. Close Plate.SLDPRT in SolidWorks first, then open it again.", answer.Message);
        Assert.DoesNotContain(Plate, t.A.Disk.Launched);
        Assert.Null(t.A.Engine.View.Prompt); // the dismissed one stays dismissed; Plate is checked out
        t.A.Close(Bracket);
        await t.A.SyncAsync();
        t.A.Clock.Advance(TimeSpan.FromSeconds(5));
        t.A.Open(Bracket);
        await t.A.SyncAsync();
        Assert.Equal(Bracket, t.A.Engine.View.Prompt!.Path); // opened again: asks again
        Assert.NotEqual(newest.Key, t.A.Engine.View.Prompt!.Key);
    }

    // Open launches the file's own program and refuses programs and scripts (D14).
    [PostgresFact]
    public async Task Open_starts_the_file_and_never_a_program()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write("Robot 2027/Tools/setup.exe", "MZ");
        await t.A.SyncAsync();
        Assert.Equal("Opening Plate.SLDPRT.", (await t.A.Engine.LaunchAsync(Plate)).Message);
        Assert.Contains(Plate, t.A.Disk.Launched);
        var refused = await t.A.Engine.LaunchAsync("Robot 2027/Tools/setup.exe");
        Assert.False(refused.Ok);
        Assert.DoesNotContain("Robot 2027/Tools/setup.exe", t.A.Disk.Launched);
    }

    // A file that shares a name is renamed in the app, then added under its new name; a file
    // in Armory is renamed for everyone, and not while someone else has it checked out.
    [PostgresFact]
    public async Task Rename_adds_a_file_that_shares_a_name_and_moves_one_in_Armory()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "Alex's plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        const string Copy = "Robot 2027/Pack/Plate.SLDPRT";
        t.B.Write(Copy, "Maria's own plate");
        await t.B.SyncAsync();
        var shared = t.B.Card(NoticeKinds.NameShared)!;
        Assert.Equal("1 file shares a name with another file in this project", shared.Title);
        var item = Assert.Single(shared.Items);
        Assert.Equal(Copy, item.Path);
        Assert.Equal("Robot 2027 already has Plate.SLDPRT in Drivetrain.", item.Detail);
        Assert.Equal(FileStatuses.NotInArmory, t.B.Row(Copy).Status);
        var renamed = await t.B.Engine.RenameFileAsync(Copy, "Plate-Pack.SLDPRT");
        Assert.True(renamed.Ok);
        Assert.Equal("Renamed Plate.SLDPRT to Plate-Pack.SLDPRT.", renamed.Message);
        Assert.Null(t.B.Card(NoticeKinds.NameShared));
        Assert.Equal(Hash("Maria's own plate"), await t.CurrentHash(await t.FileId("Plate-Pack.SLDPRT")));
        Assert.True(t.B.Disk.IsReadOnly("Robot 2027/Pack/Plate-Pack.SLDPRT"));
        // In Armory: a move for everyone, refused while someone else has it.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await t.B.SyncAsync();
        var refused = await t.B.Engine.RenameFileAsync(Plate, "Plate-Left.SLDPRT");
        Assert.False(refused.Ok);
        Assert.Equal("Alex Kim on student A laptop has Plate.SLDPRT checked out, so it can't be renamed now.", refused.Message);
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.True((await t.B.Engine.RenameFileAsync(Plate, "Plate-Left.SLDPRT")).Ok);
        var file = await t.FileId("Plate-Left.SLDPRT");
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal("Alex's plate", t.B.Text("Robot 2027/Drivetrain/Plate-Left.SLDPRT"));
        await t.A.SyncAsync();
        Assert.Equal("Alex's plate", t.A.Text("Robot 2027/Drivetrain/Plate-Left.SLDPRT"));
        Assert.Null(t.A.Read(Plate));
        NoViolations(t.A, t.B);
    }

    // A check in asked for offline waits, and finishes when the computer is back online; the
    // file stays read-only meanwhile, so nothing more is saved into it.
    [PostgresFact]
    public async Task A_check_in_asked_for_offline_finishes_when_back_online()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Offline = true;
        t.A.Save(Plate, "v2 on the bus");
        var queued = await t.A.CheckInAsync(Plate);
        Assert.True(queued.Ok);
        Assert.Equal("You're offline. Plate.SLDPRT is checked in as soon as this computer is back online.", queued.Message);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal("Checks in when this computer is back online.", Assert.Single(t.A.Engine.View.MyFiles).Note);
        Assert.Equal("1 file is waiting to upload. It uploads when this computer is back online.", t.A.Engine.View.Activity.Waiting!.Line);
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("v2 on the bus"), await t.CurrentHash(file));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Equal(0, await t.Sides(file));
    }
}
