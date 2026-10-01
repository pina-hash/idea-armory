using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Cases an adversarial review of the engine found the first proof did not cover. Each
// test names the product rule it holds.
public sealed class HardeningTests
{
    private static Task<long> LiveLocks(Team t, Guid file) => t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file));
    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    // A crash at any upload step of a SECOND version still replays to exactly one more version.
    [PostgresFact]
    public async Task A_crash_during_a_second_version_replays_to_exactly_one_more_version()
    {
        await using var t = await TeamAsync();
        var points = new[] { "after-capture", "before-lock", "after-lock", "before-commit", "after-blob", "after-commit-rpc", "after-commit", "before-release", "after-release" };
        var reached = new HashSet<string>();
        foreach (var point in points)
        {
            var path = $"Robot 2027/Crash2/{point}.SLDPRT";
            t.A.CrashPoint = null; t.A.Restart();
            t.A.Write(path, "v1 " + point);
            await t.A.SyncAsync();
            var file = await t.FileId($"{point}.SLDPRT");
            Assert.Equal(1, await t.Versions(file));
            t.A.CrashPoint = p => { if (p == point) { reached.Add(p); throw new SimulatedCrash(p); } };
            t.A.Restart();
            t.A.Write(path, "v2 " + point);
            await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
            t.A.CrashPoint = null; t.A.Restart();
            await t.A.SyncTimesAsync(2);
            Assert.True(2 == await t.Versions(file), $"crash at {point}: expected exactly two versions");
            Assert.True(0 == await t.Sides(file), $"crash at {point}: expected no side version");
            Assert.Equal(Hash("v2 " + point), await t.CurrentHash(file));
            Assert.Equal(0, await LiveLocks(t, file));
            Assert.DoesNotContain(t.A.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.SideVersion && n.Path == path);
        }
        Assert.Equal(points.ToHashSet(), reached);
        NoViolations(t.A);
    }

    // A mentor breaks an offline student's lock without editing: the student's later bytes
    // are still kept as their side version, and editing afterward advances normally.
    [PostgresFact]
    public async Task A_broken_lock_without_a_mentor_edit_still_preserves_and_then_moves_on()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Offline = true;
        t.A.Write(Plate, "Alex offline");
        await t.A.SyncAsync();
        Assert.True(await t.Mentor.Api.BreakLockAsync(file, t.Mentor.Device, Guid.NewGuid()));
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(Hash("v1"), await t.CurrentHash(file));
        Assert.Equal("Alex offline", t.A.Text(Plate));
        // Nobody else edited and the lock is free again, so Core lets Alex's work continue as
        // the shared version once the file is closed (the reference simulation does the same).
        t.A.Close(Plate);
        await t.A.SyncTimesAsync(2);
        Assert.DoesNotContain(t.A.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.LockBroken);
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("Alex offline"), await t.CurrentHash(file));
        t.A.Write(Plate, "Alex after");
        await t.A.SyncAsync();
        Assert.Equal(3, await t.Versions(file));
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
        Assert.Contains(t.B.Engine.View.NeedsMe, n => n.Title == "This file was renamed");
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate));
        Assert.Equal("v1", t.B.Text(Renamed));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        // Opened between the plan and the write: the engine looks again and waits.
        t.A.Write(Renamed, "v2");
        await t.A.SyncAsync();
        t.B.CrashPoint = p => { if (p == "before-Download") t.B.Open(Renamed); };
        t.B.Restart();
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Renamed));
        Assert.Contains(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.NewerWaiting);
        t.B.CrashPoint = null; t.B.Restart();
        t.B.Close(Renamed);
        await t.B.SyncAsync();
        Assert.Equal("v2", t.B.Text(Renamed));
        NoViolations(t.B);
    }

    // Files someone else is editing are read-only on this computer, and writable again after.
    [PostgresFact]
    public async Task Files_someone_else_holds_are_read_only_until_they_release()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(LockOwnership.Free, t.B.Disk.Attributes[Plate]);
        t.A.Open(Plate);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(LockOwnership.OtherPerson, t.B.Disk.Attributes[Plate]);
        Assert.Equal(LockOwnership.ThisDevice, t.A.Disk.Attributes[Plate]);
        t.A.Close(Plate);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(LockOwnership.Free, t.B.Disk.Attributes[Plate]);
        // Reopening takes the lock again each time.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            t.A.Open(Plate);
            await t.A.SyncAsync();
            var file = await t.FileId("Plate.SLDPRT");
            Assert.Equal(1, await LiveLocks(t, file));
            Assert.Equal(Alex, (await t.World.QueryAsync("select holder_email from armory_locks where file_id=@f", r => r.GetString(0), ("f", file))).Single());
            t.A.Close(Plate);
            await t.A.SyncAsync();
            Assert.Equal(0, await LiveLocks(t, file));
        }
    }

    // A ~$ marker left behind when SolidWorks dies stops counting as "open" after a while,
    // so the lock is released and the team is not blocked forever.
    [PostgresFact]
    public async Task A_stale_SolidWorks_marker_stops_holding_the_lock()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(1, await LiveLocks(t, file));
        t.A.Disk.CrashApp(Plate); // the ~$ file stays on disk
        await t.A.SyncAsync();
        Assert.Equal(1, await LiveLocks(t, file)); // a fresh marker still counts as open
        t.A.Clock.Advance(TimeSpan.FromMinutes(11));
        await t.A.SyncAsync();
        Assert.Equal(0, await LiveLocks(t, file));
        Assert.Contains(t.A.Engine.View.NeedsMe, n => n.Title == "SolidWorks may have closed unexpectedly");
    }

    // An Explorer rename becomes a server move, never a delete for the team; a refused one
    // is put back where it was.
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
        await t.B.SyncAsync();
        Assert.Equal("plate", t.B.Text(Moved));
        Assert.Null(t.B.Read(Plate));
        // B is editing the bracket, so A's rename of it is refused and undone on A's disk.
        t.B.Open("Robot 2027/Drivetrain/Bracket.SLDPRT");
        await t.B.SyncAsync();
        File.Move(t.A.Disk.Full("Robot 2027/Drivetrain/Bracket.SLDPRT"), t.A.Disk.Full("Robot 2027/Drivetrain/Bracket-Old.SLDPRT"));
        await t.A.SyncTimesAsync(2);
        Assert.Equal("bracket", t.A.Text("Robot 2027/Drivetrain/Bracket.SLDPRT"));
        Assert.Null(t.A.Read("Robot 2027/Drivetrain/Bracket-Old.SLDPRT"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Contains(t.A.Engine.View.NeedsMe, n => n.Title == "Renamed back for now");
        NoViolations(t.A); NoViolations(t.B);
    }

    // One scan that misses a file is not a deletion; a real deletion still goes through.
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
        // On another computer that never had it, a brand-new file with the removed file's name
        // is refused and kept on disk; it never becomes part of the removed file's history.
        t.B.Write(Plate, "a completely new part");
        await t.B.SyncTimesAsync(2);
        Assert.Equal("a completely new part", t.B.Text(Plate));
        Assert.Contains(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.NameTaken && n.Detail.Contains("removed"));
        Assert.Equal(0, await t.Sides(file));
        Assert.Equal(1, await t.Versions(file));
        // On the computer that removed it, writing that path again is Core's case of new bytes
        // over a removed file: they are kept as a side version and moved to recovery.
        t.A.Write(Plate, "Alex brought it back");
        await t.A.SyncTimesAsync(3);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Alex brought it back"))));
        Assert.Contains(Plate, t.A.Disk.Recovered);
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
        t.A.Write(Plate, "SW2025 saved back");
        await t.A.SyncAsync();
        t.A.Close(Plate);
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Hash("SW2025 saved back"), await t.CurrentHash(file));
        Assert.Equal(0, await LiveLocks(t, file)); // the 2026 draft does not hold it
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions where content_sha256=@h", ("h", Hash("SW2026 draft"))));
        // B can now edit it.
        await t.B.SyncAsync();
        t.B.Write(Plate, "SW2025 by Maria");
        await t.B.SyncAsync();
        Assert.Equal(Hash("SW2025 by Maria"), await t.CurrentHash(file));
        // Enforce: a release that cannot be read is refused too.
        Assert.True(await t.Mentor.Api.SetReleaseGateAsync(t.Project, ProjectReleaseGate.Enforce, Guid.NewGuid()));
        t.B.Write("Robot 2027/Drivetrain/Unknown.SLDPRT", "no release header");
        await t.B.SyncAsync();
        Assert.Contains(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.Refused && n.Detail.Contains("unknown"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='Unknown.SLDPRT'"));
    }

    // Reconnecting registers a new device id; locks and unsent work from the old id stay
    // this computer's: they commit as shared versions, not side versions, and release.
    [PostgresFact]
    public async Task Reconnecting_keeps_the_old_device_locks_and_work()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Offline = true;
        t.A.Write(Plate, "v2 offline");
        await t.A.SyncAsync();
        t.A.Offline = false;
        await t.A.ConnectAsync(Alex); // signed in again: a new device id
        await t.A.SyncAsync();
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("v2 offline"), await t.CurrentHash(file));
        Assert.Equal(0, await t.Sides(file));
        t.A.Close(Plate);
        await t.A.SyncAsync();
        Assert.Equal(0, await LiveLocks(t, file));
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
        Assert.Contains(t.A.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.Refused && n.Detail.Contains("2 GB"));
        Assert.Equal(Hash("small"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        t.A.Write(Plate, "small v2");
        await t.A.SyncAsync();
        Assert.Equal(Hash("small v2"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where name='drive-test.mp4'"));
        Assert.Contains(t.A.Snapshots.Enumerate(), s => s.Path.EndsWith("drive-test.mp4", StringComparison.Ordinal)); // kept on this computer
        // Removed from a project mid-way: that project's work is refused, the others carry on.
        var other = await t.Mentor.Api.CreateProjectAsync("IDEA 209 Bridge", 2027, Guid.NewGuid());
        await t.Mentor.Api.AddMemberAsync(other, Alex, MemberRole.Student, Guid.NewGuid());
        await t.A.SyncAsync();
        t.A.Write("IDEA 209 Bridge/Truss.SLDPRT", "truss");
        t.A.Write(Plate, "small v3");
        Assert.True(await t.Mentor.Api.RemoveMemberAsync(other, Alex, Guid.NewGuid()));
        await t.A.SyncAsync();
        Assert.Equal(Hash("small v3"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        t.A.Write(Plate, "small v4");
        await t.A.SyncAsync();
        Assert.Equal(Hash("small v4"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
    }
}
