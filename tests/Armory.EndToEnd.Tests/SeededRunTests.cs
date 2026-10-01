using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Armory.Client;
using Armory.Storage;
using Armory.TestSupport;
using Xunit.Abstractions;

namespace Armory.EndToEnd.Tests;

// The Core simulation's invariants, end to end: two real engines per seed, through the
// fake site and Supabase into a real PostgreSQL database and the fake S3, under a seeded
// schedule of saves, opens, closes, disconnections, crashes, lock breaks, remote and local
// deletions and simultaneous edits. ARMORY_E2E_SEED=<n> reproduces one seed;
// ARMORY_E2E_SEEDS=<count> changes the count (default 200).
public sealed class SeededRunTests(ITestOutputHelper output)
{

    [PostgresFact]
    public async Task Seeded_engines_preserve_every_save_and_converge_end_to_end()
    {
        var specific = Environment.GetEnvironmentVariable("ARMORY_E2E_SEED");
        var count = specific is not null ? 1 : int.TryParse(Environment.GetEnvironmentVariable("ARMORY_E2E_SEEDS"), out var c) ? c : 200;
        var first = specific is not null ? int.Parse(specific, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var watch = Stopwatch.StartNew();
        await using var world = await World.StartAsync();
        var mentor = await world.PersonAsync(ScenarioTests.Mentor, admin: true);
        var failures = new System.Collections.Concurrent.ConcurrentBag<(int Seed, Exception Error)>();
        await Parallel.ForEachAsync(Enumerable.Range(first, count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (seed, _) =>
        {
            try { await new SeededRun(world, mentor, seed).RunAsync(); }
            catch (Exception error) { failures.Add((seed, error)); }
        });
        output.WriteLine($"E2E_SEEDS count={count} first={first} elapsed={watch.Elapsed.TotalSeconds:F1}s failures={failures.Count}");
        output.WriteLine("E2E_CRASH_POINTS " + string.Join(',', SeededRun.Reached.Keys.Order(StringComparer.Ordinal)));
        // The schedule must actually land crashes inside uploads, side versions, downloads and
        // lock changes; otherwise the crash oracle would be vacuous.
        if (failures.IsEmpty && specific is null && count >= 200)
        {
            var missed = new[] { "after-capture", "before-commit", "after-blob", "after-commit-rpc", "after-commit", "before-side", "after-side",
                "before-Download", "after-download", "before-lock", "after-lock", "before-release", "after-release" }.Where(p => !SeededRun.Reached.ContainsKey(p)).ToArray();
            Assert.True(missed.Length == 0, "no seed crashed at " + string.Join(", ", missed));
        }
        if (!failures.IsEmpty)
        {
            var (seed, error) = failures.MinBy(f => f.Seed);
            output.WriteLine($"FAILED seeds: {string.Join(',', failures.Select(f => f.Seed).Order())}");
            throw new InvalidOperationException($"REPRO: ARMORY_E2E_SEED={seed} dotnet test tests/Armory.EndToEnd.Tests --filter Seeded | {error.Message}", error);
        }
    }
}

internal sealed class ScheduleRandom(int seed)
{
    private uint state = unchecked((uint)seed) + 0x9E3779B9u;
    internal int Next(int exclusive)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)(state % (uint)exclusive);
    }
}

internal sealed class SeededRun(World world, Person mentor, int seed)
{
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Reached = new(StringComparer.Ordinal);
    private const int Steps = 40;
    private readonly ScheduleRandom random = new(seed);
    private readonly string project = $"Seed {seed:D4}";
    private readonly Dictionary<string, (Computer Saver, byte[] Bytes)> everSaved = new(StringComparer.Ordinal);
    private readonly List<(Guid Id, string Hash)> history = [];
    private Guid projectId;
    private Computer a = null!, b = null!;
    private string[] paths = [];
    private int saves, step;

    public async Task RunAsync()
    {
        projectId = await mentor.Api.CreateProjectAsync(project, 2027, Guid.NewGuid());
        var alex = $"alex{seed}@students.test";
        var maria = $"maria{seed}@students.test";
        await mentor.Api.AddMemberAsync(projectId, alex, MemberRole.Student, Guid.NewGuid());
        await mentor.Api.AddMemberAsync(projectId, maria, MemberRole.Student, Guid.NewGuid());
        a = await world.ComputerAsync($"A{seed}", alex);
        b = await world.ComputerAsync($"B{seed}", maria);
        paths = [$"{project}/robot/plate.txt", $"{project}/robot/bracket.txt", $"{project}/class/gear.SLDPRT"];
        foreach (var path in paths) Save(a, path);
        await SyncAsync(a);
        await SyncAsync(b);
        for (step = 0; step < Steps; step++)
        {
            var c = random.Next(2) == 0 ? a : b;
            var other = c == a ? b : a;
            var path = paths[random.Next(paths.Length)];
            switch (random.Next(12))
            {
                case 0: case 1: Save(c, path); break;
                case 2: if (c.Read(path) is not null) c.Open(path); break;
                case 3: c.Close(path); break;
                case 4: c.Offline = true; break;
                case 5: c.Offline = false; break;
                case 6: if (random.Next(2) == 0) Save(c, path); await CrashAsync(c); break;
                case 7: await BreakLockAsync(path); break;
                case 8: await RemoteDeleteAsync(path); break;
                case 9: if (c.Read(path) is not null && !c.Disk.IsOpenNow(path)) c.Delete(path); break;
                case 10: Save(c, path); Save(other, path); await SyncAsync(other); break;
                case 11: await SyncAsync(other); break;
            }
            await SyncAsync(c);
            await CheckSafetyAsync();
            if (step % 16 == 15) await DrainAsync();
        }
        await DrainAsync();
    }

    private void Save(Computer computer, string path)
    {
        var bytes = Encoding.UTF8.GetBytes($"seed={seed};save={++saves};computer={computer.Name};path={path}");
        computer.Write(path, bytes);
        everSaved[Hash(bytes)] = (computer, bytes);
    }

    private async Task SyncAsync(Computer computer)
    {
        var report = await computer.SyncAsync();
        if (report.Online) await CheckReadOnlyAsync(computer);
    }

    // After an online pass, a file the other student is editing is read-only here, and a file
    // this computer holds is not.
    private async Task CheckReadOnlyAsync(Computer computer)
    {
        var other = computer == a ? b : a;
        var locks = await world.QueryAsync("select f.name, l.holder_email from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null",
            r => (Name: r.GetString(0), Holder: r.GetString(1)), ("p", projectId));
        foreach (var path in paths)
        {
            if (computer.Read(path) is null || !computer.Disk.Attributes.TryGetValue(path, out var applied)) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var holder = locks.FirstOrDefault(l => l.Name == name).Holder;
            if (holder is null) continue;
            var expected = holder == other.Sessions.Current!.Email ? Armory.Core.LockOwnership.OtherPerson
                : holder == computer.Sessions.Current!.Email ? Armory.Core.LockOwnership.ThisDevice : (Armory.Core.LockOwnership?)null;
            if (expected is { } want && applied != want)
                throw new InvalidOperationException($"{computer.Name}: {path} read-only state {applied}, expected {want} (step {step})");
        }
    }

    // The agent process dies, possibly in the middle of a pass; SolidWorks keeps its files open.
    private async Task CrashAsync(Computer computer)
    {
        var countdown = random.Next(14);
        computer.CrashPoint = point => { if (countdown-- == 0) { Reached[point] = true; throw new SimulatedCrash(point); } };
        computer.Restart();
        try { await computer.SyncAsync(); } catch (SimulatedCrash) { }
        computer.CrashPoint = null;
        computer.Restart();
    }

    private async Task<(Guid Id, bool Deleted, Guid? Current, bool Locked)?> FileAsync(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var rows = await world.QueryAsync("select f.id, f.deleted_at is not null, f.current_version_id, exists(select 1 from armory_locks l where l.file_id=f.id and l.broken_at is null) from armory_files f where f.project_id=@p and f.name=@n",
            r => (r.GetGuid(0), r.GetBoolean(1), r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), r.GetBoolean(3)), ("p", projectId), ("n", name));
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task BreakLockAsync(string path)
    {
        if (await FileAsync(path) is { Locked: true } file) await mentor.Api.BreakLockAsync(file.Id, mentor.Device, Guid.NewGuid());
    }

    private async Task RemoteDeleteAsync(string path)
    {
        if (await FileAsync(path) is not { Deleted: false } file) return;
        if (file.Locked) await mentor.Api.BreakLockAsync(file.Id, mentor.Device, Guid.NewGuid());
        if (!await mentor.Api.AcquireLockAsync(file.Id, mentor.Device, Guid.NewGuid())) return;
        await mentor.Api.TombstoneAsync(file.Id, file.Current, mentor.Device, Guid.NewGuid());
        await mentor.Api.ReleaseLockAsync(file.Id, mentor.Device, Guid.NewGuid());
    }

    private async Task CheckSafetyAsync()
    {
        foreach (var computer in new[] { a, b })
        {
            if (computer.Disk.OpenWriteViolations.Count > 0)
                throw new InvalidOperationException($"open file overwritten at step {step} on {computer.Name}: {string.Join("; ", computer.Disk.OpenWriteViolations)}");
            if (computer.Disk.UnpreservedOverwrites.Count > 0)
                throw new InvalidOperationException($"unpreserved bytes replaced at step {step} on {computer.Name}: {string.Join("; ", computer.Disk.UnpreservedOverwrites)}");
        }
        // History is an immutable, growing prefix and every version's bytes are stored.
        var rows = await world.QueryAsync("select x.id, x.content_sha256 from (select v.id, v.content_sha256, v.created_at from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p union all select s.id, s.content_sha256, s.created_at from armory_side_versions s join armory_files f on f.id=s.file_id where f.project_id=@p) x order by x.created_at, x.id",
            r => (r.GetGuid(0), r.GetString(1)), ("p", projectId));
        var known = rows.ToDictionary(r => r.Item1, r => r.Item2);
        foreach (var (id, hash) in history)
            if (!known.TryGetValue(id, out var still) || still != hash) throw new InvalidOperationException($"a version was purged or changed at step {step}");
        foreach (var row in rows.Where(r => !history.Any(h => h.Id == r.Item1))) history.Add(row);
        foreach (var (_, hash) in rows)
            if (!world.S3.Objects.ContainsKey(ContentObjectKey.FromHash(hash))) throw new InvalidOperationException($"version bytes missing from storage at step {step}");
        // A save is never lost: it is on the server or still in its computer's snapshots.
        var serverHashes = rows.Select(r => r.Item2).ToHashSet(StringComparer.Ordinal);
        foreach (var (hash, (saver, _)) in everSaved)
            if (!serverHashes.Contains(hash) && !saver.Snapshots.Enumerate().Any(s => s.Hash == hash) && !SameBytesOnDisk(saver, hash))
                throw new InvalidOperationException($"saved bytes lost at step {step}");
        await CheckAdvancesAsync();
    }

    private bool SameBytesOnDisk(Computer computer, string hash)
        => paths.Any(p => computer.Read(p) is { } bytes && Hash(bytes) == hash);

    // Every shared advance was made by the device holding the file's lock at that moment.
    private async Task CheckAdvancesAsync()
    {
        var changes = await world.QueryAsync("select kind, entity_id, payload::text from armory_change_feed where project_id=@p order by cursor",
            r => (Kind: r.GetString(0), Entity: r.GetGuid(1), Payload: System.Text.Json.Nodes.JsonNode.Parse(r.GetString(2))!), ("p", projectId));
        var holders = new Dictionary<Guid, string?>();
        foreach (var change in changes)
        {
            var file = change.Kind is "version" or "side_version" ? Guid.Parse(change.Payload["file_id"]!.GetValue<string>()) : change.Entity;
            switch (change.Kind)
            {
                case "lock_acquired": holders[file] = change.Payload["device_id"]!.GetValue<string>(); break;
                case "lock_released": case "lock_broken": holders[file] = null; break;
                case "version":
                    var device = change.Payload["device_id"]!.GetValue<string>();
                    if (holders.GetValueOrDefault(file) != device) throw new InvalidOperationException($"a shared advance by a device that did not hold the lock at step {step}");
                    break;
            }
        }
    }

    private async Task DrainAsync()
    {
        foreach (var computer in new[] { a, b })
        {
            computer.Offline = false;
            foreach (var open in computer.Disk.OpenFiles()) computer.Close(open);
        }
        for (var round = 0; round < 4; round++)
        {
            await SyncAsync(a); await CheckSafetyAsync();
            await SyncAsync(b); await CheckSafetyAsync();
        }
        // Every capture made by either computer is retrievable from server history, byte for byte.
        var server = (await world.QueryAsync("select v.content_sha256 from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p union select s.content_sha256 from armory_side_versions s join armory_files f on f.id=s.file_id where f.project_id=@p",
            r => r.GetString(0), ("p", projectId))).ToHashSet(StringComparer.Ordinal);
        foreach (var computer in new[] { a, b })
            foreach (var (snapshot, bytes) in computer.Snapshots.All())
            {
                if (!server.Contains(snapshot.Hash)) throw new InvalidOperationException($"{computer.Name}: a captured save never reached the server (step {step})");
                if (!world.S3.Objects.TryGetValue(ContentObjectKey.FromHash(snapshot.Hash), out var stored) || !stored.SequenceEqual(bytes))
                    throw new InvalidOperationException($"{computer.Name}: stored bytes differ from the capture (step {step})");
            }
        foreach (var hash in everSaved.Keys)
            if (!server.Contains(hash)) throw new InvalidOperationException($"saved bytes missing from history after an online drain (step {step})");
        // Both computers converge on the server's latest state.
        foreach (var path in paths)
        {
            var file = await FileAsync(path);
            var current = file is { Deleted: false, Current: { } id } ? (await world.QueryAsync("select content_sha256 from armory_versions where id=@v", r => r.GetString(0), ("v", id))).Single() : null;
            foreach (var computer in new[] { a, b })
            {
                var local = computer.Read(path) is { } bytes ? Hash(bytes) : null;
                if (local != current) throw new InvalidOperationException($"{computer.Name} did not converge on {path} (step {step}): local {Short(local)} server {Short(current)}");
            }
        }
    }

    private static string Short(string? hash) => hash is null ? "none" : hash[..8];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
