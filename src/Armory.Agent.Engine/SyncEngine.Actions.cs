using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.Storage;

namespace Armory.Agent.Engine;

public sealed partial class SyncEngine
{
    private const string EarlierSaveReason = "earlier save, kept";
    // Why a file's bytes are not on the server: the release gate refused them, they are too
    // large, their name is taken in the project, or the server refused them.
    internal const string GateKind = "gate", TooLargeKind = "tooLarge", NameTakenKind = "nameTaken", RefusedKind = "refused";
    // The reason a kept copy (side version) carries on the server, by Core's SideVersionReason.
    internal const string SavedWhileCheckedOutReason = "saved while checked out", ChangedWithoutCheckOutReason = "changed without a check out",
        UndoReason = "kept when the check out was undone", LockBrokenReason = "lock broken", ConflictReason = "conflict";
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
                // Removed by the team while open here: nothing newer is waiting, it goes aside once closed.
                if (input.Remote is { IsTombstone: true }) { st.RemovedWaiting = true; return true; }
                st.NewerWaiting = true;
                st.NewerAuthor = remote?.Current?.Author;
                return true;
            case SyncActionKind.Refuse:
                // A SolidWorks file with bytes on disk is refused only by the release gate:
                // those bytes are a private draft, kept here and never holding the lock.
                var gate = input.LocalHash is not null && Reconciler.IsSolidWorks(path);
                st.Refusal = gate ? GateWords(input, project, action.Reason) : PlainReason(action.Reason);
                st.RefusalKind = gate ? GateKind : RefusedKind;
                refused++;
                return false;
            case SyncActionKind.Download:
                return await DownloadAsync(st, project, path, input, remote!, ct);
            case SyncActionKind.Upload:
            case SyncActionKind.AcquireLockThenUpload:
                return await UploadAsync(st, project, path, input, action, ct);
            case SyncActionKind.SaveSideVersion:
                return await PreserveAsync(st, project, path, input.LocalHash!, action.ReleaseNotChecked, input.SavedRelease, remote, action.Why, ct);
            case SyncActionKind.MoveLocalToRecovery:
                // Removed by the team: it goes aside once it is closed, never while open.
                if (IsOpenNow(path)) { st.RemovedWaiting = true; return false; }
                var moved = fs.MoveToRecovery(path, input.LocalHash!);
                if (!moved.Succeeded)
                {
                    Problem(NoticeKinds.CantRead, path.Value, "It was removed from the project, and Armory couldn't move your copy aside yet. Close any program that might be using it. Armory tries again by itself.",
                        moved.Problem ?? "MoveToRecovery refused");
                    return false;
                }
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

    // The release gate's refusal in a student's words, naming both releases when it knows them.
    private static string GateWords(SyncInput input, ProjectState project, string? reason)
    {
        var pin = input.PinnedRelease?.Year ?? project.PinnedRelease;
        if (input.SavedRelease is { } saved && saved.Year >= 1995 && saved.Year > pin)
            return $"Saved in SolidWorks {saved.Year}, and {project.Name} uses SolidWorks {pin}. In SolidWorks, use Save As and pick {pin}, then it uploads by itself.";
        if (input.SavedRelease is not { Year: >= 1995 } && pin >= 1995)
            return $"The SolidWorks year it was saved in is unknown, and {project.Name} only takes files whose year Armory can check. It stays on this computer.";
        return reason ?? "Armory can't take this file. It stays on this computer.";
    }

    private static string PlainReason(string? reason) => reason switch
    {
        "Invalid server path; a lead must fix it." => "Its name in Armory can't be used on Windows. A lead must rename it on ideabosco.com.",
        "An open file cannot propose deletion." => "It is open, so it can't be removed now. Close it first.",
        null => "Armory can't take this file. It stays on this computer.",
        _ => reason,
    };

    private static string ReasonFor(SideVersionReason? why, bool broken) => why switch
    {
        SideVersionReason.SavedWhileCheckedOut => SavedWhileCheckedOutReason,
        SideVersionReason.ChangedWithoutCheckOut => ChangedWithoutCheckOutReason,
        SideVersionReason.UndoCheckOut => UndoReason,
        SideVersionReason.LockBroken => LockBrokenReason,
        SideVersionReason.Conflict => ConflictReason,
        _ => broken ? LockBrokenReason : ConflictReason,
    };

    private async Task<bool> DownloadAsync(FileState st, ProjectState project, VaultPath path, SyncInput input, RemoteFile remote, CancellationToken ct)
    {
        var current = remote.Current!;
        // Recheck right before writing: the plan was made a moment ago.
        if (IsOpenNow(path)) { st.NewerWaiting = true; st.NewerAuthor = current.Author; return false; }
        // The read-only rule is set on the staged copy, so the new bytes are never writable here
        // unless this computer has the file checked out.
        var ownership = DesiredOwnership(st, OwnershipOf(remote.Lock), path);
        var readOnly = CheckoutRules.IsReadOnlyOnDisk(ownership);
        var staging = fs.CreateStaging(out var stagingName);
        var transfer = activity.Start(Directions.Download, st.Path, current.Bytes);
        var arrived = false;
        try
        {
            await deps.Blobs.DownloadAsync(project.Id, current.Hash, current.Bytes, staging, ct, transfer);
            staging.Position = 0;
            Checkpoint("before-replace", ct);
            if (IsOpenNow(path)) { st.NewerWaiting = true; st.NewerAuthor = current.Author; return false; }
            var outcome = fs.Replace(path, input.LocalHash, staging, readOnly);
            if (!outcome.Succeeded)
            {
                Problem(NoticeKinds.CantRead, path.Value, "Armory couldn't put the team's newest version in place yet. Close any program that might be using it. Armory tries again by itself.",
                    outcome.Problem ?? "Replace refused");
                return false;
            }
            arrived = true;
        }
        finally
        {
            if (arrived) activity.Finish(transfer);
            else activity.Fail(transfer);
            await staging.DisposeAsync();
            fs.DeleteStaging(stagingName);
        }
        local[path.Value] = new LocalFile(path, current.Hash, current.Bytes, readOnly);
        st.SetBase(input.Remote);
        st.Preserved = null;
        st.LastCaptured = current.Hash;
        // The bit is right already; the pass's one batch at its end records the intent with the
        // platform (its read-only manifest), so a restart re-applies it.
        st.AppliedOwnership = null;
        Complete(st, current.Hash);
        downloaded++;
        lastActivity = deps.Clock.GetUtcNow();
        MarkDirty();
        Checkpoint("after-download", ct);
        return true;
    }

    private async Task<bool> UploadAsync(FileState st, ProjectState project, VaultPath path, SyncInput input, SyncAction action, CancellationToken ct)
    {
        var hash = input.LocalHash!;
        var snapshot = EnsureSnapshot(st, path, hash);
        if (snapshot is null) return false;
        var bytes = SizeOf(snapshot);
        if (TooLarge(st, bytes)) return false;
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, revive: true, ct)) return false;
        if (action.Kind == SyncActionKind.AcquireLockThenUpload)
        {
            // In Explicit mode only an add takes the lock with its first version (decision D2):
            // checked in by this pass once closed, or by the pass after it closes when it is
            // open now. Durable before the lock is asked for.
            st.AutoCheckIn = true;
            if (!await AcquireAsync(st, snapshot.Id, ct)) return false;
        }
        // Parent and attempt are part of the id: a commit the server kept as a side version is
        // finished, and any later commit of the same bytes is a new intent.
        var parent = ParentOf(st);
        var flight = new Inflight("commit", OperationIds.Derive(snapshot.Id, "commit", parent ?? "", Text(st.Attempt)), snapshot.Id, project.Id, st.FileId,
            ParentId: parent, Hash: hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: input.SavedRelease?.Year,
            ReleaseNotChecked: action.ReleaseNotChecked, Device: HolderDevice(st));
        return await SendAsync(st, flight, ct);
    }

    private async Task<bool> PreserveAsync(FileState st, ProjectState project, VaultPath path, string hash, bool releaseNotChecked, SolidWorksRelease? saved, RemoteFile? remote,
        SideVersionReason? why, CancellationToken ct)
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
        if (!await EnsureServerFileAsync(st, project, path, snapshot.Id, revive: false, ct)) return false;
        var flight = new Inflight("side", OperationIds.Derive(snapshot.Id, "side"), snapshot.Id, project.Id, st.FileId, ParentId: ParentOf(st),
            Hash: hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: saved?.Year,
            Reason: ReasonFor(why, st.BreakNotice), ReleaseNotChecked: releaseNotChecked);
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
        // The team's file lives at another path now and this computer has it there: it moved
        // (a folder move whose record a stop left behind), it was never removed here.
        if (remoteById.TryGetValue(st.FileId.Value, out var moved) && !string.Equals(moved.Path.Value, path.Value, StringComparison.OrdinalIgnoreCase) &&
            TryLocal(moved.Path.Value, out var there) && (there.Hash == st.BaseHash || there.Hash == moved.File.Current?.Hash))
            return false;
        if (st.DeleteEntry is null)
        {
            st.DeleteEntry = NextId("delete");
            journal.Append(new JournalEntry(st.DeleteEntry, IntentKind.Tombstone, path.Value, null, null, state.Email!));
            MarkDirty();
        }
        // Core is told a lock held only for a removal is nobody's; it is still held here.
        var holds = st.FileId is { } held && remoteById.TryGetValue(held, out var record) && OwnershipOf(record.File.Lock) == LockOwnership.ThisDevice;
        if (!holds)
        {
            // A lock taken only for the removal, let go once it is done.
            st.TransientLock = true;
            if (!await AcquireAsync(st, st.DeleteEntry, ct)) return false;
        }
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
        var id = NextId("save");
        try
        {
            SavedSnapshot snapshot;
            using (var source = fs.OpenRead(path)) snapshot = Record(id, path, state.Email!, source);
            st.Entries.Add(id);
            st.LastCaptured = snapshot.Hash;
            MarkDirty();
            if (snapshot.Hash == hash) return snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Problem(NoticeKinds.CantRead, path.Value, "Armory couldn't read it to upload it. Close any program that might be using it. Armory tries again by itself.", error.Message);
        }
        return null; // the file changed since the scan; the next pass plans again
    }

    // The server record a write needs. A new file is created. An add over a removed file
    // (revive: Core's re-add of a removed name) asks armory_create_file too, which revives the
    // removed file with its id and history (contract C4); its operation id is new for each
    // capture, never the original create's, whose receipt would only replay. Kept copies of
    // bytes over a removed file need no revival: they go to the removed file's history.
    private async Task<bool> EnsureServerFileAsync(FileState st, ProjectState project, VaultPath path, string entryId, bool revive, CancellationToken ct)
    {
        var removed = revive && st.FileId is { } known && remoteById.TryGetValue(known, out var record) && record.File.Deleted;
        if (st.FileId is not null && !removed) return true;
        var (folder, name) = Split(path);
        // The name is looked up in what the server listed this pass first: a name another live
        // file holds is refused here, with no call, until that file is renamed or removed (a Pack
        // and Go with a hundred shared names costs no server call on later passes).
        if (LiveNameHolder(project, name) is { } holder)
        {
            if (string.Equals(holder.Folder, folder, StringComparison.OrdinalIgnoreCase) && string.Equals(holder.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                // Someone else added this same path first: it is one file. Core decides the rest.
                st.FileId = holder.Id;
                return false;
            }
            RefuseName(st, holder.Folder, holder.Name);
            return false;
        }
        Inflight flight;
        if (removed) flight = new Inflight("create", OperationIds.Derive(entryId, "revive", st.FileId.ToString()!), entryId, project.Id, null, folder, name);
        else
        {
            st.CreateEntry ??= entryId;
            flight = new Inflight("create", OperationIds.Derive(st.CreateEntry, "create"), st.CreateEntry, project.Id, null, folder, name);
        }
        return await SendAsync(st, flight, ct) && st.FileId is not null;
    }

    // The live file in the project that holds this name, as this pass read the project (names
    // compare as the server compares them: the same letters in any case).
    private RemoteFile? LiveNameHolder(ProjectState project, string name)
    {
        if (!remoteProjects.TryGetValue(project.Id, out var files)) return null;
        if (!liveNames.TryGetValue(project.Id, out var names))
        {
            // Built once per read of the project: the first live file with each name.
            liveNames[project.Id] = names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                if (!file.Deleted) names.TryAdd(file.Name.Normalize(System.Text.NormalizationForm.FormC), file);
        }
        return names.GetValueOrDefault(name.Normalize(System.Text.NormalizationForm.FormC));
    }

    // Each project's live files by name (as the server compares names: the same letters in any case).
    private readonly Dictionary<Guid, Dictionary<string, RemoteFile>> liveNames = [];

    // Contract v2 (C4, D6): a name whose only holder is a removed file revives that file, with its
    // id and history. The added bytes are committed on top of the revived file's current version:
    // with no parent the server would keep them aside as a stale parent, and the removed bytes
    // would come back over them (docs/server/contract.md, open point 1). Whether the id is a
    // revival is read from the change feed (file_revived) before the first commit, so a removal
    // between this pass's refresh and the add never falls back to no parent; the revived file's
    // current version is then fetched fresh. This computer's record of the removed file at its
    // old path is that file's past, not a second file: with nothing of it on this disk it is
    // forgotten, so the revived file is never fetched back to the old path.
    private async Task ContinueRevivedHistoryAsync(FileState st, Guid projectId, CancellationToken ct)
    {
        if (st.FileId is not { } id || !state.Projects.TryGetValue(projectId, out var project)) return;
        RemoteVersion? current = null;
        var changes = await deps.Api.ListChangesAsync(project.Id, project.Cursor, ct);
        Proceed(ct);
        var revival = changes.FirstOrDefault(c => c.Kind == "file_revived" && c.EntityId == id);
        if (revival is not null)
        {
            RecordRevival(id, revival.CreatedAt);
            var files = await deps.Api.ProjectFilesAsync(project.Id, ct);
            Proceed(ct);
            if (files.FirstOrDefault(f => f.Id == id) is { } revived)
            {
                Know(project, revived);
                current = revived.Current;
            }
        }
        else if (remoteById.TryGetValue(id, out var known)) current = known.File.Current;
        if (current is not null && st.Base is not { IsTombstone: false }) st.SetBase(new(current.Id.ToString(), current.Hash, current.Author));
        foreach (var past in state.WithFileId(id, except: st).Where(f => f.Inflight is null && !local.ContainsKey(f.Path)))
            state.Files.Remove(past.Path);
    }

    private static string? ParentOf(FileState st) => Guid.TryParse(st.BaseId, out _) ? st.BaseId : null;

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
        Proceed(ct); // a crash elsewhere in this pass: nothing more is saved or sent
        st.Inflight = flight;
        await FlushAsync(); // group commit: durable before the call
        return await SendDurableAsync(st, flight, ct);
    }

    // The write whose in-flight record is already on disk: sent, its answer applied, cleared.
    private async Task<bool> SendDurableAsync(FileState st, Inflight flight, CancellationToken ct)
    {
        Proceed(ct);
        wrote = true;
        if (flight.ProjectId is { } written) projectsWritten.Add(written);
        Checkpoint($"before-{flight.Kind}", ct);
        bool result;
        try { result = await SendRecordedAsync(st, flight, ct); }
        catch (ArmoryOfflineException) { online = false; throw; }
        catch (Exception error) when (error is ArmoryClientException or HashMismatchException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            st.Inflight = null;
            if (error is StorageTransferException) { FileProblem(st.Path, error); result = false; } // this file waits for the next try
            else if (flight.Kind == "create" && error is ArmoryRpcException { IsNameTaken: true } taken) result = AdoptOrRefuseName(st, flight, taken);
            else if (flight.Kind is "lock" or "release" or "move")
            {
                Problem(NoticeKinds.CantSend, st.Path, flight.Kind == "move" ? "The server didn't accept the rename, so it keeps its name for now."
                    : "The server didn't accept it this time. Armory tries again by itself.", error.Message, $"Armory couldn't finish a change to {NameOf(st.Path)}");
                result = false;
            }
            else { st.Refusal = PlainRefusal(error, state.Projects.GetValueOrDefault(st.ProjectId)); st.RefusalKind = RefusedKind; refused++; result = false; }
            MarkDirty();
            return result;
        }
        Checkpoint($"after-{flight.Kind}", ct);
        st.Inflight = null;
        MarkDirty();
        return result;
    }

    // A refusal in the window's words. The server's own message is for the log only: it can
    // name things a student never sees (a vault, a lock, an RPC).
    private static string PlainRefusal(Exception error, ProjectState? project) => error switch
    {
        BlobRefusedException { Status: 403 } => "Armory wouldn't take this file: you may no longer be in this project. Ask your CAD lead.",
        BlobRefusedException => "Armory wouldn't take this file. It stays on this computer.",
        ArmoryRpcException { IsForbidden: true } => "Armory wouldn't take this file: you may no longer be in this project. Ask your CAD lead.",
        ArmoryRpcException rpc when rpc.Message.Contains("SolidWorks", StringComparison.Ordinal) && project is not null =>
            $"It was saved in a SolidWorks year {project.Name} can't take. In SolidWorks, use Save As and pick {project.PinnedRelease}, then it uploads by itself.",
        ArmoryRpcException rpc when rpc.Message.Contains("SolidWorks", StringComparison.Ordinal) =>
            "It was saved in a SolidWorks year this project can't take. It stays on this computer.",
        ArmoryRpcException { IsInvalidInput: true } => "Armory can't take it under this name. Rename it, then it uploads by itself.",
        ArmoryRpcException => "The server didn't take it this time. It stays on this computer, and Armory tries again by itself.",
        _ => "This save couldn't be read back from this computer's safe copy. It stays on this computer.",
    };

    // Every answer is applied only while the pass goes on (Proceed right after each call): after
    // a crash elsewhere, an answer that arrives is dropped with its in-flight record kept, exactly
    // as a real crash would lose it, so the state saved never holds half of a step.
    private async Task<bool> SendRecordedAsync(FileState st, Inflight f, CancellationToken ct)
    {
        var device = f.Device ?? state.DeviceId!.Value;
        switch (f.Kind)
        {
            case "create":
            {
                var created = await deps.Api.CreateFileAsync(f.ProjectId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
                Proceed(ct);
                st.FileId = created;
                // An id this computer has seen is a revival (or this same create answered again):
                // its history goes on. A new id is a new file, with nothing to read for it.
                if (remoteById.ContainsKey(created)) await ContinueRevivedHistoryAsync(st, f.ProjectId!.Value, ct);
                return true;
            }
            case "lock":
            {
                // Every answer spends the attempt: a later acquire is a new intent.
                var held = await deps.Api.AcquireLockAsync(f.FileId!.Value, device, f.Operation, ct);
                Proceed(ct);
                st.Attempt++;
                if (held) KnowLock(f.FileId.Value, new RemoteLock(state.Email!, device, deps.Sessions.Current?.DeviceName, deps.Clock.GetUtcNow(), null, null, null, null));
                return held;
            }
            case "release":
                await deps.Api.ReleaseLockAsync(f.FileId!.Value, device, f.Operation, ct);
                Proceed(ct);
                KnowLock(f.FileId.Value, null);
                return true;
            case "tombstone":
                var removed = await deps.Api.TombstoneAsync(f.FileId!.Value, Parse(f.ParentId), device, f.Operation, ct);
                Proceed(ct);
                if (removed)
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
                var transfer = activity.Start(Directions.Upload, st.Path, f.Bytes);
                CommitResult answer;
                try
                {
                    await UploadBlobAsync(f, transfer, ct);
                    Checkpoint("after-blob", ct);
                    answer = await deps.Api.CommitVersionWithReleaseAsync(f.FileId!.Value, Parse(f.ParentId), ContentObjectKey.FromHash(f.Hash!), f.Hash!, f.Bytes,
                        device, f.Operation, f.SavedRelease, ct);
                    Proceed(ct);
                }
                catch
                {
                    activity.Fail(transfer);
                    throw;
                }
                activity.Finish(transfer);
                Checkpoint("after-commit-rpc", ct);
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
                    AddSide(st, answer.VersionId, f.Hash!, ConflictReason);
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
                var transfer = activity.Start(Directions.Upload, st.Path, f.Bytes);
                Guid id;
                try
                {
                    await UploadBlobAsync(f, transfer, ct);
                    Checkpoint("after-blob", ct);
                    id = await deps.Api.SaveSideVersionWithReleaseAsync(f.FileId!.Value, Parse(f.ParentId), ContentObjectKey.FromHash(f.Hash!), f.Hash!, f.Bytes,
                        f.Reason ?? "conflict", device, f.Operation, f.SavedRelease, ct);
                    Proceed(ct);
                }
                catch
                {
                    activity.Fail(transfer);
                    throw;
                }
                activity.Finish(transfer);
                if (f.Kind == "side")
                {
                    st.Preserved = f.Hash;
                    st.BreakNotice = false;
                    Complete(st, f.Hash!);
                }
                state.Completed.Add(f.EntryId!);
                st.Entries.Remove(f.EntryId!);
                st.Drafts.Remove(f.EntryId!);
                AddSide(st, id, f.Hash!, f.Reason ?? ConflictReason);
                st.ReleaseNotChecked = f.ReleaseNotChecked;
                lastActivity = deps.Clock.GetUtcNow();
                return true;
            }
            case "move":
            {
                var moved = await deps.Api.MoveFileAsync(f.FileId!.Value, f.Folder!, f.Name!, device, f.Operation, ct);
                Proceed(ct);
                return moved;
            }
            default:
                throw new InvalidOperationException($"Unknown in-flight write {f.Kind}.");
        }
    }

    // Every save made while checked out is a kept copy (decision D1), so the list keeps only
    // the newest ones: enough to recognize bytes already kept and to show recent notices.
    private const int SidesKept = 20;
    private void AddSide(FileState st, Guid versionId, string hash, string reason)
    {
        if (st.Sides.Any(s => s.VersionId == versionId)) return;
        st.Sides.Add(new(versionId, hash, reason, deps.Clock.GetUtcNow()));
        if (st.Sides.Count > SidesKept) st.Sides.RemoveRange(0, st.Sides.Count - SidesKept);
        sideVersions++;
    }

    private async Task UploadBlobAsync(Inflight f, IProgress<long> progress, CancellationToken ct)
        => await deps.Blobs.UploadAsync(f.ProjectId!.Value, f.Hash!, f.Bytes, () => deps.Snapshots.OpenRead(f.SnapshotId!), ct, progress);

    // The server refuses a name only while a live file holds it (a removed one is revived, C4).
    private bool AdoptOrRefuseName(FileState st, Inflight f, ArmoryRpcException error)
    {
        var folder = ParseExistingFolder(error.Details, out var existingId, out var existingName);
        if (existingId is { } id && string.Equals(folder, f.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(existingName, f.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Someone else created this same path first: it is one file. Core decides the rest.
            st.FileId = id;
            return false;
        }
        RefuseName(st, folder ?? "", existingName ?? f.Name!);
        return false;
    }

    // A name another file in the project holds: this file stays on this computer, in the
    // nameShared card, until one of them is renamed.
    private void RefuseName(FileState st, string folder, string name)
    {
        var project = state.Projects.GetValueOrDefault(st.ProjectId)?.Name ?? "This project";
        st.Refusal = $"{project} already has {name} in {(string.IsNullOrEmpty(folder) ? "its top folder" : folder.Replace("/", " \u203a ", StringComparison.Ordinal))}.";
        st.RefusalKind = NameTakenKind;
        refused++;
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
            if (st.Inflight!.Kind is "release" or "tombstone") { st.Inflight = null; MarkDirty(); continue; }
            // Already on disk from before the stop: sent again as it is, with the same id.
            try { await SendDurableAsync(st, st.Inflight, ct); sent = true; }
            catch (ArmoryOfflineException) { online = false; return sent; }
            staleProjects.Add(project.Id);
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
            if (project is null || !project.Usable || (project.Archived && !MineToFinish(st)) || HeldByWork(st.Path) || heldProjects.Contains(project.Id)) continue;
            local.TryGetValue(st.Path, out var current);
            var remote = st.FileId is { } fid && remoteById.TryGetValue(fid, out var r) ? r.File : null;
            foreach (var id in st.Entries.Concat(st.Drafts).ToArray())
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Hash is null) continue;
                if (entry.Hash == current?.Hash) continue; // the plan uploads the bytes on disk
                if (entry.Hash == remote?.Current?.Hash || entry.Hash == st.BaseHash || st.Sides.Any(s => s.Hash == entry.Hash))
                {
                    state.Completed.Add(id); st.Entries.Remove(id); st.Drafts.Remove(id); MarkDirty();
                    continue;
                }
                SolidWorksRelease? saved = null;
                var notChecked = false;
                if (Reconciler.IsSolidWorks(path))
                {
                    try { saved = await ReadReleaseFromSnapshotAsync(entry.SnapshotId!, ct); }
                    catch (Exception error) when (error is IOException or InvalidDataException) { EarlierSaveProblem(st.Path, error); continue; }
                    var gate = SolidWorksVersionGate.Decide(saved, new SolidWorksRelease(project.PinnedRelease), project.Enforce ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn);
                    if (!gate.Allowed)
                    {
                        // Kept as a private draft on this computer; offered again if the gate changes.
                        if (st.Entries.Remove(id)) { st.Drafts.Add(id); MarkDirty(); }
                        continue;
                    }
                    notChecked = gate.ReleaseNotChecked;
                }
                try
                {
                    var snapshot = SnapshotById(entry.SnapshotId!) ?? throw new InvalidOperationException($"The safe copy {entry.SnapshotId} is missing.");
                    var bytes = SizeOf(snapshot);
                    if (bytes > options.MaximumFileBytes) { if (st.Entries.Remove(id)) { st.Drafts.Add(id); MarkDirty(); } continue; }
                    if (!await EnsureServerFileAsync(st, project, path, id, revive: false, ct)) break;
                    var flight = new Inflight("archive", OperationIds.Derive(snapshot.Id, "side"), id, project.Id, st.FileId, ParentId: ParentOf(st),
                        Hash: entry.Hash, Bytes: bytes, SnapshotId: snapshot.Id, SavedRelease: saved?.Year, Reason: EarlierSaveReason, ReleaseNotChecked: notChecked);
                    if (!await SendAsync(st, flight, ct)) break;
                }
                catch (ArmoryOfflineException) { online = false; return; }
                catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException) { EarlierSaveProblem(st.Path, error); break; }
            }
        }
    }

    private void EarlierSaveProblem(string path, Exception error)
        => Problem(NoticeKinds.CantRead, path, "Armory couldn't read an earlier save of it from this computer's safe copy. It tries again by itself.", error.Message);

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
        var moving = state.Files.Values.Where(f => f.FileId is not null && f.LocalMoveTo is null &&
            remoteById.TryGetValue(f.FileId.Value, out var remote) && !string.Equals(remote.Path.Value, f.Path, StringComparison.Ordinal)).ToArray();
        if (moving.Length == 0) return false;
        // The pass's moves are one operation, shown as moving to the folder they all go to.
        BeginMoving(moving.Count(f => local.ContainsKey(f.Path)), CommonFolder(moving.Select(f => remoteById[f.FileId!.Value].Path.Value)));
        try
        {
            foreach (var st in moving) any |= ApplyRemoteMove(st);
        }
        finally { EndMoving(); }
        if (any) SaveNow();
        return any;
    }

    // The folder every one of these paths is in (the deepest one they share).
    private static string CommonFolder(IEnumerable<string> paths)
    {
        string[]? common = null;
        foreach (var path in paths)
        {
            var parts = path.Split('/')[..^1];
            if (common is null) { common = parts; continue; }
            var same = 0;
            while (same < common.Length && same < parts.Length && string.Equals(common[same], parts[same], StringComparison.OrdinalIgnoreCase)) same++;
            common = common[..same];
        }
        return string.Join('/', common ?? []);
    }

    // One file whose server folder or name changed. True when it moved (or its record did).
    private bool ApplyRemoteMove(FileState st)
    {
        if (!remoteById.TryGetValue(st.FileId!.Value, out var remote) || string.Equals(remote.Path.Value, st.Path, StringComparison.Ordinal)) return false;
        // Already where the team has it on this disk (a folder move whose record a stop left
        // behind): the record follows, nothing moves, and the file is never taken as gone.
        if (!local.ContainsKey(st.Path) && !remote.File.Deleted && local.TryGetValue(remote.Path.Value, out var there) &&
            (there.Hash == st.BaseHash || there.Hash == remote.File.Current?.Hash) && !HeldByWork(remote.Path.Value))
        {
            if (state.Files.TryGetValue(remote.Path.Value, out var newcomer) && !ReferenceEquals(newcomer, st))
            {
                // Captured at its new place as a new file: that is this file.
                if ((newcomer.FileId is { } other && other != st.FileId) || newcomer.Inflight is not null) return false;
                state.Files.Remove(remote.Path.Value);
                foreach (var id in newcomer.Entries) if (!st.Entries.Contains(id)) st.Entries.Add(id);
                st.LastCaptured = newcomer.LastCaptured ?? st.LastCaptured;
            }
            Rekey(st, remote.Path.Value);
            if (st.BaseHash is not null) Complete(st, st.BaseHash);
            return true;
        }
        // A folder being renamed or put back here moves as one, never file by file.
        if (Held(st.Path) || Held(remote.Path.Value)) return false;
        if (!VaultPath.TryCreate(st.Path, out var from, out _, options.VaultRoot)) return false;
        if (state.Files.TryGetValue(remote.Path.Value, out var occupant) && !ReferenceEquals(occupant, st) && !string.Equals(remote.Path.Value, st.Path, StringComparison.OrdinalIgnoreCase))
        {
            Notice(NoticeKinds.NameShared, null, remote.Path.Value, $"{from.Name} was renamed to {remote.Path.Name} on the team's side. Rename your own {remote.Path.Name} so both can stay.");
            return false;
        }
        if (local.TryGetValue(st.Path, out var file))
        {
            if (IsOpenNow(from))
            {
                // Its folder was renamed or moved for the team (the rest of the folder moved already).
                var sameName = string.Equals(from.Name, remote.Path.Name, StringComparison.Ordinal);
                Notice(NoticeKinds.NewerWaiting, st.FileId, st.Path,
                    sameName ? $"It was moved to {Where(Parent(remote.Path.Value)!)}. Close {from.Name} to finish moving it." : $"It was renamed to {remote.Path.Name}. Close {from.Name} to finish moving it.",
                    sameName ? $"{from.Name} was moved" : $"{from.Name} was renamed");
                return false;
            }
            var outcome = fs.Move(from, remote.Path, file.Hash);
            if (!outcome.Succeeded)
            {
                Problem(NoticeKinds.CantRead, from.Value, $"It was renamed to {remote.Path.Name} for the team, and Armory couldn't rename it here yet. Close any program that might be using it. Armory tries again by itself.",
                    outcome.Problem ?? "Move refused");
                return false;
            }
            local.Remove(st.Path);
            local[remote.Path.Value] = file with { Path = remote.Path };
        }
        Rekey(st, remote.Path.Value);
        return true;
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
            if (project is null || !project.Usable || project.Archived || HeldByWork(st.Path) || heldProjects.Contains(project.Id)) continue;
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
            MarkDirty();
        }
    }

    // Requested with MoveAsync, or detected from Explorer: the holder renames through
    // armory_move_file. A refused Explorer rename is put back where it was.
    private async Task ExecutePendingMovesAsync(CancellationToken ct)
    {
        var any = false;
        foreach (var move in state.Moves.ToArray())
        {
            var st = state.FirstWithFileId(move.FileId);
            if (st is null || !VaultPath.TryCreate(move.To, out var to, out _, options.VaultRoot) || !VaultPath.TryCreate(move.From, out var from, out _, options.VaultRoot))
            { state.Moves.Remove(move); moveResults[move.Operation] = false; MarkDirty(); continue; }
            remoteById.TryGetValue(move.FileId, out var remote);
            bool done;
            try
            {
                done = remote.File is { Deleted: false } && (OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice || await AcquireTransientAsync(st, move.Operation.ToString(), ct));
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
                if (move.Local) Notice(NoticeKinds.NameShared, move.FileId, move.To, error.IsNameTaken ? $"{to.Name} is already used in this project." : error.Message);
                done = false;
            }
            any = true;
            state.Moves.Remove(move);
            moveResults[move.Operation] = done;
            if (move.Local) FinishLocalMove(st, from, to, done, remote.File?.Lock);
            staleProjects.Add(st.ProjectId);
            SaveNow();
        }
        // The projects a move changed are read again (only those), and the team's moves follow.
        if (any && await RefreshStaleAsync(ct)) ApplyRemoteMoves();
    }

    private void FinishLocalMove(FileState st, VaultPath from, VaultPath to, bool moved, RemoteLock? held)
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
            var who = OwnershipOf(held) is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice ? Who(held!) : null;
            Remember(NoticeKinds.FolderPutBack, st.FileId, from.Value,
                who is null ? $"{from.Name} was put back where it was" : $"{from.Name} was put back: {who} has it checked out.",
                who is null ? $"It can't be renamed right now, so Armory put it back. Try again later."
                    : $"A file can be renamed only while nobody else has it checked out. Try again after it's checked in.",
                who is null ? "It was put back where it was." : $"{who} has it checked out.", who is null ? null : CheckedOutReason);
        }
        else
        {
            st.LocalMoveTo = to.Value; // try again on the next pass; never delete the original
            Notice(NoticeKinds.NewerWaiting, st.FileId, from.Value, $"Close {to.Name} so Armory can finish or undo the rename.", $"{from.Name} is waiting to be renamed");
            state.Moves.Add(new PendingMove(Guid.NewGuid(), st.FileId!.Value, from.Value, to.Value, Local: true));
        }
    }

    private readonly Dictionary<Guid, bool> moveResults = [];

    public async Task<bool> MoveAsync(VaultPath from, VaultPath to, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => MoveAsync(from, to, cancellationToken));
        if (!from.IsValid || !to.IsValid) return false;
        Guid operation;
        await EnterActionAsync(cancellationToken);
        try
        {
            if (!state.Files.TryGetValue(from.Value, out var st) || st.FileId is null) return false;
            if (!string.Equals(ProjectOf(from)?.Name, ProjectOf(to)?.Name, StringComparison.OrdinalIgnoreCase)) return false;
            // Never rename under an open document or onto a file this computer already has.
            if (fs.IsOpen(from) || markerDocuments.Contains(from.Value) || state.Files.ContainsKey(to.Value) || Exists(to)) return false;
            operation = Guid.NewGuid();
            state.Moves.Add(new PendingMove(operation, st.FileId.Value, from.Value, to.Value));
            MarkDirty();
        }
        finally { LeaveAction(); }
        await SyncOnceAsync(cancellationToken);
        return moveResults.Remove(operation, out var done) && done;
    }

    // ---- Locks -------------------------------------------------------------------------

    // A lock taken only so this computer can move or remove the file; FinishRequestsAsync lets
    // it go once that is done. Durable before the lock is asked for.
    private async Task<bool> AcquireTransientAsync(FileState st, string entryId, CancellationToken ct)
    {
        st.TransientLock = true;
        return await AcquireAsync(st, entryId, ct);
    }

    // What the read-only rule is applied from: the file's lock ownership, except that a file
    // this computer is letting go of (a check in, an undo, a closed add, a lock taken only for a
    // move or a removal) is read-only already, before its lock is released.
    private LockOwnership DesiredOwnership(FileState st, LockOwnership ownership, VaultPath path)
        => ownership == LockOwnership.ThisDevice && (st.Request != CheckoutRequest.None || st.TransientLock || (st.AutoCheckIn && !IsOpenNow(path)))
            ? LockOwnership.Free : ownership; // MUTATION: a checked-in file left writable

    // Decision D4: a file the server has is read-only on disk unless this computer has it
    // checked out (CheckoutRules.IsReadOnlyOnDisk). Applied every pass, offline too (from the
    // ownership this computer last knew), and again whenever the scan finds the bit cleared.
    // Files the server does not have (not added yet, refused, drafts) are never touched. What
    // this computer knows of the server includes its own lock changes of this pass (KnowLock),
    // so a check in whose connection dropped right after the lock went is read-only all the same.
    private void ApplyReadOnly()
    {
        List<(VaultPath Path, LockOwnership Ownership)> batch = [];
        List<FileState> changed = [];
        foreach (var st in state.Files.Values)
        {
            // Where the file is on disk (in a folder waiting to go back, too: the rule holds there).
            if (st.FileId is not { } id || !TryLocal(st.Path, out var file)) continue;
            // Archived (decision D8): its files are left as they are.
            if (state.Projects.GetValueOrDefault(st.ProjectId) is { Archived: true }) continue;
            LockOwnership ownership;
            if (remoteById.TryGetValue(id, out var remote))
            {
                if (remote.File.Deleted || remote.File.Current is null) continue;
                ownership = OwnershipOf(remote.File.Lock);
            }
            // Offline with nothing read from the server since the start: a file with a live base
            // is the server's, ruled by the ownership last applied; with none applied (a crash
            // before the rule ran), by the check out last known, and with none known, nobody's.
            else if (st.BaseHash is not null) ownership = st.AppliedOwnership ?? KnownOwnership(st);
            else continue;
            var desired = DesiredOwnership(st, ownership, file.Path);
            if (st.AppliedOwnership == desired && file.ReadOnly == CheckoutRules.IsReadOnlyOnDisk(desired)) continue;
            batch.Add((file.Path, desired));
            changed.Add(st);
        }
        if (batch.Count == 0) return;
        try
        {
            fs.ApplyLockAttributes(batch);
            for (var i = 0; i < batch.Count; i++)
            {
                changed[i].AppliedOwnership = batch[i].Ownership;
                local[batch[i].Path.Value] = local[batch[i].Path.Value] with { ReadOnly = CheckoutRules.IsReadOnlyOnDisk(batch[i].Ownership) };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Problem(NoticeKinds.CantRead, null, "Armory couldn't set which files can be saved on this computer. It tries again by itself.", error.Message);
        }
        MarkDirty();
    }

    // One file's read-only bit, now (a check out makes it writable; a check in read-only), where
    // the file is on disk. False when the bit could not be set: a lock is then never let go over
    // a writable file.
    private bool SetAttribute(VaultPath path, FileState st, LockOwnership ownership)
    {
        if (!TryLocal(path.Value, out var file)) return true;
        try
        {
            fs.ApplyLockAttribute(file.Path, ownership);
            st.AppliedOwnership = ownership;
            local[file.Path.Value] = file with { ReadOnly = CheckoutRules.IsReadOnlyOnDisk(ownership) };
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Problem(NoticeKinds.CantRead, path.Value, "Armory couldn't make it read-only or writable yet. Close any program that might be using it. Armory tries again by itself.", error.Message);
            return false;
        }
    }
}
