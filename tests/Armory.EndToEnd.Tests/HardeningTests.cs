using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Cases an adversarial review of the engine found the first proof did not cover. Each
// test names the product rule it holds, kept through v2 check out: a file the server has is
// read-only unless this computer has it checked out, and the shared file advances only at a
// check in or an add.
public sealed class HardeningTests
{
    private static Task<long> LiveLocks(Team t, Guid file) => t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file));
    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    // A crash at any upload step of a SECOND version still replays to exactly one more version.
    // The lock's steps happen at the check out, the commit's and the release's at the check in.
    [PostgresFact]
    public async Task A_crash_during_a_second_version_replays_to_exactly_one_more_version()
    {
        await using var t = await TeamAsync();
        var points = new[] { "after-capture", "before-lock", "after-lock", "before-commit", "after-blob", "after-commit-rpc", "after-commit", "before-release", "after-release" };
        var atCheckOut = new[] { "before-lock", "after-lock" };
        var reached = new HashSet<string>();
        foreach (var point in points)
        {
            var path = $"Robot 2027/Crash2/{point}.SLDPRT";
            t.A.CrashPoint = null; t.A.Restart();
            t.A.Write(path, "v1 " + point);
            await t.A.SyncAsync();
            var file = await t.FileId($"{point}.SLDPRT");
            Assert.Equal(1, await t.Versions(file));
            Action<string> crash = p => { if (p == point) { reached.Add(p); throw new SimulatedCrash(p); } };
            if (atCheckOut.Contains(point))
            {
                t.A.CrashPoint = crash; t.A.Restart();
                await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.CheckOutAsync(path));
                t.A.CrashPoint = null; t.A.Restart();
                await t.A.SyncAsync(); // the check out was durable: the next pass finishes it
            }
            else Assert.True((await t.A.CheckOutAsync(path)).Ok);
            Assert.False(t.A.Disk.IsReadOnly(path), $"crash at {point}: the check out must make the file writable");
            t.A.Save(path, "v2 " + point);
            if (!atCheckOut.Contains(point))
            {
                t.A.CrashPoint = crash; t.A.Restart();
                await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.CheckInAsync(path));
            }
            else Assert.True((await t.A.CheckInAsync(path)).Ok);
            t.A.CrashPoint = null; t.A.Restart();
            await t.A.SyncTimesAsync(2);
            Assert.True(2 == await t.Versions(file), $"crash at {point}: expected exactly two versions");
            Assert.True(0 == await t.Sides(file), $"crash at {point}: expected no side version");
            Assert.Equal(Hash("v2 " + point), await t.CurrentHash(file));
            Assert.Equal(0, await LiveLocks(t, file));
            Assert.True(t.A.Disk.IsReadOnly(path), $"crash at {point}: a checked-in file must be read-only");
            Assert.DoesNotContain(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.KeptCopy && n.Item.Path == path);
        }
        Assert.Equal(points.ToHashSet(), reached);
        NoViolations(t.A);
    }

    // A mentor takes back an offline student's check out without editing: the student's later
    // bytes are still kept as their side version, the checked-in version comes back once the
    // file is closed, and checking it out again moves on normally.
    [PostgresFact]
    public async Task A_broken_lock_without_a_mentor_edit_still_preserves_and_then_moves_on()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Open(Plate);
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Offline = true;
        t.A.Save(Plate, "Alex offline");
        await t.A.SyncAsync();
        Assert.True(await t.Mentor.Api.BreakLockAsync(file, t.Mentor.Device, Guid.NewGuid()));
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(Hash("v1"), await t.CurrentHash(file));
        Assert.Equal("Alex offline", t.A.Text(Plate));
        // Bytes saved without a check out never become the shared version (v2): with the file
        // closed, the checked-in version comes back and Alex's work stays his kept copy.
        t.A.Close(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(Hash("v1"), await t.CurrentHash(file));
        Assert.Equal("v1", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Single(t.A.Card(NoticeKinds.TakenBack)!.Items);
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex after");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("Alex after"), await t.CurrentHash(file));
        Assert.Single(await t.SideAuthors(file));
        Assert.Equal(0, await LiveLocks(t, file));
        NoViolations(t.A);
    }

    // A rename that lands while B has the file open waits for B to close it; a file that
    // opens between planning and writing is rechecked and never overwritten.
    [PostgresFact]
    public async Task Renames_and_downloads_wait_for_an_open_file()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.B.Open(Plate);
        const string Renamed = "Robot 2027/Drivetrain/Plate-Left.SLDPRT";
        Assert.True(await t.A.Engine.MoveAsync(PortableVaultFileSystem.P(Plate), PortableVaultFileSystem.P(Renamed)));
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate));
        Assert.Null(t.B.Read(Renamed));
        var waiting = Assert.Single(t.B.NoticeItems, n => n.Card.Kind == NoticeKinds.NewerWaiting && n.Item.Path == Plate);
        Assert.Contains("renamed to Plate-Left.SLDPRT", waiting.Item.Detail);
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate));
        Assert.Equal("v1", t.B.Text(Renamed));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        // Opened between the plan and the write: the engine looks again and waits.
        Assert.True((await t.A.CheckOutAsync(Renamed)).Ok);
        t.A.Save(Renamed, "v2");
        Assert.True((await t.A.CheckInAsync(Renamed)).Ok);
        t.B.CrashPoint = p => { if (p == "before-Download") t.B.Open(Renamed); };
        t.B.Restart();
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Renamed));
        Assert.Contains(t.B.NoticeItems, n => n.Card.Kind == NoticeKinds.NewerWaiting && n.Item.Path == Renamed);
        t.B.CrashPoint = null; t.B.Restart();
        t.B.Close(Renamed);
        await t.B.SyncAsync();
        Assert.Equal("v2", t.B.Text(Renamed));
        NoViolations(t.B);
    }

    // The v2 read-only rule: a file the server has is read-only on every computer unless that
    // computer has it checked out. Opening it takes nothing (one quiet offer to check it out);
    // each check out takes the lock and makes only that copy writable, and each check in lets
    // the lock go and makes it read-only again.
    [PostgresFact]
    public async Task Files_someone_else_holds_are_read_only_until_they_release()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(LockOwnership.Free, t.B.Disk.Attributes[Plate]);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        t.A.Open(Plate);
        await t.A.SyncAsync();
        Assert.Equal(0, await LiveLocks(t, file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        var offer = Assert.IsType<PromptView>(t.A.Engine.View.Prompt);
        Assert.Equal(Plate, offer.Path);
        Assert.True(offer.CanCheckOut);
        Assert.Equal("Plate.SLDPRT", offer.Name);
        t.A.Close(Plate);
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.Equal(LockOwnership.OtherPerson, t.B.Disk.Attributes[Plate]);
        Assert.Equal(LockOwnership.ThisDevice, t.A.Disk.Attributes[Plate]);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Throws<IOException>(() => t.B.Save(Plate, "Maria"));
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        await t.B.SyncAsync();
        Assert.Equal(LockOwnership.Free, t.B.Disk.Attributes[Plate]);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        // Each check out takes the lock again, each check in lets it go.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
            Assert.Equal(1, await LiveLocks(t, file));
            Assert.Equal(Alex, (await t.World.QueryAsync("select holder_email from armory_locks where file_id=@f", r => r.GetString(0), ("f", file))).Single());
            Assert.False(t.A.Disk.IsReadOnly(Plate));
            Assert.True((await t.A.CheckInAsync(Plate)).Ok);
            Assert.Equal(0, await LiveLocks(t, file));
            Assert.True(t.A.Disk.IsReadOnly(Plate));
        }
    }

    // A ~$ marker left behind when SolidWorks dies stops counting as "open" after a while, so
    // the file is no longer held back from the team's newer version. A stale marker never
    // lets go of a check out, which only a check in, an undo or a take back ends.
    [PostgresFact]
    public async Task A_stale_SolidWorks_marker_stops_holding_the_lock()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.B.Open(Plate);
        t.B.Disk.CrashApp(Plate); // the ~$ file stays on disk
        await t.B.SyncAsync();
        Assert.Equal(0, await LiveLocks(t, file)); // a marker never takes the lock
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate)); // a fresh marker still counts as open
        Assert.NotNull(t.B.Card(NoticeKinds.NewerWaiting));
        t.B.Clock.Advance(TimeSpan.FromMinutes(11));
        await t.B.SyncAsync();
        Assert.Equal("v2", t.B.Text(Plate));
        Assert.Contains(t.B.Engine.View.Notices, n => n.Title == "SolidWorks may have closed unexpectedly");
        // A check out survives SolidWorks dying with the file open.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Open(Plate);
        t.A.Disk.CrashApp(Plate);
        t.A.Clock.Advance(TimeSpan.FromMinutes(11));
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, await LiveLocks(t, file));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        NoViolations(t.A); NoViolations(t.B);
    }

    // An Explorer rename becomes a server move, never a delete for the team; a refused one
    // (someone else has the file checked out) is put back where it was, naming who has it.
    [PostgresFact]
    public async Task An_Explorer_rename_is_a_move_and_a_refused_one_is_put_back()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        t.A.Write("Robot 2027/Drivetrain/Bracket.SLDPRT", "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var plate = await t.FileId("Plate.SLDPRT");
        const string Moved = "Robot 2027/Drivetrain/Gearbox/Plate.SLDPRT";
        Directory.CreateDirectory(Path.GetDirectoryName(t.A.Disk.Full(Moved))!);
        File.Move(t.A.Disk.Full(Plate), t.A.Disk.Full(Moved));
        await t.A.SyncTimesAsync(2);
        Assert.Equal("Drivetrain/Gearbox", (await t.World.QueryAsync("select folder from armory_files where id=@f", r => r.GetString(0), ("f", plate))).Single());
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(2, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        Assert.Equal(0, await LiveLocks(t, plate)); // the lock the move took is let go
        Assert.True(t.A.Disk.IsReadOnly(Moved));
        await t.B.SyncAsync();
        Assert.Equal("plate", t.B.Text(Moved));
        Assert.Null(t.B.Read(Plate));
        // B has the bracket checked out, so A's rename of it is refused and undone on A's disk.
        Assert.True((await t.B.CheckOutAsync("Robot 2027/Drivetrain/Bracket.SLDPRT")).Ok);
        File.Move(t.A.Disk.Full("Robot 2027/Drivetrain/Bracket.SLDPRT"), t.A.Disk.Full("Robot 2027/Drivetrain/Bracket-Old.SLDPRT"));
        await t.A.SyncTimesAsync(2);
        Assert.Equal("bracket", t.A.Text("Robot 2027/Drivetrain/Bracket.SLDPRT"));
        Assert.Null(t.A.Read("Robot 2027/Drivetrain/Bracket-Old.SLDPRT"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        var putBack = t.A.Card(NoticeKinds.FolderPutBack)!;
        Assert.Contains("Bracket.SLDPRT", putBack.Title);
        Assert.Contains("Maria Lopez", putBack.Title);
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Drivetrain/Bracket.SLDPRT"));
        NoViolations(t.A); NoViolations(t.B);
    }

    // One scan that misses a file is not a deletion; a real deletion still goes through. A
    // removed name is not reused for an unrelated file: adding a file with that name revives
    // the removed file (contract C4), so its history goes on and nothing is refused.
    [PostgresFact]
    public async Task A_deletion_needs_two_scans_and_a_removed_name_is_not_reused()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        var bytes = t.A.Read(Plate)!;
        t.A.Delete(Plate);
        await t.A.SyncAsync();
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        t.A.Write(Plate, bytes); // it was only briefly missing
        await t.A.SyncTimesAsync(2);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        t.A.Delete(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_tombstones where file_id=@f", ("f", file)));
        // On another computer that never had it, a new part with the removed file's name revives
        // that file: the same id, its history going on, the new bytes its current version.
        t.B.Write(Plate, "a completely new part");
        await t.B.SyncTimesAsync(2);
        Assert.Equal("a completely new part", t.B.Text(Plate));
        Assert.Equal(file, await t.FileId("Plate.SLDPRT"));
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("a completely new part"), await t.CurrentHash(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("v1"))));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where entity_id=@f and kind='tombstone'", ("f", file)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where entity_id=@f and kind='file_revived'", ("f", file)));
        Assert.Equal(0, await t.Sides(file));
        Assert.Empty(t.B.Engine.View.Notices); // nothing is refused for its name
        Assert.Equal(0, await LiveLocks(t, file));
        // On the computer that removed it, new bytes written at that path again are kept as a
        // side version, and the revived part comes down in their place.
        t.A.Write(Plate, "Alex brought it back");
        await t.A.SyncTimesAsync(3);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Alex brought it back"))));
        Assert.Equal("a completely new part", t.A.Text(Plate));
        Assert.Equal(2, await t.Versions(file));
        NoViolations(t.A); NoViolations(t.B);
    }

    // A SolidWorks save the release gate refuses touches nothing on the server and does not
    // keep the lock once a good save follows; in enforce mode an unknown release is refused.
    [PostgresFact]
    public async Task A_refused_draft_never_reaches_the_server_and_never_holds_the_lock()
    {
        await using var t = await TeamAsync();
        t.A.ReleaseReader = new FakeReleaseReader();
        t.A.Restart();
        t.A.Write(Plate, "SW2026 draft");
        await t.A.SyncAsync();
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_commit_version_with_release"));
        Assert.Equal(0, t.World.Supabase.RpcCount("armory_create_file"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks"));
        t.A.Open(Plate);
        t.A.Save(Plate, "SW2025 saved back");
        await t.A.SyncAsync();
        // Added while open: it stays checked out to Alex until it is closed (decision D2).
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(1, await LiveLocks(t, file));
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        t.A.Close(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.Equal(Hash("SW2025 saved back"), await t.CurrentHash(file));
        Assert.Equal(0, await LiveLocks(t, file)); // the 2026 draft does not hold it
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions where content_sha256=@h", ("h", Hash("SW2026 draft"))));
        // B can now edit it, once B checks it out.
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        t.B.Save(Plate, "SW2025 by Maria");
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("SW2025 by Maria"), await t.CurrentHash(file));
        // Enforce: a release that cannot be read is refused too.
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        t.B.Write("Robot 2027/Drivetrain/Unknown.SLDPRT", "no release header");
        await t.B.SyncAsync();
        Assert.Contains(t.B.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Detail!.Contains("unknown", StringComparison.Ordinal));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='Unknown.SLDPRT'"));
    }

    // Reconnecting registers a new device id; check outs and unsent work from the old id stay
    // this computer's: checking in commits them as shared versions, not side versions, and
    // releases the old id's lock.
    [PostgresFact]
    public async Task Reconnecting_keeps_the_old_device_locks_and_work()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Offline = true;
        t.A.Save(Plate, "v2 offline");
        await t.A.SyncAsync();
        t.A.Offline = false;
        await t.A.ConnectAsync(Alex); // signed in again: a new device id
        Assert.Equal("Checked out by you", t.A.Engine.View.MyFiles.Single(f => f.Path == Plate).Checkout.Label);
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("v2 offline"), await t.CurrentHash(file));
        Assert.Equal(0, await t.Sides(file));
        Assert.Equal(0, await LiveLocks(t, file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));
    }

    // A file the server will not take (too large here) is refused visibly without stopping
    // anything else: later saves of other files are still captured and sent.
    [PostgresFact]
    public async Task A_refused_file_never_stops_other_files_from_syncing()
    {
        await using var t = await TeamAsync();
        t.A.MaximumFileBytes = 64;
        t.A.Restart();
        t.A.Write("Robot 2027/Video/drive-test.mp4", new string('x', 200));
        t.A.Write(Plate, "small");
        await t.A.SyncAsync();
        Assert.Contains(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Detail!.Contains("2 GB", StringComparison.Ordinal));
        Assert.Equal(Hash("small"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "small v2");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("small v2"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='drive-test.mp4'"));
        Assert.Contains(t.A.Snapshots.Enumerate(), s => s.Path.EndsWith("drive-test.mp4", StringComparison.Ordinal)); // kept on this computer
        // Removed from a project mid-way: that project's work is refused, the others carry on.
        var other = await t.Mentor.Api.CreateProjectAsync("IDEA 209 Bridge", 2027, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(other, Alex, MemberRole.Student, Guid.NewGuid());
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Write("IDEA 209 Bridge/Truss.SLDPRT", "truss");
        t.A.Save(Plate, "small v3");
        Assert.True(await t.Mentor.Api.RemoveMemberAsync(other, Alex, Guid.NewGuid()));
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("small v3"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "small v4");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(Hash("small v4"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
    }
}
