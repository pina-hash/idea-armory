using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Armory.Core;
using Xunit.Abstractions;

namespace Armory.Core.Tests;

// The seeded simulation for Explicit check out (v2). Same oracle as SimulationTests, with
// clients that check out, save while checked out, sometimes force a save without a check out,
// check in, undo, add files and re-add removed names, among crashes, lost connections, torn
// journal writes, lock breaks and server removals. See docs/core/simulation.md.
public sealed class CheckoutSimulationTests(ITestOutputHelper output)
{
    [Fact]
    public void Seeded_explicit_checkout_scenarios_preserve_every_save_and_converge()
    {
        var specific = Environment.GetEnvironmentVariable("ARMORY_SEED");
        var count = specific is not null ? 1 : Environment.GetEnvironmentVariable("ARMORY_STRESS") == "1" ? 1_000_000 : 10_000;
        var start = specific is not null ? int.Parse(specific, CultureInfo.InvariantCulture) : 0;
        var watch = Stopwatch.StartNew();
        var stateHashes = new List<string>();
        var coverage = new CheckoutCoverage();
        for (var i = 0; i < count; i++)
        {
            var seed = start + i;
            try
            {
                var simulation = new CheckoutSimulation(seed, coverage);
                simulation.Run();
                if (seed is >= 0 and < 100) stateHashes.Add($"{seed}:{simulation.FinalStateHash()}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"REPRO: ARMORY_SEED={seed} dotnet test --filter Seeded_explicit_checkout_scenarios | {error.Message}", error);
            }
        }
        output.WriteLine($"EXPLICIT scenarios={count} elapsed={watch.Elapsed.TotalSeconds:F3}s first_seed={start}");
        output.WriteLine($"EXPLICIT coverage {coverage}");
        if (stateHashes.Count > 0) output.WriteLine("EXPLICIT state_hashes=" + string.Join(',', stateHashes));
        if (specific is null) coverage.AssertEveryRouteRan();
        if (specific is null && count == 10_000) Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"Normal explicit simulation exceeded two minutes: {watch.Elapsed}.");
    }
}

// Counts across every scenario of a run, so a run that never reaches a route fails.
internal sealed class CheckoutCoverage
{
    internal int CheckOuts, CheckIns, AddCheckIns, Undos, Adds, Revivals, ForcedSaves, PutBacks, BlockedAdds, OfflineSaves, Releases;
    internal int SavesAfterTakeBack, TornWrites, MidSyncCrashes, LostAcknowledgments;
    internal readonly int[] Kept = new int[Enum.GetValues<SideVersionReason>().Length];

    // Counted and printed, but too rare to require of every run: an add whose name another
    // device took the lock for and crashed before its first version.
    private static readonly string[] Optional = ["blocked_adds"];

    private IEnumerable<(string Name, int Count)> Routes()
    {
        yield return ("check_outs", CheckOuts);
        yield return ("check_ins", CheckIns);
        yield return ("add_check_ins", AddCheckIns);
        yield return ("undos", Undos);
        yield return ("adds", Adds);
        yield return ("revivals", Revivals);
        yield return ("forced_saves", ForcedSaves);
        yield return ("put_backs", PutBacks);
        yield return ("blocked_adds", BlockedAdds);
        yield return ("offline_saves", OfflineSaves);
        yield return ("releases", Releases);
        yield return ("saves_after_take_back", SavesAfterTakeBack);
        yield return ("torn_writes", TornWrites);
        yield return ("mid_sync_crashes", MidSyncCrashes);
        yield return ("lost_acknowledgments", LostAcknowledgments);
        foreach (var reason in Enum.GetValues<SideVersionReason>()) yield return ("kept_" + reason, Kept[(int)reason]);
    }
    public override string ToString() => string.Join(' ', Routes().Select(r => $"{r.Name}={r.Count}"));
    internal void AssertEveryRouteRan()
    {
        foreach (var (name, count) in Routes().Where(r => !Optional.Contains(r.Name)))
            Assert.True(count > 0, $"The explicit simulation never reached {name}.");
    }
}

internal sealed class CheckoutSimulation
{
    // Who took the lock a shared write happens under.
    private enum Origin { CheckOut, Add, Delete, Mentor, Implicit }
    private static readonly VaultPath[] Paths =
    [
        Fixtures.Path("robot/plate.txt"), Fixtures.Path("robot/bracket.txt"), Fixtures.Path("class/design.txt"),
        Fixtures.Path("robot/gear.txt"), Fixtures.Path("class/notes.txt"),
    ];
    private static readonly VaultPath[] Ordered = [.. Paths.Order()];
    private const int SharedAtStart = 3; // the last two paths start on no computer and no server
    private readonly int seed;
    private readonly ScheduleRandom random;
    private readonly CheckoutCoverage coverage;
    private readonly Server server = new();
    private readonly Client[] clients;
    private readonly Dictionary<string, byte[]> everSaved = new(StringComparer.Ordinal);
    private readonly HashSet<string> savedWithoutCheckOut = new(StringComparer.Ordinal);
    private readonly List<StoredVersion> immutableHistory = [];
    private int sequence;
    private int step;
    private int crashCountdown = -1;

    internal CheckoutSimulation(int seed, CheckoutCoverage coverage)
    {
        this.seed = seed;
        this.coverage = coverage;
        random = new(seed);
        clients = Enumerable.Range(0, 2 + random.Next(2)).Select(i => new Client(i)).ToArray();
        foreach (var path in Paths)
        foreach (var client in clients)
        {
            client.Files[path] = new LocalFile();
            client.Attributes[path] = new DiskAttribute();
        }
        foreach (var path in Paths.Take(SharedAtStart))
        {
            var bytes = Encoding.UTF8.GetBytes("initial:" + path);
            var hash = Hash(bytes);
            server.Blobs[hash] = bytes;
            server.AddVersion(path, hash, "initial", false, "initial:" + path);
            foreach (var client in clients)
            {
                client.Files[path] = new LocalFile { Hash = hash, Base = server.Latest[path] };
                Apply(client, path);
            }
        }
    }

    internal void Run()
    {
        for (step = 0; step < 48; step++)
        {
            var client = clients[random.Next(clients.Length)];
            var path = Paths[random.Next(Paths.Length)];
            var kind = random.Next(20);
            // Most editing, checking in and undoing happens on files this client has checked out.
            if (kind is 1 or 2 or 4 or 15 or 16 or 18 && Paths.Where(p => Holds(client, p)).ToArray() is { Length: > 0 } held && random.Next(4) != 0)
                path = held[random.Next(held.Length)];
            try
            {
                switch (kind)
                {
                    case 0:
                    case 19:
                        CheckOut(client, path, open: random.Next(2) == 0);
                        break;
                    case 1: Edit(client, path); break;
                    case 2: Save(client, path); break;
                    case 3: if (client.Files[path].Hash is not null) client.Open.Add(path); break;
                    case 4: Close(client, path); break;
                    case 5: client.Online = false; break;
                    case 6: client.Online = true; break;
                    case 7: Crash(client); break;
                    case 8: BreakLock(path); break;
                    case 9: RemoteDelete(path); break;
                    case 10: LocalDelete(client, path); break;
                    case 11:
                        Save(client, path);
                        // The other client's own torn write (carried over) crashes that client.
                        var other = clients[(client.Number + 1) % clients.Length];
                        try { Save(other, path); }
                        catch (SimulatedCrash) { Crash(other); }
                        break;
                    case 12:
                        // A torn journal write: the next append is cut short, now or later (as in
                        // v1). The save goes to a writable file when there is one, so it journals.
                        client.Store.CrashAfter = random.Next(240);
                        if (Paths.Where(p => client.Files[p].Hash is not null && Writable(client, p)).ToArray() is { Length: > 0 } writable)
                            path = writable[random.Next(writable.Length)];
                        if (client.Files[path].Hash is null) Add(client, path);
                        else Save(client, path);
                        break;
                    case 13:
                        // A crash somewhere in a sync, sometimes right after a save, so the crash
                        // can also land after the server kept it but before the answer arrived.
                        if (random.Next(2) == 0 && Paths.Where(p => client.Files[p].Hash is not null && Writable(client, p)).ToArray() is { Length: > 0 } saved)
                            Save(client, saved[random.Next(saved.Length)]);
                        crashCountdown = random.Next(10);
                        Sync(client);
                        crashCountdown = -1;
                        break;
                    case 14: ForceSave(client, path); break;
                    case 15:
                        // Check in shares what is saved; the student may save first.
                        if (!Holds(client, path)) break;
                        if (client.Buffers.ContainsKey(path) && random.Next(2) == 0) Save(client, path);
                        client.Requests[path] = CheckoutRequest.CheckIn;
                        break;
                    case 16:
                        // Undo check out refuses an open file, so the student closes it first.
                        if (!Holds(client, path)) break;
                        Close(client, path);
                        client.Requests[path] = CheckoutRequest.Undo;
                        break;
                    case 17: Add(client, path); break;
                    case 18: Save(client, path); break;
                }
                Sync(client);
            }
            catch (SimulatedCrash) { Crash(client); }
            finally { crashCountdown = -1; }
            CheckSafety();
            if (step % 16 == 15) Drain();
        }
        Drain();
        coverage.TornWrites += clients.Sum(c => c.Store.TornAppends);
    }

    internal string FinalStateHash()
    {
        var canonical = string.Join('\n', server.Versions.Select(v => $"{v.Path}|{v.Revision.Id}|{v.Revision.Hash}|{v.Revision.Author}|{v.Side}|{v.OperationId}"))
            + "\n--latest--\n" + string.Join('\n', server.Latest.OrderBy(v => v.Key).Select(v => $"{v.Key}|{v.Value.Id}|{v.Value.Hash}|{v.Value.Author}"))
            + "\n--blobs--\n" + string.Join('\n', server.Blobs.OrderBy(v => v.Key).Select(v => $"{v.Key}|{Convert.ToHexStringLower(v.Value)}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private LockHolder? Holder(VaultPath path) => Owner(server.Locks.GetValueOrDefault(path));
    private bool Holds(Client client, VaultPath path) => Holder(path) == client.Actor.Identity;
    private LockOwnership Ownership(Client client, VaultPath path) => Holder(path) is not { } holder ? LockOwnership.Free
        : holder == client.Actor.Identity ? LockOwnership.ThisDevice
        : holder.Person == client.Person ? LockOwnership.MyOtherDevice : LockOwnership.OtherPerson;
    // The read-only attribute on disk, as this client's agent last applied it (CheckoutRules
    // .IsReadOnlyOnDisk for a file the server has; a file the server does not have, an add,
    // stays writable). Only the agent and the student change it, so a holder whose check out
    // was taken back keeps saving until its next pass.
    private static bool Writable(Client client, VaultPath path) => !client.Attributes[path].ReadOnly;
    // The agent applies the attribute from the server's lock table: every online pass, on a
    // check out, and on a staged download before it replaces the file. The oracle notes, from
    // the lock table directly and never through CheckoutRules, whether this device held the
    // lock and whether the server had the file.
    private void Apply(Client client, VaultPath path, bool? shared = null)
    {
        var attribute = client.Attributes[path];
        attribute.From = Ownership(client, path);
        attribute.Held = Holds(client, path);
        attribute.Shared = shared ?? client.Files[path].Base is { IsTombstone: false };
        Reapply(attribute);
    }
    // Offline the agent applies it again from the ownership it last knew, which also sets
    // the attribute again after the student cleared it.
    private static void Reapply(DiskAttribute attribute)
    {
        attribute.Cleared = false;
        attribute.ReadOnly = attribute.Shared && CheckoutRules.IsReadOnlyOnDisk(attribute.From);
    }

    // Check out (and "Check out and open"), as CheckoutRules.NextCheckOutStep says: a copy that
    // is missing or behind and closed is brought up to date first, and bytes saved without a
    // check out are kept and the shared version put back first (one pass does either), so the
    // lock is only ever taken over the live shared version.
    private void CheckOut(Client client, VaultPath path, bool open)
    {
        if (!client.Online) return;
        var step = NextCheckOutStep(client, path);
        if (step is CheckOutStep.DownloadFirst or CheckOutStep.KeepChangesFirst)
        {
            Sync(client);
            step = NextCheckOutStep(client, path);
        }
        if (step != CheckOutStep.TakeLock) return;
        if (server.Locks.GetValueOrDefault(path) is not (null or FreeLock or Broken)) return;
        Assert.True(TryAcquire(path, client, Origin.CheckOut));
        Apply(client, path);
        client.Requests.Remove(path);
        coverage.CheckOuts++;
        if (open) client.Open.Add(path);
    }
    private CheckOutStep NextCheckOutStep(Client client, VaultPath path)
    {
        var file = client.Files[path];
        return CheckoutRules.NextCheckOutStep(file.Base, file.Hash, server.Latest.GetValueOrDefault(path), client.Open.Contains(path));
    }

    // SolidWorks opens a read-only file read-only: there is nothing to save.
    private void Edit(Client client, VaultPath path)
    {
        if (client.Files[path].Hash is null) return;
        client.Open.Add(path);
        if (!Writable(client, path)) return;
        client.Buffers[path] = Encoding.UTF8.GetBytes($"seed={seed};save={++sequence};device={client.Device};path={path}");
        if (client.Online && Holds(client, path)) Transition(path, LockEvent.Edit, client.Actor);
    }

    // A save fails on a read-only file, as in SolidWorks; the edit stays in the open window.
    private void Save(Client client, VaultPath path)
    {
        if (!client.Buffers.ContainsKey(path)) Edit(client, path);
        if (!client.Buffers.TryGetValue(path, out var bytes) || !Writable(client, path)) return;
        client.Buffers.Remove(path);
        // A save after the student cleared the attribute is still a save without a check out.
        Write(client, path, bytes, forced: client.Attributes[path].Cleared);
    }

    private void Close(Client client, VaultPath path)
    {
        if (client.Buffers.ContainsKey(path)) Save(client, path);
        client.Buffers.Remove(path); // an edit that could not be saved is closed without saving
        client.Open.Remove(path);
    }

    // The student cleared the read-only attribute and saved anyway.
    private void ForceSave(Client client, VaultPath path)
    {
        if (client.Files[path].Hash is null || Writable(client, path)) return;
        client.Buffers.Remove(path);
        var bytes = Encoding.UTF8.GetBytes($"seed={seed};forced={++sequence};device={client.Device};path={path}");
        if (random.Next(2) == 0) client.Open.Add(path);
        client.Attributes[path].Cleared = true;
        client.Attributes[path].ReadOnly = false;
        coverage.ForcedSaves++;
        Write(client, path, bytes, forced: true);
    }

    // A new file, or a name this copy saw removed (re-adding it revives its history).
    private void Add(Client client, VaultPath path)
    {
        var file = client.Files[path];
        if (file.Hash is not null || file.Base is { IsTombstone: false }) return;
        var bytes = Encoding.UTF8.GetBytes($"seed={seed};add={++sequence};device={client.Device};path={path}");
        if (random.Next(2) == 0) client.Open.Add(path);
        // A file the student creates is writable; the server does not have it yet.
        var attribute = client.Attributes[path];
        attribute.Shared = false;
        attribute.Held = false;
        attribute.Cleared = false;
        attribute.ReadOnly = false;
        Write(client, path, bytes);
    }

    private void LocalDelete(Client client, VaultPath path)
    {
        var file = client.Files[path];
        if (file.Hash is null) return;
        file.Hash = null;
        file.Preserved = null;
        client.Journal.Append(new($"{client.Device}:delete:{++sequence}", IntentKind.Tombstone, path.Value, null, null, client.Person));
    }

    // Every save that is not forced is to a file the server does not have, or to one this
    // device held the lock of when its agent last made it writable (the holder may keep saving
    // after a Take back, until its next pass). A forced save is never shared.
    private void Write(Client client, VaultPath path, byte[] bytes, bool forced = false)
    {
        var file = client.Files[path];
        var hash = Hash(bytes);
        var attribute = client.Attributes[path];
        if (forced) savedWithoutCheckOut.Add(hash);
        else
        {
            Assert.True(!attribute.Shared || attribute.Held, $"a save to a shared file this device had not checked out at step {step}");
            if (attribute.Shared && !Holds(client, path)) coverage.SavesAfterTakeBack++;
        }
        file.Hash = hash;
        file.Preserved = null;
        everSaved[hash] = bytes.ToArray();
        if (!client.Online) coverage.OfflineSaves++;
        client.Recorder.Record($"{client.Device}:save:{++sequence}", path, client.Person, new MemoryStream(bytes));
    }

    private void Sync(Client client)
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
        foreach (var path in Ordered)
        {
            var file = client.Files[path];
            var ownership = Ownership(client, path);
            var open = client.Open.Contains(path);
            var request = client.Requests.GetValueOrDefault(path);
            // A file added while open stays checked out to its creator and is checked in when it
            // closes. Explicit check outs are never checked in automatically.
            if (request == CheckoutRequest.None && ownership == LockOwnership.ThisDevice && !open &&
                server.Origins.GetValueOrDefault(path) == Origin.Add)
                request = CheckoutRequest.CheckIn;
            var input = new SyncInput(path, file.Base, file.Hash, server.Latest.GetValueOrDefault(path), ownership, open, client.Online,
                server.BreakNotices.Contains((client.Device, path)), PreservedLocalHash: file.Preserved,
                Checkout: CheckoutMode.Explicit, Request: request);
            var plan = Reconciler.Plan(input);
            if (!client.Online)
            {
                Assert.Empty(plan.Actions);
                foreach (var intent in plan.Intents)
                {
                    // SaveRecorder already journals the immutable upload snapshot. Remaining
                    // lock/deletion requests carry stable ids and are safe to append repeatedly.
                    if (intent.Kind == IntentKind.Upload) continue;
                    client.Journal.Append(new($"{client.Device}:intent:{intent.Kind}:{path}:{file.Hash}:{file.Base?.Id}",
                        intent.Kind, path.Value, intent.Hash, null, client.Person));
                }
                Reapply(client.Attributes[path]);
                continue;
            }
            Execute(client, path, input, plan);
            FinishCheckOut(client, path, request);
            Apply(client, path);
        }
        if (!client.Online) return;
        CheckClientArchives(client);
        // An add holds its lock only while it is open: a closed add is checked in by the pass.
        foreach (var path in Paths)
            Assert.False(Holds(client, path) && server.Origins.GetValueOrDefault(path) == Origin.Add && !client.Open.Contains(path),
                $"a closed add kept its lock after a pass at step {step}");
    }

    private void Execute(Client client, VaultPath path, SyncInput input, SyncPlan plan)
    {
        var file = client.Files[path];
        foreach (var action in plan.Actions)
        {
            CrashPoint();
            switch (action.Kind)
            {
                case SyncActionKind.SaveSideVersion:
                    Assert.NotNull(action.Why);
                    Preserve(client, path, file.Hash!);
                    coverage.Kept[(int)action.Why.Value]++;
                    CrashPoint();
                    file.Preserved = file.Hash;
                    server.BreakNotices.Remove((client.Device, path));
                    break;
                case SyncActionKind.AcquireLockThenUpload:
                    // The harness labels the lock by what the server has, never by the plan: only a
                    // file the server has no live version of is an add.
                    var origin = input.Remote is { IsTombstone: false } ? Origin.Implicit : Origin.Add;
                    if (!TryAcquire(path, client, origin))
                    {
                        coverage.BlockedAdds++; // someone else holds the name; the next pass tries again
                        return;
                    }
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
                        $"unsynced local overwrite without side-version acknowledgment at step {step}");
                    if (input.Request == CheckoutRequest.Undo && file.Hash is not null && file.Hash != file.Base?.Hash)
                    {
                        Assert.True(server.Versions.Any(v => v.Side && v.Revision.Hash == file.Hash), $"undo replaced bytes the server does not keep at step {step}");
                        coverage.Undos++;
                    }
                    if (input.LocalHash is null && input.Lock is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice &&
                        input.Base is not null && Reconciler.SameRevision(input.Base, input.Remote)) coverage.PutBacks++;
                    Assert.True(Reconciler.SameRevision(input.Remote, server.Latest.GetValueOrDefault(path)));
                    Apply(client, path, shared: true); // set on the staged file before it replaces this one
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
                    // A deletion takes a lock only for itself when nobody holds the file.
                    if (Holder(path) is null) Assert.True(TryAcquire(path, client, Origin.Delete));
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
    }

    // Check in and Undo release once the file is clean (committed, or restored). So do adds
    // (once closed, in the same pass that added them) and a deletion's own lock. An explicit
    // check out is otherwise kept. The file is made read-only before the lock is released.
    private void FinishCheckOut(Client client, VaultPath path, CheckoutRequest request)
    {
        if (!Holds(client, path))
        {
            client.Requests.Remove(path); // taken back or gone: nothing is left to check in or undo
            return;
        }
        var file = client.Files[path];
        var origin = server.Origins[path];
        // The request was read before this pass ran; an add it created and committed while
        // closed is checked in now (D2).
        if (request == CheckoutRequest.None && origin == Origin.Add && !client.Open.Contains(path)) request = CheckoutRequest.CheckIn;
        if (file.Hash != file.Base?.Hash || request == CheckoutRequest.None && origin != Origin.Delete) return;
        Transition(path, LockEvent.Synced, client.Actor);
        CrashPoint();
        var attribute = client.Attributes[path];
        attribute.From = LockOwnership.Free;
        attribute.Held = false;
        attribute.Shared = file.Base is { IsTombstone: false };
        Reapply(attribute);
        CrashPoint();
        Transition(path, LockEvent.Release, client.Actor);
        server.Origins.Remove(path);
        coverage.Releases++;
        CrashPoint();
        client.Requests.Remove(path);
    }

    // The new invariants live here: every shared version is written under a lock its writer
    // took by an explicit check out or by adding the file, only at check in (or as the first
    // version of an add), and never with bytes saved without a check out.
    private void SharedWrite(Client client, VaultPath path, SyncInput expected, string? hash)
    {
        var holder = Holder(path);
        Assert.Equal(client.Actor.Identity, holder);
        Assert.True(Reconciler.SameRevision(expected.Remote, server.Latest.GetValueOrDefault(path)), "stale remote revision");
        var origin = server.Origins.GetValueOrDefault(path, Origin.Implicit);
        if (hash is not null)
        {
            Assert.True(origin is Origin.CheckOut or Origin.Add, $"a shared version under a lock taken by {origin}, not a check out or an add, at step {step}");
            var add = expected.Remote is null || expected.Remote.IsTombstone;
            Assert.True(add || expected.Request == CheckoutRequest.CheckIn, $"the shared file advanced before check in at step {step}");
            Assert.False(savedWithoutCheckOut.Contains(hash), $"a save made without a check out became the shared version at step {step}");
            if (expected.Remote is null) coverage.Adds++;
            else if (expected.Remote.IsTombstone) coverage.Revivals++;
            else if (origin == Origin.Add) coverage.AddCheckIns++;
            else coverage.CheckIns++;
            CopyBlob(client, hash);
        }
        else Assert.True(origin is Origin.CheckOut or Origin.Add or Origin.Delete, $"a removal under a lock taken by {origin} at step {step}");
        server.Advances.Add((client.Actor.Identity, holder));
        server.AddVersion(path, hash, client.Person, false, $"shared:{client.Device}:{path}:{expected.Remote?.Id}:{hash}");
    }

    private void Preserve(Client client, VaultPath path, string hash)
    {
        CopyBlob(client, hash);
        server.AddVersion(path, hash, client.Person, true, $"side:{client.Device}:{path}:{hash}");
    }
    private void CopyBlob(Client client, string hash)
    {
        if (server.Blobs.ContainsKey(hash)) return;
        var snapshot = client.Snapshots.Items.Values.First(v => v.Metadata.Hash == hash);
        server.Blobs.Add(hash, snapshot.Bytes.ToArray());
    }

    private bool TryAcquire(VaultPath path, Client client, Origin origin)
    {
        var state = server.Locks.GetValueOrDefault(path, new FreeLock());
        var result = LockMachine.Apply(state, LockEvent.Acquire, client.Actor, DateTimeOffset.UnixEpoch.AddSeconds(sequence));
        if (!result.Succeeded)
        {
            Assert.True(Owner(state) is { } other && other != client.Actor.Identity, result.Problem);
            return false;
        }
        server.Locks[path] = result.State;
        server.Origins[path] = origin;
        if (result.RecoveryOwner is { } owner) server.BreakNotices.Add((owner.Device, path));
        return true;
    }

    private void BreakLock(VaultPath path)
    {
        var state = server.Locks.GetValueOrDefault(path, new FreeLock());
        if (Owner(state) is null) return;
        var actor = new LockActor(new("Mentor", "admin"), true);
        Transition(path, LockEvent.RequestBreak, actor);
        Transition(path, LockEvent.ConfirmBreak, actor);
        server.Origins.Remove(path);
    }
    // A mentor removes a file on the site (Take back, then remove).
    private void RemoteDelete(VaultPath path)
    {
        if (server.Latest.GetValueOrDefault(path) is not { IsTombstone: false }) return;
        BreakLock(path);
        var actor = new LockActor(new("Mentor", "admin"), true);
        Transition(path, LockEvent.Acquire, actor);
        server.Origins[path] = Origin.Mentor;
        server.Advances.Add((actor.Identity, Owner(server.Locks[path])));
        server.AddVersion(path, null, "Mentor", false, $"remote-delete:{++sequence}");
        Transition(path, LockEvent.Release, actor);
        server.Origins.Remove(path);
    }
    private void Transition(VaultPath path, LockEvent action, LockActor actor)
    {
        var result = LockMachine.Apply(server.Locks.GetValueOrDefault(path, new FreeLock()), action, actor,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));
        Assert.True(result.Succeeded, result.Problem);
        server.Locks[path] = result.State;
        if (result.RecoveryOwner is { } owner) server.BreakNotices.Add((owner.Device, path));
    }
    private void CrashPoint(bool acknowledgment = false)
    {
        if (crashCountdown < 0) return;
        if (crashCountdown-- != 0) return;
        if (acknowledgment) coverage.LostAcknowledgments++;
        else coverage.MidSyncCrashes++;
        throw new SimulatedCrash();
    }
    // A crash loses open windows and unsaved edits. Check outs and requests are durable.
    private static void Crash(Client client)
    {
        client.Open.Clear();
        client.Buffers.Clear();
        client.Store.CrashAfter = null;
        client.NeedsRecovery = true;
        client.Restart();
    }
    // Every client saves what it can, closes everything, goes online and checks in every check out.
    private void Drain()
    {
        crashCountdown = -1;
        foreach (var client in clients)
        {
            client.Store.CrashAfter = null;
            foreach (var path in client.Buffers.Keys.ToArray()) Save(client, path);
            client.Buffers.Clear();
            client.Open.Clear();
            client.Online = true;
        }
        for (var round = 0; round < 5; round++)
            foreach (var client in clients)
            {
                foreach (var path in Paths.Where(p => Holds(client, p))) client.Requests[path] = CheckoutRequest.CheckIn;
                Sync(client);
                CheckSafety();
            }
        foreach (var path in Paths) Assert.True(Holder(path) is null, $"{path} is still checked out after the drain");
        foreach (var client in clients)
        foreach (var path in Paths)
            Assert.Equal(server.Latest.GetValueOrDefault(path)?.Hash, client.Files[path].Hash);
        foreach (var saved in everSaved)
        {
            Assert.True(server.Versions.Any(v => v.Revision.Hash == saved.Key), "saved bytes missing from version history after idle");
            Assert.Equal(saved.Value, server.Blobs[saved.Key]);
        }
    }
    private void CheckClientArchives(Client client)
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
        {
            if (version.Revision.Hash is { } hash) Assert.True(server.Blobs.ContainsKey(hash), "version bytes purged");
            if (!version.Side && version.Revision.Hash is { } shared) Assert.DoesNotContain(shared, savedWithoutCheckOut);
        }
        foreach (var advance in server.Advances) Assert.Equal(advance.Actor, advance.Holder);
        foreach (var saved in everSaved)
            Assert.True(server.Blobs.ContainsKey(saved.Key) || clients.Any(c => c.Snapshots.Items.Values.Any(v => v.Metadata.Hash == saved.Key)), "saved bytes lost during interruption");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static LockHolder? Owner(FileLock? state) => state switch { HeldByMe me => me.Holder, HeldByOther other => other.Holder, _ => null };

    private sealed class ArchiveSink(CheckoutSimulation simulation, Client client) : IIntentSink
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
            simulation.CrashPoint(acknowledgment: true); // Server effect and id committed; acknowledgment lost.
        }
    }
    private sealed record StoredVersion(VaultPath Path, Revision Revision, bool Side, string OperationId);
    private sealed class Server
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public List<StoredVersion> Versions { get; } = [];
        public Dictionary<VaultPath, Revision> Latest { get; } = [];
        public Dictionary<VaultPath, FileLock> Locks { get; } = [];
        public Dictionary<VaultPath, Origin> Origins { get; } = [];
        public HashSet<(string Device, VaultPath Path)> BreakNotices { get; } = [];
        public Dictionary<string, JournalEntry> Applied { get; } = new(StringComparer.Ordinal);
        public List<(LockHolder Actor, LockHolder? Holder)> Advances { get; } = [];
        private readonly HashSet<string> operations = new(StringComparer.Ordinal);
        public void AddVersion(VaultPath path, string? hash, string author, bool side, string operation)
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
    private sealed class DiskAttribute
    {
        internal bool ReadOnly;
        internal LockOwnership From;  // the ownership the agent last applied it from
        internal bool Shared;         // the copy's base was a live server version then
        internal bool Held;           // the oracle: the lock table said this device held the lock then
        internal bool Cleared;        // the student cleared it; the agent sets it again
    }
    private sealed class Client
    {
        internal int Number { get; }
        internal string Device => $"device-{Number}";
        internal string Person => Number == 2 ? "Alex" : Number == 0 ? "Alex" : "Maria";
        internal LockActor Actor => new(new(Person, Device), false);
        internal bool Online = true;
        internal bool NeedsRecovery;
        internal long Clock;
        internal Dictionary<VaultPath, LocalFile> Files { get; } = [];
        internal Dictionary<VaultPath, DiskAttribute> Attributes { get; } = [];
        internal Dictionary<VaultPath, byte[]> Buffers { get; } = [];
        internal HashSet<VaultPath> Open { get; } = [];
        internal Dictionary<VaultPath, CheckoutRequest> Requests { get; } = [];
        internal HashSet<string> Recovery { get; } = new(StringComparer.Ordinal);
        internal MemoryJournalStore Store { get; } = new();
        internal MemorySnapshots Snapshots { get; } = new();
        internal OfflineJournal Journal { get; private set; }
        internal SaveRecorder Recorder { get; private set; }
        internal Client(int number)
        {
            Number = number;
            Journal = new(Store);
            Recorder = new(Snapshots, Journal);
        }
        internal void Restart() { Journal = new(Store); Recorder = new(Snapshots, Journal); }
    }
}
