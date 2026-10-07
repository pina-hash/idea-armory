using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Folders, projects and imports (docs/agent/ENGINE.md, v2-design.md 4.3, decisions D8, D16 and
// D17; contract C2 to C6). A folder renamed on this disk is ONE armory_rename_folder and a folder
// removed here ONE armory_delete_folder, never a call per file. A refused one is put back, with
// one notice that names who has its files checked out, from this computer's own lock data. A
// project's folder follows the project's name on the site and is never made again beside a
// folder a student renamed or removed. A bulk add is one import summary.
public sealed partial class SyncEngine
{
    internal const int ImportAtLeast = 10;
    private static readonly TimeSpan ImportShownFor = TimeSpan.FromDays(1);
    private const string RenameOp = "rename", DeleteOp = "delete", PutBackOp = "putBack";

    // Pass-scoped. Known folders gone from this scan with files the server has in them (a
    // removal waits for two scans, and nothing under them is planned file by file meanwhile).
    private readonly HashSet<string> missingFolders = new(StringComparer.OrdinalIgnoreCase);
    // Projects whose own folder is gone or waiting to be put back: nothing in them is planned.
    private readonly HashSet<Guid> heldProjects = [];
    // Projects whose own folder was removed on this disk: made again and downloaded (D16).
    private readonly HashSet<Guid> restoreProjects = [];
    // Files this pass met for the first time (the import summary's evidence).
    private readonly List<string> createdThisPass = [];
    // The scan listed folders (VaultScan.Folders); without that, folder removals can't be seen.
    private bool folderScan;

    // ---- On this disk, before anything is captured ----------------------------------------

    // Folder renames first (the files keep their records, never captured again as new files at
    // the new path), then each project's own folder, then known folders gone from this scan.
    private void DetectFolderChanges(VaultScan scan)
    {
        missingFolders.Clear(); heldProjects.Clear(); restoreProjects.Clear();
        if (scan.FolderMoves is { } moves)
        {
            foreach (var move in moves) FolderMoved(Clean(move.From), Clean(move.To));
        }
        else FolderMovesFromBytes();
        PutFoldersBack();
        foreach (var ps in state.Projects.Values.Where(p => p.Usable && !p.Archived && p.Folder.Length > 0).ToArray()) CheckProjectFolder(ps);
        CountMissingFolders();
        Save();
    }

    private static string Clean(string folder) => folder.Replace('\\', '/').Trim('/');

    // One directory move the platform proved (in the platform's order: each From is the path
    // after the earlier moves).
    private void FolderMoved(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0) return;
        // A project folder waiting to be put back, renamed again: it is put back from there.
        foreach (var waiting in state.Projects.Values.Where(p => p.PutBackFrom is { } moved && string.Equals(moved, from, StringComparison.OrdinalIgnoreCase)))
            waiting.PutBackFrom = to;
        var source = ProjectOfFolder(from);
        if (source is null || source.Archived) return; // not a project this computer syncs, or archived (left as it is)
        if (string.Equals(from, source.Folder, StringComparison.OrdinalIgnoreCase))
        {
            // The project's own folder (decision D16): it is put back, never followed.
            if (ProjectOfFolder(to) is null) { source.PutBackFrom = to; Save(); }
            return;
        }
        var target = ProjectOfFolder(to);
        if (target?.Id != source.Id || string.Equals(to, target.Folder, StringComparison.OrdinalIgnoreCase))
        {
            // Out of its project (into another one, or to the top of the Armory folder): a folder
            // moves only inside its project, so it is put back where it was.
            state.FolderOps.Add(new PendingFolderOp(PutBackOp, Guid.NewGuid(), source.Id, from, to,
                $"{Leaf(from)} was put back: a folder can only move inside its project"));
            return;
        }
        StartFolderRename(source, from, to); // MUTATION: a directory rename is one folder move
    }

    // A folder renamed (or moved inside its project) on this disk: its records follow the disk
    // at once, and armory_rename_folder is sent once, durable first (SendFolderOpsAsync).
    private void StartFolderRename(ProjectState ps, string from, string to)
    {
        var op = new PendingFolderOp(RenameOp, OperationIds.Derive(state.NextId("folder"), RenameOp), ps.Id, from, to);
        RekeyFolder(from, to, moveLocal: false);
        state.FolderOps.Add(op);
        Save();
    }

    // Without directory identity (VaultScan.FolderMoves null): a known folder that is gone,
    // every file it had found byte for byte at the same place under exactly one new folder of
    // the same project, is that folder renamed.
    private void FolderMovesFromBytes()
    {
        if (!folderScan) return;
        foreach (var known in state.KnownFolders.OrderBy(f => f.Length).ToArray())
        {
            if (FolderOnDisk(known) || !state.KnownFolders.Contains(known)) continue;
            var ps = ProjectOfFolder(known);
            if (ps is null || ps.Archived || string.Equals(known, ps.Folder, StringComparison.OrdinalIgnoreCase) || !FolderOnDisk(ps.Folder)) continue;
            if (FolderMovedByBytes(known, ps) is { } to) FolderMoved(known, to);
        }
    }

    // Where the files of a folder that is gone are now, by their bytes: the one folder that holds
    // every one of them at the same relative path, untracked. Null when there is no such folder.
    private string? FolderMovedByBytes(string from, ProjectState ps, bool topLevel = false)
    {
        var tracked = state.Files.Values.Where(f => Inside(f.Path, from) && f.FileId is not null && f.BaseHash is not null).ToList();
        if (tracked.Count == 0) return null;
        // Files on disk this computer has no server record for, by their bytes.
        var untracked = local.Values.Where(f => !Inside(f.Path.Value, from) && (!state.Files.TryGetValue(f.Path.Value, out var other) || other.FileId is null))
            .ToLookup(f => f.Hash, StringComparer.Ordinal);
        string? found = null;
        foreach (var st in tracked)
        {
            var rel = st.Path[(from.Length + 1)..];
            var candidates = untracked[st.BaseHash!].Concat(st.LastCaptured is { } captured && captured != st.BaseHash ? untracked[captured] : [])
                .Where(f => f.Path.Value.EndsWith("/" + rel, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path.Value[..^(rel.Length + 1)]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (topLevel) candidates.RemoveAll(c => c.Contains('/', StringComparison.Ordinal));
            else candidates.RemoveAll(c => ProjectOfFolder(c)?.Id != ps.Id || string.Equals(c, ps.Folder, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count != 1 || (found is not null && !string.Equals(found, candidates[0], StringComparison.OrdinalIgnoreCase))) return null;
            found = candidates[0];
        }
        return found;
    }

    // Folders waiting to go back where they were (a refused rename, a folder moved out of its
    // project): moved back as soon as nothing inside is open, with one notice.
    private void PutFoldersBack()
    {
        foreach (var op in state.FolderOps.Where(o => o.Kind == PutBackOp).ToArray()) TryPutBack(op);
    }

    private void TryPutBack(PendingFolderOp op)
    {
        var name = Leaf(op.LocalFrom);
        if (!FolderOnDisk(op.LocalTo))
        {
            // Nothing is left to move back (the student moved it themselves, or it is gone):
            // whatever is on the disk now is what the next scan reads.
            state.FolderOps.Remove(op);
            if (FolderOnDisk(op.LocalFrom)) RekeyFolder(op.LocalTo, op.LocalFrom, moveLocal: false);
            Save();
            return;
        }
        var outcome = fs.MoveFolder(op.LocalTo, op.LocalFrom);
        if (!outcome.Succeeded)
        {
            Notice(NoticeKinds.FolderPutBack, null, op.LocalTo,
                $"{(OpenUnder(op.LocalTo) is { } open ? $"Close {open}" : "Close the files in it")} so Armory can put it back where it was. {PutBackDetail(op)}",
                op.Refusal is { } refusal ? refusal : $"{name} is waiting to be put back");
            deps.Log?.Invoke($"put back {op.LocalTo}: {outcome.Problem}");
            return;
        }
        // Done with before the records move, so the move does not rewrite it.
        state.FolderOps.Remove(op);
        RekeyFolder(op.LocalTo, op.LocalFrom, moveLocal: true);
        Remember(NoticeKinds.FolderPutBack, null, op.LocalFrom, op.Refusal is { } said ? said + "." : $"{name} was put back where it was", PutBackDetail(op));
        lastActivity = deps.Clock.GetUtcNow();
        Save();
    }

    private static string PutBackDetail(PendingFolderOp op)
        => op.Refusal?.Contains("checked out", StringComparison.Ordinal) == true
            ? "A folder is renamed or deleted only when nobody else has a file in it checked out. Ask them to check the files in, then try again."
            : op.Refusal?.Contains("inside its project", StringComparison.Ordinal) == true
            ? "Folders can be renamed and moved inside a project, never into another one."
            : "Armory couldn't rename it for the team. Try again later.";

    // A project's own folder (decision D16). Renamed in Explorer: moved back, "Project names are
    // changed on ideabosco.com."; removed: made again and downloaded once online. Never a second
    // folder beside a renamed one, and never a removal of the project's files.
    private void CheckProjectFolder(ProjectState ps)
    {
        if (ps.PutBackFrom is { } moved)
        {
            if (FolderOnDisk(ps.Folder) || !FolderOnDisk(moved)) ps.PutBackFrom = null; // put back by hand, or gone
            else
            {
                var outcome = fs.MoveFolder(moved, ps.Folder);
                if (outcome.Succeeded)
                {
                    RekeyFolder(moved, ps.Folder, moveLocal: true);
                    ps.PutBackFrom = null;
                    Remember(NoticeKinds.ProjectPutBack, null, ps.Folder, $"The {ps.Folder} folder was renamed back", ProjectNamesWords);
                    lastActivity = deps.Clock.GetUtcNow();
                }
                else
                {
                    heldProjects.Add(ps.Id);
                    Notice(NoticeKinds.ProjectPutBack, null, moved,
                        $"{(OpenUnder(moved) is { } open ? $"Close {open}" : "Close the files in it")} so Armory can rename {moved} back to {ps.Folder}. {ProjectNamesWords}",
                        $"The {ps.Folder} folder is waiting to be renamed back");
                    deps.Log?.Invoke($"put back {moved}: {outcome.Problem}");
                }
                return;
            }
        }
        if (FolderOnDisk(ps.Folder) || !HadFiles(ps)) return;
        // Gone from this scan. Renamed without a proven move: found by its files' bytes.
        if (FolderMovedByBytes(ps.Folder, ps, topLevel: true) is { } found && ProjectOfFolder(found) is null)
        {
            ps.PutBackFrom = found;
            CheckProjectFolder(ps);
            return;
        }
        // Removed: nothing in it is planned (never a removal for the team), and the next online
        // refresh makes it again and downloads its files.
        heldProjects.Add(ps.Id);
        restoreProjects.Add(ps.Id);
    }

    private const string ProjectNamesWords = "Project names are changed on ideabosco.com.";

    // A project whose folder this computer had files in.
    private bool HadFiles(ProjectState ps)
        => state.Files.Values.Any(f => f.ProjectId == ps.Id && f.FileId is not null && f.BaseHash is not null) ||
           state.KnownFolders.Any(k => Inside(k, ps.Folder));

    // The project's folder was removed on this disk: made again, and every file the server has
    // that was here is downloaded again (its record forgets the base, so Core plans a download).
    private void RestoreProjectFolder(ProjectState ps)
    {
        try { fs.EnsureFolder(ps.Folder); }
        catch (IOException error)
        {
            Problem(NoticeKinds.CantRead, ps.Folder, $"Armory couldn't make the folder for {ps.Name} again on this computer. It tries again by itself.", error.Message);
            return;
        }
        localFolders.Add(ps.Folder);
        foreach (var st in state.Files.Values.Where(f => f.ProjectId == ps.Id && f.FileId is not null && f.BaseHash is not null && !local.ContainsKey(f.Path)))
        {
            st.SetBase(null);
            st.AbsentScans = 0;
            st.DeleteEntry = null;
        }
        heldProjects.Remove(ps.Id);
        restoreProjects.Remove(ps.Id);
        foreach (var known in state.KnownFolders.Where(k => Inside(k, ps.Folder)).ToArray()) state.AbsentFolders.Remove(known);
        Remember(NoticeKinds.ProjectPutBack, null, ps.Folder, $"The {ps.Folder} folder was put back",
            "A project's folder stays on this computer while you're in the project. Armory is downloading its files again.");
        Save();
    }

    // Known folders gone from this scan with files the server has in them.
    private void CountMissingFolders()
    {
        if (!folderScan) return;
        foreach (var known in state.KnownFolders.ToArray())
        {
            if (FolderOnDisk(known)) { state.AbsentFolders.Remove(known); continue; }
            var ps = ProjectOfFolder(known);
            if (ps is null || ps.Archived || heldProjects.Contains(ps.Id) || HeldByWork(known)) continue;
            if (!state.Files.Values.Any(f => Inside(f.Path, known) && f.FileId is not null && f.BaseHash is not null)) continue;
            state.AbsentFolders[known] = state.AbsentFolders.GetValueOrDefault(known) + 1;
            missingFolders.Add(known);
        }
    }

    // ---- With the server -------------------------------------------------------------------

    // Folder renames and removals made on this disk, oldest first: one server call each, with
    // the operation id persisted before it. Returns true when anything was sent.
    private async Task<bool> SendFolderOpsAsync(CancellationToken ct)
    {
        var sent = false;
        foreach (var op in state.FolderOps.Where(o => o.Kind is RenameOp or DeleteOp).ToArray())
        {
            if (!state.FolderOps.Contains(op)) continue;
            var ps = state.Projects.GetValueOrDefault(op.ProjectId);
            if (ps is null || !ps.Usable || ps.Archived) continue;
            try
            {
                if (op.Kind == RenameOp) await SendFolderRenameAsync(op, ps, ct);
                else await SendFolderDeleteAsync(op, ps, ct);
                sent = true;
            }
            catch (ArmoryOfflineException) { online = false; return sent; }
            catch (ArmoryClientException error)
            {
                // Not an answer about the folder (the server could not be asked properly): the
                // same operation id is sent again on the next pass.
                deps.Log?.Invoke($"folder {op.Kind} {op.LocalFrom}: {error.Message}");
            }
        }
        return sent;
    }

    private async Task SendFolderRenameAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        var to = Relative(op.LocalTo, ps);
        foreach (var from in ServerFolders(op.LocalFrom, ps))
        {
            try
            {
                CrashPoint?.Invoke("before-folder");
                wrote = true;
                await deps.Api.RenameFolderAsync(ps.Id, from, to, state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), from), ct);
                CrashPoint?.Invoke("after-folder");
            }
            catch (ArmoryRpcException error) when (!error.IsTransient)
            {
                // Refused (someone else has a file in it checked out, or the project already has
                // that folder): put back where it was, with one notice naming who.
                var refusal = await RenameRefusalAsync(error, op, ps, ct);
                var index = state.FolderOps.IndexOf(op);
                var putBack = new PendingFolderOp(PutBackOp, op.Operation, op.ProjectId, op.LocalFrom, op.LocalTo, refusal);
                state.FolderOps[index] = putBack;
                Save();
                TryPutBack(putBack);
                return;
            }
        }
        state.FolderOps.Remove(op);
        lastActivity = deps.Clock.GetUtcNow();
        Save();
    }

    private async Task<string> RenameRefusalAsync(ArmoryRpcException error, PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        deps.Log?.Invoke($"rename folder {op.LocalFrom} refused: {error.Message}");
        var name = Leaf(op.LocalFrom);
        if (FolderRefusal.TryParse(error.Details) is { IsTargetExists: true })
            return $"{name} was put back: {ps.Name} already has a folder named {Leaf(op.LocalTo)}";
        if (error.IsInUse) return $"{name} was put back: {await HoldersAsync(op.LocalFrom, ps, ct)}";
        return $"{name} was put back";
    }

    private async Task SendFolderDeleteAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        // Looked at once more right before the removal goes to the whole team.
        var tracked = state.Files.Values.Where(f => Inside(f.Path, op.LocalFrom) && f.FileId is { } id &&
            (!remoteById.TryGetValue(id, out var remote) || Inside(remote.Path.Value, op.LocalFrom))).ToList();
        if (tracked.Any(f => VaultPath.TryCreate(f.Path, out var p, out _, options.VaultRoot) && Exists(p)))
        {
            state.FolderOps.Remove(op);
            state.AbsentFolders.Remove(op.LocalFrom);
            Save();
            return;
        }
        foreach (var folder in ServerFolders(op.LocalFrom, ps))
        {
            try
            {
                CrashPoint?.Invoke("before-folder");
                wrote = true;
                await deps.Api.DeleteFolderAsync(ps.Id, folder, state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), folder), ct);
                CrashPoint?.Invoke("after-folder");
            }
            catch (ArmoryRpcException error) when (!error.IsTransient)
            {
                // Refused: the folder is made again and its files downloaded, with one notice.
                deps.Log?.Invoke($"delete folder {op.LocalFrom} refused: {error.Message}");
                var who = error.IsInUse ? await HoldersAsync(op.LocalFrom, ps, ct) : null;
                state.FolderOps.Remove(op);
                RestoreFolder(op.LocalFrom, who);
                return;
            }
        }
        // Removed for the team in one call: each record here is the removed file now.
        foreach (var st in tracked)
        {
            st.SetBase(new($"tombstone:{st.FileId}", null, ""));
            st.DeleteEntry = null;
            st.AbsentScans = 0;
            st.Preserved = null;
        }
        ForgetFolder(op.LocalFrom);
        state.FolderOps.Remove(op);
        lastActivity = deps.Clock.GetUtcNow();
        Save();
    }

    // A refused folder removal: the folder is made again and every file the server still has
    // in it comes down again (Core plans a download once the record forgets its base).
    private void RestoreFolder(string folder, string? who)
    {
        try { fs.EnsureFolder(folder); localFolders.Add(folder); }
        catch (IOException error) { deps.Log?.Invoke($"restore {folder}: {error.Message}"); }
        foreach (var st in state.Files.Values.Where(f => Inside(f.Path, folder) && f.FileId is not null && !local.ContainsKey(f.Path)))
        {
            if (!remoteById.TryGetValue(st.FileId!.Value, out var remote) || remote.File.Deleted) continue;
            st.SetBase(null);
            st.AbsentScans = 0;
            st.DeleteEntry = null;
        }
        foreach (var gone in missingFolders.Where(m => Inside(m, folder)).ToArray()) missingFolders.Remove(gone);
        foreach (var gone in state.AbsentFolders.Keys.Where(m => Inside(m, folder)).ToArray()) state.AbsentFolders.Remove(gone);
        Remember(NoticeKinds.FolderPutBack, null, folder, who is null ? $"{Leaf(folder)} was put back" : $"{Leaf(folder)} was put back: {who}.",
            who is null ? "Armory couldn't remove it for the team, so its files are coming back. Try again later."
                : "A folder is renamed or deleted only when nobody else has a file in it checked out. Its files are coming back now. Ask them to check the files in, then try again.");
        Save();
    }

    // Who has files under a folder checked out, from this computer's own lock data (the
    // project's files as the server lists them, read again when this pass's copy names nobody).
    private async Task<string> HoldersAsync(string folder, ProjectState ps, CancellationToken ct)
    {
        var locks = remoteById.Values.Where(r => r.Project.Id == ps.Id && !r.File.Deleted && Inside(r.Path.Value, folder) && r.File.Lock is { IsLive: true } held &&
            OwnershipOf(held) != LockOwnership.ThisDevice).Select(r => r.File.Lock!).ToList();
        if (locks.Count == 0)
        {
            try
            {
                foreach (var file in await deps.Api.ProjectFilesAsync(ps.Id, ct))
                {
                    var path = ps.Folder + "/" + (file.Folder.Length == 0 ? "" : file.Folder + "/") + file.Name;
                    if (!file.Deleted && Inside(path, folder) && file.Lock is { IsLive: true } held && OwnershipOf(held) != LockOwnership.ThisDevice) locks.Add(held);
                }
            }
            catch (ArmoryClientException error) { deps.Log?.Invoke("who has the files: " + error.Message); }
        }
        return HoldersWords(locks, "its files");
    }

    // "Maria Lopez has 2 of its files checked out", "Maria Lopez and Sam Lee have 3 of its files
    // checked out", "you have 1 of its files checked out on LAB-PC-07".
    private string HoldersWords(IReadOnlyCollection<RemoteLock> locks, string what)
    {
        if (locks.Count == 0) return $"someone else has some of {what} checked out";
        var people = locks.Where(l => OwnershipOf(l) == LockOwnership.OtherPerson).ToList();
        if (people.Count == 0)
        {
            var devices = locks.Select(l => l.HolderDeviceName ?? "another computer").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return $"you have {locks.Count:N0} of {what} checked out on {string.Join(" and ", devices)}";
        }
        var names = people.GroupBy(l => DisplayName(l.HolderEmail), StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        var who = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            3 => $"{names[0]}, {names[1]} and {names[2]}",
            _ => $"{names[0]}, {names[1]} and {names.Count - 2:N0} others",
        };
        return $"{who} {(names.Count == 1 ? "has" : "have")} {locks.Count:N0} of {what} checked out";
    }

    // The server's spelling of a folder, from its files (the match on the server is exact): one
    // per spelling, almost always one.
    private List<string> ServerFolders(string localFolder, ProjectState ps)
    {
        var depth = localFolder.Split('/').Length - 1;
        return remoteById.Values.Where(r => r.Project.Id == ps.Id && !r.File.Deleted && Inside(r.Path.Value, localFolder))
            .Select(r => string.Join('/', r.File.Folder.Split('/').Take(depth))).Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    // Known folders that are gone from the disk for a second scan, each the top of what is gone,
    // with every file the server has there missing too (looked at again right now): ONE
    // armory_delete_folder each. Unsent saves under them were kept already this pass
    // (ArchiveSupersededAsync). Returns true when anything was sent.
    private async Task<bool> RemoveMissingFoldersAsync(CancellationToken ct)
    {
        var sent = false;
        foreach (var folder in missingFolders.OrderBy(f => f.Length).ToArray())
        {
            if (online != true) break;
            if (!missingFolders.Contains(folder)) continue; // went with a folder above it
            if (missingFolders.Any(m => !string.Equals(m, folder, StringComparison.OrdinalIgnoreCase) && Inside(folder, m))) continue;
            if (state.AbsentFolders.GetValueOrDefault(folder) < 2 || HeldByWork(folder)) continue;
            var ps = ProjectOfFolder(folder);
            if (ps is null || ps.Archived) continue;
            var tracked = state.Files.Values.Where(f => Inside(f.Path, folder) && f.FileId is { } id && f.BaseHash is not null &&
                remoteById.TryGetValue(id, out var r) && !r.File.Deleted).ToList();
            if (tracked.Count == 0) continue;
            // Moved out of it on their own (an Explorer move each), or still being sent: not yet.
            if (tracked.Any(f => f.LocalMoveTo is not null || f.Inflight is not null || state.Moves.Any(m => m.FileId == f.FileId))) continue;
            if (tracked.Any(f => VaultPath.TryCreate(f.Path, out var p, out _, options.VaultRoot) && Exists(p))) continue;
            var op = new PendingFolderOp(DeleteOp, OperationIds.Derive(state.NextId("folder"), DeleteOp), ps.Id, folder, folder);
            state.FolderOps.Add(op);
            Save();
            try { await SendFolderDeleteAsync(op, ps, ct); sent = true; }
            catch (ArmoryOfflineException) { online = false; break; }
            catch (ArmoryClientException error) { deps.Log?.Invoke($"folder delete {folder}: {error.Message}"); }
        }
        return sent;
    }

    // ---- Changes from the team -------------------------------------------------------------

    // A folder renamed on the server (folder_renamed, or every file of a folder moved under one
    // new folder) is ONE local move: nothing is downloaded, nothing goes to recovery. When that
    // can't be done in one step (something inside is open, or the new folder is already here),
    // each file moves on its own (ApplyRemoteMoves), the open ones once they close.
    private bool ApplyRemoteFolderMoves()
    {
        var any = false;
        foreach (var hint in state.RemoteFolderRenames.ToArray())
        {
            state.RemoteFolderRenames.Remove(hint);
            if (!state.Projects.TryGetValue(hint.ProjectId, out var ps) || !ps.Usable || ps.Archived || heldProjects.Contains(ps.Id)) continue;
            any |= TryMoveRemoteFolder(ps.Folder + "/" + hint.From, ps.Folder + "/" + hint.To, everyFile: false);
        }
        var candidates = new Dictionary<(string From, string To), int>();
        foreach (var st in state.Files.Values)
        {
            if (st.FileId is not { } id || st.LocalMoveTo is not null || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) continue;
            if (string.Equals(remote.Path.Value, st.Path, StringComparison.Ordinal) || PrefixChange(st.Path, remote.Path.Value) is not { } change) continue;
            candidates[change] = candidates.GetValueOrDefault(change) + 1;
        }
        foreach (var ((from, to), count) in candidates.OrderBy(c => c.Key.From.Length))
            if (count >= 2) any |= TryMoveRemoteFolder(from, to, everyFile: true);
        if (any) Save();
        return any;
    }

    private bool TryMoveRemoteFolder(string from, string to, bool everyFile)
    {
        var source = localFolders.FirstOrDefault(f => string.Equals(f, from, StringComparison.OrdinalIgnoreCase));
        if (source is null || HeldByWork(source) || HeldByWork(to)) return false;
        var ps = ProjectOfFolder(source);
        if (ps is null || ProjectOfFolder(to)?.Id != ps.Id || string.Equals(source, ps.Folder, StringComparison.OrdinalIgnoreCase)) return false;
        var moving = 0;
        foreach (var st in state.Files.Values.Where(f => Inside(f.Path, source)))
        {
            if (st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) continue; // removed ones go along, then to recovery
            if (!string.Equals(remote.Path.Value, to + st.Path[source.Length..], StringComparison.OrdinalIgnoreCase)) return false; // not one folder move
            moving++;
        }
        if (moving == 0) return false;
        // Without the server saying so, files of the student's own in it mean it is not simply
        // the team's folder renamed: each file moves on its own.
        if (everyFile && local.Keys.Any(k => Inside(k, source) && (!state.Files.TryGetValue(k, out var s) || s.FileId is null))) return false;
        if (!string.Equals(source, to, StringComparison.OrdinalIgnoreCase) && FolderOnDisk(to)) return false;
        var outcome = fs.MoveFolder(source, to);
        if (!outcome.Succeeded)
        {
            deps.Log?.Invoke($"move folder {source} to {to}: {outcome.Problem}");
            return false;
        }
        RekeyFolder(source, to, moveLocal: true);
        lastActivity = deps.Clock.GetUtcNow();
        return true;
    }

    // The folder change that turns one path into another with the same file name: the deepest
    // folders that differ ("Robot 2027/Pack/CopyDesignTemp" to "Robot 2027/Pack/Gearbox"). Null
    // for a renamed file, another project, or a move into or out of a project's top folder.
    internal static (string From, string To)? PrefixChange(string from, string to)
    {
        var a = from.Split('/');
        var b = to.Split('/');
        if (!string.Equals(a[^1], b[^1], StringComparison.OrdinalIgnoreCase) || !string.Equals(a[0], b[0], StringComparison.OrdinalIgnoreCase)) return null;
        int i = a.Length - 2, j = b.Length - 2;
        while (i >= 1 && j >= 1 && string.Equals(a[i], b[j], StringComparison.Ordinal)) { i--; j--; }
        if (i < 1 || j < 1) return null;
        return (string.Join('/', a[..(i + 1)]), string.Join('/', b[..(j + 1)]));
    }

    // The project's name changed on the site (contract C2): its folder moves in place, nothing is
    // downloaded or removed. While a file inside is open, the project keeps syncing in its old
    // folder, with one notice.
    private void MoveProjectFolder(ProjectState ps)
    {
        var from = ps.Folder;
        var to = ps.Name;
        if (state.Projects.Values.Any(p => !ReferenceEquals(p, ps) && p.Usable && string.Equals(p.Folder, to, StringComparison.OrdinalIgnoreCase)))
        {
            Notice(NoticeKinds.ProjectRenaming, null, from, $"Another project still uses the folder {to}. Armory renames {from} as soon as it can.", $"{from} is now {to} on ideabosco.com");
            return;
        }
        if (!FolderOnDisk(from))
        {
            // Nothing here to move: the records follow the new name.
            RekeyFolder(from, to, moveLocal: true);
            ps.Folder = to;
            Save();
            return;
        }
        var outcome = fs.MoveFolder(from, to);
        if (!outcome.Succeeded)
        {
            deps.Log?.Invoke($"rename project folder {from} to {to}: {outcome.Problem}");
            if (OpenUnder(from) is { } open)
                Notice(NoticeKinds.ProjectRenaming, null, from,
                    "A mentor renamed the project on ideabosco.com. Armory renames its folder on this computer as soon as nothing in it is open.",
                    $"Close {open} to finish renaming {from} to {to}");
            else if (FolderOnDisk(to))
                Notice(NoticeKinds.ProjectRenaming, null, from,
                    $"Something named {to} is already in your Armory folder. Rename or move it, and Armory finishes renaming {from}.",
                    $"Armory can't rename {from} to {to} yet");
            else
                Notice(NoticeKinds.ProjectRenaming, null, from,
                    "A mentor renamed the project on ideabosco.com. Close any program using its files, and Armory finishes renaming its folder.",
                    $"Armory can't rename {from} to {to} yet");
            return;
        }
        RekeyFolder(from, to, moveLocal: true);
        ps.Folder = to;
        lastActivity = deps.Clock.GetUtcNow();
        Save();
    }

    // ---- After the plans: known and empty folders (decision D17) ---------------------------

    // Folders that hold files the server has become known; a known folder with nothing left in
    // it (no file here, no live file on the server, nothing waiting) is removed, on every
    // computer. Folders a student makes, empty or not, stay.
    private void TidyFolders()
    {
        foreach (var (file, project, path) in remoteById.Values)
        {
            if (file.Deleted || project.Archived || !project.Usable || !local.ContainsKey(path.Value)) continue;
            for (var folder = Parent(path.Value); folder is not null && !string.Equals(folder, project.Folder, StringComparison.OrdinalIgnoreCase); folder = Parent(folder))
                if (!state.KnownFolders.Add(folder)) break;
        }
        // Every folder that still holds something: a file here, a live file on the server, or a
        // file with work waiting (an unsent save, a write in flight, a request).
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Busy(string path) { for (var folder = Parent(path); folder is not null && busy.Add(folder); folder = Parent(folder)) { } }
        foreach (var remote in remoteById.Values) if (!remote.File.Deleted) Busy(remote.Path.Value);
        foreach (var key in local.Keys) Busy(key);
        foreach (var st in state.Files.Values)
            if (st.Inflight is not null || st.Entries.Count > 0 || st.Drafts.Count > 0 || st.LocalMoveTo is not null || st.CheckOut is not null || st.Request != CheckoutRequest.None)
                Busy(st.Path);
        foreach (var folder in state.KnownFolders.OrderByDescending(f => f.Length).ToArray())
        {
            var ps = ProjectOfFolder(folder);
            if (ps is null || ps.Archived || heldProjects.Contains(ps.Id) || HeldByWork(folder) || missingFolders.Contains(folder)) continue;
            if (string.Equals(folder, ps.Folder, StringComparison.OrdinalIgnoreCase)) { state.KnownFolders.Remove(folder); continue; }
            if (busy.Contains(folder) || !fs.DeleteEmptyFolder(folder)) continue;
            ForgetFolder(folder);
            lastActivity = deps.Clock.GetUtcNow();
        }
        Save();
    }

    private void ForgetFolder(string folder)
    {
        state.KnownFolders.RemoveWhere(k => Inside(k, folder));
        foreach (var gone in state.AbsentFolders.Keys.Where(k => Inside(k, folder)).ToArray()) state.AbsentFolders.Remove(gone);
        missingFolders.RemoveWhere(k => Inside(k, folder));
        localFolders.RemoveWhere(k => Inside(k, folder));
    }

    // ---- Imports ---------------------------------------------------------------------------

    // A bulk add (an unzip, a paste, a Pack and Go): at least ImportAtLeast files new to this
    // computer and to the server under one new folder (or one folder), in one pass, become ONE
    // import summary, however many there are.
    private void DetectImports()
    {
        if (createdThisPass.Count < ImportAtLeast) return;
        var created = createdThisPass.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = state.Imports.SelectMany(i => i.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Folders that held anything before this pass.
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in state.Files.Keys)
        {
            if (created.Contains(key)) continue;
            for (var folder = Parent(key); folder is not null && occupied.Add(folder); folder = Parent(folder)) { }
        }
        var groups = new Dictionary<string, (Guid Project, List<string> Paths)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in createdThisPass)
        {
            if (imported.Contains(path) || !state.Files.TryGetValue(path, out var st) || st.FileId is not null || st.BaseId is not null ||
                !local.ContainsKey(path) || remoteByPath.ContainsKey(path)) continue;
            var root = ImportRoot(path, occupied);
            if (!groups.TryGetValue(root, out var group)) groups[root] = group = (st.ProjectId, []);
            group.Paths.Add(path);
        }
        var any = false;
        foreach (var (root, (project, paths)) in groups)
        {
            if (paths.Count < ImportAtLeast) continue;
            state.Imports.Add(new ImportRecord(Guid.NewGuid(), project, root, paths, deps.Clock.GetUtcNow()));
            any = true;
        }
        if (any) Save();
    }

    // The folder a new file came in with: the topmost folder that held nothing before (and was
    // not known), or else its own folder.
    private string ImportRoot(string path, HashSet<string> occupied)
    {
        var parts = path.Split('/');
        for (var i = 2; i < parts.Length; i++)
        {
            var folder = string.Join('/', parts[..i]);
            if (!occupied.Contains(folder) && !state.KnownFolders.Contains(folder)) return folder;
        }
        return string.Join('/', parts[..^1]);
    }

    // What an import came to now: in Armory, sharing a name, still on its way, refused.
    private (int Added, int Shared, int Waiting, int Other, int Total) ImportTally(ImportRecord import)
    {
        int added = 0, shared = 0, waiting = 0, other = 0;
        foreach (var path in import.Paths)
        {
            state.Files.TryGetValue(path, out var st);
            if (st?.FileId is not null && st.BaseHash is not null) added++;
            else if (st?.RefusalKind == NameTakenKind) shared++;
            else if (st?.Refusal is not null) other++;
            else if (st is not null || local.ContainsKey(path)) waiting++;
        }
        return (added, shared, waiting, other, added + shared + waiting + other);
    }

    // ---- The window's folder actions (v2-design.md 4.3) ------------------------------------

    // New folder: made on this computer (the server keeps no empty folders; the folder is the
    // team's as soon as a file is in it).
    public async Task<ActionResult> CreateFolderAsync(Guid projectId, string parent, string name, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        if (!VaultPath.TryValidateName(name, out var problem)) return new(false, problem ?? "That name can't be used for a folder.");
        await passGate.WaitAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            var inside = Join(ps.Folder, parent);
            if (!VaultPath.TryCreate(inside + "/" + name, out var folder, out var tooLong, options.VaultRoot)) return new(false, tooLong ?? "That name can't be used here.");
            if (FolderOnDisk(folder.Value) || local.ContainsKey(folder.Value) || state.Files.ContainsKey(folder.Value) || remoteByPath.Keys.Any(k => Inside(k, folder.Value)) || Exists(folder))
                return new(false, $"Something named {name} is already in {Where(inside)}.");
            try { fs.EnsureFolder(folder.Value); }
            catch (IOException error)
            {
                deps.Log?.Invoke($"new folder {folder}: {error.Message}");
                return new(false, $"Armory couldn't make the folder {name}. Try again in a moment.");
            }
            for (var f = folder.Value; f is not null && !string.Equals(f, ps.Folder, StringComparison.OrdinalIgnoreCase); f = Parent(f)) localFolders.Add(f);
            PublishLocked();
            return new(true, $"Made the folder {name} in {Where(inside)}.");
        }
        finally { passGate.Release(); }
    }

    // Rename folder: for everyone first (one armory_rename_folder, refused while someone else has
    // a file in it checked out), then on this computer (one move).
    public async Task<ActionResult> RenameFolderAsync(Guid projectId, string folder, string newName, CancellationToken cancellationToken = default)
    {
        newName = newName?.Trim() ?? "";
        if (!VaultPath.TryValidateName(newName, out var problem)) return new(false, problem ?? "That name can't be used for a folder.");
        await passGate.WaitAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            if (string.IsNullOrEmpty(folder)) return new(false, ProjectNamesWords);
            var from = Join(ps.Folder, folder);
            var name = Leaf(from);
            var parent = Parent(from)!;
            if (!VaultPath.TryCreate(parent + "/" + newName, out var target, out var tooLong, options.VaultRoot)) return new(false, tooLong ?? "That name can't be used here.");
            var to = target.Value;
            if (string.Equals(from, to, StringComparison.Ordinal)) return new(false, "That is already its name.");
            var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!FolderOnDisk(from) && !remoteByPath.Keys.Any(k => Inside(k, from))) return new(false, $"{name} isn't there any more.");
            if (!caseOnly && (FolderOnDisk(to) || local.ContainsKey(to) || local.Keys.Any(k => Inside(k, to)) || remoteByPath.ContainsKey(to) || remoteByPath.Keys.Any(k => Inside(k, to)) || Exists(target)))
                return new(false, $"Something named {newName} is already in {Where(parent)}.");
            if (HeldByWork(from)) return new(false, $"Armory is still working on {name}. Try again in a moment.");
            if (state.Files.Values.Any(f => Inside(f.Path, from) && (f.Inflight is not null || f.LocalMoveTo is not null)))
                return new(false, $"Armory is still sending files in {name}. Try again in a moment.");
            if (OpenUnder(from) is { } open) return new(false, $"Close {open} in SolidWorks first.");
            var holders = HeldUnder(from, ps);
            if (holders.Count > 0) return new(false, $"{name} can't be renamed now: {HoldersWords(holders, "its files")}.");
            var operation = Guid.NewGuid();
            foreach (var serverFolder in ServerFolders(from, ps))
            {
                try { await deps.Api.RenameFolderAsync(ps.Id, serverFolder, Relative(to, ps), state.DeviceId!.Value, OperationIds.Derive(operation.ToString(), serverFolder), cancellationToken); }
                catch (ArmoryOfflineException) { online = false; return Offline("Folders can be renamed once this computer is back online."); }
                catch (ArmoryRpcException error)
                {
                    deps.Log?.Invoke($"rename folder {from}: {error.Message}");
                    if (FolderRefusal.TryParse(error.Details) is { IsTargetExists: true }) return new(false, $"{ps.Name} already has a folder named {newName}.");
                    if (error.IsInUse) return new(false, $"{name} can't be renamed now: {await HoldersAsync(from, ps, cancellationToken)}.");
                    return new(false, $"Armory couldn't rename {name}. Try again in a moment.");
                }
            }
            // Then here, in one move; if that can't be done now, the next pass moves it as the
            // team's rename (file by file for anything open).
            if (FolderOnDisk(from))
            {
                var moved = fs.MoveFolder(from, to);
                if (moved.Succeeded) RekeyFolder(from, to, moveLocal: true);
                else deps.Log?.Invoke($"rename folder {from} here: {moved.Problem}");
            }
            Save();
            await PassLockedAsync(cancellationToken);
            return new(true, $"Renamed {name} to {newName}.");
        }
        finally { passGate.Release(); }
    }

    // Delete folder: for everyone first (one armory_delete_folder; refused while someone else
    // has a file in it checked out), then here: each file is kept in its history on the server
    // and moved to Armory's recovery folder, and the empty folder goes.
    public async Task<ActionResult> DeleteFolderAsync(Guid projectId, string folder, CancellationToken cancellationToken = default)
    {
        await passGate.WaitAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            if (string.IsNullOrEmpty(folder)) return new(false, "A project's top folder can't be deleted here. A mentor archives a project on ideabosco.com.");
            var path = Join(ps.Folder, folder);
            var name = Leaf(path);
            if (!FolderOnDisk(path) && !remoteByPath.Keys.Any(k => Inside(k, path))) return new(false, $"{name} isn't there any more.");
            if (HeldByWork(path)) return new(false, $"Armory is still working on {name}. Try again in a moment.");
            var notInArmory = local.Keys.Count(k => Inside(k, path) && (!state.Files.TryGetValue(k, out var s) || s.FileId is null));
            if (notInArmory > 0)
                return new(false, $"{name} has {Count(notInArmory, "file that isn't", "files that aren't")} in Armory. Move or delete {(notInArmory == 1 ? "it" : "them")} first, then delete the folder.");
            if (state.Files.Values.Any(f => Inside(f.Path, path) && (f.Inflight is not null || f.LocalMoveTo is not null)))
                return new(false, $"Armory is still sending files in {name}. Try again in a moment.");
            if (OpenUnder(path) is { } open) return new(false, $"Close {open} in SolidWorks first.");
            var holders = HeldUnder(path, ps);
            if (holders.Count > 0) return new(false, $"{name} can't be deleted now: {HoldersWords(holders, "its files")}.");
            var removed = 0;
            var operation = Guid.NewGuid();
            foreach (var serverFolder in ServerFolders(path, ps))
            {
                try { removed += await deps.Api.DeleteFolderAsync(ps.Id, serverFolder, state.DeviceId!.Value, OperationIds.Derive(operation.ToString(), serverFolder), cancellationToken); }
                catch (ArmoryOfflineException) { online = false; return Offline("Folders can be deleted once this computer is back online."); }
                catch (ArmoryRpcException error)
                {
                    deps.Log?.Invoke($"delete folder {path}: {error.Message}");
                    if (error.IsInUse) return new(false, $"{name} can't be deleted now: {await HoldersAsync(path, ps, cancellationToken)}.");
                    return new(false, $"Armory couldn't delete {name}. Try again in a moment.");
                }
            }
            if (removed == 0 && FolderOnDisk(path) && !fs.DeleteEmptyFolder(path)) return new(false, $"Armory couldn't delete {name}. Close any program using it, then try again.");
            if (removed == 0) ForgetFolder(path);
            Save();
            // Kept copies first where needed, then recovery, then the empty folder (two passes at most).
            await PassLockedAsync(cancellationToken);
            if (local.Keys.Any(k => Inside(k, path))) await PassLockedAsync(cancellationToken);
            return new(true, removed == 0 ? $"Deleted {name}." : $"Deleted {name} and its {Count(removed, "file", "files")}. Their history is kept.");
        }
        finally { passGate.Release(); }
    }

    // Add files: files and whole folders from this computer, copied in (never over anything
    // already there), then added by a pass. One import summary, however many there are.
    public async Task<ActionResult> AddFilesAsync(Guid projectId, string folder, IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) return new(false, "");
        await passGate.WaitAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureKnownAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            var target = Join(ps.Folder, folder);
            if (HeldByWork(target) || heldProjects.Contains(ps.Id)) return new(false, $"Armory is still working on {Leaf(target)}. Try again in a moment.");
            List<string> copied = [];
            int already = 0, leftOut = 0;
            void Copy(string source, string destination)
            {
                if (!VaultPath.TryCreate(destination, out var to, out _, options.VaultRoot)) { leftOut++; return; }
                var outcome = fs.CopyIn(source, to);
                if (outcome.Succeeded) { copied.Add(to.Value); return; }
                if (outcome.Problem?.StartsWith("Something named", StringComparison.Ordinal) == true) already++;
                else { leftOut++; deps.Log?.Invoke($"add {source}: {outcome.Problem}"); }
            }
            foreach (var source in sources)
            {
                if (Directory.Exists(source))
                {
                    var top = target + "/" + Path.GetFileName(source.TrimEnd('/', '\\'));
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).ToList(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { leftOut++; deps.Log?.Invoke($"add {source}: {error.Message}"); continue; }
                    foreach (var file in files) Copy(file, top + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'));
                }
                else Copy(source, target + "/" + Path.GetFileName(source));
            }
            if (copied.Count > 0)
            {
                state.Imports.Add(new ImportRecord(Guid.NewGuid(), ps.Id, target, copied, deps.Clock.GetUtcNow()));
                Save();
                await PassLockedAsync(cancellationToken);
            }
            var where = Where(target);
            var message = copied.Count > 0 ? $"Copied {Count(copied.Count, "file", "files")} into {where}." : $"Nothing was copied into {where}.";
            if (already > 0) message += $" {Count(already, "file was", "files were")} already there and {(already == 1 ? "was" : "were")} left as {(already == 1 ? "it is" : "they are")}.";
            if (leftOut > 0) message += $" {Count(leftOut, "file", "files")} couldn't be copied.";
            return new(copied.Count > 0, message);
        }
        finally { passGate.Release(); }
    }

    private static readonly ActionResult NoProject = new(false, "That project isn't on this computer.");
    private static ActionResult ArchivedAnswer(ProjectState ps) => new(false, $"{ps.Name} is archived. It no longer syncs.");
    private ProjectState? ProjectFor(Guid id) => state.Projects.TryGetValue(id, out var ps) && ps.Usable ? ps : null;

    // Files under a folder someone else (or my other computer) has checked out, as this pass read them.
    private List<RemoteLock> HeldUnder(string folder, ProjectState ps)
        => remoteById.Values.Where(r => r.Project.Id == ps.Id && !r.File.Deleted && Inside(r.Path.Value, folder) && r.File.Lock is { IsLive: true } held &&
            OwnershipOf(held) is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice).Select(r => r.File.Lock!).ToList();

    // ---- Paths -----------------------------------------------------------------------------

    // Everything this computer records under folder from now lives under to: file states,
    // pending moves and folder work, known folders and imports; with moveLocal (the engine moved
    // the folder itself), this pass's picture of the disk too.
    private void RekeyFolder(string from, string to, bool moveLocal)
    {
        string Map(string path) => to + path[from.Length..];
        foreach (var st in state.Files.Values.Where(f => Inside(f.Path, from)).ToArray())
        {
            var target = Map(st.Path);
            if (state.Files.TryGetValue(target, out var occupant) && !ReferenceEquals(occupant, st))
            {
                // A record left at the new path (a removed file's past): its saves go with the file there now.
                state.Files.Remove(target);
                foreach (var id in occupant.Entries) if (!st.Entries.Contains(id)) st.Entries.Add(id);
            }
            Rekey(st, target);
        }
        foreach (var st in state.Files.Values) if (st.LocalMoveTo is { } moving && Inside(moving, from)) st.LocalMoveTo = Map(moving);
        for (var i = 0; i < state.Moves.Count; i++)
        {
            var m = state.Moves[i];
            if (Inside(m.From, from) || Inside(m.To, from)) state.Moves[i] = m with { From = Inside(m.From, from) ? Map(m.From) : m.From, To = Inside(m.To, from) ? Map(m.To) : m.To };
        }
        for (var i = 0; i < state.FolderOps.Count; i++)
        {
            var op = state.FolderOps[i];
            if (Inside(op.LocalFrom, from) || Inside(op.LocalTo, from))
                state.FolderOps[i] = op with { LocalFrom = Inside(op.LocalFrom, from) ? Map(op.LocalFrom) : op.LocalFrom, LocalTo = Inside(op.LocalTo, from) ? Map(op.LocalTo) : op.LocalTo };
        }
        Remap(state.KnownFolders);
        Remap(missingFolders);
        foreach (var key in state.AbsentFolders.Keys.Where(k => Inside(k, from)).ToArray())
        {
            state.AbsentFolders.Remove(key, out var count);
            state.AbsentFolders[Map(key)] = count;
        }
        for (var i = 0; i < state.Imports.Count; i++)
        {
            var import = state.Imports[i];
            for (var p = 0; p < import.Paths.Count; p++) if (Inside(import.Paths[p], from)) import.Paths[p] = Map(import.Paths[p]);
            if (Inside(import.Folder, from)) state.Imports[i] = import with { Folder = Map(import.Folder) };
        }
        for (var i = 0; i < createdThisPass.Count; i++) if (Inside(createdThisPass[i], from)) createdThisPass[i] = Map(createdThisPass[i]);
        if (!moveLocal) return;
        foreach (var key in local.Keys.Where(k => Inside(k, from)).ToArray())
        {
            local.Remove(key, out var file);
            if (VaultPath.TryCreate(Map(key), out var path, out _, options.VaultRoot)) local[path.Value] = file! with { Path = path };
        }
        Remap(localFolders);
        Remap(markerDocuments);
        foreach (var key in markerFirstSeen.Keys.Where(k => Inside(k, from)).ToArray()) { markerFirstSeen.Remove(key, out var at); markerFirstSeen[Map(key)] = at; }
        foreach (var key in markerSince.Keys.Where(k => Inside(k, from)).ToArray()) { markerSince.Remove(key, out var at); markerSince[Map(key)] = at; }

        void Remap(HashSet<string> set)
        {
            var moved = set.Where(k => Inside(k, from)).ToArray();
            foreach (var key in moved) set.Remove(key);
            foreach (var key in moved) set.Add(Map(key));
        }
    }

    // A path at or under a folder (vault-relative, compared as Windows compares names).
    private static bool Inside(string path, string folder)
        => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || (path.Length > folder.Length && path[folder.Length] == '/' &&
           path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static string? Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : null;
    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Join(string projectFolder, string? inside) => string.IsNullOrEmpty(inside) ? projectFolder : projectFolder + "/" + inside.Trim('/');
    private static string Relative(string path, ProjectState ps) => path.Length > ps.Folder.Length ? path[(ps.Folder.Length + 1)..] : "";
    // "Robot 2027 › Pack › CopyDesignTemp" (addendum 7's separator).
    private static string Where(string folder) => folder.Replace("/", " › ", StringComparison.Ordinal);

    // Whether a folder is on this disk, as this pass's scan (and the engine's own moves) left it.
    // Without folders from the platform, a folder with any file under it.
    private bool FolderOnDisk(string folder) => folderScan ? localFolders.Contains(folder) : local.Keys.Any(k => Inside(k, folder) && k.Length > folder.Length);

    // The project a folder belongs to (its first segment is the project's folder).
    private ProjectState? ProjectOfFolder(string folder)
    {
        var slash = folder.IndexOf('/', StringComparison.Ordinal);
        var top = slash < 0 ? folder : folder[..slash];
        return state.Projects.Values.FirstOrDefault(p => p.Usable && string.Equals(p.Folder, top, StringComparison.OrdinalIgnoreCase));
    }

    // The first file SolidWorks (or anything) has open under a folder, for "Close X" sentences.
    private string? OpenUnder(string folder)
    {
        foreach (var document in markerDocuments) if (Inside(document, folder)) return NameOf(document);
        foreach (var file in local.Values) if (Inside(file.Path.Value, folder) && fs.IsOpen(file.Path)) return file.Path.Name;
        return null;
    }

    // Work in progress on a folder here: a rename or removal being sent, a refused rename waiting
    // to be put back, or a project folder waiting to be put back. Nothing under it is planned,
    // captured as new, or moved file by file meanwhile.
    private bool HeldByWork(string path)
    {
        foreach (var op in state.FolderOps) if (Inside(path, op.LocalFrom) || Inside(path, op.LocalTo)) return true;
        foreach (var ps in state.Projects.Values) if (ps.PutBackFrom is { } moved && Inside(path, moved)) return true;
        return false;
    }

    // Not planned this pass: held by work, in a project whose folder is gone or waiting, or in
    // a known folder that is gone (one removal for the folder, never one per file).
    private bool Held(string path)
    {
        if (HeldByWork(path)) return true;
        if (ProjectOfFolder(path) is { } ps && heldProjects.Contains(ps.Id)) return true;
        foreach (var folder in missingFolders) if (Inside(path, folder)) return true;
        return false;
    }

    // Bytes on disk not captured as a new file here: a folder waiting to go back where it was.
    private bool HeldForCapture(string path)
    {
        foreach (var op in state.FolderOps) if (op.Kind == PutBackOp && Inside(path, op.LocalTo)) return true;
        foreach (var ps in state.Projects.Values) if (ps.PutBackFrom is { } moved && Inside(path, moved)) return true;
        return false;
    }

    // A file this computer must still be able to finish in an archived project: its own check
    // out, or a check in or undo it asked for (addendum 7).
    private bool MineToFinish(FileState? st)
        => st?.FileId is { } id && (st.Request != CheckoutRequest.None || st.AutoCheckIn || st.CheckOut is not null ||
           (remoteById.TryGetValue(id, out var remote) ? !remote.File.Deleted && OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice : KnownOwnership(st) == LockOwnership.ThisDevice));
}
