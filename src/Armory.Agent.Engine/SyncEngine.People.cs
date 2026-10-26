using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Several people in one folder (0.5.0, ledger 0437; docs/agent/PROFILES.md and ENGINE.md, "Phase A,
// identity"). The state document is the computer's: Email and DeviceId are the person Armory acts
// for now, and People keeps every person's device ids here. When the person in use changes, the
// engine for the next person hands the folder over by itself on its first pass, with no question:
//
//   - what the last person left waiting (a file checked out, a save not sent, a file changed or
//     new that Armory hasn't kept, a folder change not sent) moves out of the records into Held,
//     under their address, exactly as it was;
//   - a held file is never captured, planned, downloaded over, moved or sent while someone else is
//     in use, and it is kept read-only on disk, so SolidWorks opens it read-only for the next
//     person (a document SolidWorks already has open is never closed or moved: only its read-only
//     bit changes, and its next save is refused, which protects the owner's work);
//   - when its person is in use again, their held work goes back into the records as it was, and
//     their own engine finishes it under their own sign-in. Nobody's save is ever sent as
//     someone else.
//
// Everything that is not anyone's waiting work is the team's version, the same for everyone in the
// project, so it stays in the records and nothing is downloaded again.
public sealed partial class SyncEngine
{
    // The person in use's held work goes back into the records (at the start of every pass, before
    // anything is captured: cheap when there is none).
    private void ReturnHeld(string email)
    {
        if (!state.Held.Remove(email, out var held)) return;
        var back = 0;
        var overwritten = 0;
        foreach (var (key, st) in held.Files)
        {
            // Still being read, or not read yet: it waits held until a scan can say what is there.
            var expected = held.Hashes.GetValueOrDefault(key) is { Length: > 0 } h ? h : null;
            var now = local.GetValueOrDefault(key);
            if (now is { Unread: true })
            {
                KeepHeld(email, held, key, st);
                continue;
            }
            // Gone from the disk (its fold not finished, or someone removed it): put back from its
            // person's own save when there is one; otherwise it stays held, never replaced by the
            // team's version and never taken for a removal.
            if (now is null && expected is not null && SaveWithHash(email, key, st, expected) is null)
            {
                KeepHeld(email, held, key, st);
                deps.Log?.Invoke($"held: {key} is not on this disk; it stays held for {email}");
                continue;
            }
            // Bytes that are not the ones held: someone else wrote over this file while its person
            // was away. Never this person's save: set aside, and their own version put back.
            if (!string.Equals(now?.Hash, expected, StringComparison.Ordinal))
            {
                state.Aside[key] = new AsideFile
                {
                    Hash = now?.Hash, For = email, Since = deps.Clock.GetUtcNow(),
                    Restore = expected is null ? null : SaveWithHash(email, key, st, expected),
                };
                // Nothing of theirs to bring back (their file was not on disk): it stays gone.
                if (expected is null) state.Aside[key].Gone = true;
                deps.Log?.Invoke($"held: {key} changed on this disk while {email} was away ({expected ?? "none"} to {now?.Hash ?? "none"}); those bytes go aside");
                overwritten++;
            }
            if (state.Files.TryGetValue(key, out var occupant))
            {
                // Nothing should have made a record at a held path; one with work of its own keeps
                // its place and the held one waits (logged, never dropped).
                if (HasWork(occupant))
                {
                    KeepHeld(email, held, key, st);
                    deps.Log?.Invoke($"held: {key} stays held for {email}; the record there has work of its own");
                    continue;
                }
                state.Files.Remove(key);
            }
            st.Path = key;
            state.Files[key] = st;
            back++;
        }
        state.Moves.AddRange(held.Moves);
        state.FolderOps.AddRange(held.FolderOps);
        state.ForceCheckIns.AddRange(held.ForceCheckIns);
        MarkDirty();
        SaveNow();
        if (back + held.Moves.Count + held.FolderOps.Count + held.ForceCheckIns.Count == 0) return;
        deps.Log?.Invoke($"held: {back} files and {held.Moves.Count + held.FolderOps.Count + held.ForceCheckIns.Count} folder changes back with their person");
        activity.Log($"Your work that waited on this computer is back: {Count(back, "file", "files")}. Armory sends it now.");
        if (overwritten > 0)
            activity.Log($"Someone changed {(overwritten == 1 ? "one" : overwritten.ToString(System.Globalization.CultureInfo.InvariantCulture))} of your waiting files while you were away. Armory put your own version back and kept theirs in {RecoveryWords}.");
    }

    private const string RecoveryWords = "the .armory\\recovery folder";

    // A held file that can't go back yet stays held for its person (never captured meanwhile).
    private void KeepHeld(string email, HeldWork from, string key, FileState st)
    {
        if (!state.Held.TryGetValue(email, out var still)) state.Held[email] = still = new HeldWork { Since = from.Since };
        still.Files[key] = st;
        still.Hashes[key] = from.Hashes.GetValueOrDefault(key);
    }

    // A save of exactly these bytes that its person made (this record's own, or any save of theirs
    // at this path kept here, sent already or not): the copy to put back. Null when there is none.
    private string? SaveWithHash(string email, string key, FileState st, string hash)
    {
        var mine = st.Entries.Concat(st.Drafts).ToHashSet(StringComparer.Ordinal);
        return journal.Read().Entries.LastOrDefault(e => e.Hash == hash && e.SnapshotId is not null && e.Kind == IntentKind.Upload &&
            (mine.Contains(e.Id) || SameEmail(e.Author, email) && string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase)))?.Id;
    }

    // Whether this person's own sign-in, at its last read here, listed the project: false only
    // when it is known they are not in it.
    private bool NotIn(string email, Guid project)
        => state.People.TryGetValue(email, out var person) && person.Projects is { } listed && !listed.Contains(project);

    // Each pass, right after the scan: bytes set aside go to Armory's recovery folder once nothing
    // has them open, and the file's own version comes back (its person's last save, written back
    // here, or the team's version, downloaded by whoever is in that project). Until then the path
    // is nobody's: never captured, planned or sent, and read-only.
    private void SetAsideNow()
    {
        foreach (var (key, aside) in state.Aside.ToArray())
        {
            var file = local.GetValueOrDefault(key);
            if (aside.Restored is { } restored)
            {
                // Put back last pass: done once a scan sees exactly those bytes there.
                if (file is { Unread: true }) continue;
                if (file is not null && file.Hash == restored) { state.Aside.Remove(key); MarkDirty(); continue; }
                // Changed or gone again before a scan saw it back: set aside (or put back) again.
                aside.Hash = file?.Hash;
                aside.Restored = null;
                MarkDirty();
            }
            if (file is not null)
            {
                if (file.Unread || IsOpenNow(file.Path)) continue;
                var moved = fs.MoveToRecovery(file.Path, file.Hash);
                if (!moved.Succeeded)
                {
                    deps.Log?.Invoke($"aside: {key} stays for now ({moved.Problem})");
                    continue;
                }
                local.Remove(key);
                deps.Log?.Invoke($"aside: {key} ({file.Hash}) moved to the recovery folder");
            }
            MarkDirty();
            // Its person had removed it: it stays removed, as they left it.
            if (aside.Gone) { state.Aside.Remove(key); continue; }
            var record = state.Files.GetValueOrDefault(key);
            if (record is not null && aside.Restore is { } save && journal.Read().Entries.FirstOrDefault(e => e.Id == save) is { SnapshotId: { } snapshot } entry
                && VaultPath.TryCreate(key, out var path, out _, options.VaultRoot))
            {
                try
                {
                    using var bytes = deps.Snapshots.OpenRead(snapshot);
                    var put = fs.Replace(path, null, bytes, readOnly: record.AppliedOwnership != LockOwnership.ThisDevice);
                    if (put.Succeeded) { aside.Restored = entry.Hash; continue; }
                    deps.Log?.Invoke($"aside: {key} couldn't be put back yet ({put.Problem})");
                    continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    deps.Log?.Invoke($"aside: {key} couldn't be put back yet ({error.Message})");
                    continue;
                }
            }
            // No save of theirs to put back: the team's version comes back, downloaded by whoever
            // is in the project (its record forgets its base, as when a project folder is removed).
            if (record is { FileId: not null })
            {
                record.SetBase(null);
                record.AbsentScans = 0;
                record.DeleteEntry = null;
            }
            state.Aside.Remove(key);
        }
    }

    // The folder goes to the person signed in now (session), from the last person (state.Email):
    // their waiting work is held for them, from the records and this pass's scan, and the next
    // person's own held work (if they were here before) comes back. Saved before anything else.
    private void HandOver(ArmorySession session, VaultScan scan, int priorMoves, int priorFolderOps, int priorForce)
    {
        var last = state.Email!;
        state.RememberPerson(last);
        var held = Hold(last, scan, priorMoves, priorFolderOps, priorForce);
        // What was only the last person's news (their notices, kept copies, refusals) is not the
        // next person's: as the 0.3.2 take-over did.
        state.Remembered.Clear();
        state.Dismissed.Clear();
        state.Imports.Clear();
        foreach (var st in state.Files.Values)
        {
            st.Sides.Clear();
            st.BreakNotice = false;
            st.Refusal = null;
            st.RefusalKind = null;
        }
        state.Email = session.Email;
        state.DeviceId = session.DeviceId;
        state.FormerDevices.Clear();
        if (state.People.TryGetValue(session.Email, out var person))
            foreach (var device in person.Devices) if (device != session.DeviceId) state.FormerDevices.Add(device);
        state.RememberPerson(session.Email);
        MarkDirty();
        SaveNow();
        ownerWaiting = null;
        var first = DisplayName(last).Split(' ')[0];
        deps.Log?.Invoke($"folder: handed from {last} to {session.Email}; {held} of {last}'s files held for them");
        activity.Log(held == 0 ? $"This Armory folder is yours now. It was {first}'s."
            : $"This Armory folder is yours now. {first}'s work waits here for {first}: {Count(held, "file", "files")}, read-only for you.");
        ReturnHeld(session.Email);
    }

    // Everything of email's that waits in the folder moves into Held[email]: every record with work
    // (WaitingIn's own test, per record), every file in a project that Armory doesn't have yet, and
    // the folder changes that were theirs before this pass. Returns how many files.
    private int Hold(string email, VaultScan scan, int priorMoves, int priorFolderOps, int priorForce)
    {
        var onDisk = scan.Files.ToDictionary(f => f.Path.Value, StringComparer.OrdinalIgnoreCase);
        if (!state.Held.TryGetValue(email, out var held)) held = new HeldWork { Since = deps.Clock.GetUtcNow() };
        var count = 0;
        var aside = 0;
        foreach (var (key, st) in state.Files.ToArray())
        {
            if (st.Purged || state.Aside.ContainsKey(key)) continue;
            var file = onDisk.GetValueOrDefault(key);
            if (!HasWork(st) && !ChangedOnDisk(st, file)) continue;
            // A change by someone not in this project: nobody's to send, never held for them (they
            // could never send it) and never the next person's. It goes aside, and the team's
            // version comes back.
            if (!HasWork(st) && NotIn(email, st.ProjectId))
            {
                state.Aside[key] = new AsideFile { Hash = file?.Hash, Of = email, Since = deps.Clock.GetUtcNow() };
                aside++;
                continue;
            }
            state.Files.Remove(key);
            held.Files[key] = st;
            held.Hashes[key] = file?.Hash;
            count++;
        }
        foreach (var file in scan.Files)
        {
            var key = file.Path.Value;
            // (Any project the folder belongs to: the flags of who can work in it are the last
            // person's read of the server, not the next one's.)
            if (state.Files.ContainsKey(key) || held.Files.ContainsKey(key) || HeldForOther(key) || (ProjectOf(file.Path) ?? KnownProjectOf(file.Path)) is not { } project) continue;
            if (NotIn(email, project.Id))
            {
                state.Aside[key] = new AsideFile { Hash = file.Hash, Of = email, Since = deps.Clock.GetUtcNow() };
                aside++;
                continue;
            }
            // A new file of theirs that Armory doesn't have yet: theirs to add when they are back.
            held.Files[key] = new FileState { Path = key, ProjectId = project.Id };
            held.Hashes[key] = file.Hash;
            count++;
        }
        // Their folder changes not sent yet (the ones from before this pass; anything this pass's
        // look at the disk found is the person in use's). Folders being put back are everyone's.
        held.Moves.AddRange(state.Moves.Take(priorMoves));
        state.Moves.RemoveRange(0, Math.Min(priorMoves, state.Moves.Count));
        var ops = state.FolderOps.Take(priorFolderOps).ToList();
        foreach (var op in ops.Where(o => o.Kind != PutBackOp))
        {
            state.FolderOps.Remove(op);
            held.FolderOps.Add(op);
        }
        held.ForceCheckIns.AddRange(state.ForceCheckIns.Take(priorForce));
        state.ForceCheckIns.RemoveRange(0, Math.Min(priorForce, state.ForceCheckIns.Count));
        if (!held.Empty) state.Held[email] = held;
        if (aside > 0)
        {
            var first = DisplayName(email).Split(' ')[0];
            deps.Log?.Invoke($"aside: {aside} files {email} changed or added in projects they are not in");
            activity.Log($"{first} changed {Count(aside, "file", "files")} in a project {first} isn't in. Nothing of it was sent: Armory keeps it in {RecoveryWords} and puts the team's version back.");
        }
        MarkDirty();
        return count;
    }

    // A record with work of its person's in it: a check out, a check in or undo asked, a save not
    // sent (its first capture included), a write in flight, an Explorer rename being sent.
    private bool HasWork(FileState st)
        => st.AppliedOwnership == LockOwnership.ThisDevice || st.CheckOut is not null || st.Request != CheckoutRequest.None || st.AutoCheckIn || st.TransientLock ||
           st.Entries.Concat(st.Drafts).Concat(new[] { st.CreateEntry, st.DeleteEntry, st.MarkerEntry }.OfType<string>()).Any(e => !state.Completed.Contains(e)) ||
           st.Inflight is not null || st.LocalMoveTo is not null;

    // Bytes on disk that are not the version this record last matched (or a record's file gone
    // from the disk): a change Armory hasn't kept yet.
    private bool ChangedOnDisk(FileState st, LocalFile? file)
        => file is not null ? file.Hash != st.BaseHash && file.Hash != st.LastCaptured : st.BaseHash is not null && st.FileId is not null && !RemovedForTeam(st);

    // Held for someone other than the person in use: never captured, planned, downloaded over or sent.
    // (Anything still in Held is outside the records, the person in use's own included until it
    // goes back to them, and bytes set aside are nobody's.)
    private bool HeldForOther(string key)
    {
        if (state.Aside.ContainsKey(key)) return true;
        foreach (var held in state.Held.Values)
            if (held.Files.ContainsKey(key)) return true;
        return false;
    }

    private bool HeldIdForOther(Guid id)
    {
        foreach (var held in state.Held.Values)
            foreach (var st in held.Files.Values) if (st.FileId == id) return true;
        return false;
    }

    // Who a held path is held for, and its record (for the window).
    private (string Email, FileState Record)? HeldRecord(string key)
    {
        foreach (var (email, held) in state.Held)
            if (!SameEmail(email, state.Email) && held.Files.TryGetValue(key, out var st)) return (email, st);
        return null;
    }

    // A journaled save belongs to the person who made it (its author is the address it was kept
    // under); with nobody in use yet, nothing is known to be anyone else's.
    private static bool SavedBy(JournalEntry entry, string? email)
        => email is null || string.IsNullOrEmpty(entry.Author) || SameEmail(entry.Author, email);

    // A journal entry a held record of someone else's carries.
    private bool HeldEntry(string id)
    {
        foreach (var held in state.Held.Values)
                foreach (var st in held.Files.Values)
                    if (st.Entries.Contains(id) || st.Drafts.Contains(id) || st.CreateEntry == id || st.DeleteEntry == id) return true;
        return false;
    }

    // The words for a window action on a file held for someone else.
    private ActionResult HeldRefusal(string key)
    {
        var who = HeldRecord(key) is { } held ? DisplayName(held.Email) : "Someone else";
        var first = who.Split(' ')[0];
        var name = key[(key.LastIndexOf('/') + 1)..];
        return new(false, $"{who}'s work in {name} is waiting on this computer for {first}. It opens read-only for you until {first} picks their name here and finishes it.");
    }

    private static bool SameEmail(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // A held file renamed or moved in File Explorer by someone else: an untracked file with the very
    // bytes a held file had when it was held, whose held path is empty now, is that file. Its held
    // record follows it, so it is never captured as the new person's new file.
    private bool FollowHeldRename(LocalFile file)
    {
        foreach (var (email, held) in state.Held)
        {
            if (SameEmail(email, state.Email)) continue;
            foreach (var (key, hash) in held.Hashes)
            {
                if (hash is null || hash != file.Hash || local.ContainsKey(key) || !held.Files.TryGetValue(key, out var st)) continue;
                var to = file.Path.Value;
                held.Files.Remove(key);
                held.Hashes.Remove(key);
                st.Path = to;
                held.Files[to] = st;
                held.Hashes[to] = hash;
                MarkDirty();
                deps.Log?.Invoke($"held: {key} was moved to {to} on this disk; it stays held for {email}");
                return true;
            }
        }
        return false;
    }

    // Held files follow a folder renamed or moved here, as the records do (RekeyFolder).
    private void RekeyHeld(string from, string to)
    {
        foreach (var held in state.Held.Values)
        {
            foreach (var key in held.Files.Keys.Where(k => Inside(k, from)).ToArray())
            {
                var target = to + key[from.Length..];
                held.Files.Remove(key, out var st);
                held.Hashes.Remove(key, out var hash);
                st!.Path = target;
                held.Files[target] = st;
                held.Hashes[target] = hash;
            }
        }
    }

    // Held files on disk are read-only for whoever is in use (the read-only rule as they see it).
    private void SealHeld(List<(VaultPath Path, LockOwnership Ownership)> batch)
    {
        foreach (var key in state.Held.Values.SelectMany(h => h.Files.Keys).Concat(state.Aside.Keys))
            if (local.TryGetValue(key, out var file) && !file.ReadOnly && !file.Unread && !Fenced(key)) batch.Add((file.Path, LockOwnership.OtherPerson));
    }

    // What the window says about work held here for other people: each person's name and how many
    // of their files wait (0.5.0, Home's "Waiting for others" line).
    private IReadOnlyList<HeldView> HeldViews()
    {
        List<HeldView> views = [];
        foreach (var (email, held) in state.Held.OrderBy(h => h.Value.Since))
        {
            if (SameEmail(email, state.Email) || held.Empty) continue;
            var files = held.Files.Count;
            var changes = held.Moves.Count + held.FolderOps.Count + held.ForceCheckIns.Count;
            List<string> parts = [];
            if (files > 0) parts.Add(Count(files, "file", "files"));
            if (changes > 0) parts.Add(Count(changes, "folder change", "folder changes"));
            views.Add(new HeldView(email, DisplayName(email), files, JoinWords(parts), held.Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return views;
    }

    // Held work for each person, read from a folder's state document without an engine (the host's
    // tiles, and the fold's report): address and how many files.
    public static IReadOnlyDictionary<string, int> HeldOf(IEngineStateStore store)
    {
        try { return EngineState.Load(store).Held.Where(h => !h.Value.Empty).ToDictionary(h => h.Key, h => h.Value.Files.Count, StringComparer.OrdinalIgnoreCase); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new Dictionary<string, int>(); }
    }
}
