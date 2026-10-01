using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.Storage;

namespace Armory.Agent.Engine;

public sealed partial class SyncEngine
{
    private const string EarlierSaveReason = "earlier save, kept";

    // Executes one Core action. Returns false to stop this file's plan until the next pass.
    private async Task<bool> ExecuteAsync(SyncAction action, SyncInput input, FileState st, ProjectState project, RemoteFile? remote, CancellationToken ct)
    {
        var path = input.Path;
        try
        {
            switch (action.Kind)
            {
                case SyncActionKind.None:
                    if (input.LocalHash == input.Remote?.Hash) st.SetBase(input.Remote);
                    if (input.LocalHash is null) st.BreakNotice = false;
                    if (input.LocalHash is not null && input.LocalHash == st.BaseHash) Complete(st, input.LocalHash);
                    return true;
                case SyncActionKind.NotifyNewerVersionWaiting:
                    st.NewerWaiting = true;
                    st.NewerAuthor = remote?.Current?.Author;
                    return true;
                case SyncActionKind.Refuse:
                    st.Refusal = action.Reason;
                    st.RefusalKind = AttentionKinds.Refused;
                    refused++;
                    return false;
                case SyncActionKind.Download:
                    return await DownloadAsync(st, project, path, input, remote!, ct);
                case SyncActionKind.Upload:
                case SyncActionKind.AcquireLockThenUpload:
                    return await UploadAsync(st, project, path, input, action, ct);
                case SyncActionKind.SaveSideVersion:
                    return await PreserveAsync(st, project, path, input.LocalHash!, action.ReleaseNotChecked, input.SavedRelease, ct);
                case SyncActionKind.MoveLocalToRecovery:
                    if (IsOpenNow(path)) { st.NewerWaiting = true; return false; }
                    var moved = fs.MoveToRecovery(path, input.LocalHash!);
                    if (!moved.Succeeded) { problems.Add($"{path}: {moved.Problem}"); return false; }
                    local.Remove(path.Value);
                    st.SetBase(input.Remote);
                    st.Preserved = null;
                    st.BreakNotice = false;
                    return true;
                case SyncActionKind.ProposeTombstone:
                    return await TombstoneAsync(st, path, input, ct);
                default:
                    throw new InvalidOperationException($"Unknown action {action.Kind}.");
            }
        }
        catch (ArmoryOfflineException error) { problems.Add($"{path}: {error.Message}"); online = false; return false; }
        catch (BlobRefusedException error) { problems.Add($"{path}: {error.Message}"); return false; }
        catch (HashMismatchException error) { problems.Add($"{path}: {error.Message}"); return false; }
        catch (ArmoryRpcException error) { problems.Add($"{path}: {error.Message}"); return false; }
    }

    private async Task<bool> DownloadAsync(FileState st, ProjectState project, VaultPath path, SyncInput input, RemoteFile remote, CancellationToken ct)
    {
        var current = remote.Current!;
        // Recheck right before writing: the plan was made a moment ago.
        if (IsOpenNow(path)) { st.NewerWaiting = true; st.NewerAuthor = current.Author; return false; }
        var staging = fs.CreateStaging(out var stagingName);
        try
        {
            await deps.Blobs.DownloadAsync(project.Id, current.Hash, current.Bytes, staging, ct);
            staging.Position = 0;
            CrashPoint?.Invoke("before-replace");
            var outcome = fs.Replace(path, input.LocalHash, staging);
            if (!outcome.Succeeded) { problems.Add($"{path}: {outcome.Problem}"); return false; }
        }
        finally
        {
            await staging.DisposeAsync();
            fs.DeleteStaging(stagingName);
        }
        local[path.Value] = new LocalFile(path, current.Hash, current.Bytes);
        st.SetBase(input.Remote);
        st.Preserved = null;
        st.LastCaptured = current.Hash;
        st.AppliedOwnership = null;
        Complete(st, current.Hash);
        downloaded++;
        lastActivity = deps.Clock.GetUtcNow();
        Save();
        CrashPoint?.Invoke("after-download");
        return true;
    }

    private async Task<bool> UploadAsync(FileState st, ProjectState project, VaultPath path, SyncInput input, SyncAction action, CancellationToken ct)
    {
        var hash = input.LocalHash!;
        var snapshot = EnsureSnapshot(st, path, hash);
        if (snapshot is null) return false;
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, ct)) return false;
        if (action.Kind == SyncActionKind.AcquireLockThenUpload && !await AcquireAsync(st, snapshot.Id, ct)) return false;
        var flight = new Inflight("commit", OperationIds.Derive(snapshot.Id, "commit"), snapshot.Id, project.Id, st.FileId, ParentId: ParentOf(st),
            Hash: hash, Bytes: SizeOf(snapshot), SnapshotId: snapshot.Id, SavedRelease: input.SavedRelease?.Year, ReleaseNotChecked: action.ReleaseNotChecked);
        return await SendAsync(st, flight, ct);
    }

    private async Task<bool> PreserveAsync(FileState st, ProjectState project, VaultPath path, string hash, bool releaseNotChecked, SolidWorksRelease? saved, CancellationToken ct)
    {
        var snapshot = EnsureSnapshot(st, path, hash);
        if (snapshot is null) return false;
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, ct)) return false;
        var flight = new Inflight("side", OperationIds.Derive(snapshot.Id, "side"), snapshot.Id, project.Id, st.FileId, ParentId: ParentOf(st),
            Hash: hash, Bytes: SizeOf(snapshot), SnapshotId: snapshot.Id, SavedRelease: saved?.Year,
            Reason: st.BreakNotice ? "lock broken" : "conflict", ReleaseNotChecked: releaseNotChecked);
        return await SendAsync(st, flight, ct);
    }

    private async Task<bool> TombstoneAsync(FileState st, VaultPath path, SyncInput input, CancellationToken ct)
    {
        if (st.FileId is null) return true;
        if (st.DeleteEntry is null)
        {
            st.DeleteEntry = state.NextId("delete");
            journal.Append(new JournalEntry(st.DeleteEntry, IntentKind.Tombstone, path.Value, null, null, state.Email!));
            Save();
        }
        if (input.Lock != LockOwnership.ThisDevice && !await AcquireAsync(st, st.DeleteEntry, ct)) return false;
        var flight = new Inflight("tombstone", OperationIds.Derive(st.DeleteEntry, "tomb", st.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            st.DeleteEntry, st.ProjectId, st.FileId, ParentId: ParentOf(st));
        return await SendAsync(st, flight, ct);
    }

    private async Task<bool> AcquireAsync(FileState st, string entryId, CancellationToken ct)
    {
        var flight = new Inflight("lock", OperationIds.Derive(entryId, "lock", st.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)), entryId, st.ProjectId, st.FileId);
        return await SendAsync(st, flight, ct);
    }

    // The bytes behind a hash: the newest capture of this file with that hash, or a fresh
    // capture if the disk still holds those bytes (a download that was never a save).
    private SavedSnapshot? EnsureSnapshot(FileState st, VaultPath path, string hash)
    {
        var existing = SnapshotFor(st, hash);
        if (existing is not null) return existing;
        var id = state.NextId("save");
        Save();
        try
        {
            SavedSnapshot snapshot;
            using (var source = fs.OpenRead(path)) snapshot = recorder.Record(id, path, state.Email!, source);
            st.Entries.Add(id);
            st.LastCaptured = snapshot.Hash;
            Save();
            if (snapshot.Hash == hash) return snapshot;
        }
        catch (IOException error) { problems.Add($"{path}: {error.Message}"); }
        return null; // the file changed since the scan; the next pass plans again
    }

    private async Task<bool> EnsureServerFileAsync(FileState st, ProjectState project, VaultPath path, string entryId, CancellationToken ct)
    {
        if (st.FileId is not null) return true;
        st.CreateEntry ??= entryId;
        var (folder, name) = Split(path);
        var flight = new Inflight("create", OperationIds.Derive(st.CreateEntry, "create"), st.CreateEntry, project.Id, null, folder, name);
        return await SendAsync(st, flight, ct) && st.FileId is not null;
    }

    private static string? ParentOf(FileState st) => Guid.TryParse(st.BaseId, out _) ? st.BaseId : null;
    private long SizeOf(SavedSnapshot snapshot)
    {
        using var stream = deps.Snapshots.OpenRead(snapshot.Id);
        return stream.Length;
    }

    // ---- In-flight writes ------------------------------------------------------------

    // Persist the write and its operation id, send it, apply the answer, clear it. A crash
    // anywhere leaves the record; the next online pass re-sends the same id.
    private async Task<bool> SendAsync(FileState st, Inflight flight, CancellationToken ct)
    {
        st.Inflight = flight;
        wrote = true;
        Save();
        CrashPoint?.Invoke($"before-{flight.Kind}");
        bool result;
        try { result = await SendRecordedAsync(st, flight, ct); }
        catch (ArmoryRpcException error) when (error.IsInvalidInput || error.IsForbidden || flight.Kind == "create")
        {
            // A refusal is an answer: nothing was written, so the record is cleared.
            st.Inflight = null;
            if (flight.Kind == "create" && error.IsNameTaken) result = AdoptOrRefuseName(st, flight, error);
            else { st.Refusal = error.Message; st.RefusalKind = AttentionKinds.Refused; refused++; result = false; }
            Save();
            return result;
        }
        CrashPoint?.Invoke($"after-{flight.Kind}");
        st.Inflight = null;
        Save();
        return result;
    }

    private async Task<bool> SendRecordedAsync(FileState st, Inflight f, CancellationToken ct)
    {
        var device = state.DeviceId!.Value;
        switch (f.Kind)
        {
            case "create":
                st.FileId = await deps.Api.CreateFileAsync(f.ProjectId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
                return true;
            case "lock":
                if (await deps.Api.AcquireLockAsync(f.FileId!.Value, device, f.Operation, ct)) return true;
                st.Attempt++;
                return false;
            case "release":
                await deps.Api.ReleaseLockAsync(f.FileId!.Value, device, f.Operation, ct);
                return true;
            case "tombstone":
                if (await deps.Api.TombstoneAsync(f.FileId!.Value, Parse(f.ParentId), device, f.Operation, ct))
                {
                    st.SetBase(new($"tombstone:{f.FileId}", null, ""));
                    state.Completed.Add(f.EntryId!);
                    st.DeleteEntry = null;
                    return true;
                }
                st.Attempt++;
                return false;
            case "commit":
            {
                await UploadBlobAsync(f, ct);
                CrashPoint?.Invoke("after-blob");
                var answer = await deps.Api.CommitVersionWithReleaseAsync(f.FileId!.Value, Parse(f.ParentId), ContentObjectKey.FromHash(f.Hash!), f.Hash!, f.Bytes,
                    device, f.Operation, f.SavedRelease, ct);
                CrashPoint?.Invoke("after-commit-rpc");
                if (answer.Advanced)
                {
                    st.SetBase(new(answer.VersionId.ToString(), f.Hash, state.Email!));
                    st.Preserved = null;
                    uploaded++;
                }
                else
                {
                    // The server kept it as a side version (the lock or parent changed).
                    st.Preserved = f.Hash;
                    st.Sides.Add(new(answer.VersionId, f.Hash!, "conflict", deps.Clock.GetUtcNow()));
                    sideVersions++;
                }
                st.ReleaseNotChecked = f.ReleaseNotChecked;
                Complete(st, f.Hash!);
                lastActivity = deps.Clock.GetUtcNow();
                return answer.Advanced;
            }
            case "side":
            case "archive":
            {
                await UploadBlobAsync(f, ct);
                CrashPoint?.Invoke("after-blob");
                var id = await deps.Api.SaveSideVersionWithReleaseAsync(f.FileId!.Value, Parse(f.ParentId), ContentObjectKey.FromHash(f.Hash!), f.Hash!, f.Bytes,
                    f.Reason ?? "conflict", device, f.Operation, f.SavedRelease, ct);
                if (f.Kind == "side")
                {
                    st.Preserved = f.Hash;
                    st.BreakNotice = false;
                    Complete(st, f.Hash!);
                }
                else state.Completed.Add(f.EntryId!);
                st.Entries.Remove(f.EntryId!);
                st.Sides.Add(new(id, f.Hash!, f.Reason ?? "conflict", deps.Clock.GetUtcNow()));
                st.ReleaseNotChecked = f.ReleaseNotChecked;
                sideVersions++;
                lastActivity = deps.Clock.GetUtcNow();
                return true;
            }
            case "move":
                return await deps.Api.MoveFileAsync(f.FileId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
            default:
                throw new InvalidOperationException($"Unknown in-flight write {f.Kind}.");
        }
    }

    private async Task UploadBlobAsync(Inflight f, CancellationToken ct)
        => await deps.Blobs.UploadAsync(f.ProjectId!.Value, f.Hash!, f.Bytes, () => deps.Snapshots.OpenRead(f.SnapshotId!), ct);

    private bool AdoptOrRefuseName(FileState st, Inflight f, ArmoryRpcException error)
    {
        var folder = ParseExistingFolder(error.Details, out var existingId, out var existingName);
        if (existingId is { } id && string.Equals(folder, f.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(existingName, f.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Someone else created this same path first: it is one file. Core decides the rest.
            st.FileId = id;
            return false;
        }
        var where = string.IsNullOrEmpty(folder) ? "the project's main folder" : "the folder " + folder;
        st.Refusal = $"Another file named {existingName ?? f.Name} is already in {where}. Rename yours to keep both.";
        st.RefusalKind = AttentionKinds.NameTaken;
        refused++;
        return false;
    }

    private static Guid? Parse(string? id) => Guid.TryParse(id, out var value) ? value : null;

    private async Task ResumeInflightAsync(CancellationToken ct)
    {
        foreach (var st in state.Files.Values.Where(f => f.Inflight is not null).ToArray())
        {
            try { await SendAsync(st, st.Inflight!, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryRpcException error) { problems.Add($"{st.Path}: {error.Message}"); st.Inflight = null; Save(); }
        }
    }

    // ---- Superseded saves ------------------------------------------------------------

    private async Task ArchiveSupersededAsync(Dictionary<string, JournalEntry> entries, CancellationToken ct)
    {
        foreach (var st in state.Files.Values.Where(f => f.Entries.Count > 0 && f.Inflight is null).ToArray())
        {
            if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot)) continue;
            var project = state.Projects.GetValueOrDefault(st.ProjectId);
            if (project is null || !project.Usable) continue;
            local.TryGetValue(st.Path, out var current);
            remoteByPath.TryGetValue(st.Path, out var remote);
            foreach (var id in st.Entries.ToArray())
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Hash is null) continue;
                if (entry.Hash == current?.Hash) continue; // the plan uploads the bytes on disk
                if (entry.Hash == remote.File?.Current?.Hash || entry.Hash == st.BaseHash || st.Sides.Any(s => s.Hash == entry.Hash))
                {
                    state.Completed.Add(id); st.Entries.Remove(id); Save();
                    continue;
                }
                SolidWorksRelease? saved = null;
                var notChecked = false;
                if (Reconciler.IsSolidWorks(path))
                {
                    saved = await ReadReleaseFromSnapshotAsync(entry.SnapshotId!, ct);
                    var gate = SolidWorksVersionGate.Decide(saved, new SolidWorksRelease(project.PinnedRelease), project.Enforce ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn);
                    if (!gate.Allowed) { st.Refusal = gate.Problem; st.RefusalKind = AttentionKinds.Refused; refused++; continue; }
                    notChecked = gate.ReleaseNotChecked;
                }
                try
                {
                    if (!await EnsureServerFileAsync(st, project, path, id, ct)) break;
                    var snapshot = deps.Snapshots.Enumerate().First(s => s.Id == entry.SnapshotId);
                    var flight = new Inflight("archive", OperationIds.Derive(snapshot.Id, "side"), id, project.Id, st.FileId, ParentId: ParentOf(st),
                        Hash: entry.Hash, Bytes: SizeOf(snapshot), SnapshotId: snapshot.Id, SavedRelease: saved?.Year, Reason: EarlierSaveReason, ReleaseNotChecked: notChecked);
                    if (!await SendAsync(st, flight, ct)) break;
                }
                catch (ArmoryOfflineException) { online = false; return; }
                catch (ArmoryRpcException error) { problems.Add($"{st.Path}: {error.Message}"); break; }
            }
        }
    }

    private async Task<SolidWorksRelease?> ReadReleaseFromSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        if (deps.ReleaseReader is null) return null;
        await using var stream = deps.Snapshots.OpenRead(snapshotId);
        return await deps.ReleaseReader.ReadAsync(stream, ct);
    }

    // ---- Moves -----------------------------------------------------------------------

    // A file whose server folder or name changed moves on this disk too: a move, never a
    // delete plus an add. Returns true when anything moved.
    private async Task<bool> ApplyRemoteMovesAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        var any = false;
        foreach (var st in state.Files.Values.Where(f => f.FileId is not null).ToArray())
        {
            if (!remoteById.TryGetValue(st.FileId!.Value, out var remote) || string.Equals(remote.Path.Value, st.Path, StringComparison.Ordinal)) continue;
            if (!VaultPath.TryCreate(st.Path, out var from, out _, options.VaultRoot)) continue;
            if (state.Files.TryGetValue(remote.Path.Value, out var occupant) && !ReferenceEquals(occupant, st) && !string.Equals(remote.Path.Value, st.Path, StringComparison.OrdinalIgnoreCase))
            {
                Notice(AttentionKinds.Refused, st.FileId, st.Path, "A file was renamed onto a name you already have", "Rename your copy so Armory can finish the move.");
                continue;
            }
            if (local.TryGetValue(st.Path, out var file))
            {
                if (IsOpenNow(from)) { Notice(AttentionKinds.NewerWaiting, st.FileId, st.Path, "This file was renamed", $"Close {from.Name} to finish moving it to {remote.Path.Name}."); continue; }
                var outcome = fs.Move(from, remote.Path, file.Hash);
                if (!outcome.Succeeded) { problems.Add($"{from}: {outcome.Problem}"); continue; }
                local.Remove(st.Path);
                local[remote.Path.Value] = file with { Path = remote.Path };
            }
            state.Files.Remove(st.Path);
            st.Path = remote.Path.Value;
            state.Files[st.Path] = st;
            st.AppliedOwnership = null;
            any = true;
            Save();
        }
        return any;
    }

    // Requested with MoveAsync: the holder renames through armory_move_file.
    private async Task ExecutePendingMovesAsync(CancellationToken ct)
    {
        foreach (var move in state.Moves.ToArray())
        {
            var st = state.Files.Values.FirstOrDefault(f => f.FileId == move.FileId);
            if (st is null || !VaultPath.TryCreate(move.To, out var to, out _, options.VaultRoot)) { state.Moves.Remove(move); moveResults[move.Operation] = false; Save(); continue; }
            remoteById.TryGetValue(move.FileId, out var remote);
            if (OwnershipOf(remote.File?.Lock) != LockOwnership.ThisDevice && !await AcquireAsync(st, move.Operation.ToString(), ct))
            {
                Notice(AttentionKinds.Refused, move.FileId, move.From, "This file can't be renamed right now", "Someone else is editing it.");
                state.Moves.Remove(move); moveResults[move.Operation] = false; Save();
                continue;
            }
            var (folder, name) = Split(to);
            var flight = new Inflight("move", move.Operation, null, st.ProjectId, move.FileId, folder, name);
            bool done;
            try { done = await SendAsync(st, flight, ct); }
            catch (ArmoryRpcException error) when (error.IsNameTaken)
            {
                st.Inflight = null;
                Notice(AttentionKinds.NameTaken, move.FileId, move.From, "That name is already used", error.Message);
                done = false;
            }
            state.Moves.Remove(move);
            moveResults[move.Operation] = done;
            Save();
        }
        if (moveResults.Count > 0) { await RefreshAsync(ct); await ApplyRemoteMovesAsync(ct); }
    }

    private readonly Dictionary<Guid, bool> moveResults = [];

    public async Task<bool> MoveAsync(VaultPath from, VaultPath to, CancellationToken cancellationToken = default)
    {
        if (!from.IsValid || !to.IsValid) return false;
        Guid operation;
        await passGate.WaitAsync(cancellationToken);
        try
        {
            if (!state.Files.TryGetValue(from.Value, out var st) || st.FileId is null) return false;
            if (!string.Equals(ProjectOf(from)?.Name, ProjectOf(to)?.Name, StringComparison.OrdinalIgnoreCase)) return false;
            operation = Guid.NewGuid();
            state.Moves.Add(new PendingMove(operation, st.FileId.Value, from.Value, to.Value));
            Save();
        }
        finally { passGate.Release(); }
        await SyncOnceAsync(cancellationToken);
        return moveResults.Remove(operation, out var done) && done;
    }

    // ---- Locks -------------------------------------------------------------------------

    // Without an add-in, SolidWorks' ~$ file beside a document means it was opened: take
    // the lock while it is free, as the first edit would.
    private async Task AcquireForMarkersAsync(CancellationToken ct)
    {
        foreach (var st in state.Files.Values.ToArray())
        {
            var hasMarker = markerDocuments.Contains(st.Path);
            if (!hasMarker)
            {
                if (st.MarkerEntry is not null) { state.Completed.Add(st.MarkerEntry); st.MarkerEntry = null; Save(); }
                continue;
            }
            if (st.FileId is null || st.Inflight is not null || !VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot) || !Reconciler.IsSolidWorks(path)) continue;
            remoteById.TryGetValue(st.FileId.Value, out var remote);
            if (remote.File is null || remote.File.Deleted) continue;
            var held = remote.File.Lock;
            FileLock current = held is null ? new FreeLock() : held.IsLive ? new HeldByOther(new(held.HolderEmail, held.HolderDeviceId.ToString()), held.AcquiredAt, true)
                : new Broken(new(held.BrokenHolderEmail ?? "", held.BrokenHolderDeviceId?.ToString() ?? ""), true);
            var actor = new LockActor(new(state.Email!, state.DeviceId.ToString()!), false);
            if (!LockMachine.Apply(current, LockEvent.Acquire, actor, deps.Clock.GetUtcNow()).Succeeded) continue;
            if (st.MarkerEntry is null)
            {
                st.MarkerEntry = state.NextId("open");
                journal.Append(new JournalEntry(st.MarkerEntry, IntentKind.AcquireLock, st.Path, null, null, state.Email!));
                Save();
            }
            try { await AcquireAsync(st, st.MarkerEntry, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryRpcException error) { problems.Add($"{st.Path}: {error.Message}"); }
        }
    }

    // Closing the file, saved, releases the lock: Core's LockMachine allows a release only
    // with no unsynced changes, and nothing for the file may be waiting or in flight.
    private async Task ReleaseFinishedLocksAsync(CancellationToken ct)
    {
        foreach (var st in state.Files.Values.Where(f => f.FileId is not null && f.Inflight is null).ToArray())
        {
            if (!remoteById.TryGetValue(st.FileId!.Value, out var remote) || remote.File.Lock is not { IsLive: true } held) continue;
            if (OwnershipOf(held) != LockOwnership.ThisDevice) continue;
            if (state.Moves.Any(m => m.FileId == st.FileId)) continue;
            local.TryGetValue(st.Path, out var file);
            var clean = (file?.Hash == st.BaseHash || (file is not null && file.Hash == st.Preserved)) && st.Entries.Count == 0;
            if (IsOpenNow(remote.Path)) continue;
            var me = new LockHolder(state.Email!, state.DeviceId.ToString()!);
            if (!LockMachine.Apply(new HeldByMe(me, held.AcquiredAt, !clean), LockEvent.Release, new LockActor(me, false), deps.Clock.GetUtcNow()).Succeeded) continue;
            var flight = new Inflight("release", OperationIds.Derive("release", state.DeviceId.ToString()!, st.FileId.ToString()!, held.AcquiredAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                null, st.ProjectId, st.FileId);
            try { await SendAsync(st, flight, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryRpcException error) { problems.Add($"{st.Path}: {error.Message}"); }
        }
    }

    private void ApplyReadOnly()
    {
        foreach (var st in state.Files.Values)
        {
            if (st.FileId is null || !local.ContainsKey(st.Path) || !remoteById.TryGetValue(st.FileId.Value, out var remote)) continue;
            var ownership = OwnershipOf(remote.File.Lock);
            if (st.AppliedOwnership == ownership) continue;
            try { fs.ApplyLockAttribute(remote.Path, ownership); st.AppliedOwnership = ownership; }
            catch (IOException error) { problems.Add($"{st.Path}: {error.Message}"); }
            catch (UnauthorizedAccessException error) { problems.Add($"{st.Path}: {error.Message}"); }
        }
        Save();
    }
}
