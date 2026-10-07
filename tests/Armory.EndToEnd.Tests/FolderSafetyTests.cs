using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The second E2 review (safety and product lenses): folders never remove the team's work, never
// lose a save and never mix one file's record into another's, whatever stops the engine, however
// a student moves folders and whatever the team does meanwhile. Each test is one class of problem
// the review found, replayed as the review replayed it (its probe named in the comment).
public sealed class FolderSafetyTests
{
    private const string Drivetrain = "Robot 2027/Drivetrain";
    private const string Gearbox = "Robot 2027/Drivetrain/Gearbox";
    private const string Gears = "Robot 2027/Drivetrain/Gears";
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    private static Task<long> Changes(Team t, string kind) => t.World.CountAsync("select count(*) from armory_change_feed where project_id=@p and kind=@k", ("p", t.Project), ("k", kind));
    private static Task<List<(Guid Id, string Folder, string Name)>> LiveFiles(Team t, Guid? project = null) =>
        t.World.QueryAsync("select id, folder, name from armory_files where project_id=@p and deleted_at is null order by folder, name",
            r => (r.GetGuid(0), r.GetString(1), r.GetString(2)), ("p", project ?? t.Project));
    private static bool FolderExists(Computer c, string folder) => Directory.Exists(c.Disk.Full(folder));
    private static int Rpc(Team t, string function) => t.World.Supabase.RpcCount(function);

    // Two parts in Gearbox, on both computers.
    private static async Task<Team> GearboxTeamAsync()
    {
        var t = await TeamAsync();
        t.A.Write($"{Gearbox}/Housing.SLDPRT", "housing v1");
        t.A.Write($"{Gearbox}/Gear.SLDPRT", "gear v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        return t;
    }

    private static void CrashAt(Computer c, string point)
    {
        c.CrashPoint = p => { if (p == point) throw new SimulatedCrash(p); };
        c.Restart();
    }

    private static void Recover(Computer c)
    {
        c.CrashPoint = null;
        c.Restart();
    }

    // P18. Maria's computer stops right after it moved Gearbox to Gears for Alex's rename, before it
    // wrote the move down. The next start finishes the move from the disk: nothing is removed for
    // the team, nothing goes to recovery, nothing is downloaded again, and nothing "shares a name".
    [PostgresFact]
    public async Task A_stop_right_after_the_team_folder_move_here_removes_nothing()
    {
        await using var t = await GearboxTeamAsync();
        t.A.RenameFolder(Gearbox, Gears);
        await t.A.SyncAsync();
        Assert.Equal(1, Rpc(t, "armory_rename_folder"));
        var replaces = t.B.Disk.Replaces;
        CrashAt(t.B, "after-team-folder-move");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.B.SyncAsync());
        Assert.True(FolderExists(t.B, Gears)); // the move happened; its record did not
        Recover(t.B);
        await t.B.SyncTimesAsync(3);
        await t.A.SyncTimesAsync(2);

        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        Assert.Equal(0, Rpc(t, "armory_delete_folder"));
        Assert.Equal(["Drivetrain/Gears|Gear.SLDPRT", "Drivetrain/Gears|Housing.SLDPRT"], (await LiveFiles(t)).Select(f => f.Folder + "|" + f.Name));
        foreach (var c in new[] { t.A, t.B })
        {
            Assert.Equal("housing v1", c.Text($"{Gears}/Housing.SLDPRT"));
            Assert.Equal("gear v1", c.Text($"{Gears}/Gear.SLDPRT"));
            Assert.False(FolderExists(c, Gearbox));
            Assert.Empty(c.Disk.Recovered);
            Assert.Empty(c.Engine.View.Notices);
            Assert.True(c.Disk.IsReadOnly($"{Gears}/Housing.SLDPRT"));
        }
        Assert.Equal(replaces, t.B.Disk.Replaces);
        NoViolations(t.A); NoViolations(t.B);
    }

    // P19. The window's Rename folder: the server renamed it, then the computer stopped right after
    // its own folder moved. Nothing is removed for the team; the rename is one change.
    [PostgresFact]
    public async Task A_stop_right_after_the_window_renames_a_folder_here_removes_nothing()
    {
        await using var t = await GearboxTeamAsync();
        CrashAt(t.A, "after-app-folder-move");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gears"));
        Recover(t.A);
        await t.A.SyncTimesAsync(3);
        await t.B.SyncTimesAsync(2);

        Assert.Equal(1, Rpc(t, "armory_rename_folder"));
        Assert.Equal(1, await Changes(t, "folder_renamed"));
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        Assert.Equal(0, Rpc(t, "armory_move_file"));
        Assert.Equal(["Drivetrain/Gears|Gear.SLDPRT", "Drivetrain/Gears|Housing.SLDPRT"], (await LiveFiles(t)).Select(f => f.Folder + "|" + f.Name));
        foreach (var c in new[] { t.A, t.B })
        {
            Assert.Equal("housing v1", c.Text($"{Gears}/Housing.SLDPRT"));
            Assert.False(FolderExists(c, Gearbox));
            Assert.Empty(c.Disk.Recovered);
            Assert.Empty(c.Engine.View.Notices);
        }
        NoViolations(t.A); NoViolations(t.B);
    }

    // P17. The project is renamed on the site; Maria's computer stops right after it moved the
    // project's folder. On the next start the folder is the project's, nothing is made again or
    // downloaded, and her newer save of the file she has checked out is kept.
    [PostgresFact]
    public async Task A_stop_right_after_a_project_folder_moves_here_downloads_nothing()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Bracket)).Ok);
        t.B.Save(Bracket, "bracket, Maria's first save");
        var (creates, replaces, gets) = (Rpc(t, "armory_create_file"), t.B.Disk.Replaces, t.B.Network.StorageGets);
        Assert.True(await t.Mentor.Api.RenameProjectAsync(t.Project, "Robot 2028", Guid.NewGuid()));
        CrashAt(t.B, "after-project-folder-move");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.B.SyncAsync());
        Recover(t.B);
        const string moved = "Robot 2028/Drivetrain/Bracket.SLDPRT";
        t.B.Save(moved, "bracket, Maria's newer save");
        await t.B.SyncTimesAsync(2);

        Assert.False(FolderExists(t.B, "Robot 2027"));
        Assert.Equal("plate", t.B.Text("Robot 2028/Drivetrain/Plate.SLDPRT"));
        Assert.Equal((replaces, gets), (t.B.Disk.Replaces, t.B.Network.StorageGets));
        Assert.Equal(creates, Rpc(t, "armory_create_file"));
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        Assert.True(t.World.HashOnServer(Hash("bracket, Maria's first save")));
        Assert.True(t.World.HashOnServer(Hash("bracket, Maria's newer save")));
        Assert.Empty(t.B.Engine.View.Notices);
        Assert.Equal(moved, Assert.Single(t.B.Engine.View.MyFiles).Path);
        NoViolations(t.B);
    }

    // A stop right after a folder went back where it was (a refused rename), and right after a
    // project folder renamed in Explorer went back: the next start knows the folder is back, says
    // so once, and removes, adds and downloads nothing.
    [PostgresFact]
    public async Task A_stop_right_after_a_folder_is_put_back_keeps_its_records()
    {
        await using var t = await GearboxTeamAsync();
        Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Gear.SLDPRT")).Ok);
        var (creates, replaces) = (Rpc(t, "armory_create_file"), t.A.Disk.Replaces);
        t.A.RenameFolder(Gearbox, Gears);
        CrashAt(t.A, "after-putBack-folder-move");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
        Recover(t.A);
        await t.A.SyncTimesAsync(2);
        Assert.True(FolderExists(t.A, Gearbox));
        Assert.False(FolderExists(t.A, Gears));
        Assert.Equal("housing v1", t.A.Text($"{Gearbox}/Housing.SLDPRT"));
        Assert.Equal("Gearbox was put back: Maria Lopez has 1 of its files checked out.", Assert.Single(t.A.Engine.View.Notices).Title);
        Assert.All(await LiveFiles(t), f => Assert.Equal("Drivetrain/Gearbox", f.Folder));

        t.A.Engine.DismissNotice(t.A.Card(NoticeKinds.FolderPutBack)!.Key);
        t.A.RenameFolder("Robot 2027", "Robot X");
        CrashAt(t.A, "after-projectPutBack-folder-move");
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
        Recover(t.A);
        await t.A.SyncTimesAsync(2);
        Assert.True(FolderExists(t.A, "Robot 2027"));
        Assert.False(FolderExists(t.A, "Robot X"));
        Assert.Equal("The Robot 2027 folder was renamed back", Assert.Single(t.A.Engine.View.Notices).Title);

        Assert.Equal(1, Rpc(t, "armory_rename_folder"));
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        Assert.Equal(0, Rpc(t, "armory_delete_folder"));
        Assert.Equal((creates, replaces), (Rpc(t, "armory_create_file"), t.A.Disk.Replaces));
        Assert.Equal(2, (await LiveFiles(t)).Count);
        Assert.True(t.A.Disk.IsReadOnly($"{Gearbox}/Housing.SLDPRT"));
        NoViolations(t.A);
    }

    // The two named crash points of the folder calls themselves: a stop before or right after
    // armory_rename_folder or armory_delete_folder replays to exactly one change, with no call per
    // file and the folders as they should be.
    [PostgresFact]
    public async Task A_stop_before_or_after_a_folder_call_replays_to_one_change()
    {
        foreach (var point in new[] { "before-folder", "after-folder" })
        {
            await using (var t = await GearboxTeamAsync())
            {
                t.A.RenameFolder(Gearbox, Gears);
                CrashAt(t.A, point);
                await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
                Recover(t.A);
                await t.A.SyncTimesAsync(2);
                await t.B.SyncTimesAsync(2);
                Assert.Equal(1, Rpc(t, "armory_rename_folder"));
                Assert.Equal(1, await Changes(t, "folder_renamed"));
                Assert.Equal(0, Rpc(t, "armory_move_file") + Rpc(t, "armory_tombstone"));
                Assert.All(await LiveFiles(t), f => Assert.Equal("Drivetrain/Gears", f.Folder));
                foreach (var c in new[] { t.A, t.B })
                {
                    Assert.Equal("housing v1", c.Text($"{Gears}/Housing.SLDPRT"));
                    Assert.False(FolderExists(c, Gearbox));
                    Assert.Empty(c.Engine.View.Notices);
                }
            }
            await using (var t = await GearboxTeamAsync())
            {
                t.A.Write(Plate, "plate");
                await t.A.SyncAsync();
                Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
                await t.A.SyncAsync();
                CrashAt(t.A, point);
                await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
                Recover(t.A);
                await t.A.SyncTimesAsync(3);
                await t.B.SyncTimesAsync(2);
                Assert.Equal(1, Rpc(t, "armory_delete_folder"));
                Assert.Equal(1, await Changes(t, "folder_deleted"));
                Assert.Equal(0, Rpc(t, "armory_tombstone"));
                Assert.Equal(["Plate.SLDPRT"], (await LiveFiles(t)).Select(f => f.Name));
                foreach (var c in new[] { t.A, t.B })
                {
                    Assert.False(FolderExists(c, Gearbox));
                    Assert.Empty(c.Engine.View.Notices);
                }
                Assert.Equal(2, t.B.Disk.Recovered.Count);
            }
        }
    }

    // P6, P6b and the product review's replay: Alex deletes Gearbox while his computer is off or
    // between passes; meanwhile Maria checks in a newer Housing and adds a part there. The folder is
    // never removed over that work: it comes back on Alex's computer with her newer files and one
    // notice naming her. Deleted again once Alex has what the team has, it goes in one call.
    [PostgresFact]
    public async Task A_folder_deleted_over_newer_work_is_put_back_and_removes_nothing()
    {
        await using (var t = await GearboxTeamAsync())
        {
            Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
            Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Housing.SLDPRT")).Ok);
            t.B.Save($"{Gearbox}/Housing.SLDPRT", "housing v2 by Maria");
            Assert.True((await t.B.CheckInAsync($"{Gearbox}/Housing.SLDPRT")).Ok);
            t.B.Write($"{Gearbox}/Motor-Mount.SLDPRT", "Maria's new part");
            await t.B.SyncAsync();
            await t.A.SyncTimesAsync(3);
            await t.B.SyncTimesAsync(2);

            Assert.Equal(0, Rpc(t, "armory_delete_folder"));
            Assert.Equal(0, Rpc(t, "armory_tombstone"));
            Assert.Equal(["Gear.SLDPRT", "Housing.SLDPRT", "Motor-Mount.SLDPRT"], (await LiveFiles(t)).Select(f => f.Name));
            Assert.Equal("housing v2 by Maria", t.A.Text($"{Gearbox}/Housing.SLDPRT"));
            Assert.Equal("gear v1", t.A.Text($"{Gearbox}/Gear.SLDPRT"));
            Assert.Equal("Maria's new part", t.A.Text($"{Gearbox}/Motor-Mount.SLDPRT"));
            var notice = Assert.Single(t.A.Engine.View.Notices);
            Assert.Equal(NoticeKinds.FolderPutBack, notice.Kind);
            Assert.Equal("Gearbox was put back: Maria Lopez has newer work in it.", notice.Title);
            Assert.Empty(t.B.Disk.Recovered);
            Assert.Empty(t.B.Engine.View.Notices);

            // Alex has what the team has now: deleted again, the folder goes in one call.
            Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
            await t.A.SyncTimesAsync(2);
            Assert.Equal(1, Rpc(t, "armory_delete_folder"));
            Assert.Empty(await LiveFiles(t));
            NoViolations(t.A); NoViolations(t.B);
        }
        // Only a part Alex's computer never had (P6).
        await using (var t = await GearboxTeamAsync())
        {
            t.A.Offline = true;
            Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
            await t.A.SyncTimesAsync(2);
            t.B.Write($"{Gearbox}/Maria-New.SLDPRT", "Maria's new part");
            await t.B.SyncAsync();
            t.A.Offline = false;
            await t.A.SyncTimesAsync(2);
            await t.B.SyncTimesAsync(2);
            Assert.Equal(0, Rpc(t, "armory_delete_folder") + Rpc(t, "armory_tombstone"));
            Assert.Equal(3, (await LiveFiles(t)).Count);
            Assert.Equal("Maria's new part", t.A.Text($"{Gearbox}/Maria-New.SLDPRT"));
            Assert.Equal("housing v1", t.A.Text($"{Gearbox}/Housing.SLDPRT"));
            Assert.Equal("Gearbox was put back: Maria Lopez has newer work in it.", t.A.Card(NoticeKinds.FolderPutBack)!.Title);
            Assert.Empty(t.B.Disk.Recovered);
            Assert.Equal("Maria's new part", t.B.Text($"{Gearbox}/Maria-New.SLDPRT"));
            NoViolations(t.A); NoViolations(t.B);
        }
    }

    // P20. Alex drags the project's folder into another project's folder. It goes back to the top
    // (waiting while a file inside is open), and none of its files is ever added to the other project.
    [PostgresFact]
    public async Task A_project_folder_dragged_into_another_project_is_put_back()
    {
        await using var t = await TeamAsync();
        var outreach = await t.Mentor.Api.CreateProjectAsync("Outreach", null, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(outreach, Alex, MemberRole.Student, Guid.NewGuid());
        t.A.Write(Plate, "plate");
        t.A.Write(Bracket, "bracket");
        t.A.Write("Outreach/Booth/Sign.SLDPRT", "sign");
        await t.A.SyncTimesAsync(2);
        var (creates, replaces) = (Rpc(t, "armory_create_file"), t.A.Disk.Replaces);

        t.A.RenameFolder("Robot 2027", "Outreach/Robot 2027");
        t.A.Open("Outreach/Robot 2027/Drivetrain/Plate.SLDPRT");
        await t.A.SyncTimesAsync(2);
        Assert.Equal(["Sign.SLDPRT"], (await LiveFiles(t, outreach)).Select(f => f.Name));
        Assert.False(FolderExists(t.A, "Robot 2027"));
        var waiting = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.ProjectPutBack, waiting.Kind);
        Assert.Equal("The Robot 2027 folder is waiting to be moved back", waiting.Title);
        Assert.Contains("Close Plate.SLDPRT", waiting.Detail, StringComparison.Ordinal);

        t.A.Close("Outreach/Robot 2027/Drivetrain/Plate.SLDPRT");
        await t.A.SyncTimesAsync(2);
        Assert.True(FolderExists(t.A, "Robot 2027"));
        Assert.False(FolderExists(t.A, "Outreach/Robot 2027"));
        Assert.Equal("plate", t.A.Text(Plate));
        Assert.Equal("The Robot 2027 folder was moved back", Assert.Single(t.A.Engine.View.Notices).Title);
        Assert.Equal(["Sign.SLDPRT"], (await LiveFiles(t, outreach)).Select(f => f.Name));
        Assert.Equal(2, (await LiveFiles(t)).Count);
        Assert.Equal((creates, replaces), (Rpc(t, "armory_create_file"), t.A.Disk.Replaces));
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        NoViolations(t.A);
    }

    // P4, P4b and the product review's replays: while a folder waits to be put back (a project
    // folder renamed in Explorer, a refused rename) because a checked-out file inside is open, the
    // student saves that file twice. Both saves are kept and reach the server, and My files shows
    // the file as changed meanwhile, never as missing.
    [PostgresFact]
    public async Task Saves_while_a_folder_waits_to_be_put_back_are_all_kept()
    {
        await using (var t = await TeamAsync())
        {
            t.A.Write(Plate, "plate v1");
            await t.A.SyncAsync();
            Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
            t.A.RenameFolder("Robot 2027", "Robot X");
            const string moved = "Robot X/Drivetrain/Plate.SLDPRT";
            t.A.Open(moved);
            await t.A.SyncAsync();
            t.A.Save(moved, "plate save 1");
            await t.A.SyncAsync();
            t.A.Save(moved, "plate save 2");
            await t.A.SyncAsync();
            var mine = Assert.Single(t.A.Engine.View.MyFiles);
            Assert.Equal((Plate, FileStatuses.Changed), (mine.Path, mine.Status));
            t.A.Close(moved);
            await t.A.SyncTimesAsync(2);
            Assert.True(FolderExists(t.A, "Robot 2027"));
            Assert.True(t.World.HashOnServer(Hash("plate save 1")));
            Assert.True(t.World.HashOnServer(Hash("plate save 2")));
            Assert.True((await t.A.CheckInAsync(Plate)).Ok);
            Assert.Equal(Hash("plate save 2"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
            NoViolations(t.A);
        }
        await using (var t = await GearboxTeamAsync())
        {
            Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Gear.SLDPRT")).Ok);
            Assert.True((await t.A.CheckOutAsync($"{Gearbox}/Housing.SLDPRT")).Ok);
            t.A.RenameFolder(Gearbox, Gears);
            const string moved = $"{Gears}/Housing.SLDPRT";
            t.A.Open(moved);
            await t.A.SyncAsync();
            Assert.Equal("Gearbox goes back once Housing.SLDPRT is closed: Maria Lopez has 1 of its files checked out", t.A.Card(NoticeKinds.FolderPutBack)!.Title);
            t.A.Save(moved, "housing save 1");
            await t.A.SyncAsync();
            t.A.Save(moved, "housing save 2");
            await t.A.SyncAsync();
            Assert.NotEqual(FileStatuses.Synced, Assert.Single(t.A.Engine.View.MyFiles).Status);
            t.A.Close(moved);
            await t.A.SyncTimesAsync(2);
            Assert.True(FolderExists(t.A, Gearbox));
            Assert.Equal("Gearbox was put back: Maria Lopez has 1 of its files checked out.", t.A.Card(NoticeKinds.FolderPutBack)!.Title);
            Assert.True(t.World.HashOnServer(Hash("housing save 1")));
            Assert.True(t.World.HashOnServer(Hash("housing save 2")));
            NoViolations(t.A); NoViolations(t.B);
        }
    }

    // P16. While a project folder waits to be put back, a mentor takes back the file Alex has
    // checked out and open in it. The read-only rule holds there too: the file is read-only at once,
    // so he can't keep saving a file he no longer holds.
    [PostgresFact]
    public async Task A_file_taken_back_while_its_project_folder_waits_is_read_only()
    {
        await using var t = await TeamAsync();
        t.A.Write(Bracket, "bracket v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Bracket)).Ok);
        t.A.RenameFolder("Robot 2027", "Robot X");
        const string moved = "Robot X/Drivetrain/Bracket.SLDPRT";
        t.A.Open(moved);
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(moved));
        t.A.Save(moved, "bracket, Alex's work");
        Assert.True(await t.Mentor.Api.BreakLockAsync(await t.FileId("Bracket.SLDPRT"), t.Mentor.Device, Guid.NewGuid()));
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(moved));
        Assert.Throws<IOException>(() => t.A.Save(moved, "bracket, saved after the take back"));
        t.A.Close(moved);
        await t.A.SyncTimesAsync(2);
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.True(t.World.HashOnServer(Hash("bracket, Alex's work")));
        NoViolations(t.A);
    }

    // P5. Two folders swapped by name while offline (the platform reports Left to "Left (moving)",
    // Right to Left, "Left (moving)" to Right): three renames, sent in order through the temporary
    // name, nothing put back, and a save in one of them kept.
    [PostgresFact]
    public async Task Folders_swapped_offline_go_through_their_temporary_name()
    {
        await using var t = await TeamAsync();
        t.A.Write("Robot 2027/Left/L1.SLDPRT", "l1");
        t.A.Write("Robot 2027/Right/R1.SLDPRT", "r1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Left/L1.SLDPRT")).Ok);
        t.A.Offline = true;
        t.A.RenameFolder("Robot 2027/Left", "Robot 2027/Left (moving)");
        t.A.RenameFolder("Robot 2027/Right", "Robot 2027/Left");
        t.A.RenameFolder("Robot 2027/Left (moving)", "Robot 2027/Right");
        await t.A.SyncAsync();
        t.A.Save("Robot 2027/Right/L1.SLDPRT", "l1, saved after the swap");
        await t.A.SyncAsync();
        t.A.Offline = false;
        await t.A.SyncTimesAsync(2);
        var replaces = t.B.Disk.Replaces;
        await t.B.SyncTimesAsync(2);

        Assert.Equal(3, Rpc(t, "armory_rename_folder"));
        Assert.Equal(0, Rpc(t, "armory_move_file") + Rpc(t, "armory_tombstone"));
        Assert.Equal(["Left|R1.SLDPRT", "Right|L1.SLDPRT"], (await LiveFiles(t)).Select(f => f.Folder + "|" + f.Name));
        Assert.Null(t.A.Card(NoticeKinds.FolderPutBack));
        Assert.True(t.World.HashOnServer(Hash("l1, saved after the swap")));
        Assert.Equal("r1", t.B.Text("Robot 2027/Left/R1.SLDPRT"));
        Assert.Equal("l1", t.B.Text("Robot 2027/Right/L1.SLDPRT"));
        Assert.Equal(replaces, t.B.Disk.Replaces);
        Assert.Empty(t.B.Disk.Recovered);
        NoViolations(t.A); NoViolations(t.B);
    }

    // P2. A folder and a folder inside it renamed before Armory looks: two renames, each worked out
    // from the server as the one before left it. The inner rename is never undone, and a file not
    // yet in Armory stays with its folder.
    [PostgresFact]
    public async Task A_folder_and_a_folder_inside_it_renamed_together_are_two_renames()
    {
        await using var t = await TeamAsync();
        t.A.Write($"{Gearbox}/Housing.SLDPRT", "housing");
        t.A.Write($"{Gearbox}/Shafts/Input.SLDPRT", "input");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.A.Write($"{Gearbox}/Shafts/New.SLDPRT", "new");
        t.A.RenameFolder(Gearbox, Gears);
        t.A.RenameFolder($"{Gears}/Shafts", $"{Gears}/Axles");
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);

        Assert.Equal(2, Rpc(t, "armory_rename_folder"));
        Assert.Equal(0, Rpc(t, "armory_move_file") + Rpc(t, "armory_tombstone"));
        Assert.Equal(["Drivetrain/Gears|Housing.SLDPRT", "Drivetrain/Gears/Axles|Input.SLDPRT", "Drivetrain/Gears/Axles|New.SLDPRT"],
            (await LiveFiles(t)).Select(f => f.Folder + "|" + f.Name));
        foreach (var c in new[] { t.A, t.B })
        {
            Assert.Equal("input", c.Text($"{Gears}/Axles/Input.SLDPRT"));
            Assert.Equal("new", c.Text($"{Gears}/Axles/New.SLDPRT"));
            Assert.False(FolderExists(c, $"{Gears}/Shafts"));
            Assert.False(FolderExists(c, Gearbox));
        }
        Assert.DoesNotContain(t.A.Engine.View.Notices, n => n.Kind != NoticeKinds.Import);
        NoViolations(t.A); NoViolations(t.B);
    }

    // P15b. Alex has Housing checked out with a save not yet sent, deletes Gearbox, and renames
    // Gearbox2 (whose own Housing.SLDPRT shares the name, so it is not in Armory) to Gearbox. The
    // renamed folder never takes over the old Housing's record: it goes back, the unsent save
    // reaches the server as Housing's, and Gearbox2's Housing keeps its own bytes.
    [PostgresFact]
    public async Task A_renamed_folder_never_takes_over_another_files_record()
    {
        await using var t = await GearboxTeamAsync();
        t.A.Write("Robot 2027/Drivetrain/Gearbox2/Housing.SLDPRT", "Gearbox2's own housing");
        await t.A.SyncAsync();
        Assert.Equal(1, t.A.Card(NoticeKinds.NameShared)!.Count);
        Assert.True((await t.A.CheckOutAsync($"{Gearbox}/Housing.SLDPRT")).Ok);
        t.A.Offline = true;
        t.A.Save($"{Gearbox}/Housing.SLDPRT", "housing, Alex's unsent save");
        await t.A.SyncAsync();
        Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
        t.A.RenameFolder("Robot 2027/Drivetrain/Gearbox2", Gearbox);
        await t.A.SyncAsync();
        Assert.True(FolderExists(t.A, "Robot 2027/Drivetrain/Gearbox2"));
        Assert.Equal("Gearbox2 was put back: Gearbox still has files in Armory.", t.A.Card(NoticeKinds.FolderPutBack)!.Title);
        t.A.Offline = false;
        await t.A.SyncTimesAsync(4);

        var housing = await t.FileId("Housing.SLDPRT");
        Assert.Contains(Hash("housing, Alex's unsent save"), await t.World.QueryAsync("select content_sha256 from armory_side_versions where file_id=@f", r => r.GetString(0), ("f", housing)));
        Assert.Equal("Gearbox2's own housing", t.A.Text("Robot 2027/Drivetrain/Gearbox2/Housing.SLDPRT"));
        Assert.Equal(0, Rpc(t, "armory_rename_folder"));
        NoViolations(t.A);
    }

    // P1, the product review's D17 probes: folders a student makes stay, empty or not, made in the
    // app or in File Explorer, at the top of a project or inside a folder Armory knows, after the
    // team empties the folder they are in.
    [PostgresFact]
    public async Task Folders_a_student_makes_stay()
    {
        await using var t = await TeamAsync();
        t.A.Write($"{Gearbox}/Housing.SLDPRT", "housing");
        t.A.Write("Robot 2027/Intake/Roller.SLDPRT", "roller");
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.Engine.CreateFolderAsync(t.Project, "Drivetrain/Gearbox", "Notes")).Ok);
        Assert.True((await t.B.Engine.CreateFolderAsync(t.Project, "Intake", "Rollers")).Ok);
        Directory.CreateDirectory(t.B.Disk.Full($"{Gearbox}/Explorer Notes"));
        Directory.CreateDirectory(t.B.Disk.Full("Robot 2027/Sketches"));
        await t.B.SyncAsync();

        t.A.Delete($"{Gearbox}/Housing.SLDPRT");
        t.A.Delete("Robot 2027/Intake/Roller.SLDPRT");
        await t.A.SyncTimesAsync(2);
        Assert.Equal(["Plate.SLDPRT"], (await LiveFiles(t)).Select(f => f.Name));
        await t.B.SyncTimesAsync(3);
        Assert.Equal(2, t.B.Disk.Recovered.Count);
        foreach (var folder in new[] { $"{Gearbox}/Notes", $"{Gearbox}/Explorer Notes", "Robot 2027/Sketches", "Robot 2027/Intake/Rollers" })
            Assert.True(FolderExists(t.B, folder), folder);
        Assert.DoesNotContain(t.B.Disk.DeletedFolders, f => f.StartsWith("Robot 2027/Drivetrain/Gearbox", StringComparison.Ordinal) || f.StartsWith("Robot 2027/Intake", StringComparison.Ordinal));
        // On Alex's computer, which made none, the emptied folders Armory knew go (D17).
        Assert.False(FolderExists(t.A, Gearbox));
        Assert.False(FolderExists(t.A, "Robot 2027/Intake"));
        NoViolations(t.A); NoViolations(t.B);
    }

    // P3. The window's Rename folder: the server renamed it but the answer was lost. The rename is
    // durable with its own id, so the next pass finishes it in one move here, a file not yet in
    // Armory included: never a split folder, and one change on the server.
    [PostgresFact]
    public async Task A_folder_rename_from_the_window_whose_answer_is_lost_finishes_once()
    {
        await using var t = await GearboxTeamAsync();
        t.A.Write($"{Gearbox}/Sketch.SLDPRT", "a sketch not in Armory yet");
        t.World.Supabase.DropRpcAcknowledgement("armory_rename_folder", Rpc(t, "armory_rename_folder") + 1);
        var answer = await t.A.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gears");
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("You're offline. Armory renames Gearbox to Gears as soon as this computer is back online.", answer.Message);
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);

        Assert.Equal(1, await Changes(t, "folder_renamed"));
        Assert.Equal(0, Rpc(t, "armory_move_file") + Rpc(t, "armory_tombstone"));
        Assert.False(FolderExists(t.A, Gearbox));
        Assert.Equal("a sketch not in Armory yet", t.A.Text($"{Gears}/Sketch.SLDPRT"));
        Assert.Equal(["Gear.SLDPRT", "Housing.SLDPRT", "Sketch.SLDPRT"], (await LiveFiles(t)).Where(f => f.Folder == "Drivetrain/Gears").Select(f => f.Name));
        Assert.Equal(3, (await LiveFiles(t)).Count);
        Assert.Empty(t.A.Engine.View.Notices);
        Assert.Equal("a sketch not in Armory yet", t.B.Text($"{Gears}/Sketch.SLDPRT"));

        // The window's Delete folder, its answer lost too: finished once, by the next pass.
        t.World.Supabase.DropRpcAcknowledgement("armory_delete_folder", Rpc(t, "armory_delete_folder") + 1);
        var deleted = await t.A.Engine.DeleteFolderAsync(t.Project, "Drivetrain/Gears");
        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal("You're offline. Armory deletes Gears as soon as this computer is back online.", deleted.Message);
        await t.A.SyncTimesAsync(3);
        await t.B.SyncTimesAsync(2);
        Assert.Equal(1, await Changes(t, "folder_deleted"));
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        Assert.Empty(await LiveFiles(t));
        Assert.False(FolderExists(t.A, Gears));
        Assert.False(FolderExists(t.B, Gears));
        Assert.Equal(3, t.A.Disk.Recovered.Count);
        Assert.Empty(t.A.Engine.View.Notices);
        NoViolations(t.A); NoViolations(t.B);
    }

    // The product review's cost finding: after a Pack and Go whose parts share names with the
    // project's, the names are looked up in what the pass already read. No armory_create_file goes
    // out for them, on the first pass or any later one, and an idle pass is three calls.
    [PostgresFact]
    public async Task Files_sharing_a_name_cost_no_server_call()
    {
        await using var t = await TeamAsync();
        for (var i = 1; i <= 20; i++) t.B.Write($"Robot 2027/Parts/Part-{i:D3}.SLDPRT", "p" + i);
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        var creates = Rpc(t, "armory_create_file");
        for (var i = 1; i <= 20; i++) t.A.Write($"Robot 2027/Pack/CopyDesignTemp/Part-{i:D3}.SLDPRT", "copy " + i);
        t.A.Write("Robot 2027/Pack/CopyDesignTemp/Unique-1.SLDPRT", "u1");
        t.A.Write("Robot 2027/Pack/CopyDesignTemp/Unique-2.SLDPRT", "u2");
        await t.A.SyncAsync();
        Assert.Equal(2, Rpc(t, "armory_create_file") - creates);
        Assert.Equal(20, t.A.Card(NoticeKinds.NameShared)!.Count);
        for (var pass = 0; pass < 3; pass++)
        {
            var (made, calls) = (Rpc(t, "armory_create_file"), t.A.Network.RpcRequests);
            await t.A.SyncAsync();
            Assert.Equal(made, Rpc(t, "armory_create_file"));
            Assert.InRange(t.A.Network.RpcRequests - calls, 1, 3);
        }
        Assert.Equal(20, t.A.Card(NoticeKinds.NameShared)!.Count);
        Assert.Equal("Added 2 of 22 files to Robot 2027 › Pack", t.A.Card(NoticeKinds.Import)!.Title);
    }

    // The product review's replay 6: the student deletes the unzipped folder after the card about
    // shared names. Nothing is left waiting or retried: no failing calls, nothing pending, no card
    // about files that are gone, and the bytes of the never-added files stay in this computer's
    // safe copies.
    [PostgresFact]
    public async Task Deleting_an_unzipped_folder_leaves_nothing_waiting()
    {
        await using var t = await TeamAsync();
        for (var i = 1; i <= 4; i++) t.B.Write($"Robot 2027/Parts/Bolt-{i}.SLDPRT", "bolt " + i);
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        const string inner = "Robot 2027/Gearbox Pack/CopyDesignTemp";
        for (var i = 1; i <= 12; i++) t.A.Write($"{inner}/Gear-{i:D2}.SLDPRT", "gear " + i);
        for (var i = 1; i <= 4; i++) t.A.Write($"{inner}/Bolt-{i}.SLDPRT", "zip bolt " + i);
        await t.A.SyncAsync();
        Assert.Equal(4, t.A.Card(NoticeKinds.NameShared)!.Count);
        Directory.Delete(t.A.Disk.Full("Robot 2027/Gearbox Pack"), recursive: true);
        var creates = Rpc(t, "armory_create_file");
        for (var pass = 0; pass < 4; pass++)
        {
            await t.A.SyncAsync();
            t.A.Clock.Advance(TimeSpan.FromMinutes(10));
        }
        var view = t.A.Engine.View;
        Assert.Equal(creates, Rpc(t, "armory_create_file"));
        Assert.Equal(1, Rpc(t, "armory_delete_folder"));
        Assert.Equal(0, view.Sync.PendingCount);
        Assert.Equal(SyncStates.Synced, view.Sync.State);
        Assert.Empty(view.Notices);
        Assert.Null(view.Activity.Waiting);
        Assert.Contains(t.A.Snapshots.Enumerate(), s => s.Hash == Hash("zip bolt 1"));
        Assert.Equal(4, (await LiveFiles(t)).Count);
    }

    // The product review's replay 3: a rename refused and a removal refused in one pass, both for
    // Maria's check outs. One card names her in its title and says it in each item.
    [PostgresFact]
    public async Task Two_folders_put_back_in_one_pass_are_one_card_naming_who()
    {
        await using var t = await TeamAsync();
        foreach (var n in new[] { "Housing", "Gear" }) t.A.Write($"{Gearbox}/{n}.SLDPRT", n);
        foreach (var n in new[] { "Roller", "Arm" }) t.A.Write($"Robot 2027/Intake/{n}.SLDPRT", n);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync($"{Gearbox}/Gear.SLDPRT", "Robot 2027/Intake/Arm.SLDPRT")).Ok);
        await t.A.SyncAsync();
        t.A.RenameFolder(Gearbox, Gears);
        Directory.Delete(t.A.Disk.Full("Robot 2027/Intake"), recursive: true);
        await t.A.SyncTimesAsync(2);

        var card = Assert.Single(t.A.Engine.View.Notices);
        Assert.Equal(NoticeKinds.FolderPutBack, card.Kind);
        Assert.Equal(2, card.Count);
        Assert.Equal("2 folders were put back: Maria Lopez has files in them checked out", card.Title);
        Assert.Equal("A folder is renamed or deleted only when nobody else has a file in it checked out. Ask them to check the files in, then try again.", card.Detail);
        Assert.Equal(("Show them", "expand"), (card.Action!.Label, card.Action.Command));
        Assert.All(card.Items, i => Assert.Equal("Maria Lopez has 1 of its files checked out.", i.Detail));
        Assert.Equal(["Gearbox", "Intake"], card.Items.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.True(FolderExists(t.A, Gearbox));
        Assert.Equal("Arm", t.A.Text("Robot 2027/Intake/Arm.SLDPRT"));
        NoViolations(t.A);
    }

    // The product review's replay 3: a Pack and Go unzipped into the wrong project while offline
    // (nothing of it in Armory yet), then dragged into the right one. The move stands, and its files
    // are added to the project they now sit in, never to the one they left.
    [PostgresFact]
    public async Task A_folder_of_new_files_moved_to_another_project_is_added_there()
    {
        await using var t = await TeamAsync();
        var outreach = await t.Mentor.Api.CreateProjectAsync("Outreach", null, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(outreach, Alex, MemberRole.Student, Guid.NewGuid());
        t.A.Write(Plate, "plate");
        t.A.Write("Outreach/Booth/Sign.SLDPRT", "sign");
        await t.A.SyncTimesAsync(2);
        t.A.Offline = true;
        for (var i = 1; i <= 12; i++) t.A.Write($"Robot 2027/Booth Pack/Panel-{i:D2}.SLDPRT", "panel " + i);
        await t.A.SyncAsync();
        t.A.RenameFolder("Robot 2027/Booth Pack", "Outreach/Booth Pack");
        await t.A.SyncAsync();
        t.A.Offline = false;
        await t.A.SyncTimesAsync(2);

        Assert.True(FolderExists(t.A, "Outreach/Booth Pack"));
        Assert.False(FolderExists(t.A, "Robot 2027/Booth Pack"));
        Assert.Equal(13, (await LiveFiles(t, outreach)).Count);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and name like 'Panel-%'", ("p", t.Project)));
        Assert.Null(t.A.Card(NoticeKinds.FolderPutBack));
        Assert.Equal("Added 12 of 12 files to Outreach › Booth Pack", t.A.Card(NoticeKinds.Import)!.Title);
    }

    // The product review's replay: a large unzip lands over several passes (the last part with
    // fewer than ten files). It is one import, never split by folder or left out.
    [PostgresFact]
    public async Task An_unzip_seen_over_several_passes_is_one_import()
    {
        await using var t = await TeamAsync();
        t.A.Write("Robot 2027/Parts/Bolt.SLDPRT", "bolt");
        await t.A.SyncAsync();
        const string inner = "Robot 2027/Gearbox Pack/CopyDesignTemp";
        for (var i = 1; i <= 30; i++) t.A.Write($"{inner}/Gear-{i:D2}.SLDPRT", "g" + i);
        await t.A.SyncAsync();
        for (var i = 31; i <= 60; i++) t.A.Write($"{inner}/Gear-{i:D2}.SLDPRT", "g" + i);
        await t.A.SyncAsync();
        for (var i = 61; i <= 65; i++) t.A.Write($"{inner}/Gear-{i:D2}.SLDPRT", "g" + i);
        await t.A.SyncAsync();
        var import = t.A.Card(NoticeKinds.Import)!;
        Assert.Equal("Added 65 of 65 files to Robot 2027 › Gearbox Pack", import.Title);
        Assert.Single(import.Items);
    }

    // P14. Alex, offline, renames Gearbox to Gears and adds a part there; Maria renames Gearbox to
    // Box in the app. When Alex comes back, his folder follows the team's name with everything in
    // it (his new part too), with one notice saying so: never two folders.
    [PostgresFact]
    public async Task A_folder_renamed_here_and_by_the_team_at_once_follows_the_team()
    {
        await using var t = await GearboxTeamAsync();
        t.A.Offline = true;
        t.A.RenameFolder(Gearbox, Gears);
        t.A.Write($"{Gears}/Alex-New.SLDPRT", "Alex's new part");
        await t.A.SyncAsync();
        Assert.True((await t.B.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Box")).Ok);
        t.A.Offline = false;
        await t.A.SyncTimesAsync(3);
        await t.B.SyncTimesAsync(2);

        Assert.Equal(["Alex-New.SLDPRT", "Gear.SLDPRT", "Housing.SLDPRT"], (await LiveFiles(t)).Where(f => f.Folder == "Drivetrain/Box").Select(f => f.Name));
        Assert.Equal(3, (await LiveFiles(t)).Count);
        Assert.False(FolderExists(t.A, Gears));
        Assert.Equal("Alex's new part", t.A.Text("Robot 2027/Drivetrain/Box/Alex-New.SLDPRT"));
        Assert.Equal("Alex's new part", t.B.Text("Robot 2027/Drivetrain/Box/Alex-New.SLDPRT"));
        Assert.Equal("Gears is now Box: someone renamed Gearbox first", t.A.Card(NoticeKinds.FolderPutBack)!.Title);
        Assert.Equal(0, Rpc(t, "armory_tombstone"));
        NoViolations(t.A); NoViolations(t.B);
    }

    // The product review's replay: Alex deletes Gearbox while Maria has Housing open. Maria is told
    // the file was removed (nothing newer, nothing uploading), and her copy goes aside once closed.
    [PostgresFact]
    public async Task A_file_removed_while_it_is_open_here_says_so()
    {
        await using var t = await GearboxTeamAsync();
        t.B.Open($"{Gearbox}/Housing.SLDPRT");
        Directory.Delete(t.A.Disk.Full(Gearbox), recursive: true);
        await t.A.SyncTimesAsync(2);
        await t.B.SyncTimesAsync(2);
        var card = Assert.Single(t.B.Engine.View.Notices);
        Assert.Equal(NoticeKinds.NewerWaiting, card.Kind);
        Assert.Equal("Housing.SLDPRT was removed from Robot 2027", card.Title);
        Assert.Equal("Close Housing.SLDPRT in SolidWorks, and Armory moves your copy aside. Nothing is lost.", card.Detail);
        Assert.Null(card.Action);
        Assert.Equal(FileStatuses.NotInArmory, t.B.Row($"{Gearbox}/Housing.SLDPRT").Status);
        Assert.Equal("housing v1", t.B.Text($"{Gearbox}/Housing.SLDPRT"));
        t.B.Close($"{Gearbox}/Housing.SLDPRT");
        await t.B.SyncTimesAsync(2);
        Assert.Contains($"{Gearbox}/Housing.SLDPRT", t.B.Disk.Recovered);
        Assert.False(FolderExists(t.B, Gearbox));
        Assert.Empty(t.B.Engine.View.Notices);
        NoViolations(t.A); NoViolations(t.B);
    }
}
