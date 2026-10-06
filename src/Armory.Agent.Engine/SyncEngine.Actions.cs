using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.Storage;

namespace Armory.Agent.Engine;

public sealed partial class SyncEngine
{
    private const string EarlierSaveReason = "earlier save, kept";
    internal const string GateKind = "gate", TooLargeKind = "tooLarge";
    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Executes one Core action. Returns false to stop this file's plan until the next pass.
    private async Task<bool> ExecuteAsync(SyncAction action, SyncInput input, FileState st, ProjectState project, RemoteFile? remote, CancellationToken ct)
    {
        var path = input.Path;
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
                // A SolidWorks file with bytes on disk is refused only by the release gate:
                // those bytes are a private draft, kept here and never holding the lock.
                st.RefusalKind = input.LocalHash is not null && Reconciler.IsSolidWorks(path) ? GateKind : AttentionKinds.Refused;
                refused++;
                return false;
            case SyncActionKind.Download:
                return await DownloadAsync(st, project, path, input, remote!, ct);
            case SyncActionKind.Upload:
            case SyncActionKind.AcquireLockThenUpload:
                return await UploadAsync(st, project, path, input, action, ct);
            case SyncActionKind.SaveSideVersion:
                return await PreserveAsync(st, project, path, input.LocalHash!, action.ReleaseNotChecked, input.SavedRelease, remote, ct);
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
            if (IsOpenNow(path)) { st.NewerWaiting = true; st.NewerAuthor = current.Author; return false; }
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
        var bytes = SizeOf(snapshot);
        if (TooLarge(st, bytes)) return false;
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, ct)) return false;
        if (action.Kind == SyncActionKind.AcquireLockThenUpload && !await AcquireAsync(st, snapshot.Id, ct)) return false;
        // Parent and attempt are part of the id: a commit the server kept as a side version is
        // finished, and any later commit of the same bytes is a new intent.
        var parent = ParentOf(st);
        var flight = new Inflight("commit", OperationIds.Derive(snapshot.Id, "commit", parent ?? "", Text(st.Attempt)), snapshot.Id, project.Id, st.FileId,
            ParentId: parent, Hash: hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: input.SavedRelease?.Year,
            ReleaseNotChecked: action.ReleaseNotChecked, Device: HolderDevice(st));
        return await SendAsync(st, flight, ct);
    }

    private async Task<bool> PreserveAsync(FileState st, ProjectState project, VaultPath path, string hash, bool releaseNotChecked, SolidWorksRelease? saved, RemoteFile? remote, CancellationToken ct)
    {
        // Already durable on the server (as this file's current version, or as a side version
        // the server or this engine already acknowledged): the obligation is met.
        if (st.Preserved == hash || remote?.Current?.Hash == hash || st.Sides.Any(s => s.Hash == hash))
        {
            st.Preserved = hash;
            st.BreakNotice = false;
            Complete(st, hash);
            return true;
        }
        var snapshot = EnsureSnapshot(st, path, hash);
        if (snapshot is null) return false;
        var bytes = SizeOf(snapshot);
        if (TooLarge(st, bytes)) return false;
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, ct)) return false;
        var flight = new Inflight("side", OperationIds.Derive(snapshot.Id, "side"), snapshot.Id, project.Id, st.FileId, ParentId: ParentOf(st),
            Hash: hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: saved?.Year,
            Reason: st.BreakNotice ? "lock broken" : "conflict", ReleaseNotChecked: releaseNotChecked);
        return await SendAsync(st, flight, ct);
    }

    private bool TooLarge(FileState st, long bytes)
    {
        if (bytes <= options.MaximumFileBytes) return false;
        st.Refusal = "Files larger than 2 GB can't be saved to Armory yet. This one stays on this computer.";
        st.RefusalKind = TooLargeKind;
        refused++;
        return true;
    }

    private async Task<bool> TombstoneAsync(FileState st, VaultPath path, SyncInput input, CancellationToken ct)
    {
        if (st.FileId is null) return true;
        // A deletion goes to the whole team: look once more that the file is really gone.
        if (Exists(path)) return false;
        if (st.DeleteEntry is null)
        {
            st.DeleteEntry = state.NextId("delete");
            journal.Append(new JournalEntry(st.DeleteEntry, IntentKind.Tombstone, path.Value, null, null, state.Email!));
            Save();
        }
        if (input.Lock != LockOwnership.ThisDevice && !await AcquireAsync(st, st.DeleteEntry, ct)) return false;
        if (Exists(path)) return false;
        var flight = new Inflight("tombstone", OperationIds.Derive(st.DeleteEntry, "tomb", Text(st.Attempt)),
            st.DeleteEntry, st.ProjectId, st.FileId, ParentId: ParentOf(st), Device: HolderDevice(st));
        return await SendAsync(st, flight, ct);
    }

    private bool Exists(VaultPath path)
    {
        try { using var _ = fs.OpenRead(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; } // present but held by another program
        catch (UnauthorizedAccessException) { return true; }
    }

    private async Task<bool> AcquireAsync(FileState st, string entryId, CancellationToken ct)
    {
        var flight = new Inflight("lock", OperationIds.Derive(entryId, "lock", Text(st.Attempt)), entryId, st.ProjectId, st.FileId);
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
        catch (UnauthorizedAccessException error) { problems.Add($"{path}: {error.Message}"); }
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

    // Contract v2 (C4, D6): a name whose only holder is a removed file revives that file, with its
    // id and history. A file this computer has just created has no version yet, so an id whose
    // server record already has one is a revival: that version becomes the base, and the added
    // bytes are committed on top of it. With no parent the server would keep them aside as a stale
    // parent, and the removed bytes would come back over them (docs/server/contract.md, open
    // point 1). This computer's record of the removed file at its old path is that file's past,
    // not a second file: with nothing of it on this disk it is forgotten, so the revived file is
    // never fetched back to the old path. A revival this pass's refresh did not see still ends
    // the 0.1.0 way; the v2 engine reads the change feed's file_revived for it.
    private void ContinueRevivedHistory(FileState st)
    {
        if (st.FileId is not { } id || !remoteById.TryGetValue(id, out var revived)) return;
        if (st.Base is null && revived.File.Current is { } current) st.SetBase(new(current.Id.ToString(), current.Hash, current.Author));
        foreach (var past in state.Files.Values.Where(f => f.FileId == id && !ReferenceEquals(f, st) && f.Inflight is null && !local.ContainsKey(f.Path)).ToArray())
            state.Files.Remove(past.Path);
    }

    private static string? ParentOf(FileState st) => Guid.TryParse(st.BaseId, out _) ? st.BaseId : null;
    private long SizeOf(SavedSnapshot snapshot)
    {
        using var stream = deps.Snapshots.OpenRead(snapshot.Id);
        return stream.Length;
    }

    // The device that holds this file's live lock when it is one of this computer's ids
    // (a former id after a reconnect); otherwise this computer's current id.
    private Guid? HolderDevice(FileState st)
        => st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && remote.File.Lock is { IsLive: true } held &&
           string.Equals(held.HolderEmail, state.Email, StringComparison.OrdinalIgnoreCase) && state.IsMine(held.HolderDeviceId)
            ? held.HolderDeviceId : null;

    // ---- In-flight writes ------------------------------------------------------------

    // Persist the write and its operation id, send it, apply the answer, clear it. A crash
    // anywhere leaves the record; the next online pass re-sends the same id. Being offline
    // keeps the record; any other failure is an answer: nothing was written, the record is
    // cleared, and Core plans again from fresh state (the same intent re-derives its id).
    private async Task<bool> SendAsync(FileState st, Inflight flight, CancellationToken ct)
    {
        st.Inflight = flight;
        wrote = true;
        Save();
        CrashPoint?.Invoke($"before-{flight.Kind}");
        bool result;
        try { result = await SendRecordedAsync(st, flight, ct); }
        catch (ArmoryOfflineException) { online = false; throw; }
        catch (Exception error) when (error is ArmoryClientException or HashMismatchException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            st.Inflight = null;
            if (flight.Kind == "create" && error is ArmoryRpcException { IsNameTaken: true } taken) result = AdoptOrRefuseName(st, flight, taken);
            else if (flight.Kind is "lock" or "release" or "move") { problems.Add($"{st.Path}: {error.Message}"); result = false; }
            else { st.Refusal = PlainRefusal(error); st.RefusalKind = AttentionKinds.Refused; refused++; result = false; }
            Save();
            return result;
        }
        CrashPoint?.Invoke($"after-{flight.Kind}");
        st.Inflight = null;
        Save();
        return result;
    }

    private static string PlainRefusal(Exception error) => error switch
    {
        BlobRefusedException { Status: 403 } => "Armory wouldn't take this file: you may no longer be in this project. Ask your CAD lead.",
        BlobRefusedException => "Armory wouldn't take this file. It stays on this computer.",
        ArmoryRpcException rpc => rpc.Message,
        _ => "This save couldn't be read back from this computer's safe copy. It stays on this computer.",
    };

    private async Task<bool> SendRecordedAsync(FileState st, Inflight f, CancellationToken ct)
    {
        var device = f.Device ?? state.DeviceId!.Value;
        switch (f.Kind)
        {
            case "create":
                st.FileId = await deps.Api.CreateFileAsync(f.ProjectId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
                ContinueRevivedHistory(st);
                return true;
            case "lock":
            {
                // Every answer spends the attempt: a later acquire is a new intent.
                var held = await deps.Api.AcquireLockAsync(f.FileId!.Value, device, f.Operation, ct);
                st.Attempt++;
                return held;
            }
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
                    AddSide(st, answer.VersionId, f.Hash!, "conflict");
                    st.Attempt++;
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
                state.Completed.Add(f.EntryId!);
                st.Entries.Remove(f.EntryId!);
                st.Drafts.Remove(f.EntryId!);
                AddSide(st, id, f.Hash!, f.Reason ?? "conflict");
                st.ReleaseNotChecked = f.ReleaseNotChecked;
                lastActivity = deps.Clock.GetUtcNow();
                return true;
            }
            case "move":
                return await deps.Api.MoveFileAsync(f.FileId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
            default:
                throw new InvalidOperationException($"Unknown in-flight write {f.Kind}.");
        }
    }

    private void AddSide(FileState st, Guid versionId, string hash, string reason)
    {
        if (st.Sides.Any(s => s.VersionId == versionId)) return;
        st.Sides.Add(new(versionId, hash, reason, deps.Clock.GetUtcNow()));
        sideVersions++;
    }

    private async Task UploadBlobAsync(Inflight f, CancellationToken ct)
        => await deps.Blobs.UploadAsync(f.ProjectId!.Value, f.Hash!, f.Bytes, () => deps.Snapshots.OpenRead(f.SnapshotId!), ct);

    private bool AdoptOrRefuseName(FileState st, Inflight f, ArmoryRpcException error)
    {
        var folder = ParseExistingFolder(error.Details, out var existingId, out var existingName);
        var deleted = existingId is { } known && remoteById.TryGetValue(known, out var existing) && existing.File.Deleted;
        if (existingId is { } id && !deleted && string.Equals(folder, f.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(existingName, f.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Someone else created this same path first: it is one file. Core decides the rest.
            st.FileId = id;
            return false;
        }
        st.Refusal = deleted
            ? $"A file named {existingName ?? f.Name} was removed from this project, and names stay with their history. Rename yours to keep it."
            : $"Another file named {existingName ?? f.Name} is already in {(string.IsNullOrEmpty(folder) ? "the project's main folder" : "the folder " + folder)}. Rename yours to keep both.";
        st.RefusalKind = AttentionKinds.NameTaken;
        refused++;
        return false;
    }

    private static Guid? Parse(string? id) => Guid.TryParse(id, out var value) ? value : null;

    // Re-sends what a stopped process left in flight. Writes of immutable bytes and the
    // create/lock/move steps are re-sent under their recorded id; a release or a deletion is
    // dropped instead and planned again from fresh state, because the file may have changed.
    // Returns true when anything was sent.
    private async Task<bool> ResumeInflightAsync(CancellationToken ct)
    {
        var sent = false;
        foreach (var st in state.Files.Values.Where(f => f.Inflight is not null).ToArray())
        {
            var project = state.Projects.GetValueOrDefault(st.ProjectId);
            if (project is null || !project.Usable) continue;
            if (st.Inflight!.Kind is "release" or "tombstone") { st.Inflight = null; Save(); continue; }
            try { await SendAsync(st, st.Inflight, ct); sent = true; }
            catch (ArmoryOfflineException) { online = false; return sent; }
        }
        return sent;
    }

    // ---- Superseded saves ------------------------------------------------------------

    private async Task ArchiveSupersededAsync(Dictionary<string, JournalEntry> entries, CancellationToken ct)
    {
        foreach (var st in state.Files.Values.Where(f => (f.Entries.Count > 0 || f.Drafts.Count > 0) && f.Inflight is null && f.LocalMoveTo is null).ToArray())
        {
            if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot)) continue;
            var project = state.Projects.GetValueOrDefault(st.ProjectId);
            if (project is null || !project.Usable) continue;
            local.TryGetValue(st.Path, out var current);
            var remote = st.FileId is { } fid && remoteById.TryGetValue(fid, out var r) ? r.File : null;
            foreach (var id in st.Entries.Concat(st.Drafts).ToArray())
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Hash is null) continue;
                if (entry.Hash == current?.Hash) continue; // the plan uploads the bytes on disk
                if (entry.Hash == remote?.Current?.Hash || entry.Hash == st.BaseHash || st.Sides.Any(s => s.Hash == entry.Hash))
                {
                    state.Completed.Add(id); st.Entries.Remove(id); st.Drafts.Remove(id); Save();
                    continue;
                }
                SolidWorksRelease? saved = null;
                var notChecked = false;
                if (Reconciler.IsSolidWorks(path))
                {
                    try { saved = await ReadReleaseFromSnapshotAsync(entry.SnapshotId!, ct); }
                    catch (Exception error) when (error is IOException or InvalidDataException) { problems.Add($"{st.Path}: {error.Message}"); continue; }
                    var gate = SolidWorksVersionGate.Decide(saved, new SolidWorksRelease(project.PinnedRelease), project.Enforce ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn);
                    if (!gate.Allowed)
                    {
                        // Kept as a private draft on this computer; offered again if the gate changes.
                        if (st.Entries.Remove(id)) { st.Drafts.Add(id); Save(); }
                        continue;
                    }
                    notChecked = gate.ReleaseNotChecked;
                }
                try
                {
                    var snapshot = deps.Snapshots.Enumerate().First(s => s.Id == entry.SnapshotId);
                    var bytes = SizeOf(snapshot);
                    if (bytes > options.MaximumFileBytes) { if (st.Entries.Remove(id)) { st.Drafts.Add(id); Save(); } continue; }
                    if (!await EnsureServerFileAsync(st, project, path, id, ct)) break;
                    var flight = new Inflight("archive", OperationIds.Derive(snapshot.Id, "side"), id, project.Id, st.FileId, ParentId: ParentOf(st),
                        Hash: entry.Hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: saved?.Year, Reason: EarlierSaveReason, ReleaseNotChecked: notChecked);
                    if (!await SendAsync(st, flight, ct)) break;
                }
                catch (ArmoryOfflineException) { online = false; return; }
                catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException) { problems.Add($"{st.Path}: {error.Message}"); break; }
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
    private bool ApplyRemoteMoves()
    {
        var any = false;
        foreach (var st in state.Files.Values.Where(f => f.FileId is not null && f.LocalMoveTo is null).ToArray())
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
            Rekey(st, remote.Path.Value);
            any = true;
        }
        if (any) Save();
        return any;
    }

    private void Rekey(FileState st, string to)
    {
        state.Files.Remove(st.Path);
        st.Path = to;
        state.Files[to] = st;
        st.AppliedOwnership = null;
        st.AbsentScans = 0;
    }

    // An Explorer rename or move: a tracked file vanished from its path and the same file
    // (by NTFS file id, or by its exact bytes at exactly one new untracked path in the same
    // project) appeared elsewhere. It is sent as a server move, never as a deletion.
    private void DetectLocalMoves(VaultScan scan)
    {
        var platform = (scan.Renames ?? []).ToDictionary(r => r.From.Value, r => r.To.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var st in state.Files.Values.ToArray())
        {
            if (st.FileId is null || st.BaseHash is null || st.LocalMoveTo is not null || st.Inflight is not null || local.ContainsKey(st.Path)) continue;
            if (state.Moves.Any(m => m.FileId == st.FileId)) continue;
            var project = state.Projects.GetValueOrDefault(st.ProjectId);
            if (project is null || !project.Usable) continue;
            string? to = null;
            if (platform.TryGetValue(st.Path, out var renamed) && local.ContainsKey(renamed)) to = renamed;
            else
            {
                var candidates = local.Values.Where(f => f.Hash == st.BaseHash && !remoteByPath.ContainsKey(f.Path.Value) &&
                    ProjectOf(f.Path)?.Id == st.ProjectId && (!state.Files.TryGetValue(f.Path.Value, out var other) || (other.FileId is null && other.Base is null)))
                    .Select(f => f.Path.Value).ToArray();
                if (candidates.Length == 1) to = candidates[0];
            }
            if (to is null || !VaultPath.TryCreate(to, out var target, out _, options.VaultRoot) || ProjectOf(target)?.Id != st.ProjectId) continue;
            st.LocalMoveTo = target.Value;
            state.Moves.Add(new PendingMove(Guid.NewGuid(), st.FileId.Value, st.Path, target.Value, Local: true));
            Save();
        }
    }

    // Requested with MoveAsync, or detected from Explorer: the holder renames through
    // armory_move_file. A refused Explorer rename is put back where it was.
    private async Task ExecutePendingMovesAsync(CancellationToken ct)
    {
        var any = false;
        foreach (var move in state.Moves.ToArray())
        {
            var st = state.Files.Values.FirstOrDefault(f => f.FileId == move.FileId);
            if (st is null || !VaultPath.TryCreate(move.To, out var to, out _, options.VaultRoot) || !VaultPath.TryCreate(move.From, out var from, out _, options.VaultRoot))
            { state.Moves.Remove(move); moveResults[move.Operation] = false; Save(); continue; }
            remoteById.TryGetValue(move.FileId, out var remote);
            bool done;
            try
            {
                done = remote.File is { Deleted: false } && (OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice || await AcquireAsync(st, move.Operation.ToString(), ct));
                if (done)
                {
                    var (folder, name) = Split(to);
                    done = await SendAsync(st, new Inflight("move", move.Operation, null, st.ProjectId, move.FileId, folder, name, Device: HolderDevice(st)), ct);
                }
            }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryRpcException error)
            {
                st.Inflight = null;
                Notice(AttentionKinds.NameTaken, move.FileId, move.From, "That name can't be used", error.Message);
                done = false;
            }
            any = true;
            state.Moves.Remove(move);
            moveResults[move.Operation] = done;
            if (move.Local) FinishLocalMove(st, from, to, done);
            else if (!done) Notice(AttentionKinds.Refused, move.FileId, move.From, "This file can't be renamed right now", "Someone else is editing it, or the new name is already used.");
            Save();
        }
        if (any && await RefreshAsync(ct)) ApplyRemoteMoves();
    }

    private void FinishLocalMove(FileState st, VaultPath from, VaultPath to, bool moved)
    {
        st.LocalMoveTo = null;
        state.Files.TryGetValue(to.Value, out var newcomer);
        if (moved)
        {
            // The bytes captured at the new path are this file's; fold that state into it.
            if (newcomer is not null && !ReferenceEquals(newcomer, st))
            {
                state.Files.Remove(to.Value);
                foreach (var id in newcomer.Entries) if (!st.Entries.Contains(id)) st.Entries.Add(id);
                st.LastCaptured = newcomer.LastCaptured ?? st.LastCaptured;
            }
            Rekey(st, to.Value);
            if (st.BaseHash is not null) Complete(st, st.BaseHash);
            return;
        }
        // Put the file back where the team has it, so nothing is deleted for anyone.
        if (local.TryGetValue(to.Value, out var file) && !IsOpenNow(to) && fs.Move(to, from, file.Hash).Succeeded)
        {
            local.Remove(to.Value);
            local[from.Value] = file with { Path = from };
            if (newcomer is not null && !ReferenceEquals(newcomer, st))
            {
                state.Files.Remove(to.Value);
                foreach (var id in newcomer.Entries) if (!st.Entries.Contains(id)) st.Entries.Add(id);
            }
            if (st.BaseHash is not null) Complete(st, st.BaseHash);
            Remember(AttentionKinds.Refused, st.FileId, from.Value, "Renamed back for now",
                $"{from.Name} can't be renamed while someone else is editing it, so Armory put it back.");
        }
        else
        {
            st.LocalMoveTo = to.Value; // try again on the next pass; never delete the original
            Notice(AttentionKinds.Refused, st.FileId, from.Value, "This rename is waiting", $"Close {to.Name} so Armory can finish or undo the rename.");
            state.Moves.Add(new PendingMove(Guid.NewGuid(), st.FileId!.Value, from.Value, to.Value, Local: true));
        }
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
            // Never rename under an open document or onto a file this computer already has.
            if (fs.IsOpen(from) || markerDocuments.Contains(from.Value) || state.Files.ContainsKey(to.Value) || Exists(to)) return false;
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
    // the lock while it is free, as the first edit would, but only for a copy that is the
    // current version (a stale copy waiting for a newer version must not block the team).
    private async Task AcquireForMarkersAsync(CancellationToken ct)
    {
        foreach (var st in state.Files.Values.ToArray())
        {
            if (!markerDocuments.Contains(st.Path))
            {
                if (st.MarkerEntry is not null) { state.Completed.Add(st.MarkerEntry); st.MarkerEntry = null; Save(); }
                continue;
            }
            if (st.FileId is null || st.Inflight is not null || st.NewerWaiting || !VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot) || !Reconciler.IsSolidWorks(path)) continue;
            if (!remoteById.TryGetValue(st.FileId.Value, out var remote) || remote.File.Deleted || remote.File.Current is not { } current) continue;
            if (st.BaseId != current.Id.ToString()) continue;
            var held = remote.File.Lock;
            FileLock lockState = held is null ? new FreeLock() : held.IsLive ? new HeldByOther(new(held.HolderEmail, held.HolderDeviceId.ToString()), held.AcquiredAt, true)
                : new Broken(new(held.BrokenHolderEmail ?? "", held.BrokenHolderDeviceId?.ToString() ?? ""), true);
            var actor = new LockActor(new(state.Email!, state.DeviceId.ToString()!), false);
            if (!LockMachine.Apply(lockState, LockEvent.Acquire, actor, deps.Clock.GetUtcNow()).Succeeded) continue;
            if (st.MarkerEntry is null)
            {
                st.MarkerEntry = state.NextId("open");
                journal.Append(new JournalEntry(st.MarkerEntry, IntentKind.AcquireLock, st.Path, null, null, state.Email!));
                Save();
            }
            try { await AcquireAsync(st, st.MarkerEntry, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
        }
    }

    // Closing the file, saved, releases the lock: Core's LockMachine allows a release only
    // with no unsynced changes, and nothing for the file may be waiting or in flight. A
    // private draft the release gate refused can never be the shared version, so it does
    // not hold the lock.
    private async Task ReleaseFinishedLocksAsync(CancellationToken ct)
    {
        foreach (var st in state.Files.Values.Where(f => f.FileId is not null && f.Inflight is null && f.LocalMoveTo is null).ToArray())
        {
            if (!remoteById.TryGetValue(st.FileId!.Value, out var remote) || remote.File.Lock is not { IsLive: true } held) continue;
            if (OwnershipOf(held) != LockOwnership.ThisDevice) continue;
            if (state.Moves.Any(m => m.FileId == st.FileId)) continue;
            if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot) || IsOpenNow(path)) continue;
            local.TryGetValue(st.Path, out var file);
            var draft = st.RefusalKind is GateKind or TooLargeKind;
            var clean = (file?.Hash == st.BaseHash || (file is not null && file.Hash == st.Preserved) || draft) && st.Entries.Count == 0;
            var me = new LockHolder(state.Email!, held.HolderDeviceId.ToString());
            if (!LockMachine.Apply(new HeldByMe(me, held.AcquiredAt, !clean), LockEvent.Release, new LockActor(me, false), deps.Clock.GetUtcNow()).Succeeded) continue;
            var flight = new Inflight("release", OperationIds.Derive("release", held.HolderDeviceId.ToString(), st.FileId.ToString()!, held.AcquiredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
                null, st.ProjectId, st.FileId, Device: held.HolderDeviceId);
            try { await SendAsync(st, flight, ct); }
            catch (ArmoryOfflineException) { online = false; return; }
        }
    }

    private void ApplyReadOnly()
    {
        foreach (var st in state.Files.Values)
        {
            if (st.FileId is null || !local.ContainsKey(st.Path) || !remoteById.TryGetValue(st.FileId.Value, out var remote)) continue;
            if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot)) continue;
            var ownership = OwnershipOf(remote.File.Lock);
            if (st.AppliedOwnership == ownership) continue;
            try { fs.ApplyLockAttribute(path, ownership); st.AppliedOwnership = ownership; }
            catch (IOException error) { problems.Add($"{st.Path}: {error.Message}"); }
            catch (UnauthorizedAccessException error) { problems.Add($"{st.Path}: {error.Message}"); }
        }
        Save();
    }
}
