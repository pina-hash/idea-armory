using System.Text.Json;
using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine;

// What the account a folder belongs to still has waiting in it (WaitingAsync): counts, and the
// same phrases the take-over refuses with ("2 files checked out", "1 save not sent").
public sealed record FolderWaiting(string? Owner, int CheckedOut, int Unsent, int Changed, int Added, int FolderChanges, IReadOnlyList<string> Parts)
{
    public bool Any => Parts.Count > 0;
    // The phrases in one: "2 files checked out and 1 save not sent".
    public string Words => SyncEngine.JoinWords(Parts);
}

// One Armory folder, taken in turns (0.3.2): a lab computer where several students sign in to
// Armory in turn keeps one folder, never a folder per student. The folder is handed to the
// account signed in now only when the account it belongs to has nothing waiting in it: no file
// checked out here, no save not sent, no change on disk Armory hasn't kept, no new file not in
// Armory yet, no move or folder change not sent. Anything waiting stays that person's (it is
// their work), so the folder stays theirs and the window says what is waiting and who can
// finish it. Two accounts in one folder at once is never allowed: the second would see the
// first one's work as its own.
//
// On a shared computer (0.3.3, docs/agent/PROFILES.md) the host asks the same questions before a
// switch: WaitingAsync of the student running on the shared folder, OwnerOf the folder without an
// engine, and SealCheckOutsAsync when the next student moves to a folder of their own.
public sealed partial class SyncEngine
{
    public async Task<ActionResult> TakeOverFolderAsync(CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => TakeOverFolderAsync(cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            var session = deps.Sessions.Current;
            if (session is null) return new(false, "Connect this computer first.");
            if (state.Email is not { } owner || string.Equals(owner, session.Email, StringComparison.OrdinalIgnoreCase))
                return new(true, "This Armory folder is already yours.");
            VaultScan scan;
            try { scan = fs.Scan(); }
            catch (IOException) { return new(false, "Armory can't look through this folder right now. Try again in a moment."); }
            var waiting = WaitingIn(scan);
            ownerWaiting = (owner, waiting.Parts, deps.Clock.GetTimestamp());
            var who = DisplayName(owner);
            var first = who.Split(' ')[0];
            if (waiting.Any)
            {
                RequestPublish();
                return new(false, $"{who} still has {waiting.Words} in this folder. {first} can sign in to Armory here to finish them, or you can use a folder of your own.");
            }
            // Nothing of theirs is waiting: the folder's files are the team's versions, the same for
            // anyone in the project. The next pass binds it to this account and this device; a
            // project this account isn't in stays on disk as it is and stops syncing.
            state.Email = null;
            state.DeviceId = null;
            state.FormerDevices.Clear();
            state.Remembered.Clear();
            state.Dismissed.Clear();
            state.Imports.Clear();
            foreach (var st in state.Files.Values)
            {
                st.Sides.Clear(); // their kept copies are in each file's history, not news for this account
                st.BreakNotice = false;
                st.Refusal = null;
                st.RefusalKind = null;
            }
            MarkDirty();
            SaveNow();
            ownerWaiting = null;
            deps.Log?.Invoke("folder: handed over by its account to the one signed in now");
            activity.Log($"This Armory folder is yours now. It was {first}'s.");
            return new(true, $"This Armory folder is yours now. {who}'s files here were all saved to Armory, so nothing of theirs changes.");
        }
        finally { LeaveAction(); }
    }

    // What the folder's account has waiting here now, from a fresh scan, under the action gate (a
    // pass running finishes first). The shared computer's host asks it of the student running on
    // the shared folder before handing it to the next one, and records it for the picker's tiles.
    // An IOException means the folder couldn't be looked through: nothing can be handed over.
    public async Task<FolderWaiting> WaitingAsync(CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => WaitingAsync(cancellationToken));
        await EnterActionAsync(cancellationToken);
        try { return WaitingIn(fs.Scan()); }
        finally { LeaveAction(); }
    }

    // The address a folder is bound to, read from its state document without an engine (null: no
    // state yet, or bound to nobody, as after a hand-over). An unreadable document reads as null:
    // an engine started over it would start afresh the same way.
    public static string? OwnerOf(IEngineStateStore store)
    {
        byte[]? bytes;
        try { bytes = store.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("email", out var email) &&
                email.ValueKind == JsonValueKind.String && email.GetString() is { Length: > 0 } address ? address : null;
        }
        catch (JsonException) { return null; }
    }

    // The next student moves to a folder of their own because this one has work waiting here
    // (X-parked-writable): every file the team has that is writable here (the ones this computer
    // has checked out) becomes read-only, so SolidWorks opens it read-only for whoever sits down
    // next and nobody's save lands in another student's check out. The read-only rule as the
    // student in use sees it. What this engine knows of its check outs is unchanged: its next
    // pass (the owner back) finds the bit set on a file it still has checked out and makes it
    // writable again. Returns how many files were made read-only.
    public async Task<int> SealCheckOutsAsync(CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => SealCheckOutsAsync(cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            var onDisk = fs.Scan().Files.ToDictionary(f => f.Path.Value, StringComparer.OrdinalIgnoreCase);
            List<(VaultPath Path, LockOwnership Ownership)> batch = [];
            foreach (var st in state.Files.Values)
            {
                if (st.FileId is null || st.BaseHash is null || st.Purged || !onDisk.TryGetValue(st.Path, out var file) || file.ReadOnly) continue;
                // An archived project's files are left as they are (decision D8), here as in every pass.
                if (state.Projects.GetValueOrDefault(st.ProjectId) is { Archived: true }) continue;
                batch.Add((file.Path, LockOwnership.OtherPerson));
            }
            if (batch.Count > 0) fs.ApplyLockAttributes(batch);
            deps.Log?.Invoke($"folder: {batch.Count} checked-out files made read-only while their student is away");
            return batch.Count;
        }
        finally { LeaveAction(); }
    }

    // What the folder's account has waiting, as the window says it while the folder belongs to
    // another account (FolderOwnerView): looked at by the pass that finds the folder someone
    // else's, again at most once a minute, and by every take-over asked.
    private (string Owner, IReadOnlyList<string> Parts, long At)? ownerWaiting;
    private static readonly TimeSpan OwnerWaitingFor = TimeSpan.FromMinutes(1);

    private void LookAtOwnersWork()
    {
        if (state.Email is not { } owner) return;
        if (ownerWaiting is { } known && string.Equals(known.Owner, owner, StringComparison.OrdinalIgnoreCase) && deps.Clock.GetElapsedTime(known.At) < OwnerWaitingFor) return;
        try { ownerWaiting = (owner, WaitingIn(fs.Scan()).Parts, deps.Clock.GetTimestamp()); }
        catch (IOException) { return; }
        RequestPublish();
    }

    // The folder's owner for the view: who, and what waits (null: not looked at yet).
    private FolderOwnerView? FolderOwner(string connection)
    {
        if (connection != Connections.VaultOwnedByOther || state.Email is not { } owner) return null;
        var parts = ownerWaiting is { } known && string.Equals(known.Owner, owner, StringComparison.OrdinalIgnoreCase) ? known.Parts : null;
        return new FolderOwnerView(owner, DisplayName(owner), parts);
    }

    // What the folder's account still has waiting here, in plain words, from this scan and the
    // state document; nothing (Any false) when the folder can be handed over.
    private FolderWaiting WaitingIn(VaultScan scan)
    {
        int checkedOut = 0, unsent = 0, changed = 0, added = 0;
        var onDisk = scan.Files.ToDictionary(f => f.Path.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, st) in state.Files)
        {
            if (st.AppliedOwnership == LockOwnership.ThisDevice || st.CheckOut is not null || st.Request != CheckoutRequest.None || st.AutoCheckIn || st.TransientLock)
                checkedOut++;
            // A save is waiting until it is completed (a file's first capture stays on its record).
            else if (st.Entries.Concat(st.Drafts).Concat(new[] { st.CreateEntry, st.DeleteEntry, st.MarkerEntry }.OfType<string>()).Any(e => !state.Completed.Contains(e)) ||
                     st.Inflight is not null || st.LocalMoveTo is not null)
                unsent++;
            else if (onDisk.TryGetValue(key, out var file) ? file.Hash != st.BaseHash && !st.Purged : st.BaseHash is not null && st.FileId is not null && !st.Purged && !RemovedForTeam(st))
                changed++;
        }
        foreach (var file in scan.Files)
            if (!state.Files.ContainsKey(file.Path.Value) && ProjectOf(file.Path) is not null) added++;
        // A journaled save no file record holds yet (a capture a crash left out of the state document).
        var held = state.Files.Values.SelectMany(f => f.Entries.Concat(f.Drafts).Concat(new[] { f.CreateEntry, f.DeleteEntry, f.MarkerEntry }.OfType<string>()))
            .ToHashSet(StringComparer.Ordinal);
        unsent += journal.Read().Entries.Count(e => e.Kind == IntentKind.Upload && !state.Completed.Contains(e.Id) && !held.Contains(e.Id));
        var folderWork = state.Moves.Count + state.FolderOps.Count + state.MovingFolders.Count;
        List<string> waiting = [];
        if (checkedOut > 0) waiting.Add(Count(checkedOut, "file checked out", "files checked out"));
        if (unsent > 0) waiting.Add(Count(unsent, "save not sent", "saves not sent"));
        if (changed > 0) waiting.Add(Count(changed, "file changed and not saved to Armory", "files changed and not saved to Armory"));
        if (added > 0) waiting.Add(Count(added, "new file not in Armory yet", "new files not in Armory yet"));
        if (folderWork > 0) waiting.Add(Count(folderWork, "folder change not sent", "folder changes not sent"));
        return new FolderWaiting(state.Email, checkedOut, unsent, changed, added, folderWork, waiting);
    }

    // A file the team removed: gone from the disk here is what the team asked, not a change.
    private bool RemovedForTeam(FileState st) => st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && remote.File.Deleted;

    internal static string JoinWords(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => parts[0] + " and " + parts[1],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
