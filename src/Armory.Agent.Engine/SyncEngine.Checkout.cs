using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// v2 check out (docs/agent/ENGINE.md, v2-design.md 4.2, decisions D1 to D4 and D18): what the
// student asks for in the window. Each request is durable in its FileState before any server
// call and is carried out inside a pass, under the pass gate, with operation ids derived from
// it; a crash finishes it on the next pass. Every action answers with one plain sentence. Every
// action runs on the engine thread: called from any other thread, it marshals there first.
public sealed partial class SyncEngine
{
    private enum CheckOutOutcome { Unknown, Done, AlreadyMine, Held, Waiting, ChangedHere, CloseFirst, Removed, NotShared, CantRead, Refused }
    // armory_break_lock's refusal for a caller who may not (P0001, unchanged in 0233).
    internal const string TakeBackRefused = "only a mentor or cad_lead may break a lock";
    private enum ReleaseOutcome { Unknown, Released, TakenBack, Refused }
    private readonly Dictionary<FileState, CheckOutOutcome> checkOutResults = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FileState, ReleaseOutcome> releaseResults = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> dismissedPrompts = new(StringComparer.OrdinalIgnoreCase);
    internal const string PromptPrefix = "prompt:";

    // Check out (and "Check out and open"). A folder means every file the server has under it.
    // The lock is taken only over a copy that is the live shared version
    // (CheckoutRules.NextCheckOutStep): a copy that is behind, or holds bytes saved without a
    // check out, gets one pass with the lock free (it downloads, or keeps those bytes as a kept
    // copy and puts the shared version back), and then the rule is asked again.
    public async Task<ActionResult> CheckOutAsync(IReadOnlyList<string> paths, bool open = false, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => CheckOutAsync(paths, open, cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            var targets = new List<(FileState State, VaultPath Path)>();
            foreach (var (file, project, path) in remoteById.Values.OrderBy(r => r.Path.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (file.Deleted || file.Current is null || !Under(path.Value, paths)) continue;
                var st = state.Files.Values.FirstOrDefault(f => f.FileId == file.Id) ?? FileFor(project, path.Value);
                st.FileId ??= file.Id;
                // Checking out again a file this computer is checking in or undoing, added while
                // open, or holds only for a move keeps it checked out. That is decided inside the
                // pass once the server was read (KeepCheckedOut), so a check out refused offline
                // never cancels a check in or an undo still waiting.
                st.CheckOut ??= NextId("checkout");
                targets.Add((st, path));
            }
            if (targets.Count == 0)
            {
                if (online != true) return Offline("Files can be checked out once this computer is back online.");
                return new(false, paths.Count == 1 && !IsFolder(paths[0]) ? $"{NameOf(paths[0])} isn't in Armory yet." : "There are no files there to check out.");
            }
            await FlushAsync(); // the requests are durable before any server call
            checkOutResults.Clear();
            checkOutRefusals.Clear();
            // Only these files move in this pass; the loop moves everything else.
            var scope = PassScope.Of(targets.Select(t => t.State));
            await PassLockedAsync(cancellationToken, scope);
            // Ask again after one pass with the lock free (a download, or a kept copy put back).
            if (online == true && targets.Any(t => checkOutResults.GetValueOrDefault(t.State) == CheckOutOutcome.Waiting))
                await PassLockedAsync(cancellationToken, scope);
            var wasOnline = online == true;
            foreach (var (st, _) in targets)
            {
                if (st.CheckOut is null) continue;
                // A check out that could not finish is not left to happen later by surprise.
                st.CheckOut = null;
                if (!checkOutResults.ContainsKey(st)) checkOutResults[st] = CheckOutOutcome.Waiting;
            }
            await SettleAsync();
            PublishLocked();
            // A lock taken before the connection dropped is a check out all the same.
            if (!wasOnline && !targets.Any(t => checkOutResults.GetValueOrDefault(t.State) is CheckOutOutcome.Done or CheckOutOutcome.AlreadyMine))
                return Offline("Files can be checked out once this computer is back online.");
            return CheckOutAnswer(targets, open, wasOnline);
        }
        finally { LeaveAction(); }
    }

    private ActionResult CheckOutAnswer(List<(FileState State, VaultPath Path)> targets, bool open, bool wasOnline)
    {
        var outcomes = targets.Select(t => (t.State, t.Path, Outcome: checkOutResults.GetValueOrDefault(t.State))).ToList();
        var mine = outcomes.Where(o => o.Outcome is CheckOutOutcome.Done or CheckOutOutcome.AlreadyMine).ToList();
        string message;
        if (targets.Count == 1)
        {
            var (st, path, outcome) = outcomes[0];
            message = outcome switch
            {
                CheckOutOutcome.Done => $"Checked out {path.Name}.",
                CheckOutOutcome.AlreadyMine => $"{path.Name} is already checked out by you.",
                CheckOutOutcome.Held => HeldWords(st, path),
                CheckOutOutcome.ChangedHere => $"{path.Name} was changed without a check out. Close it in SolidWorks first, then check it out.",
                CheckOutOutcome.CloseFirst => $"A newer {path.Name} is waiting. Close it in SolidWorks first, then check it out.",
                CheckOutOutcome.Removed => $"{path.Name} was removed on this computer, so it can't be checked out.",
                CheckOutOutcome.NotShared => $"{path.Name} isn't in Armory.",
                CheckOutOutcome.CantRead => $"Armory couldn't read {path.Name}. Close any program using it, then try again.",
                CheckOutOutcome.Refused => $"Armory couldn't check out {path.Name}. {checkOutRefusals.GetValueOrDefault(st) ?? "The server refused it."}",
                _ => $"Armory couldn't bring {path.Name} up to date to check it out. Try again in a moment.",
            };
        }
        else
        {
            message = mine.Count == targets.Count ? $"Checked out {Count(targets.Count, "file", "files")}." : $"Checked out {mine.Count:N0} of {Count(targets.Count, "file", "files")}.";
            message += HeldBy(outcomes.Where(o => o.Outcome == CheckOutOutcome.Held).Select(o => o.State).ToList());
            // Each file a batch refused says why (v0.3): what landed is never reported as failed.
            message += RefusedWords(targets);
            var rest = outcomes.Count(o => o.Outcome is not (CheckOutOutcome.Done or CheckOutOutcome.AlreadyMine or CheckOutOutcome.Held or CheckOutOutcome.Refused));
            if (rest > 0)
                message += wasOnline ? $" {Count(rest, "file needs", "files need")} you first: open {(rest == 1 ? "it" : "them")} here to see why."
                    : $" The {(rest == 1 ? "other one" : "others")} can be checked out once this computer is back online.";
        }
        if (open && mine.Count == 1)
        {
            var path = mine[0].Path;
            if (IsOpenNow(path)) message += $" Close {path.Name} in SolidWorks first, then open it again.";
            else
            {
                var launched = fs.Launch(path);
                if (!launched.Succeeded) message += " " + launched.Problem;
            }
        }
        else if (!open)
        {
            // SolidWorks opened these read-only before they were checked out: it saves them only
            // once they are opened again.
            var reopen = outcomes.Where(o => o.Outcome == CheckOutOutcome.Done && IsOpenNow(o.Path)).Select(o => o.Path).ToList();
            if (reopen.Count == 1 && targets.Count == 1) message += " Close it in SolidWorks and open it again to save changes.";
            else if (reopen.Count == 1) message += $" Close {reopen[0].Name} in SolidWorks and open it again to save changes.";
            else if (reopen.Count > 1) message += $" Close {Count(reopen.Count, "file", "files")} in SolidWorks and open them again to save changes.";
        }
        return new(mine.Count > 0, message);
    }

    // Who has the files a check out could not take, in one sentence per kind of holder:
    // " Maria Lopez and Sam Lee have 3 of them checked out." " 1 is checked out on your other
    // computer, LAB-PC-07."
    private string HeldBy(List<FileState> held)
    {
        if (held.Count == 0) return "";
        var people = new List<string>();
        var otherComputers = new List<string>();
        var byPeople = 0;
        foreach (var st in held)
        {
            if (st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && remote.File.Lock is { IsLive: true } lck && OwnershipOf(lck) == LockOwnership.MyOtherDevice)
            {
                otherComputers.Add(lck.HolderDeviceName ?? "another computer");
                continue;
            }
            byPeople++;
            people.Add(HolderName(st));
        }
        var text = "";
        if (byPeople > 0)
        {
            var names = people.GroupBy(n => n, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
            var who = names.Count switch
            {
                1 => names[0],
                2 => $"{names[0]} and {names[1]}",
                3 => $"{names[0]}, {names[1]} and {names[2]}",
                _ => $"{names[0]}, {names[1]} and {names.Count - 2:N0} others",
            };
            text += $" {who} {(names.Count == 1 ? "has" : "have")} {byPeople:N0} of them checked out.";
        }
        if (otherComputers.Count > 0)
        {
            var devices = otherComputers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var where = devices.Count == 1 ? $"your other computer, {devices[0]}" : "your other computers, " + string.Join(" and ", devices);
            text += $" {otherComputers.Count:N0} {(otherComputers.Count == 1 ? "is" : "are")} checked out on {where}.";
        }
        return text;
    }

    // Check in: commit what is on disk if it changed, make it read-only, then let the lock go.
    public async Task<ActionResult> CheckInAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => CheckInAsync(paths, cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            var targets = MyCheckOuts(paths);
            if (targets.Count == 0) return new(false, "Nothing there is checked out by you.");
            foreach (var (st, _) in targets) { st.Request = CheckoutRequest.CheckIn; st.CheckOut = null; }
            await FlushAsync(); // the requests are durable before any server call
            releaseResults.Clear();
            await PassLockedAsync(cancellationToken, PassScope.Of(targets.Select(t => t.State)));
            return ReleaseAnswer(targets, undo: false, [], kept: false);
        }
        finally { LeaveAction(); }
    }

    // Undo check out: never while the file is open. Bytes not checked in are kept as a kept
    // copy (SideVersionReason.UndoCheckOut), the shared version is put back, the file is made
    // read-only, then the lock is let go.
    public async Task<ActionResult> UndoCheckOutAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => UndoCheckOutAsync(paths, cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            var targets = MyCheckOuts(paths);
            if (targets.Count == 0) return new(false, "Nothing there is checked out by you.");
            var open = targets.Where(t => IsOpenNow(t.Path)).ToList();
            if (open.Count == targets.Count)
                return new(false, open.Count == 1 ? $"Close {open[0].Path.Name} in SolidWorks first." : "Close these files in SolidWorks first.");
            var closed = targets.Except(open).ToList();
            var keptBefore = closed.SelectMany(t => t.State.Sides).Select(s => s.VersionId).ToHashSet();
            foreach (var (st, _) in closed) { st.Request = CheckoutRequest.Undo; st.CheckOut = null; }
            await FlushAsync(); // the requests are durable before any server call
            releaseResults.Clear();
            await PassLockedAsync(cancellationToken, PassScope.Of(closed.Select(t => t.State)));
            // Bytes not checked in were kept as a kept copy; the answer says so.
            var kept = closed.SelectMany(t => t.State.Sides).Any(s => s.Reason == UndoReason && !keptBefore.Contains(s.VersionId));
            return ReleaseAnswer(closed, undo: true, open.Select(o => o.Path).ToList(), kept);
        }
        finally { LeaveAction(); }
    }

    private ActionResult ReleaseAnswer(List<(FileState State, VaultPath Path)> targets, bool undo, List<VaultPath> open, bool kept)
    {
        var done = targets.Where(t => releaseResults.GetValueOrDefault(t.State) == ReleaseOutcome.Released).ToList();
        var pending = targets.Where(t => t.State.Request != CheckoutRequest.None).ToList();
        string message;
        if (targets.Count == 1 && open.Count == 0)
        {
            var (st, path) = targets[0];
            var outcome = releaseResults.GetValueOrDefault(st);
            message = outcome switch
            {
                ReleaseOutcome.Released when undo => kept ? $"Undid the check out of {path.Name}. Your changes are kept as your own copy." : $"Undid the check out of {path.Name}.",
                ReleaseOutcome.Released => $"Checked in {path.Name}.",
                ReleaseOutcome.TakenBack => $"{path.Name} was force checked in by a mentor before {(undo ? "the check out was undone" : "it was checked in")}. Your changes are kept in its history.",
                ReleaseOutcome.Refused => $"{path.Name} can't be checked in. {st.Refusal} It stays checked out by you.",
                _ when online != true => $"You're offline. {path.Name} is {(undo ? "put back" : "checked in")} as soon as this computer is back online.",
                _ => $"Armory couldn't finish {(undo ? "undoing" : "checking in")} {path.Name} yet. It tries again by itself.",
            };
            return new(st.Request != CheckoutRequest.None || outcome == ReleaseOutcome.Released, message);
        }
        message = undo
            ? (done.Count == targets.Count && open.Count == 0 ? $"Undid {Count(done.Count, "check out", "check outs")}." : $"Undid {done.Count:N0} of {Count(targets.Count + open.Count, "check out", "check outs")}.")
            : (done.Count == targets.Count ? $"Checked in {Count(done.Count, "file", "files")}." : $"Checked in {done.Count:N0} of {Count(targets.Count, "file", "files")}.");
        if (undo && kept) message += " Your changes are kept as your own copies.";
        if (pending.Count > 0)
            message += online != true ? $" The other {Count(pending.Count, "file finishes", "files finish")} when this computer is back online." : $" Armory finishes the other {Count(pending.Count, "file", "files")} by itself.";
        var refusedCount = targets.Count(t => releaseResults.GetValueOrDefault(t.State) == ReleaseOutcome.Refused);
        if (refusedCount > 0) message += $" {Count(refusedCount, "file", "files")} can't be uploaded and {(refusedCount == 1 ? "stays" : "stay")} checked out by you.";
        if (open.Count > 0) message += $" Close {(open.Count == 1 ? open[0].Name : Count(open.Count, "file", "files"))} in SolidWorks first to undo {(open.Count == 1 ? "it" : "them")}.";
        return new(done.Count > 0 || pending.Count > 0, message);
    }

    // Force check in (armory_break_lock; "take back" until v0.2.1), when the server says can_take_back
    // (v0.3: a mentor, a CAD lead or a site admin).
    // The holder's computer keeps anything not checked in as their own copy (a kept copy), as
    // for any check out taken back.
    public async Task<ActionResult> TakeBackAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => TakeBackAsync(fileId, cancellationToken));
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (!remoteById.TryGetValue(fileId, out var remote) || remote.File.Deleted) return new(false, "That file isn't in your projects.");
            var name = remote.File.Name;
            if (!remote.Project.CanTakeBack) return new(false, "Only a mentor or CAD lead can force a check in.");
            if (remote.File.Lock is not { IsLive: true } held) return new(false, $"{name} isn't checked out.");
            if (OwnershipOf(held) == LockOwnership.ThisDevice) return new(false, $"You have {name} checked out. Check it in or undo the check out instead.");
            // The operation id belongs to this one check out and this computer, so asking twice
            // here takes it back once, and a second mentor asking from an older view never reuses
            // another caller's id. It is asked once, never resumed after a crash: the mentor asks
            // again, and the same id answers from the server's receipt.
            var operation = OperationIds.Derive("take back", fileId.ToString(), held.HolderDeviceId.ToString(), held.AcquiredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
                state.DeviceId.ToString()!);
            bool broke;
            projectsWritten.Add(remote.Project.Id);
            try { broke = await deps.Api.BreakLockAsync(fileId, state.DeviceId!.Value, operation, cancellationToken); }
            catch (ArmoryOfflineException) { online = false; return Offline("A check in can be forced once this computer is back online."); }
            catch (ArmoryRpcException error) when (error.IsNotMember)
            {
                NoteNotMember(remote.Project.Id, error);
                return new(false, $"You may no longer be in {remote.Project.Name}, so {name} can't be force checked in.");
            }
            // P0001 "only a mentor or cad_lead may break a lock" (unchanged in 0233), or a 42501.
            catch (ArmoryRpcException error) when (error.IsForbidden || (error.SqlState == "P0001" && error.Message == TakeBackRefused))
            { return new(false, "Only a mentor or CAD lead can force a check in."); }
            catch (ArmoryRpcException)
            {
                // The server would not take it back as asked (someone else already did, or the
                // check out changed): read it again, and say what is true now.
                await PassLockedAsync(cancellationToken, PassScope.File(fileId));
                return new(false, $"{name} isn't checked out any more.");
            }
            if (broke) KnowLock(fileId, null);
            await PassLockedAsync(cancellationToken, PassScope.File(fileId));
            var from = OwnershipOf(held) == LockOwnership.MyOtherDevice ? "your other computer, " + (held.HolderDeviceName ?? "another computer") : DisplayName(held.HolderEmail);
            var kept = OwnershipOf(held) == LockOwnership.MyOtherDevice ? "Anything not checked in there is kept as your own copy." : "Anything they hadn't checked in is kept as their own copy.";
            return broke ? new(true, $"Force checked in {name} from {from}. {kept}") : new(false, $"{name} isn't checked out any more.");
        }
        finally { LeaveAction(); }
    }

    // How many armory_break_lock calls a Force check in of many files has in flight at once: each
    // is one small call, so several hundred files take seconds, not one pass per file.
    internal const int TakeBackConcurrency = 16;

    // Force check in of many files at once (Force check in all, the selection bar): one action, the
    // locks broken TakeBackConcurrency at a time with each file's own operation id (as for one
    // file), then ONE pass for all of them. Until 0.3.1 the window sent one action per file, and
    // each ran a whole pass of its own: a few hundred files took the better part of an hour.
    public async Task<ActionResult> TakeBackAsync(IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 1) return await TakeBackAsync(ids[0], cancellationToken);
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => TakeBackAsync(ids, cancellationToken));
        if (ids.Count == 0) return new(false, "There are no files there to force check in.");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            List<(Guid Id, RemoteFile File, ProjectState Project, RemoteLock Held)> targets = [];
            int notThere = 0, notOut = 0, mine = 0, notAllowed = 0;
            foreach (var id in ids)
            {
                if (!remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) { notThere++; continue; }
                if (!remote.Project.CanTakeBack) { notAllowed++; continue; }
                if (remote.File.Lock is not { IsLive: true } held) { notOut++; continue; }
                if (OwnershipOf(held) == LockOwnership.ThisDevice) { mine++; continue; }
                targets.Add((id, remote.File, remote.Project, held));
            }
            if (targets.Count == 0)
                return new(false, notAllowed > 0 && notOut + mine + notThere == 0 ? "Only a mentor or CAD lead can force a check in." : "None of those files is checked out by someone else now.");
            List<Guid> broken = [], reread = [];
            int gone = 0, refusedRole = 0, failed = 0;
            var offline = false;
            foreach (var chunk in targets.Chunk(TakeBackConcurrency))
            {
                var calls = chunk.Select(async t =>
                {
                    // The same id as a single Force check in of this check out from this computer.
                    var operation = OperationIds.Derive("take back", t.Id.ToString(), t.Held.HolderDeviceId.ToString(), t.Held.AcquiredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
                        state.DeviceId.ToString()!);
                    projectsWritten.Add(t.Project.Id);
                    try { return (t.Id, Broke: await deps.Api.BreakLockAsync(t.Id, state.DeviceId!.Value, operation, cancellationToken), Error: (Exception?)null); }
                    catch (ArmoryOfflineException error) { return (t.Id, Broke: false, Error: (Exception?)error); }
                    catch (ArmoryRpcException error) { return (t.Id, Broke: false, Error: (Exception?)error); }
                }).ToList();
                foreach (var (id, broke, error) in await Task.WhenAll(calls))
                {
                    switch (error)
                    {
                        case null when broke: broken.Add(id); break;
                        case null: gone++; break; // someone else took it back first
                        case ArmoryOfflineException: offline = true; break;
                        case ArmoryRpcException rpc when rpc.IsForbidden || (rpc.SqlState == "P0001" && rpc.Message == TakeBackRefused): refusedRole++; break;
                        case ArmoryRpcException rpc when rpc.IsNotMember: NoteNotMember(targets.First(t => t.Id == id).Project.Id, rpc); failed++; break;
                        default:
                            // As for one file: the server would not take it back as asked (someone
                            // else already did, or the check out changed), so read it again.
                            gone++;
                            reread.Add(id);
                            deps.Log?.Invoke($"take back: {id}: {error.Message}");
                            break;
                    }
                }
                if (offline) { online = false; break; }
            }
            foreach (var id in broken) KnowLock(id, null);
            // One pass for every file, never one per file.
            if (broken.Count + reread.Count > 0) await PassLockedAsync(cancellationToken, PassScope.FilesOf(broken.Concat(reread)));
            var message = broken.Count == targets.Count
                ? $"Force checked in {Count(broken.Count, "file", "files")}."
                : $"Force checked in {broken.Count:N0} of {Count(targets.Count, "file", "files")}.";
            if (broken.Count > 0) message += " Anything that wasn't checked in is kept as its holder's own copy.";
            if (gone + notOut > 0) message += $" {Count(gone + notOut, "file wasn't", "files weren't")} checked out any more.";
            if (refusedRole + notAllowed > 0) message += $" {Count(refusedRole + notAllowed, "file is", "files are")} in a project where only a mentor or CAD lead can force a check in.";
            if (mine > 0) message += $" {Count(mine, "file is", "files are")} checked out by you: check {(mine == 1 ? "it" : "them")} in instead.";
            if (offline) message += " You went offline: try again once this computer is back online to finish the rest.";
            else if (failed > 0) message += $" {Count(failed, "file is", "files are")} in a project you may no longer be in.";
            return new(broken.Count > 0, message);
        }
        finally { LeaveAction(); }
    }

    // Open: the file's own program (SolidWorks for a part). Programs and scripts are refused
    // (decision D14). Needs no pass, so it never waits behind one, and the open itself runs off
    // the engine thread (the platform answers within about a second). A file the team has that
    // is not on this computer yet is downloaded first, ahead of everything else (a pass scoped
    // to it, as for an action), and opens once it is here.
    public Task<ActionResult> LaunchAsync(string path, CancellationToken cancellationToken = default) => engineThread.InvokeAsync(async () =>
    {
        if (!VaultPath.TryCreate(path, out var file, out _, options.VaultRoot)) return new ActionResult(false, "That isn't a file in your Armory folder.");
        if (!Exists(file) && remoteByPath.TryGetValue(file.Value, out var remote) && !remote.File.Deleted && remote.File.Current is not null)
        {
            if (Unready() is { } why) return why;
            if (online != true) return Offline($"{file.Name} can be downloaded and opened once this computer is back online.");
            openWhenHere[remote.File.Id] = deps.Clock.GetUtcNow();
            _ = DownloadToOpenAsync(file);
            return new ActionResult(true, $"Downloading {file.Name}, it opens when it is here.");
        }
        var outcome = await Task.Run(() => fs.Launch(file), cancellationToken);
        return outcome.Succeeded ? new ActionResult(true, $"Opening {file.Name}.") : new ActionResult(false, outcome.Problem ?? $"Armory couldn't open {file.Name}.");
    });

    // Files the student opened before they were here: opened as soon as a pass brings them (for
    // ten minutes; after that, a later download opens nothing by surprise).
    private readonly Dictionary<Guid, DateTimeOffset> openWhenHere = [];
    private static readonly TimeSpan OpenWhenHereFor = TimeSpan.FromMinutes(10);

    private async Task DownloadToOpenAsync(VaultPath file)
    {
        try
        {
            await EnterActionAsync(stopping.Token);
            try { if (Unready() is null) await PassLockedAsync(stopping.Token, PassScope.Under(file.Value)); }
            finally { LeaveAction(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke($"open {file}: {error.GetType().Name}: {error.Message}"); }
    }

    // After every pass: the files waiting to open that are here now, closed, open in their program.
    private void OpenArrived()
    {
        if (openWhenHere.Count == 0) return;
        foreach (var (id, asked) in openWhenHere.ToArray())
        {
            if (deps.Clock.GetUtcNow() - asked > OpenWhenHereFor || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) { openWhenHere.Remove(id); continue; }
            if (!TryLocal(remote.Path.Value, out var here)) continue;
            openWhenHere.Remove(id);
            if (IsOpenNow(here.Path)) continue;
            var path = here.Path;
            _ = Task.Run(() =>
            {
                var outcome = fs.Launch(path);
                if (!outcome.Succeeded) deps.Log?.Invoke($"open {path}: {outcome.Problem}");
            });
        }
    }

    // Rename one file in its folder (addendum 7). A file Armory does not have is renamed on this
    // disk; a file in Armory is renamed for everyone through armory_move_file under a lock taken
    // for the move (v1 MoveAsync), refused while someone else has it checked out.
    public async Task<ActionResult> RenameFileAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => RenameFileAsync(path, newName, cancellationToken));
        newName = newName?.Trim() ?? "";
        if (!VaultPath.TryCreate(path, out var from, out _, options.VaultRoot)) return new(false, "That isn't a file in your Armory folder.");
        if (!VaultPath.TryValidateName(newName, out var problem)) return new(false, problem ?? "That name can't be used for a file.");
        var slash = from.Value.LastIndexOf('/');
        if (!VaultPath.TryCreate(from.Value[..(slash + 1)] + newName, out var to, out var tooLong, options.VaultRoot)) return new(false, tooLong ?? "That name can't be used here.");
        if (string.Equals(from.Value, to.Value, StringComparison.Ordinal)) return new(false, "That is already its name.");
        Guid operation;
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            state.Files.TryGetValue(from.Value, out var st);
            var caseOnly = string.Equals(from.Value, to.Value, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (state.Files.ContainsKey(to.Value) || local.ContainsKey(to.Value) || remoteByPath.ContainsKey(to.Value) || Exists(to)))
                return new(false, $"Something named {to.Name} is already in that folder.");
            if (IsOpenNow(from)) return new(false, $"Close {from.Name} in SolidWorks first.");
            // A write being sent for it (its add, a save) finishes first, or the rename could be
            // undone by that write when it is sent again under the old name.
            if (st?.Inflight is not null)
                return new(false, st.FileId is null ? $"Armory is still adding {from.Name}. Try again in a moment." : $"Armory is still sending {from.Name}. Try again in a moment.");
            if (st?.FileId is not { } fileId)
            {
                // Not in Armory: only this computer has it, so it is renamed here.
                if (!local.TryGetValue(from.Value, out var file)) return new(false, $"{from.Name} isn't on this computer.");
                var moved = fs.Move(from, to, file.Hash);
                if (!moved.Succeeded)
                {
                    deps.Log?.Invoke($"rename: {from}: {moved.Problem}");
                    return new(false, moved.Problem?.StartsWith("Something named", StringComparison.Ordinal) == true ? moved.Problem
                        : $"Armory couldn't rename {from.Name}. Close any program that might be using it, then try again.");
                }
                local.Remove(from.Value);
                local[to.Value] = file with { Path = to };
                if (st is not null)
                {
                    // Its saves go with it, and it is offered to Armory again under its new name.
                    Rekey(st, to.Value);
                    st.Refusal = null; st.RefusalKind = null; st.CreateEntry = null;
                }
                SaveNow();
                await PassLockedAsync(cancellationToken, PassScope.Under(to.Value));
                return new(true, $"Renamed {from.Name} to {to.Name}.");
            }
            if (remoteById.TryGetValue(fileId, out var remote) && OwnershipOf(remote.File.Lock) is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice)
                return new(false, $"{Who(remote.File.Lock!)} has {from.Name} checked out, so it can't be renamed now.");
            operation = Guid.NewGuid();
            state.Moves.Add(new PendingMove(operation, fileId, from.Value, to.Value));
            MarkDirty();
            await PassLockedAsync(cancellationToken, PassScope.File(fileId));
        }
        finally { LeaveAction(); }
        return moveResults.Remove(operation, out var done) && done
            ? new(true, $"Renamed {from.Name} to {to.Name}.")
            : new(false, online != true ? $"You're offline. {from.Name} can be renamed once this computer is back online." : $"Armory couldn't rename {from.Name}. Someone may have it checked out, or {to.Name} is already used in the project.");
    }

    // A notice card's Done or OK, or "prompt:..." for one check-out question. Applied with the
    // next view (during a pass, within half a second), so a window click never waits behind a pass.
    public void DismissNotice(string key) => _ = DismissNoticeAsync(key);

    // The same, done when the dismissal is in the view (or, during a pass or an action, queued
    // for the next one) and saved.
    public Task DismissNoticeAsync(string key) => engineThread.InvokeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        pendingDismissals.Enqueue(key);
        RequestPublish();
        if (passGate.CurrentCount > 0) await SettleAsync();
        return true;
    });

    // The student dismissed the card as the window last showed it: those items are hidden, even
    // when this moment of a pass has not rebuilt them yet. Items that arrived since are news and
    // stay.
    private void ApplyDismissals()
    {
        var any = false;
        while (pendingDismissals.TryDequeue(out var key))
        {
            if (key.StartsWith(PromptPrefix, StringComparison.Ordinal)) { dismissedPrompts.Add(key); continue; }
            var shown = shownNoticeItems.GetValueOrDefault(key) ?? RawNotices(state.Files.Values.ToArray()).FirstOrDefault(g => g.Key == key)?.Items.Select(i => i.Id).ToArray();
            if (shown is null) continue;
            if (!state.Dismissed.TryGetValue(key, out var hidden)) state.Dismissed[key] = hidden = new(StringComparer.Ordinal);
            foreach (var id in shown) hidden.Add(id);
            any = true;
        }
        if (any) MarkDirty();
    }

    // ---- Inside a pass -------------------------------------------------------------------

    // After the plans ran and the server was read again: check ins, undos, closed adds and
    // locks taken only for a move or a removal let their lock go once the file is clean, and
    // asked-for check outs take theirs. File by file in path order, each lock to let go is made
    // ready (read-only first) and each asked-for check out is noted; the check outs are then
    // taken, and once the releases' in-flight records are saved together (once), the releases
    // are sent, both EngineOptions.TransferConcurrency at a time like the units. What a check in
    // or an add waits for shows as "Checking in 412 of 4,900 files" in the upload direction.
    private async Task FinishRequestsAsync(CancellationToken ct)
    {
        List<(FileState State, Inflight Flight)> releases = [];
        List<FileState> checkOuts = [];
        foreach (var st in state.Files.Values.ToArray())
        {
            try
            {
                // An asked-for check out keeps whatever lock this computer holds (KeepCheckedOut);
                // a check in or undo waiting on a lock it no longer holds is over.
                if (st.CheckOut is null)
                {
                    if (PrepareRelease(st) is { } flight) releases.Add((st, flight));
                }
                else
                {
                    if (st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && OwnershipOf(remote.File.Lock) != LockOwnership.ThisDevice) LetGoDone(st);
                    checkOuts.Add(st);
                }
            }
            catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException)
            { FileProblem(st.Path, error); }
        }
        // Several check outs at once take their locks in batches (v0.3, armory_lock_files): one
        // call per 500 files, each file answered on its own.
        List<(FileState State, VaultPath Path)>? toLock = checkOuts.Count > 1 && BatchesAvailable ? [] : null;
        await RunConcurrentlyAsync(checkOuts, async (st, token) =>
        {
            try { await FinishCheckOutAsync(st, token, toLock); }
            catch (ArmoryOfflineException) { online = false; }
            catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException)
            { FileProblem(st.Path, error); }
            catch (Exception error) when (StopSaving(error)) { throw; }
        }, notStarted: null, ct);
        if (toLock is { Count: > 0 } && online == true) await LockBatchAsync(toLock, ct);
        foreach (var (st, _) in releases)
            if (st.Request == CheckoutRequest.CheckIn || (st.AutoCheckIn && st.Request == CheckoutRequest.None && !st.TransientLock)) activity.Expect(ActivityTracker.CheckIn, st.Path, 0);
        if (releases.Count > 0 && online == true) await FlushAsync();
        // Several releases at once go in batches too (armory_release_locks); what a batch can't
        // send (a site without it) goes file by file below.
        if (releases.Count > 1 && online == true && BatchesAvailable) releases = await ReleaseBatchAsync(releases, ct);
        await RunConcurrentlyAsync(releases, async (release, token) =>
        {
            var (st, flight) = release;
            try
            {
                if (await SendReleaseAsync(st, flight, token)) activity.Done(ActivityTracker.CheckIn, st.Path);
            }
            catch (ArmoryOfflineException) { online = false; }
            catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException)
            { FileProblem(st.Path, error); }
            catch (Exception error) when (StopSaving(error)) { throw; }
            finally
            {
                activity.Drop(st.Path);
                viewWanted = true;
                PublishSoon();
            }
        }, notStarted: release =>
        {
            // Never sent (the connection is gone): decided again on the next pass.
            if (ReferenceEquals(release.State.Inflight, release.Flight)) release.State.Inflight = null;
            activity.Drop(release.State.Path);
            MarkDirty();
        }, ct);
    }

    // A check in, an undo, a closed add or a lock held only for a move or a removal, once the file
    // is clean: made read-only, and its release recorded in flight (null when it is not ready).
    private Inflight? PrepareRelease(FileState st)
    {
        if (st.Inflight is not null || st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote)) return null;
        var asked = st.Request != CheckoutRequest.None || st.AutoCheckIn || st.TransientLock;
        if (remote.File.Lock is not { IsLive: true } held || OwnershipOf(held) != LockOwnership.ThisDevice)
        {
            // Taken back, or let go already: nothing is left to check in or undo here.
            if (!asked) return null;
            if (st.Request != CheckoutRequest.None) releaseResults[st] = ReleaseOutcome.TakenBack;
            st.Request = CheckoutRequest.None; st.AutoCheckIn = false; st.TransientLock = false;
            MarkDirty();
            return null;
        }
        if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot)) return null;
        // A removed file has nothing left to check out, whoever's lock removed it.
        if (!asked && !remote.File.Deleted) return null;
        if (st.AutoCheckIn && st.Request == CheckoutRequest.None && !st.TransientLock && !remote.File.Deleted && IsOpenNow(TryLocal(st.Path, out var where) ? where.Path : path)) return null; // an add stays checked out while open
        if (state.Moves.Any(m => m.FileId == id) || st.LocalMoveTo is not null) return null;
        // Bytes Armory can't take can never be checked in: the file stays checked out.
        if (st.RefusalKind is GateKind or TooLargeKind && (st.Request == CheckoutRequest.CheckIn || st.AutoCheckIn))
        {
            if (st.Request != CheckoutRequest.None) releaseResults[st] = ReleaseOutcome.Refused;
            st.Request = CheckoutRequest.None; st.AutoCheckIn = false;
            MarkDirty();
            return null;
        }
        TryLocal(st.Path, out var file);
        // A lock taken only for a move or a removal is let go as soon as that is done, whatever
        // is on disk: Core planned the file as nobody's, so bytes saved meanwhile are kept as a
        // kept copy and never wait on this lock.
        var transientOnly = st.TransientLock && st.Request == CheckoutRequest.None && !st.AutoCheckIn;
        var clean = transientOnly || (remote.File.Deleted ? file is null : file?.Hash == st.BaseHash && st.Entries.Count == 0);
        if (!clean) return null;
        // Read-only before the lock goes, so the file is never writable without a check out. A
        // bit that can't be set now keeps the lock until a later pass can set it.
        if (file is not null && !remote.File.Deleted && !SetAttribute(path, st, LockOwnership.Free)) return null;
        var flight = new Inflight("release", OperationIds.Derive("release", held.HolderDeviceId.ToString(), id.ToString(), held.AcquiredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            null, st.ProjectId, id, Device: held.HolderDeviceId);
        st.Inflight = flight;
        return flight;
    }

    // Sent once its in-flight record is on disk (FinishRequestsAsync saved them together).
    // True once the lock is let go.
    private async Task<bool> SendReleaseAsync(FileState st, Inflight flight, CancellationToken ct)
    {
        if (!await SendDurableAsync(st, flight, ct)) return false;
        if (st.Request != CheckoutRequest.None) releaseResults[st] = ReleaseOutcome.Released;
        st.Request = CheckoutRequest.None; st.AutoCheckIn = false; st.TransientLock = false;
        MarkDirty();
        return true;
    }

    // toLock: the files to take in one batch (FinishRequestsAsync); null takes this one's lock now.
    private async Task FinishCheckOutAsync(FileState st, CancellationToken ct, List<(FileState State, VaultPath Path)>? toLock = null)
    {
        if (st.Inflight is not null) return; // the resumed lock answers on the next pass
        if (!VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot) || st.FileId is not { } id ||
            !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted || remote.File.Current is null)
        {
            Answer(st, CheckOutOutcome.NotShared);
            return;
        }
        var ownership = OwnershipOf(remote.File.Lock);
        if (ownership == LockOwnership.ThisDevice)
        {
            // Now an explicit check out, never let go by itself (a lock taken for a move this
            // pass included).
            if (st.TransientLock) checkOutResults[st] = CheckOutOutcome.Done;
            LetGoDone(st);
            SetAttribute(path, st, LockOwnership.ThisDevice);
            Answer(st, checkOutResults.GetValueOrDefault(st) == CheckOutOutcome.Done ? CheckOutOutcome.Done : CheckOutOutcome.AlreadyMine);
            return;
        }
        if (ownership is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice) { Answer(st, CheckOutOutcome.Held); return; }
        // Hashed now, where the file is on disk: the bytes may have changed since this pass's scan.
        var disk = TryLocal(st.Path, out var here) ? here.Path : path;
        string? hash;
        try
        {
            await using var stream = fs.OpenRead(disk);
            hash = await ContentAddress.ComputeAsync(stream, ct);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { hash = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Answer(st, CheckOutOutcome.CantRead); return; }
        switch (CheckoutRules.NextCheckOutStep(st.Base, hash, RevisionOf(remote.File), IsOpenNow(disk)))
        {
            case CheckOutStep.TakeLock:
                if (toLock is not null) { toLock.Add((st, path)); break; }
                if (await AcquireAsync(st, st.CheckOut!, ct))
                {
                    SetAttribute(path, st, LockOwnership.ThisDevice);
                    Answer(st, CheckOutOutcome.Done);
                }
                else Answer(st, CheckOutOutcome.Held); // someone else checked it out first
                break;
            case CheckOutStep.DownloadFirst:
                checkOutResults[st] = CheckOutOutcome.Waiting; // the request stays for one more pass
                break;
            case CheckOutStep.KeepChangesFirst:
                // Closed, the pass already kept the bytes and put the shared version back unless
                // something stopped it; open, they stay until the file is closed.
                if (IsOpenNow(disk)) Answer(st, CheckOutOutcome.ChangedHere);
                else checkOutResults[st] = CheckOutOutcome.Waiting;
                break;
            case CheckOutStep.CloseFirst: Answer(st, CheckOutOutcome.CloseFirst); break;
            case CheckOutStep.RemovedHere: Answer(st, CheckOutOutcome.Removed); break;
            default: Answer(st, CheckOutOutcome.NotShared); break;
        }
    }

    // The final answer for one asked-for check out: the request is done with.
    private void Answer(FileState st, CheckOutOutcome outcome)
    {
        checkOutResults[st] = outcome;
        st.CheckOut = null;
        MarkDirty();
    }

    // Nothing is waiting to let go of this file's lock any more.
    private static void LetGoDone(FileState st)
    {
        st.Request = CheckoutRequest.None; st.AutoCheckIn = false; st.TransientLock = false;
    }

    // A check out asked for a file this computer already holds keeps it checked out: a check in
    // or an undo still waiting, an open add's automatic check in and a lock held only for a move
    // or a removal all give way to it. Run only once this pass has read the server, so a check
    // out refused offline cancels nothing.
    private void KeepCheckedOut()
    {
        var any = false;
        foreach (var st in state.Files.Values)
        {
            if (st.CheckOut is null || st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) continue;
            if (OwnershipOf(remote.File.Lock) != LockOwnership.ThisDevice || (st.Request == CheckoutRequest.None && !st.AutoCheckIn && !st.TransientLock)) continue;
            // A lock held only for a move becomes the check out the student asked for.
            if (st.TransientLock) checkOutResults[st] = CheckOutOutcome.Done;
            LetGoDone(st);
            any = true;
        }
        if (any) MarkDirty();
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private ActionResult? Unready()
    {
        var session = deps.Sessions.Current;
        if (session is null) return new(false, "Connect this computer first.");
        if (state.Email is not null && !string.Equals(state.Email, session.Email, StringComparison.OrdinalIgnoreCase))
            return new(false, "This Armory folder belongs to another account.");
        if (paused) return new(false, "Armory is paused. Resume it, then try again.");
        return null;
    }

    private static ActionResult Offline(string then) => new(false, "You're offline. " + then);

    // The window acts on what the last pass learned. Before any (a fresh start), the server is
    // read first; a vault that never synced runs a whole pass, which binds it to this account.
    private async Task EnsureKnownAsync(CancellationToken ct)
    {
        if (online is not null && remoteProjects.Count > 0) return;
        if (state.Email is null) await PassLockedAsync(ct);
        else online = await RefreshAsync(ct);
    }

    // A folder action needs to know what is on this disk too: before any pass since this start,
    // one whole pass runs first.
    private async Task EnsureScannedAsync(CancellationToken ct)
    {
        if (!scanned) await PassLockedAsync(ct);
        await EnsureKnownAsync(ct);
    }

    // Files this computer has checked out under the paths (a file, or every file in a folder):
    // by the server's lock table, or offline by the ownership it last knew. A file is found by
    // where its record is here or where the server has it (the window shows the server's path),
    // and a lock with no record here gets one first (AdoptMyLocks), so Check in and Undo always
    // work on what the window shows as checked out by you.
    private List<(FileState State, VaultPath Path)> MyCheckOuts(IReadOnlyList<string> paths)
    {
        AdoptMyLocks();
        List<(FileState, VaultPath)> mine = [];
        foreach (var st in state.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (st.FileId is not { } id || !VaultPath.TryCreate(st.Path, out var path, out _, options.VaultRoot)) continue;
            var known = remoteById.TryGetValue(id, out var remote);
            if (!Under(st.Path, paths) && !(known && Under(remote.Path.Value, paths))) continue;
            var held = known
                ? !remote.File.Deleted && OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice
                : KnownOwnership(st) == LockOwnership.ThisDevice || st.AutoCheckIn;
            if (held) mine.Add((st, path));
        }
        return mine;
    }

    // Every lock this computer holds on the server (under its current or a former device id) is
    // one of its check outs, downloaded or not: a lock with no record here (its record lost to a
    // folder change, a check out answered after it was given up, an older version's bug) gets a
    // record at the server's path, so the window shows it as checked out by you with a working
    // Check in and Undo. Run after every read of the server and before every check in or undo.
    private void AdoptMyLocks()
    {
        foreach (var (id, (file, project, path)) in remoteById.ToArray())
        {
            if (file.Deleted || file.Current is null || file.Lock is not { IsLive: true } held || OwnershipOf(held) != LockOwnership.ThisDevice) continue;
            if (state.FirstWithFileId(id) is not null || !project.Usable) continue;
            if (state.Files.TryGetValue(path.Value, out var occupant))
            {
                // Another record is at that path (a file of this computer's not in Armory): it is
                // never taken over; the lock waits until that record moves.
                deps.Log?.Invoke($"check out: the lock on {path} has no record here, and {occupant.Path} is another file's");
                continue;
            }
            var st = FileFor(project, path.Value);
            st.FileId = id;
            st.Holder = Known(held);
            deps.Log?.Invoke($"check out: the lock on {path} had no record here; it is shown as checked out by you");
            flight?.RepairedCheckout(path.Value);
        }
    }

    private static bool Under(string path, IReadOnlyList<string> paths)
        => paths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

    private bool IsFolder(string path) => !remoteByPath.ContainsKey(path) && !state.Files.ContainsKey(path) && !local.ContainsKey(path);

    private string HeldWords(FileState st, VaultPath path)
    {
        if (st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote) || remote.File.Lock is not { IsLive: true } held)
            return $"Someone else checked out {path.Name} first.";
        return OwnershipOf(held) == LockOwnership.MyOtherDevice
            ? $"{path.Name} is checked out on your other computer, {held.HolderDeviceName ?? "another computer"}. Check it in there first."
            : $"{path.Name} is checked out by {Who(held)}.";
    }

    private string HolderName(FileState st)
        => st.FileId is { } id && remoteById.TryGetValue(id, out var remote) && remote.File.Lock is { IsLive: true } held ? DisplayName(held.HolderEmail) : "Someone else";

    // "Maria Lopez on LAB-PC-07".
    private static string Who(RemoteLock held) => $"{DisplayName(held.HolderEmail)} on {held.HolderDeviceName ?? "another computer"}";

    // Who has the file checked out as this computer last knew it, for when it can't ask the server.
    private LockOwnership KnownOwnership(FileState st)
    {
        if (st.Holder is not { } held) return st.AppliedOwnership == LockOwnership.ThisDevice ? LockOwnership.ThisDevice : LockOwnership.Free;
        if (!string.Equals(held.Email, state.Email, StringComparison.OrdinalIgnoreCase)) return LockOwnership.OtherPerson;
        return state.IsMine(held.Device) ? LockOwnership.ThisDevice : LockOwnership.MyOtherDevice;
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    private static string PromptKey(string path, DateTimeOffset firstSeen) => PromptPrefix + path + ":" + firstSeen.ToString("O", CultureInfo.InvariantCulture);
}
