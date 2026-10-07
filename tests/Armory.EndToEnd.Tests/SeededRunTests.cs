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
// deletions and simultaneous edits, with v2 check out: students check files out (sometimes
// "Check out and open"), save only what is writable (SolidWorks cannot save over a read-only
// file), check in, undo, and now and then clear the read-only attribute and save anyway.
// Crashes land inside passes and inside check outs, check ins and undos, and the connection drops
// inside them too, right after a lock was taken or let go (the pass carries on offline).
// ARMORY_E2E_SEED=<n>
// reproduces one seed; ARMORY_E2E_SEEDS=<count> changes the count (default 200);
// ARMORY_E2E_TRACE=1 prints every step to standard error.
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
        output.WriteLine("E2E_CRASH_POINTS " + string.Join(',', SeededRun.Reached.Keys.Where(k => !k.StartsWith("cut:", StringComparison.Ordinal)).Order(StringComparer.Ordinal)));
        output.WriteLine("E2E_CUT_POINTS " + string.Join(',', SeededRun.Reached.Keys.Where(k => k.StartsWith("cut:", StringComparison.Ordinal)).Select(k => k[4..]).Order(StringComparer.Ordinal)));
        // The schedule must actually land crashes inside uploads, side versions, downloads and
        // lock changes; otherwise the crash oracle would be vacuous.
        if (failures.IsEmpty && specific is null && count >= 200)
        {
            var missed = new[] { "after-capture", "before-commit", "after-blob", "after-commit-rpc", "after-commit", "before-side", "after-side",
                "before-Download", "after-download", "before-lock", "after-lock", "before-release", "after-release" }.Where(p => !SeededRun.Reached.ContainsKey(p)).ToArray();
            Assert.True(missed.Length == 0, "no seed crashed at " + string.Join(", ", missed));
            // The connection must drop right after a lock was taken and right after one was let
            // go, or the oracle on files this computer let go of would be vacuous there.
            var uncut = new[] { "after-lock", "after-release" }.Where(p => !SeededRun.Reached.ContainsKey("cut:" + p)).ToArray();
            Assert.True(uncut.Length == 0, "no seed lost the connection at " + string.Join(", ", uncut));
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
    // Where the connection drops inside an action: a stream of its own, so the schedule of
    // steps and crashes is the one every seed always had.
    private readonly ScheduleRandom cuts = new(seed * 31 + 7);
    private readonly string project = $"Seed {seed:D4}";
    private readonly Dictionary<string, (Computer Saver, byte[] Bytes)> everSaved = new(StringComparer.Ordinal);
    // Bytes saved by a computer that did not have the file checked out (the read-only attribute
    // cleared by hand). They are kept, but never become the shared version.
    private readonly HashSet<string> savedWithoutCheckOut = new(StringComparer.Ordinal);
    // Check ins and undos asked for and not finished yet: their file may already be read-only.
    private readonly HashSet<(Computer, string)> asked = [];
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
        foreach (var path in paths) await SaveAsync(a, path);
        await SyncAsync(a);
        await SyncAsync(b);
        for (step = 0; step < Steps; step++)
        {
            var c = random.Next(2) == 0 ? a : b;
            var other = c == a ? b : a;
            var path = paths[random.Next(paths.Length)];
            var kind = random.Next(16);
            Trace($"step {step} {c.Name} case {kind} {path}");
            switch (kind)
            {
                case 0: case 1: await SaveAsync(c, path); break;
                case 2: if (c.Read(path) is not null) c.Open(path); break;
                case 3: c.Close(path); break;
                case 4: c.Offline = true; break;
                case 5: c.Offline = false; break;
                case 6: if (random.Next(2) == 0) await SaveAsync(c, path); await CrashAsync(c, path); break;
                case 7: await BreakLockAsync(path); break;
                case 8: await RemoteDeleteAsync(path); break;
                case 9: if (c.Read(path) is not null && !c.Disk.IsOpenNow(path)) c.Delete(path); break;
                case 10: await SaveAsync(c, path); await SaveAsync(other, path); await SyncAsync(other); break;
                case 11: await SyncAsync(other); break;
                case 12: await CheckOutAsync(c, path, open: random.Next(4) == 0); break;
                case 13: if (random.Next(2) == 0) await SaveAsync(c, path); await CheckInAsync(c, path); break;
                case 14:
                    if (random.Next(2) == 0) c.Close(path); // Undo refuses an open file
                    await UndoAsync(c, path);
                    break;
                case 15: await ForceWriteAsync(c, path); break;
            }
            await SyncAsync(c);
            await CheckSafetyAsync();
            if (step % 16 == 15) await DrainAsync();
        }
        await DrainAsync();
    }

    private static readonly bool Tracing = Environment.GetEnvironmentVariable("ARMORY_E2E_TRACE") == "1";
    private static void Trace(string line) { if (Tracing) Console.Error.WriteLine("TRACE " + line); }

    private byte[] Next(Computer computer, string path, string how) => Encoding.UTF8.GetBytes($"seed={seed};{how}={++saves};computer={computer.Name};path={path}");

    // Ctrl+S in SolidWorks: refused while the file is read-only (not checked out here). A
    // student who meets that usually checks the file out first, as the window asks.
    private async Task SaveAsync(Computer computer, string path)
    {
        if (computer.Read(path) is not null && computer.Disk.IsReadOnly(path) && random.Next(2) == 0) await CheckOutAsync(computer, path, open: false);
        var bytes = Next(computer, path, "save");
        try { computer.Save(path, bytes); }
        catch (IOException) { Trace($"  save refused {computer.Name} {path}"); return; } // SolidWorks could not save over a read-only file
        Trace($"  saved {computer.Name} {path} {Hash(bytes)[..8]}");
        everSaved[Hash(bytes)] = (computer, bytes);
    }

    // The read-only attribute cleared by hand, then a save anyway.
    private async Task ForceWriteAsync(Computer computer, string path)
    {
        // Only a file under the read-only rule needs its attribute cleared; a file the server
        // does not have (a new one, a removed one added again) is simply writable.
        var cleared = computer.Read(path) is not null && computer.Disk.IsReadOnly(path);
        var held = await HolderAsync(path) == computer.Sessions.Current!.DeviceId;
        var bytes = Next(computer, path, "forced");
        computer.ForceWrite(path, bytes);
        everSaved[Hash(bytes)] = (computer, bytes);
        if (cleared && !held) savedWithoutCheckOut.Add(Hash(bytes));
        Trace($"  forced {computer.Name} {path} {Hash(bytes)[..8]} cleared={cleared} held={held}");
    }

    private async Task CheckOutAsync(Computer computer, string path, bool open)
    {
        var (result, cut) = await MaybeCutAsync(computer, "after-lock", () => computer.Engine.CheckOutAsync([path], open));
        Trace($"  check out {computer.Name} {path}: {result.Ok} {result.Message}");
        if (result.Ok) asked.Remove((computer, path));
        // Taken just before the connection dropped: it is checked out here, and writable.
        if (cut && result.Ok && computer.Read(path) is not null)
        {
            if (await HolderAsync(path) != computer.Sessions.Current!.DeviceId)
                throw new InvalidOperationException($"{computer.Name}: the check out of {path} answered \"{result.Message}\" without a lock (step {step})");
            if (computer.Disk.IsReadOnly(path))
                throw new InvalidOperationException($"{computer.Name}: {path} is read-only though its lock was just taken here (step {step})");
        }
        if (cut) await ReconnectAsync(computer);
        if (result.Ok && open && computer.Read(path) is not null) computer.Open(path); // SolidWorks opens it
    }

    private async Task CheckInAsync(Computer computer, string path)
    {
        asked.Add((computer, path));
        var (result, cut) = await MaybeCutAsync(computer, "after-release", () => computer.Engine.CheckInAsync([path]));
        Trace($"  check in {computer.Name} {path}: {result.Ok} {result.Message}");
        if (cut) await ReconnectAsync(computer);
    }

    private async Task UndoAsync(Computer computer, string path)
    {
        asked.Add((computer, path));
        var (result, cut) = await MaybeCutAsync(computer, "after-release", () => computer.Engine.UndoCheckOutAsync([path]));
        Trace($"  undo {computer.Name} {path}: {result.Ok} {result.Message}");
        if (cut) await ReconnectAsync(computer);
    }

    // Now and then the connection drops inside an action, right after the lock was taken or let
    // go (the pass carries on offline, in the same process). True when it dropped.
    private async Task<(Armory.Agent.Engine.View.ActionResult Result, bool Cut)> MaybeCutAsync(Computer computer, string point,
        Func<Task<Armory.Agent.Engine.View.ActionResult>> action)
    {
        // Never inside a crash being staged (its own crash point stays), never while offline.
        if (computer.Offline || computer.CrashPoint is not null || cuts.Next(3) != 0) return (await action(), false);
        var fired = false;
        computer.Engine.CrashPoint = p =>
        {
            if (fired || p != point) return;
            fired = true;
            Reached["cut:" + p] = true;
            Trace($"  connection lost {computer.Name} at {p}");
            computer.Offline = true;
        };
        try { return (await action(), fired); }
        finally { computer.Engine.CrashPoint = null; }
    }

    // After a dropped connection: what this computer let go of is read-only now, and after one
    // more pass offline; then the connection comes back.
    private async Task ReconnectAsync(Computer computer)
    {
        await CheckLetGoAsync(computer);
        await computer.SyncAsync();
        await CheckLetGoAsync(computer);
        computer.Offline = false;
    }

    private async Task SyncAsync(Computer computer)
    {
        var report = await computer.SyncAsync();
        if (report.Online) await CheckReadOnlyAsync(computer);
        await CheckLetGoAsync(computer);
    }

    private async Task<Guid?> HolderAsync(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var rows = await world.QueryAsync("select l.holder_device_id from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and f.name=@n and l.broken_at is null",
            r => r.GetGuid(0), ("p", projectId), ("n", name));
        return rows.Count == 0 ? null : rows[0];
    }

    // The v2 read-only rule after an online pass: every file the server has a live version of is
    // read-only on this computer unless this computer has it checked out (decision D4), and a
    // file it has checked out is writable unless the student asked to check it in or undo it.
    private async Task CheckReadOnlyAsync(Computer computer)
    {
        var device = computer.Sessions.Current!.DeviceId;
        var live = await world.QueryAsync("select f.name, (select l.holder_device_id from armory_locks l where l.file_id=f.id and l.broken_at is null) from armory_files f where f.project_id=@p and f.deleted_at is null and f.current_version_id is not null",
            r => (Name: r.GetString(0), Holder: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1)), ("p", projectId));
        foreach (var path in paths)
        {
            if (computer.Read(path) is null) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var file = live.FirstOrDefault(f => f.Name == name);
            if (file.Name is null) continue; // the server has no live version: not under the rule
            var held = file.Holder == device;
            if (!held && !computer.Disk.IsReadOnly(path))
                throw new InvalidOperationException($"{computer.Name}: {path} is writable but this computer has not checked it out (step {step})");
            if (!held) asked.Remove((computer, path));
            else if (!asked.Contains((computer, path)) && computer.Disk.IsReadOnly(path))
                throw new InvalidOperationException($"{computer.Name}: {path} is checked out here but read-only (step {step})");
        }
    }

    // After any pass, online or offline: a file the server has live, whose newest lock change is
    // this computer letting it go, is read-only here. This computer knows it let the file go even
    // when the connection dropped right after, so no offline pass may leave it writable (a file
    // the team sees as available, edited here without a check out).
    private async Task CheckLetGoAsync(Computer computer)
    {
        var device = computer.Sessions.Current!.DeviceId.ToString();
        var changes = await world.QueryAsync("select kind, entity_id, coalesce(payload->>'device_id', '') from armory_change_feed where project_id=@p and kind in ('lock_acquired', 'lock_released', 'lock_broken', 'file_revived') order by cursor",
            r => (Kind: r.GetString(0), File: r.GetGuid(1), Device: r.GetString(2)), ("p", projectId));
        var last = new Dictionary<Guid, (string Kind, string Device)>();
        foreach (var change in changes) last[change.File] = (change.Kind, change.Device);
        var live = await world.QueryAsync("select id, name from armory_files where project_id=@p and deleted_at is null and current_version_id is not null",
            r => (Id: r.GetGuid(0), Name: r.GetString(1)), ("p", projectId));
        foreach (var (id, name) in live)
        {
            if (!last.TryGetValue(id, out var change) || change.Kind != "lock_released" || change.Device != device) continue;
            var path = paths.FirstOrDefault(p => p.EndsWith("/" + name, StringComparison.Ordinal));
            if (path is null || computer.Read(path) is null) continue;
            if (!computer.Disk.IsReadOnly(path))
                throw new InvalidOperationException($"{computer.Name}: {path} is writable after this computer let it go (step {step})");
        }
    }

    // The agent process dies, possibly in the middle of a pass, a check out, a check in or an
    // undo; SolidWorks keeps its files open. A check out, check in or undo asked for is durable.
    private async Task CrashAsync(Computer computer, string path)
    {
        var countdown = random.Next(14);
        computer.CrashPoint = point => { if (countdown-- == 0) { Reached[point] = true; Trace($"  crash {computer.Name} at {point}"); throw new SimulatedCrash(point); } };
        computer.Restart();
        var what = random.Next(8);
        try
        {
            if (what < 4) await computer.SyncAsync();
            else if (what < 6) await CheckInAsync(computer, path);
            else if (what < 7) await computer.Engine.CheckOutAsync([path]);
            else await UndoAsync(computer, path);
        }
        catch (SimulatedCrash) { }
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
        // A save made without a check out is kept, never shared (v2).
        var shared = (await world.QueryAsync("select v.content_sha256 from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p",
            r => r.GetString(0), ("p", projectId))).ToHashSet(StringComparer.Ordinal);
        if (savedWithoutCheckOut.Overlaps(shared)) throw new InvalidOperationException($"bytes saved without a check out became the shared version at step {step}");
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
                case "lock_released": case "lock_broken": case "file_revived": holders[file] = null; break;
                case "version":
                    var device = change.Payload["device_id"]!.GetValue<string>();
                    if (holders.GetValueOrDefault(file) != device) throw new InvalidOperationException($"a shared advance by a device that did not hold the lock at step {step}");
                    break;
            }
        }
    }

    // Everyone closes everything, goes online, and checks in every check out.
    private async Task DrainAsync()
    {
        foreach (var computer in new[] { a, b })
        {
            computer.Offline = false;
            foreach (var open in computer.Disk.OpenFiles()) computer.Close(open);
        }
        for (var round = 0; round < 4; round++)
        {
            foreach (var computer in new[] { a, b })
            {
                var device = computer.Sessions.Current!.DeviceId;
                List<string> held = [];
                foreach (var path in paths) if (await HolderAsync(path) == device) held.Add(path);
                if (held.Count > 0)
                {
                    foreach (var path in held) asked.Add((computer, path));
                    await computer.Engine.CheckInAsync(held);
                }
                await SyncAsync(computer);
                await CheckSafetyAsync();
            }
        }
        if (await world.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", projectId)) != 0)
            throw new InvalidOperationException($"a file is still checked out after the drain (step {step})");
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
                if (local is not null && !computer.Disk.IsReadOnly(path))
                    throw new InvalidOperationException($"{computer.Name}: {path} is writable after everything was checked in (step {step})");
            }
        }
    }

    private static string Short(string? hash) => hash is null ? "none" : hash[..8];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
