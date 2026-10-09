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

    private static List<Armory.Telemetry.FlightEvent> Since(Computer c, long sequence) => c.Flight.Snapshot().Where(e => e.Sequence > sequence).ToList();

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
        Assert.Equal("Only a mentor or CAD lead can force a check in.", (await t.B.TakeBackAsync(file)).Message);
        await mentor.SyncAsync();
        var taken = await mentor.TakeBackAsync(file);
        Assert.True(taken.Ok);
        Assert.Equal("Force checked in Plate.SLDPRT from Alex Kim. Anything they hadn't checked in is kept as their own copy.", taken.Message);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal("Plate.SLDPRT isn't checked out.", (await mentor.TakeBackAsync(file)).Message);
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal("v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        var notice = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.TakenBack, notice.Kind);
        Assert.Equal("Plate.SLDPRT was force checked in", notice.Title);
        Assert.Equal(1, await t.Versions(file));
        // The notice's OK dismisses it.
        await t.A.Engine.DismissNoticeAsync(notice.Key);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.Notices);
        NoViolations(t.A);
    }

    // Force check in of many files (Force check in all, the selection bar) is one action: every
    // lock broken with one armory_break_lock each, a few at a time, then ONE pass for all of them.
    // Until 0.3.1 each file was its own action with a whole pass, and a few hundred files took the
    // better part of an hour.
    [PostgresFact]
    public async Task Force_check_in_of_many_files_is_one_action_and_one_pass()
    {
        await using var t = await TeamAsync();
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        const int many = 40;
        for (var i = 0; i < many; i++) t.A.Write($"Robot 2027/Fonts/Font{i:00}.ttf", "font " + i);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Fonts")).Ok);
        t.A.Save("Robot 2027/Fonts/Font07.ttf", "Alex unfinished");
        await mentor.SyncAsync();
        List<Guid> ids = [];
        for (var i = 0; i < many; i++) ids.Add(await t.FileId($"Font{i:00}.ttf"));
        var plate = await t.FileId("Plate.SLDPRT");
        ids.Add(plate); // not checked out: counted, never sent

        var reads = t.World.Supabase.RpcCount("armory_list_changes");
        var took = await mentor.Engine.TakeBackAsync(ids);
        Assert.True(took.Ok);
        Assert.Equal($"Force checked in {many} files. Anything that wasn't checked in is kept as its holder's own copy. 1 file wasn't checked out any more.", took.Message);
        Assert.Equal(many, t.World.Supabase.RpcCount("armory_break_lock"));
        // One pass reads the project's changes once (and its own refresh may read once more);
        // a pass per file would read them at least once per file.
        Assert.InRange(t.World.Supabase.RpcCount("armory_list_changes") - reads, 1, 2);
        foreach (var id in ids) Assert.Equal(0, await t.LiveLocks(id));

        // Asked again from the same view: nothing is checked out, so nothing is sent.
        Assert.Equal("None of those files is checked out by someone else now.", (await mentor.Engine.TakeBackAsync(ids)).Message);
        Assert.Equal(many, t.World.Supabase.RpcCount("armory_break_lock"));
        // A student is refused before anything is sent.
        Assert.Equal("Only a mentor or CAD lead can force a check in.", (await t.B.Engine.TakeBackAsync(ids.Take(2).ToList())).Message);

        // The holder keeps what wasn't checked in, as for one file.
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(ids[7]));
        Assert.Equal("font 7", t.A.Text("Robot 2027/Fonts/Font07.ttf"));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Fonts/Font07.ttf"));
        NoViolations(t.A, mentor);
    }

    // Every other action on many files is one action too: Check out all, Check in all and Undo
    // check out of a whole folder each go in one batch call (armory_lock_files or
    // armory_release_locks, up to 500 files) and one pass, never a pass or a lock call per file.
    [PostgresFact]
    public async Task Check_out_check_in_and_undo_of_many_files_each_take_one_batch_and_one_pass()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        const int many = 120;
        const string Fonts = "Robot 2027/Fonts";
        for (var i = 0; i < many; i++) t.A.Write($"{Fonts}/Font{i:000}.ttf", "font " + i);
        await t.A.SyncAsync();
        var s = t.World.Supabase;
        (int Reads, int Locks, int Releases, int One) Calls() =>
            (s.RpcCount("armory_list_changes"), s.RpcCount("armory_lock_files"), s.RpcCount("armory_release_locks"), s.RpcCount("armory_acquire_lock") + s.RpcCount("armory_release_lock"));
        async Task OneBatchAndOnePass(Func<Task<ActionResult>> action, string said, int locks, int releases)
        {
            var before = Calls();
            Assert.Equal(said, (await action()).Message);
            var after = Calls();
            Assert.InRange(after.Reads - before.Reads, 1, 2);
            Assert.Equal((locks, releases, 0), (after.Locks - before.Locks, after.Releases - before.Releases, after.One - before.One));
        }

        // Each says what it is doing in the window's running lines (a long one shows it is working).
        IReadOnlyList<string> Lines() => [.. t.A.Engine.View.Activity.Log.Select(l => l.Line)];
        await OneBatchAndOnePass(() => t.A.CheckOutAsync(Fonts), $"Checked out {many} files.", 1, 0);
        Assert.Contains($"Asking the server to check out {many} files", Lines());
        Assert.Contains($"Checked out {many} of {many} files", Lines());
        await OneBatchAndOnePass(() => t.A.CheckInAsync(Fonts), $"Checked in {many} files.", 0, 1);
        Assert.Contains($"Checked in {many} of {many} files", Lines());
        await OneBatchAndOnePass(() => t.A.CheckOutAsync(Fonts), $"Checked out {many} files.", 1, 0);
        await OneBatchAndOnePass(() => t.A.UndoCheckOutAsync(Fonts), $"Undid {many} check outs.", 0, 1);
        Assert.Contains($"Undid {many} of {many} files", Lines());
        Assert.InRange(Lines().Count, 1, 40);
        NoViolations(t.A);
    }

    // A pass asks whether its files are open once for all of them, never once per file: on
    // Windows each question was a Restart Manager session (about 28 ms), so a quiet pass over
    // 1,500 files took 40 seconds and every click waited behind it (0.3.1's field reports). And
    // since 0.3.3 only about the files whose plan depends on it (Reconciler.OpenMatters): a quiet
    // pass over synced files asks about none (0.3.2's question about every file took the 10 second
    // Restart Manager budget every pass). Checking out a folder asks once for its files, fresh,
    // before it takes their locks. Open files are still found.
    [PostgresFact]
    public async Task A_pass_and_a_check_out_ask_whether_files_are_open_once_for_all_of_them()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        const int many = 150;
        const string Fonts = "Robot 2027/Fonts";
        for (var i = 0; i < many; i++) t.A.Write($"{Fonts}/Font{i:000}.ttf", "font " + i);
        await t.A.SyncAsync();
        var disk = t.A.Disk;
        (int One, int Batch, int Files) Asked() => (disk.IsOpenCalls, disk.OpenAmongCalls, disk.OpenAmongSizes.Sum());

        var before = Asked();
        await t.A.SyncAsync();
        var quiet = Asked();
        Assert.InRange(quiet.One - before.One, 0, 2);
        Assert.Equal(0, quiet.Files - before.Files);

        t.A.Disk.Open($"{Fonts}/Font007.ttf");
        // The open file is found through the batch: it is the one to open again.
        Assert.Equal($"Checked out {many} files. Close Font007.ttf in SolidWorks and open it again to save changes.", (await t.A.CheckOutAsync(Fonts)).Message);
        var checkedOut = Asked();
        Assert.InRange(checkedOut.One - quiet.One, 0, 4);
        Assert.InRange(checkedOut.Batch - quiet.Batch, 1, 3);
        Assert.All(disk.OpenAmongSizes.Skip(quiet.Batch), size => Assert.InRange(size, 1, many));
        t.A.Disk.Close($"{Fonts}/Font007.ttf");
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
        await t.A.Engine.DismissNoticeAsync(newest.Key);
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
        // Checked out from the window (not "and reopen") while SolidWorks has it open read-only:
        // the answer says how to save, since SolidWorks writes to it only once it is opened again.
        var outBracket = await t.A.CheckOutAsync(Bracket);
        Assert.True(outBracket.Ok);
        Assert.Equal("Checked out Bracket.SLDPRT. Close it in SolidWorks and open it again to save changes.", outBracket.Message);
        Assert.Null(t.A.Engine.View.Prompt);
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

    // v0.2.1: Open on a file the team has that isn't on this computer yet answers at once,
    // downloads it ahead of everything else, and opens it once it is here.
    [PostgresFact]
    public async Task Open_downloads_a_file_first_and_then_opens_it()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        for (var i = 0; i < 60; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT", $"bulk {i}");
        await t.A.SyncTimesAsync(2);
        // Maria's computer knows the files but has none of them (each download is slow).
        t.B.Network.StorageFault = r => r.Method == HttpMethod.Get ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : null;
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate));
        t.B.Network.StorageFault = null;
        t.B.Network.StorageDelay = r => r.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
        t.B.Engine.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await t.B.Engine.LaunchAsync(Plate);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Open answered after {watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("Downloading Plate.SLDPRT, it opens when it is here.", answer.Message);
        Assert.True(SpinWait.SpinUntil(() => t.B.Disk.Launched.Contains(Plate), TimeSpan.FromSeconds(20)), "Plate.SLDPRT never opened");
        Assert.Equal("v1", t.B.Text(Plate));
        // Ahead of the rest: most of the other files were still to come when it opened.
        var others = Directory.Exists(t.B.Disk.Full("Robot 2027/Bulk")) ? Directory.EnumerateFiles(t.B.Disk.Full("Robot 2027/Bulk")).Count() : 0;
        Assert.True(others < 60, $"all {others} other files came first");
        // A file that isn't anywhere is still said plainly.
        Assert.False((await t.B.Engine.LaunchAsync("Robot 2027/Drivetrain/Nothing.SLDPRT")).Ok);
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

    // The connection drops right after a lock was let go (a check in, an undo, a closed add):
    // the file is read-only all the same, offline, after more offline passes and after a restart,
    // so nobody edits a file the team sees as available. And right after a lock was taken, the
    // file is checked out and writable, and the answer says so.
    [PostgresFact]
    public async Task A_file_let_go_just_before_the_connection_drops_is_read_only()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        void CutAt(string point) => t.A.Engine.CrashPoint = p => { if (p == point) t.A.Offline = true; };

        // Check in.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        CutAt("after-release");
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        Assert.Equal((0L, 2L), (await t.LiveLocks(file), await t.Versions(file)));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Equal("Available", t.A.Row(Plate).Checkout.Label);
        await t.A.SyncTimesAsync(2);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Throws<IOException>(() => t.A.Save(Plate, "v3 without a check out"));
        t.A.Restart();
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(t.A.Engine.View.MyFiles);

        // Undo check out.
        t.A.Offline = false;
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        CutAt("after-release");
        Assert.Equal("Undid the check out of Plate.SLDPRT.", (await t.A.UndoCheckOutAsync(Plate)).Message);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));

        // A closed add, checked in by its own pass.
        t.A.Offline = false;
        t.A.Engine.CrashPoint = null;
        const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";
        t.A.Write(Gear, "gear");
        CutAt("after-release");
        await t.A.SyncAsync();
        var gear = await t.FileId("Gear.SLDPRT");
        Assert.Equal((0L, 1L), (await t.LiveLocks(gear), await t.Versions(gear)));
        Assert.True(t.A.Disk.IsReadOnly(Gear));
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Gear));

        // The other way: the lock was taken, then the connection dropped.
        t.A.Offline = false;
        CutAt("after-lock");
        var taken = await t.A.CheckOutAsync(Plate);
        Assert.True(taken.Ok);
        Assert.Equal("Checked out Plate.SLDPRT.", taken.Message);
        Assert.Equal(1, await t.LiveLocks(file));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal("Checked out by you", Assert.Single(t.A.Engine.View.MyFiles).Checkout.Label);
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        t.A.Save(Plate, "v3 while checked out");
        t.A.Engine.CrashPoint = null;
        t.A.Offline = false;
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("v3 while checked out"), await t.CurrentHash(file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        NoViolations(t.A);
    }

    // A rename takes the lock only for itself. Bytes saved without a check out before it (the
    // read-only bit cleared by hand) are kept as a kept copy, never shared, the checked-in
    // version comes back, and the lock is let go: the file is not left checked out to anyone.
    [PostgresFact]
    public async Task A_rename_of_a_file_saved_without_a_check_out_never_shares_those_bytes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        const string Left = "Robot 2027/Drivetrain/Plate-Left.SLDPRT";
        t.A.ForceWrite(Plate, "Alex without a check out");
        var renamed = await t.A.Engine.RenameFileAsync(Plate, "Plate-Left.SLDPRT");
        Assert.True(renamed.Ok);
        Assert.Equal("Renamed Plate.SLDPRT to Plate-Left.SLDPRT.", renamed.Message);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal([Alex + "|changed without a check out"], await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Alex without a check out"))));
        Assert.Equal((1L, Hash("v1")), (await t.Versions(file), await t.CurrentHash(file)));
        Assert.Equal("v1", t.A.Text(Left));
        Assert.True(t.A.Disk.IsReadOnly(Left));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Equal("Available", t.A.Row(Left).Checkout.Label);
        // Maria can check it out; checking it in shares only her bytes.
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Left)).Ok);
        t.B.Save(Left, "v2 by Maria");
        Assert.True((await t.B.CheckInAsync(Left)).Ok);
        Assert.Equal((2L, Hash("v2 by Maria")), (await t.Versions(file), await t.CurrentHash(file)));
        NoViolations(t.A, t.B);
    }

    // A check out while a rename's lock is still held (its release cut off by the connection)
    // makes that lock the check out: the file stays checked out, writable, in My files.
    [PostgresFact]
    public async Task Checking_out_a_file_held_for_a_rename_keeps_it_checked_out()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        var bracket = await t.FileId("Bracket.SLDPRT");
        foreach (var (from, to, id, restart) in new[] { (Plate, "Plate-Left.SLDPRT", plate, false), (Bracket, "Bracket-Left.SLDPRT", bracket, true) })
        {
            t.A.Engine.CrashPoint = p => { if (p == "before-release") t.A.Offline = true; };
            Assert.True((await t.A.Engine.RenameFileAsync(from, to)).Ok);
            Assert.Equal(1, await t.LiveLocks(id)); // the rename's lock, not let go yet
            var path = from[..(from.LastIndexOf('/') + 1)] + to;
            Assert.True(t.A.Disk.IsReadOnly(path));
            if (restart) t.A.Restart();
            t.A.Engine.CrashPoint = null;
            t.A.Offline = false;
            var answer = await t.A.CheckOutAsync(path);
            Assert.True(answer.Ok);
            Assert.Equal($"Checked out {to}.", answer.Message);
            Assert.Equal(1, await t.LiveLocks(id));
            Assert.False(t.A.Disk.IsReadOnly(path));
            Assert.Contains(t.A.Engine.View.MyFiles, f => f.Path == path && f.Checkout.Label == "Checked out by you");
            await t.A.SyncTimesAsync(2);
            Assert.Equal(1, await t.LiveLocks(id)); // a check out now: never let go by itself
            Assert.False(t.A.Disk.IsReadOnly(path));
        }
        NoViolations(t.A);
    }

    // Read-only first, then the lock goes (addendum 6): at the moment of every release the file
    // is already read-only, for a check in, an undo and a closed add. A bit that can't be set
    // keeps the lock until a later pass can set it.
    [PostgresFact]
    public async Task A_file_is_read_only_before_its_lock_is_let_go()
    {
        await using var t = await TeamAsync();
        var seen = new List<(string Path, bool ReadOnly)>();
        string? watching = null;
        void Watch(string path)
        {
            watching = path;
            t.A.Engine.CrashPoint = p => { if (p == "before-release") seen.Add((watching!, t.A.Disk.IsReadOnly(watching!))); };
        }
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        // Check in.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        Watch(Plate);
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        // Undo, with nothing changed (no download puts the bytes back read-only first).
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        Assert.True((await t.A.UndoCheckOutAsync(Plate)).Ok);
        // A closed add.
        const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";
        t.A.Write(Gear, "gear");
        Watch(Gear);
        await t.A.SyncAsync();
        Assert.Equal([(Plate, true), (Plate, true), (Gear, true)], seen);
        Assert.Equal(0, await t.LiveLocks(file));

        // The bit can't be set (say, another program holds the file): the check in commits, and
        // the lock stays with the file writable, until the bit can be set.
        t.A.Engine.CrashPoint = null;
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v3");
        t.A.Disk.RefuseAttribute = path => path == Plate;
        var answer = await t.A.CheckInAsync(Plate);
        Assert.Equal("Armory couldn't finish checking in Plate.SLDPRT yet. It tries again by itself.", answer.Message);
        Assert.Equal(Hash("v3"), await t.CurrentHash(file));
        Assert.Equal(1, await t.LiveLocks(file));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        // In plain words in the window; the raw text only in the log.
        var problem = Assert.Single(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.CantRead && n.Item.Path == Plate);
        Assert.Equal("Armory couldn't make it read-only or writable yet. Close any program that might be using it. Armory tries again by itself.", problem.Item.Detail);
        Assert.Contains(t.A.Logged, line => line.Contains("can't be changed now (test)", StringComparison.Ordinal));
        t.A.Disk.RefuseAttribute = null;
        await t.A.SyncAsync();
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        NoViolations(t.A);
    }

    // A check out that can't happen (offline) cancels nothing: a check in asked for earlier
    // still finishes once the computer is back online.
    [PostgresFact]
    public async Task A_check_out_refused_offline_keeps_a_check_in_that_is_waiting()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        t.A.Offline = true;
        Assert.Equal("You're offline. Plate.SLDPRT is checked in as soon as this computer is back online.", (await t.A.CheckInAsync(Plate)).Message);
        var refused = await t.A.CheckOutAsync("Robot 2027");
        Assert.False(refused.Ok);
        Assert.Equal("You're offline. Files can be checked out once this computer is back online.", refused.Message);
        Assert.Equal("Checks in when this computer is back online.", Assert.Single(t.A.Engine.View.MyFiles).Note);
        t.A.Offline = false;
        await t.A.SyncTimesAsync(2);
        Assert.Equal((2L, 0L), (await t.Versions(file), await t.LiveLocks(file)));
        Assert.Equal(Hash("v2"), await t.CurrentHash(file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(t.A.Engine.View.MyFiles);
        // Online, checking out a file whose check in is still waiting keeps it checked out.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v3");
        t.A.Offline = true;
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        t.A.Offline = false;
        var kept = await t.A.CheckOutAsync(Plate);
        Assert.Equal("Plate.SLDPRT is already checked out by you.", kept.Message);
        await t.A.SyncAsync();
        Assert.Equal((2L, 1L), (await t.Versions(file), await t.LiveLocks(file)));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        NoViolations(t.A);
    }

    // A second mentor or CAD lead taking back from an older view is told the truth, never an error.
    [PostgresFact]
    public async Task A_take_back_from_an_older_view_says_it_is_not_checked_out_any_more()
    {
        await using var t = await TeamAsync();
        const string Sam = "sam.lee@students.test";
        await t.Mentor.Api.AddMemberAsync(t.Project, Sam, Armory.Client.MemberRole.CadLead, Guid.NewGuid());
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        var lead = await t.World.ComputerAsync("CAD lead PC", Sam);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await mentor.SyncAsync();
        await lead.SyncAsync();
        Assert.True((await mentor.TakeBackAsync(file)).Ok);
        var late = await lead.TakeBackAsync(file);
        Assert.False(late.Ok);
        Assert.Equal("Plate.SLDPRT isn't checked out any more.", late.Message);
        Assert.Equal(0, await t.LiveLocks(file));
    }

    // A file whose add is still being sent is not renamed under it: the add would be sent again
    // under the old name and undo the rename.
    [PostgresFact]
    public async Task A_file_being_added_is_renamed_once_it_is_in_Armory()
    {
        await using var t = await TeamAsync();
        const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";
        t.A.Write(Gear, "gear");
        t.A.Engine.CrashPoint = p => { if (p == "before-create") t.A.Offline = true; };
        await t.A.SyncAsync();
        t.A.Engine.CrashPoint = null;
        var refused = await t.A.Engine.RenameFileAsync(Gear, "Gear2.SLDPRT");
        Assert.False(refused.Ok);
        Assert.Equal("Armory is still adding Gear.SLDPRT. Try again in a moment.", refused.Message);
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.True((await t.A.Engine.RenameFileAsync(Gear, "Gear2.SLDPRT")).Ok);
        await t.A.SyncAsync();
        var file = await t.FileId("Gear2.SLDPRT");
        Assert.Equal("gear", t.A.Text("Robot 2027/Drivetrain/Gear2.SLDPRT"));
        Assert.Null(t.A.Read(Gear));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        Assert.Equal(0, await t.LiveLocks(file));
    }

    // The rest of a folder check out names where it is: my other computer, or the people who
    // have it (up to three by name).
    [PostgresFact]
    public async Task A_folder_check_out_names_my_other_computer()
    {
        await using var t = await TeamAsync(secondDeviceForAlex: true);
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        var partial = await t.B.CheckOutAsync("Robot 2027/Drivetrain");
        Assert.True(partial.Ok);
        Assert.Equal("Checked out 1 of 2 files. 1 is checked out on your other computer, student A laptop.", partial.Message);
    }

    // A check in still waiting when a mentor took the file back is over; checking the file out
    // again is a new check out that is never checked in by itself.
    [PostgresFact]
    public async Task A_check_out_after_a_take_back_is_never_checked_in_by_itself()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2 on the bus");
        t.A.Offline = true;
        Assert.True((await t.A.CheckInAsync(Plate)).Ok); // waits for the connection
        Assert.True(await t.Mentor.Api.BreakLockAsync(file, t.Mentor.Device, Guid.NewGuid()));
        t.A.Offline = false;
        var again = await t.A.CheckOutAsync(Plate);
        Assert.True(again.Ok);
        Assert.Equal("Checked out Plate.SLDPRT.", again.Message);
        await t.A.SyncTimesAsync(2);
        Assert.Equal((1L, 1L), (await t.Versions(file), await t.LiveLocks(file)));
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file)); // the bytes the check in would have shared are kept
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal("Checked out by you", Assert.Single(t.A.Engine.View.MyFiles).Checkout.Label);
        NoViolations(t.A);
    }

    // ---- Check in when closed (feedback N4) --------------------------------------------------

    // Feedback N4, as it happened on IDEA-06 in 0.3.1: SolidWorks holds a checked-out part open
    // for writing, the student saves and clicks Check in. The scan can't read the part and kept
    // its old hash, so the check in let the lock go with nothing uploaded ("Checked in
    // Plate.SLDPRT."), SolidWorks went on saving through its handle, and once it closed, the
    // saves were put aside as a kept copy and the old shared version was written over them. This
    // failed before 0.3.3 (disk 'v1', server still v1) and passes since.
    [PostgresFact]
    public async Task Check_in_while_SolidWorks_holds_the_file_never_loses_the_saved_edits()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        t.A.Disk.Hold(Plate);
        t.A.Save(Plate, "Alex's edits");
        await t.A.SyncAsync(); // a loop pass while it is open
        var answer = await t.A.CheckInAsync(Plate);
        await t.A.SyncAsync(); // a loop pass, still open
        t.A.Disk.Unhold(Plate);
        await t.A.SyncAsync();
        var summary = $"answer '{answer.Message}', disk '{t.A.Text(Plate)}', kept copies [{string.Join("; ", await t.SideAuthors(file))}]";
        // The edits are never replaced on disk by the older shared version...
        Assert.True(t.A.Text(Plate) == "Alex's edits" || await t.CurrentHash(file) == Hash("Alex's edits"), summary);
        // ...and "Checked in" is only said when the edits are the shared version.
        if (answer.Ok && answer.Message.StartsWith("Checked in", StringComparison.Ordinal))
            Assert.True(await t.CurrentHash(file) == Hash("Alex's edits"), summary);
        Assert.Equal(Hash("Alex's edits"), await t.CurrentHash(file));
        Assert.DoesNotContain(Alex + "|changed without a check out", await t.SideAuthors(file));
        NoViolations(t.A);
    }

    // The check in waits while SolidWorks has the part: the lock stays, the file stays writable
    // (SolidWorks can go on saving), the window says so in one sentence and shows it as
    // checking in when closed, and the first pass after it is closed shares the last save and
    // lets the lock go. Nothing is ever said to be checked in before it is.
    [PostgresFact]
    public async Task A_check_in_while_SolidWorks_holds_the_file_waits_and_then_shares_the_saved_edits()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Disk.Hold(Plate);
        t.A.Save(Plate, "edit 1");
        await t.A.SyncAsync();
        var before = t.A.Flight.Recorded;
        var answer = await t.A.CheckInAsync(Plate);
        Assert.True(answer.Ok);
        Assert.Equal("Plate.SLDPRT is open in SolidWorks. Save it there and close it; Armory checks it in as soon as it's closed.", answer.Message);
        Assert.Equal((1L, 1L, Alex), (await t.Versions(file), await t.LiveLocks(file), await t.Holder(file)));
        Assert.False(t.A.Disk.IsReadOnly(Plate)); // SolidWorks can go on saving
        var mine = Assert.Single(t.A.Engine.View.MyFiles);
        Assert.Equal((FileStatuses.CheckingInWhenClosed, "Checks in as soon as you close it in SolidWorks."), (mine.Status, mine.Note));
        Assert.Equal(FileStatuses.CheckingInWhenClosed, t.A.Row(Plate).Status);
        // More saves while it is open, and loop passes: still waiting, nothing shared, nothing kept aside.
        t.A.Save(Plate, "edit 2");
        await t.A.SyncTimesAsync(2);
        Assert.Equal((1L, 1L), (await t.Versions(file), await t.LiveLocks(file)));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Single(Since(t.A, before), e => e.Kind == Armory.Telemetry.FlightKind.Note && e.Name == "checkInWaits" && e.Detail == Plate + ": open");
        Assert.DoesNotContain(Since(t.A, before), e => e.Kind == Armory.Telemetry.FlightKind.ReadOnlyBroken);
        // Closed: the next pass shares the last save, then lets go.
        t.A.Disk.Unhold(Plate);
        await t.A.SyncAsync();
        Assert.Equal((2L, 0L), (await t.Versions(file), await t.LiveLocks(file)));
        Assert.Equal(Hash("edit 2"), await t.CurrentHash(file));
        Assert.Equal("edit 2", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Equal(FileStatuses.Synced, t.A.Row(Plate).Status);
        Assert.DoesNotContain(await t.SideAuthors(file), s => s.EndsWith("|changed without a check out", StringComparison.Ordinal));
        await t.B.SyncAsync();
        Assert.Equal("edit 2", t.B.Text(Plate));
        NoViolations(t.A, t.B);
    }

    // Check in all (or a folder): the closed files are checked in at once, in one batch; the ones
    // SolidWorks has open wait, each checked in by the first pass after it is closed. Words say
    // exactly how many of each.
    [PostgresFact]
    public async Task Check_in_all_checks_in_closed_files_and_waits_for_open_ones()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        const string Parts = "Robot 2027/Intake";
        string[] parts = [.. Enumerable.Range(1, 5).Select(i => $"{Parts}/Roller{i}.SLDPRT")];
        foreach (var part in parts) t.A.Write(part, "v1 " + part);
        await t.A.SyncAsync();
        Assert.Equal("Checked out 5 files.", (await t.A.CheckOutAsync(Parts)).Message);
        // Three saved and closed; two saved while SolidWorks still has them open.
        foreach (var part in parts[..3]) t.A.Save(part, "edit " + part);
        foreach (var part in parts[3..])
        {
            t.A.Disk.Hold(part);
            t.A.Save(part, "edit " + part);
        }
        var answer = await t.A.CheckInAsync(Parts);
        Assert.True(answer.Ok);
        Assert.Equal("Checked in 3 of 5 files. 2 are open in SolidWorks: Armory checks them in as you close them.", answer.Message);
        foreach (var part in parts)
        {
            var id = await t.FileId(part[(part.LastIndexOf('/') + 1)..]);
            var open = Array.IndexOf(parts, part) >= 3;
            Assert.Equal((open ? Hash("v1 " + part) : Hash("edit " + part), open ? 1L : 0L), (await t.CurrentHash(id), await t.LiveLocks(id)));
            Assert.Equal(!open, t.A.Disk.IsReadOnly(part));
        }
        Assert.Equal([FileStatuses.CheckingInWhenClosed, FileStatuses.CheckingInWhenClosed], t.A.Engine.View.MyFiles.Select(f => f.Status));
        // Each one as it is closed.
        t.A.Disk.Unhold(parts[3]);
        await t.A.SyncAsync();
        Assert.Equal(parts[4], Assert.Single(t.A.Engine.View.MyFiles).Path);
        t.A.Disk.Unhold(parts[4]);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.MyFiles);
        foreach (var part in parts)
        {
            var id = await t.FileId(part[(part.LastIndexOf('/') + 1)..]);
            Assert.Equal((Hash("edit " + part), 0L), (await t.CurrentHash(id), await t.LiveLocks(id)));
            Assert.True(t.A.Disk.IsReadOnly(part));
        }
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions where reason='changed without a check out'"));
        // All of them open: no "Checked in 0 of 2".
        Assert.Equal("Checked out 2 files.", (await t.A.CheckOutAsync(parts[0], parts[1])).Message);
        t.A.Disk.Hold(parts[0]);
        t.A.Disk.Hold(parts[1]);
        Assert.Equal("These 2 files are open in SolidWorks. Save them there and close them; Armory checks each one in as soon as it's closed.",
            (await t.A.CheckInAsync(parts[0], parts[1])).Message);
        t.A.Disk.Unhold(parts[0]);
        t.A.Disk.Unhold(parts[1]);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.MyFiles);
        NoViolations(t.A);
    }

    // A check in never lets the lock go over bytes it could not read at that moment: not over
    // the hash an earlier scan left for a file another program holds (a backup or a virus scan
    // the open-file check does not see), nor when the file can't be read just before the
    // release, nor when it changed since the scan. Each waits, and a later pass shares the bytes
    // first and then lets go.
    [PostgresFact]
    public async Task A_check_in_never_lets_go_over_bytes_it_could_not_read()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");

        // Saved in SolidWorks, closed, and grabbed by another program before Armory read it.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Disk.Hold(Plate);
        t.A.Save(Plate, "saved before the backup grabbed it");
        await t.A.SyncAsync();
        t.A.Disk.Unhold(Plate);
        t.A.Disk.HoldUnreadable(Plate);
        var answer = await t.A.CheckInAsync(Plate);
        Assert.True(answer.Ok);
        Assert.Equal("Armory couldn't read Plate.SLDPRT just now, so it is still checked out by you. Close any program that might be using it; Armory checks it in as soon as it can.", answer.Message);
        await t.A.SyncAsync();
        Assert.Equal((1L, 1L), (await t.Versions(file), await t.LiveLocks(file)));
        var mine = Assert.Single(t.A.Engine.View.MyFiles);
        Assert.Equal((FileStatuses.CheckingInWhenClosed, "Checks in as soon as Armory can read it. Close any program that might be using it."), (mine.Status, mine.Note));
        t.A.Disk.Unhold(Plate);
        await t.A.SyncAsync();
        Assert.Equal((Hash("saved before the backup grabbed it"), 0L), (await t.CurrentHash(file), await t.LiveLocks(file)));
        Assert.True(t.A.Disk.IsReadOnly(Plate));

        // The scan read it, and it could not be read just before the release.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Engine.CrashPoint = p => { if (p == "after-capture") t.A.Disk.HoldUnreadable(Plate); };
        Assert.StartsWith("Armory couldn't read Plate.SLDPRT just now", (await t.A.CheckInAsync(Plate)).Message);
        t.A.Engine.CrashPoint = null;
        Assert.Equal(1L, await t.LiveLocks(file));
        t.A.Disk.Unhold(Plate);
        await t.A.SyncAsync();
        Assert.Equal(0L, await t.LiveLocks(file));

        // Saved between the scan and the release: shared first, then let go.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Engine.CrashPoint = p => { if (p == "after-capture") t.A.Save(Plate, "saved just after the scan"); };
        var late = await t.A.CheckInAsync(Plate);
        t.A.Engine.CrashPoint = null;
        Assert.False(late.Message.StartsWith("Checked in", StringComparison.Ordinal), late.Message);
        Assert.Equal(1L, await t.LiveLocks(file));
        await t.A.SyncAsync();
        Assert.Equal((Hash("saved just after the scan"), 0L), (await t.CurrentHash(file), await t.LiveLocks(file)));
        Assert.Equal("saved just after the scan", t.A.Text(Plate));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions where reason='changed without a check out'"));
        NoViolations(t.A);
    }

    // Undo of a file the scan could not read waits too: its bytes are unknown, so it is never put
    // back over them. Once read, they are kept as a kept copy first (never discarded), then the
    // shared version comes back and the lock goes.
    [PostgresFact]
    public async Task Undo_of_a_file_the_scan_could_not_read_waits()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Disk.Hold(Plate);
        t.A.Save(Plate, "an idea that did not work");
        await t.A.SyncAsync();
        // Open in SolidWorks: refused, as always.
        Assert.Equal("Close Plate.SLDPRT in SolidWorks first.", (await t.A.UndoCheckOutAsync(Plate)).Message);
        // Closed, and grabbed by another program before Armory read it (a loop pass sees it closed).
        t.A.Disk.Unhold(Plate);
        t.A.Disk.HoldUnreadable(Plate);
        await t.A.SyncAsync();
        var answer = await t.A.UndoCheckOutAsync(Plate);
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("Armory couldn't read Plate.SLDPRT just now, so it is still checked out by you. Close any program that might be using it; Armory undoes the check out as soon as it can.", answer.Message);
        await t.A.SyncAsync();
        Assert.Equal("an idea that did not work", t.A.Text(Plate));
        Assert.Equal((1L, 0L), (await t.LiveLocks(file), await t.Sides(file)));
        Assert.Equal("The check out is undone as soon as Armory can read it. Close any program that might be using it.", Assert.Single(t.A.Engine.View.MyFiles).Note);
        t.A.Disk.Unhold(Plate);
        await t.A.SyncAsync();
        Assert.Equal("v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal((0L, 1L), (await t.LiveLocks(file), await t.Versions(file)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h and reason='kept when the check out was undone'",
            ("f", file), ("h", Hash("an idea that did not work"))));
        NoViolations(t.A);
    }

    // ---- Put back on this computer (feedback N4's recovery) ----------------------------------

    // A kept copy of yours goes back on this computer from File detail: the file is checked out
    // first when it isn't (its own bytes saved without a check out kept first), nothing on disk
    // that the server doesn't have is ever replaced, and the copy stays checked out to you until
    // you check it in. Someone else's kept copy is never put back here.
    [PostgresFact]
    public async Task A_kept_copy_of_yours_is_put_back_checked_out_and_shared_only_at_check_in()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        // Abraham's evening: changes that ended up as a kept copy, the shared version back on disk.
        t.A.ForceWrite(Plate, "an hour and a half of work");
        await t.A.SyncAsync();
        Assert.Equal("v1", t.A.Text(Plate));
        var kept = (await t.A.Engine.GetFileDetailAsync(file))!.History.Single(h => h.Kind == HistoryKinds.KeptCopy);
        var version = Guid.Parse(kept.Id);

        // Not checked out: it is checked out first, then the copy goes back.
        var put = await t.A.Engine.PutBackKeptCopyAsync(file, version);
        Assert.True(put.Ok, put.Message);
        Assert.Equal("Put your copy of Plate.SLDPRT back on this computer. It's checked out to you: look at it in SolidWorks, then check it in to share it.", put.Message);
        Assert.Equal("an hour and a half of work", t.A.Text(Plate));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal((Alex, 1L, 1L), (await t.Holder(file), await t.Versions(file), await t.Sides(file)));
        Assert.Equal(FileStatuses.Changed, Assert.Single(t.A.Engine.View.MyFiles).Status);
        // A pass changes nothing: the bytes are on the server already, never kept twice.
        await t.A.SyncAsync();
        Assert.Equal((1L, 1L), (await t.Versions(file), await t.Sides(file)));
        Assert.Equal("Your copy is already Plate.SLDPRT on this computer. It's checked out to you.", (await t.A.Engine.PutBackKeptCopyAsync(file, version)).Message);

        // Checked out with new saves of its own: they are kept first, then replaced.
        t.A.Save(Plate, "a later idea");
        Assert.True((await t.A.Engine.PutBackKeptCopyAsync(file, version)).Ok);
        Assert.Equal("an hour and a half of work", t.A.Text(Plate));
        Assert.Contains(Alex + "|saved while checked out", await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("a later idea"))));

        // Open in SolidWorks: never replaced.
        t.A.Save(Plate, "unsaved elsewhere");
        t.A.Disk.Hold(Plate);
        Assert.Equal("Close Plate.SLDPRT in SolidWorks first, then put your copy back.", (await t.A.Engine.PutBackKeptCopyAsync(file, version)).Message);
        Assert.Equal("unsaved elsewhere", t.A.Text(Plate));
        t.A.Disk.Unhold(Plate);

        // Shared only when checked in.
        Assert.True((await t.A.Engine.PutBackKeptCopyAsync(file, version)).Ok);
        Assert.Equal(1L, await t.Versions(file));
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        Assert.Equal(Hash("an hour and a half of work"), await t.CurrentHash(file));
        await t.B.SyncAsync();
        Assert.Equal("an hour and a half of work", t.B.Text(Plate));

        // Someone else's copy, or one that isn't a kept copy, is refused, and nothing changes.
        t.B.ForceWrite(Plate, "Maria's idea");
        await t.B.SyncAsync();
        var marias = (await t.B.Engine.GetFileDetailAsync(file))!.History.First(h => h.Kind == HistoryKinds.KeptCopy && h.Author == "Maria Lopez");
        await t.A.SyncAsync();
        Assert.Equal("That copy of Plate.SLDPRT is Maria Lopez's. Only your own kept copies can be put back here.",
            (await t.A.Engine.PutBackKeptCopyAsync(file, Guid.Parse(marias.Id))).Message);
        var current = (await t.A.Engine.GetFileDetailAsync(file))!.History.Single(h => h.IsCurrent);
        Assert.Equal("That kept copy isn't in Plate.SLDPRT's history.", (await t.A.Engine.PutBackKeptCopyAsync(file, Guid.Parse(current.Id))).Message);
        // Checked out by someone else: says who, and waits for them.
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        await t.A.SyncAsync();
        Assert.Equal("Plate.SLDPRT is checked out by Maria Lopez on student B lab PC. Your copy can be put back once it's checked in.",
            (await t.A.Engine.PutBackKeptCopyAsync(file, version)).Message);
        Assert.Empty(t.A.Engine.View.MyFiles);
        NoViolations(t.A, t.B);
    }
}
