using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Armory.Core;
using Xunit.Abstractions;

namespace Armory.Core.Tests;

public sealed class SimulationTests(ITestOutputHelper output)
{
    [Fact]
    public void Seeded_scenarios_preserve_every_save_and_converge()
    {
        var specific = Environment.GetEnvironmentVariable("ARMORY_SEED");
        var count = specific is not null ? 1 : Environment.GetEnvironmentVariable("ARMORY_STRESS") == "1" ? 1_000_000 : 10_000;
        var start = specific is not null ? int.Parse(specific, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var seed = start + i;
            try { new Simulation(seed).Run(); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"REPRO: ARMORY_SEED={seed} dotnet test --filter Seeded_scenarios | {error.Message}", error);
            }
        }
        output.WriteLine($"SIMULATION scenarios={count} elapsed={watch.Elapsed.TotalSeconds:F3}s first_seed={start}");
        if (specific is null && count == 10_000) Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"Normal simulation exceeded two minutes: {watch.Elapsed}.");
    }
}

// The schedule PRNG is specified here rather than depending on System.Random's runtime implementation.
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

internal sealed class Simulation
{
    private static readonly VaultPath[] Paths = [Fixtures.Path("robot/plate.txt"), Fixtures.Path("robot/bracket.txt"), Fixtures.Path("class/design.txt")];
    private readonly int seed;
    private readonly ScheduleRandom random;
    private readonly FakeServer server = new();
    private readonly FakeClient[] clients;
    private readonly Dictionary<string, byte[]> everSaved = new(StringComparer.Ordinal);
    private readonly List<StoredVersion> immutableHistory = [];
    private int sequence;
    private int step;
    private int crashCountdown = -1;

    internal Simulation(int seed)
    {
        this.seed = seed;
        random = new(seed);
        clients = Enumerable.Range(0, 2 + random.Next(2)).Select(i => new FakeClient(i)).ToArray();
        foreach (var path in Paths)
        {
            var bytes = Encoding.UTF8.GetBytes("initial:" + path);
            var hash = Hash(bytes);
            server.Blobs[hash] = bytes;
            server.AddVersion(path, hash, "initial", false, "initial:" + path);
            foreach (var client in clients) client.Files[path] = new LocalFile { Hash = hash, Base = server.Latest[path] };
        }
    }

    internal void Run()
    {
        for (step = 0; step < 48; step++)
        {
            var client = clients[random.Next(clients.Length)];
            var path = Paths[random.Next(Paths.Length)];
            try
            {
                switch (random.Next(13))
                {
                    case 0: Edit(client, path); break;
                    case 1: Save(client, path); break;
                    case 2: client.Open.Add(path); break;
                    case 3:
                        if (client.Buffers.ContainsKey(path)) Save(client, path);
                        client.Open.Remove(path);
                        break;
                    case 4: client.Online = false; break;
                    case 5: client.Online = true; break;
                    case 6: Crash(client); break;
                    case 7: BreakLock(path); break;
                    case 8: RemoteDelete(path); break;
                    case 9:
                        client.Files[path].Hash = null;
                        client.Files[path].Preserved = null;
                        AppendIntent(client, new($"{client.Device}:delete:{++sequence}", IntentKind.Tombstone, path.Value, null, null, client.Person));
                        break;
                    case 10:
                        Save(client, path);
                        Save(clients[(client.Number + 1) % clients.Length], path);
                        break;
                    case 11:
                        client.Store.CrashAfter = random.Next(240);
                        Save(client, path);
                        break;
                    case 12:
                        crashCountdown = random.Next(8);
                        Sync(client);
                        crashCountdown = -1;
                        break;
                }
                Sync(client);
            }
            catch (SimulatedCrash) { Crash(client); }
            finally { crashCountdown = -1; }
            CheckSafety();
            if (step % 16 == 15) Drain();
        }
        Drain();
    }

    private void Edit(FakeClient client, VaultPath path)
    {
        client.Open.Add(path);
        client.Buffers[path] = Encoding.UTF8.GetBytes($"seed={seed};save={++sequence};device={client.Device};path={path}");
        if (client.Online)
        {
            var state = server.Locks.GetValueOrDefault(path, new FreeLock());
            if (state is FreeLock or Broken) Transition(path, LockEvent.Acquire, client.Actor);
            if (Owner(server.Locks.GetValueOrDefault(path)) == client.Actor.Identity) Transition(path, LockEvent.Edit, client.Actor);
        }
    }

    private void Save(FakeClient client, VaultPath path)
    {
        if (!client.Buffers.ContainsKey(path)) Edit(client, path);
        var bytes = client.Buffers[path];
        client.Buffers.Remove(path);
        var hash = Hash(bytes);
        client.Files[path].Hash = hash;
        client.Files[path].Preserved = null;
        everSaved[hash] = bytes.ToArray();
        var id = $"{client.Device}:save:{++sequence}";
        client.Recorder.Record(id, path, client.Person, new MemoryStream(bytes));
    }

    private void Sync(FakeClient client)
    {
        client.Clock++;
        if (client.NeedsRecovery)
        {
            client.Recorder.Recover();
            client.NeedsRecovery = false;
        }
        if (client.Online)
        {
            client.Journal.Replay(new ArchiveSink(this, client));
            CrashPoint();
        }
        foreach (var path in Paths.Order())
        {
            var file = client.Files[path];
            var holder = Owner(server.Locks.GetValueOrDefault(path));
            var ownership = holder is null ? LockOwnership.Free : holder == client.Actor.Identity ? LockOwnership.ThisDevice
                : holder.Person == client.Person ? LockOwnership.MyOtherDevice : LockOwnership.OtherPerson;
            var broken = server.BreakNotices.Contains((client.Device, path));
            var input = new SyncInput(path, file.Base, file.Hash, server.Latest.GetValueOrDefault(path), ownership,
                client.Open.Contains(path), client.Online, broken, PreservedLocalHash: file.Preserved);
            var plan = Reconciler.Plan(input);
            if (!client.Online)
            {
                Assert.Empty(plan.Actions);
                foreach (var intent in plan.Intents)
                {
                    // SaveRecorder already journals the immutable upload snapshot. Remaining
                    // lock/deletion requests carry stable ids and are safe to append repeatedly.
                    if (intent.Kind == IntentKind.Upload) continue;
                    AppendIntent(client, new($"{client.Device}:intent:{intent.Kind}:{path}:{file.Hash}:{file.Base?.Id}",
                        intent.Kind, path.Value, intent.Hash, null, client.Person));
                }
                continue;
            }
            foreach (var action in plan.Actions)
            {
                CrashPoint();
                switch (action.Kind)
                {
                    case SyncActionKind.SaveSideVersion:
                        Preserve(client, path, file.Hash!);
                        CrashPoint();
                        file.Preserved = file.Hash;
                        server.BreakNotices.Remove((client.Device, path));
                        break;
                    case SyncActionKind.AcquireLockThenUpload:
                        Transition(path, LockEvent.Acquire, client.Actor);
                        CrashPoint();
                        goto case SyncActionKind.Upload;
                    case SyncActionKind.Upload:
                        SharedWrite(client, path, input, file.Hash);
                        CrashPoint();
                        file.Base = server.Latest[path];
                        file.Preserved = null;
                        Transition(path, LockEvent.Synced, client.Actor);
                        break;
                    case SyncActionKind.Download:
                        Assert.False(client.Open.Contains(path), $"open overwrite at step {step}");
                        Assert.True(file.Hash is null || file.Hash == file.Base?.Hash || file.Preserved == file.Hash,
                            $"unsynced local overwrite without side-version acknowledgement at step {step}");
                        Assert.True(Reconciler.SameRevision(input.Remote, server.Latest.GetValueOrDefault(path)));
                        file.Hash = server.Latest[path].Hash;
                        CrashPoint();
                        file.Base = server.Latest[path];
                        file.Preserved = null;
                        break;
                    case SyncActionKind.MoveLocalToRecovery:
                        Assert.False(client.Open.Contains(path), $"open recovery move at step {step}");
                        Assert.True(file.Hash == file.Base?.Hash || file.Preserved == file.Hash, "unpreserved recovery move");
                        client.Recovery.Add(file.Hash!);
                        CrashPoint();
                        file.Hash = null;
                        CrashPoint();
                        file.Base = server.Latest[path];
                        file.Preserved = null;
                        server.BreakNotices.Remove((client.Device, path));
                        break;
                    case SyncActionKind.ProposeTombstone:
                        if (Owner(server.Locks.GetValueOrDefault(path)) is null) Transition(path, LockEvent.Acquire, client.Actor);
                        CrashPoint();
                        SharedWrite(client, path, input, null);
                        CrashPoint();
                        file.Base = server.Latest[path];
                        Transition(path, LockEvent.Synced, client.Actor);
                        break;
                    case SyncActionKind.None:
                        if (file.Hash == input.Remote?.Hash) file.Base = input.Remote;
                        if (file.Hash is null) server.BreakNotices.Remove((client.Device, path));
                        break;
                    case SyncActionKind.NotifyNewerVersionWaiting:
                    case SyncActionKind.Refuse:
                        break;
                    default: throw new InvalidOperationException("Unknown simulation action.");
                }
            }
            if (!client.Open.Contains(path) && Owner(server.Locks.GetValueOrDefault(path)) == client.Actor.Identity &&
                (file.Hash == file.Base?.Hash || file.Preserved == file.Hash))
            {
                Transition(path, LockEvent.Synced, client.Actor);
                Transition(path, LockEvent.Release, client.Actor);
            }
        }
        if (client.Online) CheckClientArchives(client);
    }

    private void SharedWrite(FakeClient client, VaultPath path, SyncInput expected, string? hash)
    {
        Assert.Equal(client.Actor.Identity, Owner(server.Locks.GetValueOrDefault(path)));
        Assert.True(Reconciler.SameRevision(expected.Remote, server.Latest.GetValueOrDefault(path)), "stale remote revision");
        if (hash is not null) CopyBlob(client, hash);
        var holder = Owner(server.Locks[path]);
        server.Advances.Add((client.Actor.Identity, holder));
        server.AddVersion(path, hash, client.Person, false, $"shared:{client.Device}:{path}:{expected.Remote?.Id}:{hash}");
    }

    private void Preserve(FakeClient client, VaultPath path, string hash)
    {
        CopyBlob(client, hash);
        server.AddVersion(path, hash, client.Person, true, $"side:{client.Device}:{path}:{hash}");
    }
    private void CopyBlob(FakeClient client, string hash)
    {
        if (server.Blobs.ContainsKey(hash)) return;
        var snapshot = client.Snapshots.Items.Values.First(v => v.Metadata.Hash == hash);
        server.Blobs.Add(hash, snapshot.Bytes.ToArray());
    }
    private void AppendIntent(FakeClient client, JournalEntry entry) => client.Journal.Append(entry);

    private void BreakLock(VaultPath path)
    {
        var state = server.Locks.GetValueOrDefault(path, new FreeLock());
        if (Owner(state) is null) return;
        var actor = new LockActor(new("Mentor", "admin"), true);
        Transition(path, LockEvent.RequestBreak, actor);
        Transition(path, LockEvent.ConfirmBreak, actor);
    }
    private void RemoteDelete(VaultPath path)
    {
        BreakLock(path);
        var actor = new LockActor(new("Mentor", "admin"), true);
        Transition(path, LockEvent.Acquire, actor);
        server.Advances.Add((actor.Identity, Owner(server.Locks[path])));
        server.AddVersion(path, null, "Mentor", false, $"remote-delete:{++sequence}");
        Transition(path, LockEvent.Release, actor);
    }
    private void Transition(VaultPath path, LockEvent action, LockActor actor)
    {
        var result = LockMachine.Apply(server.Locks.GetValueOrDefault(path, new FreeLock()), action, actor,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));
        Assert.True(result.Succeeded, result.Problem);
        server.Locks[path] = result.State;
        if (result.RecoveryOwner is { } owner) server.BreakNotices.Add((owner.Device, path));
    }
    private void CrashPoint()
    {
        if (crashCountdown < 0) return;
        if (crashCountdown-- == 0) throw new SimulatedCrash();
    }
    private static void Crash(FakeClient client)
    {
        client.Open.Clear();
        client.Buffers.Clear();
        client.Store.CrashAfter = null;
        client.NeedsRecovery = true;
        client.Restart();
    }
    private void Drain()
    {
        crashCountdown = -1;
        foreach (var client in clients)
        {
            client.Store.CrashAfter = null;
            foreach (var path in client.Buffers.Keys.ToArray()) Save(client, path);
            client.Open.Clear();
            client.Online = true;
        }
        for (var round = 0; round < 5; round++)
            foreach (var client in clients) { Sync(client); CheckSafety(); }
        foreach (var client in clients)
        foreach (var path in Paths)
            Assert.Equal(server.Latest[path].Hash, client.Files[path].Hash);
        foreach (var saved in everSaved)
        {
            Assert.True(server.Versions.Any(v => v.Revision.Hash == saved.Key), "saved bytes missing from version history after idle");
            Assert.Equal(saved.Value, server.Blobs[saved.Key]);
        }
    }
    private void CheckClientArchives(FakeClient client)
    {
        foreach (var captured in client.Snapshots.Items.Values)
        {
            Assert.True(server.Versions.Any(v => v.Revision.Hash == captured.Metadata.Hash), "online idle client missing a saved version");
            Assert.Equal(captured.Bytes, server.Blobs[captured.Metadata.Hash]);
        }
    }
    private void CheckSafety()
    {
        Assert.True(server.Versions.Count >= immutableHistory.Count, "a version was purged");
        for (var i = 0; i < immutableHistory.Count; i++) Assert.Equal(immutableHistory[i], server.Versions[i]);
        immutableHistory.AddRange(server.Versions.Skip(immutableHistory.Count));
        foreach (var version in server.Versions)
            if (version.Revision.Hash is { } hash) Assert.True(server.Blobs.ContainsKey(hash), "version bytes purged");
        foreach (var advance in server.Advances) Assert.Equal(advance.Actor, advance.Holder);
        foreach (var saved in everSaved)
            Assert.True(server.Blobs.ContainsKey(saved.Key) || clients.Any(c => c.Snapshots.Items.Values.Any(v => v.Metadata.Hash == saved.Key)), "saved bytes lost during interruption");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static LockHolder? Owner(FileLock? state) => state switch { HeldByMe me => me.Holder, HeldByOther other => other.Holder, _ => null };

    private sealed class ArchiveSink(Simulation simulation, FakeClient client) : IIntentSink
    {
        public void ApplyOnce(JournalEntry entry)
        {
            if (simulation.server.Applied.TryGetValue(entry.Id, out var previous)) { Assert.Equal(previous, entry); return; }
            if (entry.Kind == IntentKind.Upload)
            {
                var snapshot = client.Snapshots.Items[entry.SnapshotId!];
                Assert.Equal(entry.Hash, snapshot.Metadata.Hash);
                simulation.Preserve(client, Fixtures.Path(entry.Path), entry.Hash!);
            }
            // Lock and tombstone intents schedule current-state reconciliation, never replay
            // an obsolete command blindly. The following Sync loop reads fresh server state.
            simulation.server.Applied.Add(entry.Id, entry);
            simulation.CrashPoint(); // Server effect and id committed; acknowledgement lost.
        }
    }
    private sealed record StoredVersion(VaultPath Path, Revision Revision, bool Side, string OperationId);
    private sealed class FakeServer
    {
        internal Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        internal List<StoredVersion> Versions { get; } = [];
        internal Dictionary<VaultPath, Revision> Latest { get; } = [];
        internal Dictionary<VaultPath, FileLock> Locks { get; } = [];
        internal HashSet<(string Device, VaultPath Path)> BreakNotices { get; } = [];
        internal Dictionary<string, JournalEntry> Applied { get; } = new(StringComparer.Ordinal);
        internal List<(LockHolder Actor, LockHolder? Holder)> Advances { get; } = [];
        private readonly HashSet<string> operations = new(StringComparer.Ordinal);
        internal void AddVersion(VaultPath path, string? hash, string author, bool side, string operation)
        {
            if (!operations.Add(operation)) return;
            var revision = new Revision($"v{Versions.Count}", hash, author);
            Versions.Add(new(path, revision, side, operation));
            if (!side) Latest[path] = revision;
        }
    }
    private sealed class LocalFile
    {
        internal string? Hash;
        internal Revision? Base;
        internal string? Preserved;
    }
    private sealed class FakeClient
    {
        internal int Number { get; }
        internal string Device => $"device-{Number}";
        internal string Person => Number == 2 ? "Alex" : Number == 0 ? "Alex" : "Maria";
        internal LockActor Actor => new(new(Person, Device), false);
        internal bool Online = true;
        internal bool NeedsRecovery;
        internal long Clock;
        internal Dictionary<VaultPath, LocalFile> Files { get; } = [];
        internal Dictionary<VaultPath, byte[]> Buffers { get; } = [];
        internal HashSet<VaultPath> Open { get; } = [];
        internal HashSet<string> Recovery { get; } = new(StringComparer.Ordinal);
        internal MemoryJournalStore Store { get; } = new();
        internal MemorySnapshots Snapshots { get; } = new();
        internal OfflineJournal Journal { get; private set; }
        internal SaveRecorder Recorder { get; private set; }
        internal FakeClient(int number)
        {
            Number = number;
            Journal = new(Store);
            Recorder = new(Snapshots, Journal);
        }
        internal void Restart() { Journal = new(Store); Recorder = new(Snapshots, Journal); }
    }
}
