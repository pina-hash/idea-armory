using System.Security.Cryptography;
using System.Text;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;

namespace Armory.EndToEnd.Tests;

// Two agents ("student A laptop" and "student B lab PC") syncing through a real
// PostgreSQL database, the fake ideabosco.com and the shared fake S3, each scenario from
// docs/agent/ENGINE.md's product rules.
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
    }

    internal static async Task<Team> TeamAsync(bool secondDeviceForAlex = false)
    {
        var world = await World.StartAsync();
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
        // Closed and saved: A's lock released itself; nobody remembered anything.
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks where file_id=@f and broken_at is null", ("f", file)));
        Assert.Equal(SyncStates.Synced, t.A.Engine.View.Sync.State); // "release not checked" is shown, never blocking
        Assert.Contains(t.A.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.ReleaseNotChecked);
        Assert.Contains(t.B.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files), f => f.Name == "Plate.SLDPRT" && f.ReleaseNotChecked);
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
        t.B.Open(Plate);
        t.A.Write(Plate, "v2 by Alex");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal("v1", t.B.Text(Plate));
        var waiting = Assert.Single(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.NewerWaiting);
        Assert.Contains("Alex Kim", waiting.Title);
        Assert.Empty(t.B.Disk.OpenWriteViolations);
        t.B.Close(Plate);
        await t.B.SyncAsync();
        Assert.Equal("v2 by Alex", t.B.Text(Plate));
        Assert.DoesNotContain(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.NewerWaiting);
    }

    // c. A and B edit the same file offline, then reconnect. The shared file advances once,
    // the other edit becomes a named side version, and nothing is lost.
    [PostgresFact]
    public async Task Offline_edits_by_both_advance_once_and_keep_the_other_as_a_side_version()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.A.Offline = t.B.Offline = true;
        t.A.Write(Plate, "Alex offline edit");
        t.B.Write(Plate, "Maria offline edit");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.Equal(SyncStates.Offline, t.A.Engine.View.Sync.State);
        Assert.Equal(1, await t.Versions(file));
        t.A.Offline = t.B.Offline = false;
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        await t.A.SyncAsync();
        Assert.Equal(2, await t.Versions(file)); // advanced exactly once
        Assert.Equal(Hash("Alex offline edit"), await t.CurrentHash(file));
        Assert.Equal([Maria + "|conflict"], await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Maria offline edit"))));
        Assert.True(t.World.S3.Objects.ContainsKey(Armory.Storage.ContentObjectKey.FromHash(Hash("Maria offline edit"))));
        Assert.Equal("Alex offline edit", t.B.Text(Plate));
        Assert.Contains(t.B.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.SideVersion);
    }

    // d. A mentor breaks A's lock while A is offline and edits. A's later bytes become A's
    // side version.
    [PostgresFact]
    public async Task A_mentor_breaks_an_offline_lock_and_the_later_bytes_become_a_side_version()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        await t.A.SyncAsync(); // the ~$ marker takes the lock
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Alex, (await t.World.QueryAsync("select holder_email from armory_locks where file_id=@f and broken_at is null", r => r.GetString(0), ("f", file))).Single());
        t.A.Offline = true;
        t.A.Write(Plate, "Alex while offline");
        await t.A.SyncAsync();
        Assert.True(await t.Mentor.Api.BreakLockAsync(file, t.Mentor.Device, Guid.NewGuid()));
        await t.Mentor.CommitAsync(t.Project, file, Encoding.UTF8.GetBytes("Mentor fix"));
        t.A.Offline = false;
        await t.A.SyncAsync();
        Assert.Contains(Alex + "|lock broken", await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("Alex while offline"))));
        Assert.Equal(Hash("Mentor fix"), await t.CurrentHash(file));
        Assert.Equal("Alex while offline", t.A.Text(Plate)); // still open: never overwritten
        t.A.Close(Plate);
        await t.A.SyncAsync();
        Assert.Equal("Mentor fix", t.A.Text(Plate));
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
        t.A.Open(Plate);
        t.A.Write(Plate, "laptop edit");
        await t.A.SyncAsync(); // A holds the lock and keeps the file open
        t.B.Write(Plate, "lab PC edit");
        await t.B.SyncAsync();
        Assert.Equal(2, await t.Versions(file));
        Assert.Equal(Hash("laptop edit"), await t.CurrentHash(file));
        Assert.Contains(Alex + "|conflict", await t.SideAuthors(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_side_versions where file_id=@f and content_sha256=@h", ("f", file), ("h", Hash("lab PC edit"))));
        var row = t.B.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files).Single(f => f.Name == "Plate.SLDPRT");
        Assert.True(row.Holder!.IsMyOtherComputer);
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
        var replacesBefore = t.B.Disk.Recovered.Count;
        await t.B.SyncAsync();
        Assert.Null(t.B.Read(Plate));
        Assert.Equal("plate bytes", t.B.Text(Renamed));
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
        var refusal = Assert.Single(t.A.Engine.View.NeedsMe, n => n.Kind == AttentionKinds.Refused && n.Path == Plate);
        Assert.Contains("2026", refusal.Detail);
        Assert.Contains("2025", refusal.Detail);
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_versions"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions"));
        Assert.Equal("SW2026 part with a 2026-only feature", t.A.Text(Plate)); // the private draft stays
        // Saved back to 2025, it goes through and is marked checked.
        t.A.Write(Plate, "SW2025 part saved back");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(Hash("SW2025 part saved back"), await t.CurrentHash(file));
        Assert.Equal(1, await t.World.CountAsync("select count(*) from armory_version_releases where release_checked and saved_release=2025"));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_versions v where v.content_sha256=@h", ("h", Hash("SW2026 part with a 2026-only feature"))));
    }
}
