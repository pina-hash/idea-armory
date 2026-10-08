using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine;

// One Armory folder, taken in turns (0.3.2): a lab computer where several students sign in to
// Armory in turn keeps one folder, never a folder per student. The folder is handed to the
// account signed in now only when the account it belongs to has nothing waiting in it: no file
// checked out here, no save not sent, no change on disk Armory hasn't kept, no new file not in
// Armory yet, no move or folder change not sent. Anything waiting stays that person's (it is
// their work), so the folder stays theirs and the window says what is waiting and who can
// finish it. Two accounts in one folder at once is never allowed: the second would see the
// first one's work as its own.
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
            var who = DisplayName(owner);
            var first = who.Split(' ')[0];
            if (waiting.Count > 0)
                return new(false, $"{who} still has {Join(waiting)} in this folder. {first} can sign in to Armory here to finish them, or you can use a folder of your own.");
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
            deps.Log?.Invoke("folder: handed over by its account to the one signed in now");
            activity.Log($"This Armory folder is yours now. It was {first}'s.");
            return new(true, $"This Armory folder is yours now. {who}'s files here were all saved to Armory, so nothing of theirs changes.");
        }
        finally { LeaveAction(); }
    }

    // What the folder's account still has waiting here, in plain words, from this scan and the
    // state document; empty when the folder can be handed over.
    private List<string> WaitingIn(VaultScan scan)
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
        return waiting;
    }

    // A file the team removed: gone from the disk here is what the team asked, not a change.
    private bool RemovedForTeam(FileState st) => st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && remote.File.Deleted;

    private static string Join(List<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => parts[0] + " and " + parts[1],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
