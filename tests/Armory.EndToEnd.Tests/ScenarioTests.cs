using System.Security.Cryptography;
using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.TestSupport;

namespace Armory.EndToEnd.Tests;

// Two agents ("student A laptop" and "student B lab PC") syncing through a real
// PostgreSQL database, the fake ideabosco.com and the shared fake S3, each scenario from
// docs/agent/ENGINE.md's product rules. v2: a file the server has is read-only unless this
// computer has it checked out, a save while checked out is kept on the server but shared only
// at check in, and bytes saved without a check out are only ever a kept copy.
public sealed class ScenarioTests
{
    internal const string Alex = "alex.kim@students.test", Maria = "maria.lopez@students.test", Mentor = "pina@ideabosco.test";
    internal const string Plate = "Robot 2027/Drivetrain/Plate.SLDPRT";

    internal sealed record Team(World World, Person Mentor, Guid Project, Computer A, Computer B) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
        public Task<long> Versions(Guid file) => World.CountAsync("select count(*) from armory_versions where file_id=@f", ("f", file));
        public Task<long> Sides(Guid file) => World.CountAsync("select count(*) from armory_side_versions where file_id=@f", ("f", file));
        public async Task<Guid> FileId(string name) => (await World.QueryAsync("select id from armory_files where project_id=@p and name=@n", r => r.GetGuid(0), ("p", Project), ("n", name))).Single();
        public Task<List<string>> SideAuthors(Guid file) => World.QueryAsync("select author_email||'|'||reason from armory_side_versions where file_id=@f order by created_at", r => r.GetString(0), ("f", file));
        public async Task<string?> CurrentHash(Guid file) => (await World.QueryAsync("select v.content_sha256 from armory_files f join armory_versions v on v.id=f.current_version_id where f.id=@f", r => r.GetString(0), ("f", file))).SingleOrDefault();
        public Task<long> LiveLocks(Guid file) => World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file));
        public async Task<string?> Holder(Guid file) => (await World.QueryAsync("select holder_email from armory_locks where file_id=@f and broken_at is null", r => r.GetString(0), ("f", file))).SingleOrDefault();
    }

    internal static async Task<Team> TeamAsync(bool secondDeviceForAlex = false, LatencyProfile? latency = null)
    {
        var world = await World.StartAsync();
        world.Latency = latency ?? LatencyProfile.None;
        var mentor = await world.PersonAsync(Mentor, admin: true);
        var project = await mentor.Api.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        await mentor.Api.AddMemberAsync(project, Alex, MemberRole.Student, Guid.NewGuid());
        await mentor.Api.AddMemberAsync(project, Maria, MemberRole.Student, Guid.NewGuid());
        var a = await world.ComputerAsync("student A laptop", Alex);
        var b = await world.ComputerAsync(secondDeviceForAlex ? "student A lab PC" : "student B lab PC", secondDeviceForAlex ? Alex : Maria);
        return new Team(world, mentor, project, a, b);
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // a. A creates a part, and B receives it.
    [PostgresFact]
    public async Task A_creates_a_part_and_B_receives_it()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(Hash("plate v1 by Alex"), await t.CurrentHash(file));
        await t.B.SyncAsync();
        Assert.Equal("plate v1 by Alex", t.B.Text(Plate));
        Assert.Equal(0, await t.Sides(file));
        // A new part, closed, is added and checked in by one pass (decision D2): no check out is left.
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal(SyncStates.Synced, t.A.Engine.View.Sync.State); // "release not checked" is shown, never blocking
        // "SolidWorks year not checked" is never a notice: only a small tag on the file's detail.
        Assert.Empty(t.A.Engine.View.Notices);
        Assert.Empty(t.B.Engine.View.Notices);
        Assert.True((await t.A.Engine.GetFileDetailAsync(file))!.ReleaseNotChecked);
        Assert.Contains(t.B.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files), f => f.Name == "Plate.SLDPRT" && f.ReleaseNotChecked);
        // Nobody has it checked out, so it is read-only on both computers.
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.Equal("Available", t.B.Row(Plate).Checkout.Label);
    }

    // b. A edits while B has the file open: B is told a newer version is waiting, and B's
    // bytes are untouched.
    [PostgresFact]
    public async Task A_edits_while_B_has_it_open_and_B_is_told_without_losing_bytes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.B.Open(Plate);
        await t.B.SyncAsync();
        // Opening a file takes nothing: B is only asked, quietly, whether to check it out.
        Assert.Equal(0, await t.LiveLocks(file));
        var offer = Assert.IsType<PromptView>(t.B.Engine.View.Prompt);
        Assert.Equal(Plate, offer.Path);
        Assert.True(offer.CanCheckOut);
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2 by Alex");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate));
        var waiting = Assert.Single(t.B.NoticeItems, n => n.Card.Kind == NoticeKinds.NewerWaiting);
        Assert.Contains("Alex Kim", waiting.Card.Title);
        Assert.Empty(t.B.Disk.OpenWriteViolations);
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Equal("v2 by Alex", t.B.Text(Plate));
        Assert.Null(t.B.Card(NoticeKinds.NewerWaiting));
    }

    // c. A and B edit the same file offline, then reconnect. The shared file advances once
    // (by the student who had it checked out), the other edit becomes a named side version,
    // and nothing is lost.
    [PostgresFact]
    public async Task Offline_edits_by_both_advance_once_and_keep_the_other_as_a_side_version()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Offline = t.B.Offline = true;
        t.A.Save(Plate, "Alex offline edit");
        // Maria has not checked it out: SolidWorks can't save over it, so she clears the
        // read-only attribute and saves anyway.
        Assert.Throws<IOException>(() => t.B.Save(Plate, "Maria offline edit"));
        t.B.ForceWrite(Plate, "Maria offline edit");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(SyncStates.Offline, t.A.Engine.View.Sync.State);
        Assert.Equal(1, await t.Versions(file));
        t.A.Offline = t.B.Offline = false;
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(1, await t.Versions(file)); // shared only at check in
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        await t.B.SyncAsync();
        Assert.Equal(2, await t.Versions(file)); // advanced exactly once
        Assert.Equal(Hash("Alex offline edit"), await t.CurrentHash(file));
        var sides = await t.SideAuthors(file);
        Assert.Equal([Maria + "|changed without a check out"], sides.Where(s => s.StartsWith(Maria, StringComparison.Ordinal)));
        Assert.All(sides, s => Assert.Contains(s, new[] { Maria + "|changed without a check out", Alex + "|saved while checked out" }));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Maria offline edit"))));
        Assert.True(t.World.S3.Objects.ContainsKey(Armory.Storage.ContentObjectKey.FromHash(Hash("Maria offline edit"))));
        Assert.Equal("Alex offline edit", t.B.Text(Plate));
        Assert.NotNull(t.B.Card(NoticeKinds.KeptCopy));
    }

    // d. A mentor takes back A's check out while A is offline and edits. A's later bytes
    // become A's side version.
    [PostgresFact]
    public async Task A_mentor_breaks_an_offline_lock_and_the_later_bytes_become_a_side_version()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAndOpenAsync(Plate)).Ok); // Check out and open
        Assert.Contains(Plate, t.A.Disk.Launched);
        t.A.Open(Plate);
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Alex, await t.Holder(file));
        t.A.Offline = true;
        t.A.Save(Plate, "Alex while offline");
        await t.A.SyncAsync();
        Assert.True(await t.Mentor.Api.BreakLockAsync(file, t.Mentor.Device, Guid.NewGuid()));
        await t.Mentor.CommitAsync(t.Project, file, Encoding.UTF8.GetBytes("Mentor fix"));
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Alex while offline"))));
        Assert.Equal(Hash("Mentor fix"), await t.CurrentHash(file));
        Assert.Equal("Alex while offline", t.A.Text(Plate)); // still open: never overwritten
        Assert.NotNull(t.A.Card(NoticeKinds.TakenBack));
        t.A.Close(Plate);
        await t.A.SyncAsync();
        Assert.Equal("Mentor fix", t.A.Text(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Plate)); // taken back: no longer checked out here
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }

    // e. A crash between every pair of steps of an upload. On restart the journal replays
    // to exactly one version.
    [PostgresFact]
    public async Task A_crash_between_every_upload_step_replays_to_exactly_one_version()
    {
        await using var t = await TeamAsync();
        var points = new[] { "after-capture", "before-create", "after-create", "before-lock", "after-lock", "before-commit", "after-blob",
            "after-commit-rpc", "after-commit", "before-release", "after-release" };
        var reached = new HashSet<string>();
        foreach (var point in points)
        {
            var path = $"Robot 2027/Crash/{point}.SLDPRT";
            t.A.CrashPoint = p => { if (p == point) { reached.Add(p); throw new SimulatedCrash(p); } };
            t.A.Restart();
            t.A.Write(path, "bytes for " + point);
            await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
            t.A.CrashPoint = null;
            t.A.Restart();
            await t.A.SyncTimesAsync(2);
            var file = await t.FileId($"{point}.SLDPRT");
            Assert.True(1 == await t.Versions(file), $"crash at {point}: expected exactly one version");
            Assert.True(0 == await t.Sides(file), $"crash at {point}: expected no side version");
            Assert.Equal(Hash("bytes for " + point), await t.CurrentHash(file));
            Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file)));
            Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and name=@n", ("p", t.Project), ("n", $"{point}.SLDPRT")));
            Assert.True(t.A.Disk.IsReadOnly(path), $"crash at {point}: the checked-in add must be read-only");
        }
        Assert.Equal(points.ToHashSet(), reached);
        // A lost acknowledgement: the commit lands but the answer never arrives.
        t.World.Supabase.DropRpcAcknowledgement("armory_commit_version_with_release", t.World.Supabase.RpcCount("armory_commit_version_with_release") + 1);
        t.A.Write("Robot 2027/Crash/lost-ack.SLDPRT", "lost ack");
        await t.A.SyncAsync();
        await t.A.SyncTimesAsync(2);
        var lost = await t.FileId("lost-ack.SLDPRT");
        Assert.Equal(1, await t.Versions(lost));
        Assert.Equal(0, await t.Sides(lost));
        await t.B.SyncAsync();
        foreach (var point in points) Assert.Equal("bytes for " + point, t.B.Text($"Robot 2027/Crash/{point}.SLDPRT"));
    }

    // f. The same person on two devices. The second device cannot commit over the first;
    // its work becomes a side version.
    [PostgresFact]
    public async Task The_same_person_on_a_second_device_cannot_commit_over_the_first()
    {
        await using var t = await TeamAsync(secondDeviceForAlex: true);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Open(Plate);
        t.A.Save(Plate, "laptop edit");
        await t.A.SyncAsync(); // A has it checked out and keeps the file open
        await t.B.SyncAsync();
        // The lab PC sees Alex's other computer holding it: read-only, and no check out here.
        var row = t.B.Row(Plate);
        Assert.Equal(CheckoutStates.MyOtherComputer, row.Checkout.State);
        Assert.Equal("Checked out by you on student A laptop", row.Checkout.Label);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.False((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.Throws<IOException>(() => t.B.Save(Plate, "lab PC edit"));
        t.B.ForceWrite(Plate, "lab PC edit");
        await t.B.SyncAsync();
        Assert.Equal(1, await t.Versions(file)); // the lab PC's bytes never become the shared version
        Assert.Contains(Alex + "|changed without a check out", await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("lab PC edit"))));
        Assert.Equal("v1", t.B.Text(Plate)); // the checked-in version is put back
        Assert.Single(t.B.Card(NoticeKinds.KeptCopy)!.Items);
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("laptop edit"), await t.CurrentHash(file));
        await t.B.SyncAsync();
        Assert.Equal("laptop edit", t.B.Text(Plate));
    }

    // g. A rename through armory_move_file arrives on B as a move, not a delete plus add.
    [PostgresFact]
    public async Task A_rename_arrives_on_B_as_a_move()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate bytes");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        const string Renamed = "Robot 2027/Drivetrain/Gearbox/Plate-Left.SLDPRT";
        Assert.True(await t.A.Engine.MoveAsync(PortableVaultFileSystem.P(Plate), PortableVaultFileSystem.P(Renamed)));
        await t.A.SyncAsync();
        // The lock the move took is let go: a rename is not a check out.
        Assert.Equal(0, await t.LiveLocks(file));
        var replacesBefore = t.B.Disk.Recovered.Count;
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate));
        Assert.Equal("plate bytes", t.B.Text(Renamed));
        Assert.True(t.B.Disk.IsReadOnly(Renamed));
        Assert.Equal(file, await t.FileId("Plate-Left.SLDPRT"));
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_tombstones"));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where project_id=@p", ("p", t.Project)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_change_feed where kind='file_moved' and entity_id=@f", ("f", file)));
        Assert.Equal(replacesBefore, t.B.Disk.Recovered.Count);
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_files where id=@f and deleted_at is null", ("f", file)));
    }

    // h. A 2026-release SolidWorks file is refused at upload, naming both releases. The
    // saved-release reader is a test fake; no real reader exists yet.
    [PostgresFact]
    public async Task A_2026_SolidWorks_file_is_refused_naming_both_releases()
    {
        await using var t = await TeamAsync();
        t.A.ReleaseReader = new FakeReleaseReader();
        t.A.Restart();
        t.A.Write(Plate, "SW2026 part with a 2026-only feature");
        await t.A.SyncAsync();
        var refusal = Assert.Single(t.A.NoticeItems, n => n.Card.Kind == NoticeKinds.CantSend && n.Item.Path == Plate);
        Assert.Contains("2026", refusal.Item.Detail);
        Assert.Contains("2025", refusal.Item.Detail);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_versions"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions"));
        Assert.Equal("SW2026 part with a 2026-only feature", t.A.Text(Plate)); // the private draft stays
        Assert.False(t.A.Disk.IsReadOnly(Plate)); // a file the server does not have stays writable
        // Saved back to 2025, it goes through and is marked checked.
        t.A.Save(Plate, "SW2025 part saved back");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Hash("SW2025 part saved back"), await t.CurrentHash(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_version_releases where release_checked and saved_release=2025"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_versions v where v.content_sha256=@h", ("h", Hash("SW2026 part with a 2026-only feature"))));
    }

    // i. Two students try to edit the same part (brief section 5). Only the one who checked it
    // out can save; the other sees who has it, cannot check it out, and a save made anyway (the
    // read-only attribute cleared) is one kept copy and one notice, never the shared file. After
    // the check in, the other student receives the version and can check it out.
    [PostgresFact]
    public async Task Two_students_try_to_edit_the_same_part()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        var outA = await t.A.CheckOutAsync(Plate);
        Assert.True(outA.Ok);
        Assert.Equal("Checked out Plate.SLDPRT.", outA.Message);
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(CheckoutStates.Mine, t.A.Row(Plate).Checkout.State);
        Assert.Equal("Checked out by you", Assert.Single(t.A.Engine.View.MyFiles).Checkout.Label);
        await t.B.SyncAsync();
        // Maria sees who has it, her copy is read-only, SolidWorks can't save over it, and her
        // check out is refused naming Alex.
        var row = t.B.Row(Plate);
        Assert.Equal(CheckoutStates.Other, row.Checkout.State);
        Assert.Equal("Checked out by Alex Kim on student A laptop", row.Checkout.Label);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        t.B.Open(Plate);
        await t.B.SyncAsync();
        var offer = Assert.IsType<PromptView>(t.B.Engine.View.Prompt);
        Assert.False(offer.CanCheckOut);
        Assert.Equal("Checked out by Alex Kim on student A laptop", offer.Checkout.Label);
        Assert.Throws<IOException>(() => t.B.Save(Plate, "Maria's edit"));
        var outB = await t.B.CheckOutAsync(Plate);
        Assert.False(outB.Ok);
        Assert.Contains("Alex Kim", outB.Message);
        Assert.Equal(1, await t.LiveLocks(file));
        Assert.Equal(Alex, await t.Holder(file));
        Assert.Empty(t.B.Engine.View.MyFiles);
        // Alex saves while it is checked out: kept on the server, not shared until he checks in.
        t.A.Save(Plate, "v2 by Alex");
        await t.A.SyncAsync();
        Assert.Equal(1, await t.Versions(file));
        Assert.Contains(Alex + "|saved while checked out", await t.SideAuthors(file));
        // Maria clears the attribute and saves anyway: one kept copy and one notice; the shared
        // file is unchanged, the attribute is set again, and her open file is never overwritten.
        t.B.ForceWrite(Plate, "Maria saved anyway");
        await t.B.SyncAsync();
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(Hash("v1"), await t.CurrentHash(file));
        Assert.Equal([Maria + "|changed without a check out"], (await t.SideAuthors(file)).Where(s => s.StartsWith(Maria, StringComparison.Ordinal)));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Maria saved anyway"))));
        var kept = Assert.Single(t.B.Engine.View.Notices);
        Assert.Equal(NoticeKinds.KeptCopy, kept.Kind);
        Assert.Single(kept.Items);
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.Equal("Maria saved anyway", t.B.Text(Plate));
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate)); // the checked-in version came back
        Assert.Single(t.B.Engine.View.Notices);
        // Alex checks in: his copy is read-only, Maria receives the version and can check it out.
        var inA = await t.A.CheckInAsync(Plate);
        Assert.True(inA.Ok);
        Assert.Equal("Checked in Plate.SLDPRT.", inA.Message);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("v2 by Alex"), await t.CurrentHash(file));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Empty(t.A.Engine.View.MyFiles);
        await t.B.SyncAsync();
        Assert.Equal("v2 by Alex", t.B.Text(Plate));
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.Equal("Available", t.B.Row(Plate).Checkout.Label);
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        Assert.False(t.B.Disk.IsReadOnly(Plate));
        t.B.Save(Plate, "v3 by Maria");
        Assert.True((await t.B.CheckInAsync(Plate)).Ok);
        Assert.Equal(3, await t.Versions(file));
        Assert.Equal(Hash("v3 by Maria"), await t.CurrentHash(file));
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        foreach (var c in new[] { t.A, t.B })
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }
}
