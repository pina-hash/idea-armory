using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Put back on this computer (feedback N4's recovery): one of your own kept copies of a file
// becomes the file on this disk again, checked out to you, to look at in SolidWorks and check
// in. Until 0.3.3 a kept copy could only be downloaded from the file's page on ideabosco.com.
public sealed partial class SyncEngine
{
    // fileId: the file; versionId: the kept copy (a history entry of kind keptCopy, its Id).
    // Only the signed-in person's own kept copy is put back, and only over a file checked out to
    // this computer: one checked out to nobody is checked out first (CheckOutAsync, which keeps
    // bytes saved without a check out as a kept copy and puts the shared version back before it
    // takes the lock). What is on disk is never lost: a pass for the file first keeps any save
    // the server does not have yet (as a kept copy, "saved while checked out"), and the bytes
    // are replaced only when a read of them just now is on the server and the file is closed.
    // The file is left checked out: nothing is shared until the student checks it in.
    public async Task<ActionResult> PutBackKeptCopyAsync(Guid fileId, Guid versionId, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => PutBackKeptCopyAsync(fileId, versionId, cancellationToken));
        RemoteHistoryEntry kept;
        string name, path;
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (online != true) return Offline("A kept copy can be put back once this computer is back online.");
            if (!remoteById.TryGetValue(fileId, out var remote) || remote.File.Deleted || remote.File.Current is null) return new(false, "That file isn't in your projects.");
            name = remote.File.Name;
            path = remote.Path.Value;
            var found = await KeptCopyAsync(fileId, versionId, cancellationToken);
            if (found.Refusal is { } refusal) return new(false, refusal.Replace("{name}", name, StringComparison.Ordinal));
            kept = found.Entry!;
            if (remote.File.Lock is { IsLive: true } held && OwnershipOf(held) is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice)
                return new(false, OwnershipOf(held) == LockOwnership.MyOtherDevice
                    ? $"{name} is checked out on your other computer, {held.HolderDeviceName ?? "another computer"}. Check it in there first, then put your copy back."
                    : $"{name} is checked out by {Who(held)}. Your copy can be put back once it's checked in.");
        }
        finally { LeaveAction(); }
        // Checked out to nobody: a check out first, as the student would click it.
        if (!CheckedOutHere(fileId))
        {
            var checkedOut = await CheckOutAsync([path], cancellationToken: cancellationToken);
            if (!CheckedOutHere(fileId)) return new(false, checkedOut.Message);
        }
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            if (online != true || !CheckedOutHere(fileId) || !remoteById.TryGetValue(fileId, out var remote) || remote.File.Deleted)
                return new(false, $"{name} isn't checked out to you any more, so your copy wasn't put back.");
            var st = state.Files.Values.FirstOrDefault(f => f.FileId == fileId);
            if (st is null || !VaultPath.TryCreate(st.Path, out var recordPath, out _, options.VaultRoot)) return new(false, $"Armory couldn't find {name} on this computer.");
            var disk = TryLocal(st.Path, out var here) ? here.Path : recordPath;
            if (IsOpenNow(disk)) return new(false, $"Close {name} in SolidWorks first, then put your copy back.");
            // Any save the server does not have yet is kept first, as every pass keeps it.
            await PassLockedAsync(cancellationToken, PassScope.File(fileId));
            if (online != true) return Offline("A kept copy can be put back once this computer is back online.");
            if (!CheckedOutHere(fileId)) return new(false, $"{name} isn't checked out to you any more, so your copy wasn't put back.");
            // Where it is on disk after that pass (a folder put back moves it).
            disk = TryLocal(st.Path, out var now) ? now.Path : recordPath;
            if (IsOpenNow(disk)) return new(false, $"Close {name} in SolidWorks first, then put your copy back.");
            string? hash;
            try
            {
                await using var stream = fs.OpenRead(disk);
                hash = await ContentAddress.ComputeAsync(stream, cancellationToken);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { hash = null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                deps.Log?.Invoke($"put back: {disk}: {error.Message}");
                return new(false, $"Armory couldn't read {name}, so your copy wasn't put back. Close any program that might be using it, then try again.");
            }
            if (hash == kept.Hash) return new(true, $"Your copy is already {name} on this computer. It's checked out to you.");
            if (hash is not null && !await OnServerAsync(st, fileId, hash, cancellationToken))
                return new(false, $"Armory couldn't keep the changes to {name} on this computer yet, so your copy wasn't put back. Try again in a moment.");
            return await PutBackAsync(st, remote.Project, disk, hash, kept, name, cancellationToken);
        }
        finally { LeaveAction(); }
    }

    // The kept copy asked for, from the file's history on the server: the person's own, with bytes.
    private async Task<(RemoteHistoryEntry? Entry, string? Refusal)> KeptCopyAsync(Guid fileId, Guid versionId, CancellationToken ct)
    {
        IReadOnlyList<RemoteHistoryEntry> history;
        try { history = await deps.Api.FileHistoryAsync(fileId, ct); }
        catch (ArmoryOfflineException) { online = false; return (null, "You're offline. A kept copy can be put back once this computer is back online."); }
        catch (ArmoryClientException) { return (null, "Armory couldn't read {name}'s history just now. Try again in a moment."); }
        var entry = history.FirstOrDefault(h => h.Id == versionId);
        if (entry is null || entry.Kind != "side_version" || entry.Hash is null) return (null, "That kept copy isn't in {name}'s history.");
        if (!string.Equals(entry.Author, state.Email, StringComparison.OrdinalIgnoreCase))
            return (null, $"That copy of {{name}} is {DisplayName(entry.Author)}'s. Only your own kept copies can be put back here.");
        return (entry, null);
    }

    private bool CheckedOutHere(Guid fileId)
        => remoteById.TryGetValue(fileId, out var remote) && !remote.File.Deleted && OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice;

    // Whether these bytes are on the server for this file: its shared version, or a version or a
    // kept copy in its history (one this computer just kept included).
    private async Task<bool> OnServerAsync(FileState st, Guid fileId, string hash, CancellationToken ct)
    {
        if (hash == st.BaseHash || hash == st.Preserved || st.Sides.Any(s => s.Hash == hash)) return true;
        try { return (await deps.Api.FileHistoryAsync(fileId, ct)).Any(h => h.Hash == hash); }
        catch (ArmoryClientException) { return false; }
    }

    // The kept copy's bytes, downloaded to staging, then put in place over exactly the bytes read
    // just now (Replace refuses a file that changed or opened since), writable: it is checked out
    // here. Those bytes are on the server already, so they are never kept again as a new copy.
    private async Task<ActionResult> PutBackAsync(FileState st, ProjectState project, VaultPath disk, string? expected, RemoteHistoryEntry kept, string name, CancellationToken ct)
    {
        var staging = fs.CreateStaging(out var stagingName);
        var transfer = activity.Start(Directions.Download, st.Path, kept.Bytes);
        var arrived = false;
        try
        {
            try { await deps.Blobs.DownloadAsync(project.Id, kept.Hash!, kept.Bytes, staging, ct, transfer); }
            catch (ArmoryOfflineException) { online = false; return Offline("A kept copy can be put back once this computer is back online."); }
            catch (Exception error) when (error is ArmoryClientException or Armory.Storage.HashMismatchException or IOException)
            {
                deps.Log?.Invoke($"put back: {disk}: {error.Message}");
                return new(false, $"Armory couldn't download your copy of {name}. Try again in a moment.");
            }
            staging.Position = 0;
            if (IsOpenNow(disk)) return new(false, $"Close {name} in SolidWorks first, then put your copy back.");
            var outcome = fs.Replace(disk, expected, staging, readOnly: false);
            if (!outcome.Succeeded)
            {
                deps.Log?.Invoke($"put back: {disk}: {outcome.Problem}");
                return new(false, $"Armory couldn't put your copy of {name} in place. Close any program that might be using it, then try again.");
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
        local[disk.Value] = new LocalFile(disk, kept.Hash!, kept.Bytes);
        st.Preserved = kept.Hash;
        st.LastCaptured = kept.Hash;
        // Writable, as it was; the pass's one batch at its end records the intent with the platform.
        st.AppliedOwnership = null;
        MarkDirty();
        SaveNow();
        activity.Log($"Put your copy of {name} back");
        PublishLocked();
        return new(true, $"Put your copy of {name} back on this computer. It's checked out to you: look at it in SolidWorks, then check it in to share it.");
    }
}
