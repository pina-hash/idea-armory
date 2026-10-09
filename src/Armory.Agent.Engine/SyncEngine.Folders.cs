using System.Diagnostics.CodeAnalysis;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Folders, projects and imports (docs/agent/ENGINE.md, v2-design.md 4.3, decisions D8, D16 and
// D17; contract C2 to C6). A folder renamed on this disk is ONE armory_rename_folder and a folder
// removed here ONE armory_delete_folder, never a call per file, and never over work this computer
// has not seen. A refused one is put back, with one notice that names who, from this computer's
// own lock data. Every folder this engine moves on disk is durable first (MovingFolders), so a
// stop in the middle is finished from the disk, never read as missing files and new ones. A
// project's folder follows the project's name on the site and is never made again beside a
// folder a student renamed or moved. A bulk add is one import summary.
public sealed partial class SyncEngine
{
    internal const int ImportAtLeast = 10;
    private static readonly TimeSpan ImportShownFor = TimeSpan.FromDays(1);
    // An unzip that lands over several passes is one import: new files in the folder of an import
    // that grew in the last few minutes join it, however few they are.
    private static readonly TimeSpan ImportJoinWithin = TimeSpan.FromMinutes(10);
    private const string RenameOp = "rename", DeleteOp = "delete", PutBackOp = "putBack", AppRenameOp = "appRename", AppDeleteOp = "appDelete";
    // Why a folder went back (PendingFolderOp.ReasonKind, and the notices): the card's words follow it.
    internal const string CheckedOutReason = "checkedOut", TargetExistsReason = "targetExists", OutsideProjectReason = "outsideProject",
        NewerWorkReason = "newerWork", NameInUseReason = "nameInUse";
    // What a folder move on this disk is for (MovingFolder.Kind). Each has a crash point right after
    // the move and before the records follow: "after-<kind>-folder-move".
    internal const string TeamMove = "team", AppMove = "app", ProjectMove = "project", PutBackMove = "putBack", ProjectPutBackMove = "projectPutBack";

    // Pass-scoped. Known folders gone from this scan with files the server has in them (a
    // removal waits for two scans, and nothing under them is planned file by file meanwhile).
    private readonly HashSet<string> missingFolders = new(StringComparer.OrdinalIgnoreCase);
    // Projects whose own folder is gone or waiting to be put back: nothing in them is planned.
    private readonly HashSet<Guid> heldProjects = [];
    // Projects whose own folder was removed on this disk: made again and downloaded (D16).
    private readonly HashSet<Guid> restoreProjects = [];
    // Projects a folder call changed on the server since this pass read them: read again before
    // the next folder call works out what is under a folder, and before anything is planned.
    private readonly HashSet<Guid> staleProjects = [];
    // Files this pass met for the first time (the import summary's evidence).
    private readonly List<string> createdThisPass = [];
    // The scan listed folders (VaultScan.Folders); without that, folder removals can't be seen.
    private bool folderScan;

    // ---- On this disk, before anything is captured ----------------------------------------

    // A folder move this engine was making when it stopped first, then folder renames (the files
    // keep their records, never captured again as new files at the new path), then each project's
    // own folder, then folders waiting to go back, then known folders gone from this scan.
    private void DetectFolderChanges(VaultScan scan)
    {
        missingFolders.Clear(); heldProjects.Clear(); restoreProjects.Clear();
        var finished = FinishInterruptedFolderMoves();
        if (scan.FolderMoves is { } moves)
        {
            foreach (var move in moves)
            {
                var (from, to) = (Clean(move.From), Clean(move.To));
                // This engine's own move, reported once more after a stop: already followed.
                if (finished.Contains((from, to))) continue;
                FolderMoved(from, to);
            }
        }
        else FolderMovesFromBytes();
        foreach (var ps in state.Projects.Values.Where(p => p.Usable && !p.Archived && p.Folder.Length > 0).ToArray()) CheckProjectFolder(ps);
        PutFoldersBack();
        CountMissingFolders();
        MarkDirty();
    }

    private static string Clean(string folder) => folder.Replace('\\', '/').Trim('/');

    // A folder move saved as started and not as finished: the engine stopped right around it. When
    // the disk shows the folder at its new place (and not at the old), the move happened and its
    // records follow it now, before the scan's files are read; otherwise nothing moved and the
    // work that asked for it asks again (a pending put-back, the team's rename, the site's name).
    private HashSet<(string, string)> FinishInterruptedFolderMoves()
    {
        HashSet<(string, string)> finished = [];
        if (state.MovingFolders.Count == 0) return finished;
        foreach (var move in state.MovingFolders.ToArray())
        {
            state.MovingFolders.Remove(move);
            var moved = FolderSpelledOnDisk(move.To) && (Same(move.From, move.To) || !FolderOnDisk(move.From));
            deps.Log?.Invoke($"folder move {move.From} to {move.To} ({move.Kind}) was interrupted; {(moved ? "it happened" : "it did not happen")}");
            if (!moved) continue;
            FinishFolderMove(move, moveLocal: false);
            finished.Add((move.From, move.To));
        }
        SaveNow();
        return finished;
    }

    // Every folder move this engine makes on disk: saved as started, moved, then the records follow
    // and the move is saved as finished in one save. False (and nothing changed) when the platform
    // refuses the move (something inside is open, the target exists, a path would be too long).
    private bool MoveFolderDurably(MovingFolder move)
    {
        // A file the loop carried is still on its way into it: the folder moves once it landed.
        if (CarriedUnder(move.From))
        {
            deps.Log?.Invoke($"move folder {move.From} to {move.To}: waits for files on their way into it");
            return false;
        }
        state.MovingFolders.Add(move);
        SaveNow();
        var files = local.Keys.Count(k => Inside(k, move.From));
        BeginMoving(files, move.To);
        try
        {
            var outcome = fs.MoveFolder(move.From, move.To);
            if (!outcome.Succeeded)
            {
                state.MovingFolders.Remove(move);
                SaveNow();
                deps.Log?.Invoke($"move folder {move.From} to {move.To}: {outcome.Problem}");
                return false;
            }
            CrashAt($"after-{move.Kind}-folder-move");
            state.MovingFolders.Remove(move);
            FinishFolderMove(move, moveLocal: true);
            SaveNow();
            // "Moved 120 files to Chassis" (0.3.3, feedback N8: a folder move said nothing once done).
            if (files > 0) Line($"Moved {Count(files, "file", "files")} to {Leaf(move.To)}");
            return true;
        }
        finally { EndMoving(); }
    }

    // One move operation, shown as moving ("Moving 120 files to Robot 2027 › Gearbox") from its
    // start to its end: the team's answer to a rename made here, the move on this disk and the
    // records following it. A move inside one already shown is part of it. The window hears of it
    // through the activity messages, during a pass or a window action alike.
    private int movingDepth;
    private void BeginMoving(int files, string target)
    {
        if (movingDepth++ > 0) return;
        activity.Moving(files, target);
        StartActivity();
    }

    private void EndMoving()
    {
        if (movingDepth == 0 || --movingDepth > 0) return;
        activity.Moved();
        if (!inPass) StopActivity();
    }

    // What follows a folder move, right after it or after a stop (moveLocal: this pass's picture of
    // the disk moves too; after a stop the scan already shows the new place).
    private void FinishFolderMove(MovingFolder move, bool moveLocal)
    {
        var ps = move.ProjectId is { } id ? state.Projects.GetValueOrDefault(id) : null;
        PendingFolderOp? putBack = null;
        switch (move.Kind)
        {
            case AppMove:
                state.FolderOps.RemoveAll(o => o.Kind == AppRenameOp && Same(o.LocalFrom, move.From) && Same(o.LocalTo, move.To));
                break;
            case PutBackMove:
                putBack = state.FolderOps.FirstOrDefault(o => o.Kind == PutBackOp && Same(o.LocalTo, move.From) && Same(o.LocalFrom, move.To));
                if (putBack is not null) state.FolderOps.Remove(putBack);
                break;
        }
        if (move.RecordsStay) RekeyDisk(move.From, move.To, moveLocal);
        else RekeyFolder(move.From, move.To, moveLocal);
        switch (move.Kind)
        {
            case ProjectMove when ps is not null:
                ps.Folder = move.To;
                break;
            case PutBackMove when putBack is not null:
                RememberPutBack(putBack);
                break;
            case ProjectPutBackMove when ps is not null:
                ps.PutBackFrom = null;
                // Renamed at the top of the Armory folder, or moved into another folder.
                if (Parent(move.From) is null)
                    Remember(NoticeKinds.ProjectPutBack, null, move.To, $"The {move.To} folder was renamed back", ProjectNamesWords);
                else
                    Remember(NoticeKinds.ProjectPutBack, null, move.To, $"The {move.To} folder was moved back",
                        $"A project's folder stays at the top of your Armory folder, and its files stay in their project. {ProjectNamesWords}");
                break;
        }
        lastActivity = deps.Clock.GetUtcNow();
    }

    // One directory move the platform proved (in the platform's order: each From is the path
    // after the earlier moves).
    private void FolderMoved(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0) return;
        // A folder waiting to go back, moved again: it goes back from where it is now.
        var waiting = false;
        foreach (var ps in state.Projects.Values)
        {
            if (ps.PutBackFrom is not { } moved || !Inside(moved, from)) continue;
            ps.PutBackFrom = to + moved[from.Length..];
            waiting |= Same(moved, from);
        }
        for (var i = 0; i < state.FolderOps.Count; i++)
        {
            if (state.FolderOps[i] is not { Kind: PutBackOp } op || !Same(op.LocalTo, from)) continue;
            if (op.AtHome) state.FolderOps[i] = op with { LocalTo = to };
            else RekeyFolder(from, to, moveLocal: false); // its records follow the disk, and the put-back with them
            waiting = true;
        }
        if (waiting) { SaveNow(); return; }
        var source = ProjectOfFolder(from);
        if (source is null || source.Archived) return; // not a project this computer syncs, or archived (left as it is)
        if (Same(from, source.Folder))
        {
            // The project's own folder (decision D16): put back, never followed, wherever it went
            // (into another project's folder too: its files never join that project).
            source.PutBackFrom = to;
            SaveNow();
            return;
        }
        var target = ProjectOfFolder(to);
        if (target?.Id != source.Id || Same(to, target.Folder))
        {
            // Out of its project (into another one, or to the top of the Armory folder). A folder
            // with files the team has is put back: a folder moves only inside its project. A folder
            // of files Armory doesn't have yet goes where it was moved.
            if (HoldsTeamFiles(from) || (target is not null && RekeyCollides(from, to)))
            {
                state.FolderOps.Add(new PendingFolderOp(PutBackOp, Guid.NewGuid(), source.Id, from, to, "a folder can only move inside its project",
                    OutsideProjectReason, AtHome: true));
                SaveNow();
            }
            else NewFilesLeftTheProject(from, to, target);
            return;
        }
        StartFolderRename(source, from, to); // MUTATION: a directory rename is one folder move
    }

    // A folder with a file the server has, or might have (a create in flight).
    private bool HoldsTeamFiles(string folder) => state.Files.Values.Any(f => Inside(f.Path, folder) && (f.FileId is not null || f.Inflight is not null));

    // Files Armory doesn't have yet, in a folder moved to another project or out of every project:
    // the move stands. In another project they are added there; outside every project they are
    // outside Armory, and the saves already kept of them stay on this computer only.
    private void NewFilesLeftTheProject(string from, string to, ProjectState? target)
    {
        if (target is { Archived: false })
        {
            RekeyFolder(from, to, moveLocal: false);
            foreach (var st in state.Files.Values.Where(f => Inside(f.Path, to)))
            {
                st.ProjectId = target.Id;
                st.CreateEntry = null; st.Refusal = null; st.RefusalKind = null;
            }
            for (var i = 0; i < state.Imports.Count; i++)
                if (Inside(state.Imports[i].Folder, to)) state.Imports[i] = state.Imports[i] with { ProjectId = target.Id };
        }
        else
        {
            foreach (var st in state.Files.Values.Where(f => Inside(f.Path, from)).ToArray())
            {
                foreach (var entry in st.Entries.Concat(st.Drafts)) state.Completed.Add(entry);
                state.Files.Remove(st.Path);
            }
            state.Imports.RemoveAll(i => Inside(i.Folder, from));
        }
        SaveNow();
    }

    // A folder renamed (or moved inside its project) on this disk: its records follow the disk
    // at once, and armory_rename_folder is sent once, durable first (SendFolderOpsAsync). A name
    // whose old files still have records here (a folder removed here and not yet removed for the
    // team) is not taken: the folder goes back, and no file's record or saves pass to another.
    private void StartFolderRename(ProjectState ps, string from, string to)
    {
        if (RekeyCollides(from, to))
        {
            state.FolderOps.Add(new PendingFolderOp(PutBackOp, Guid.NewGuid(), ps.Id, from, to, $"{Leaf(to)} still has files in Armory", NameInUseReason, AtHome: true));
            SaveNow();
            return;
        }
        var op = new PendingFolderOp(RenameOp, OperationIds.Derive(NextId("folder"), RenameOp), ps.Id, from, to);
        RekeyFolder(from, to, moveLocal: false);
        state.FolderOps.Add(op);
        SaveNow();
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
            if (ps is null || ps.Archived || Same(known, ps.Folder) || !FolderOnDisk(ps.Folder)) continue;
            if (FolderMovedByBytes(known, ps) is { } to) FolderMoved(known, to);
        }
    }

    // Where the files of a folder that is gone are now, by their bytes: the one folder that holds
    // every one of them at the same relative path, untracked. Null when there is no such folder.
    // A project's own folder (topLevel) is looked for anywhere outside its project, never at
    // another project's own folder.
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
            if (topLevel) candidates.RemoveAll(c => ProjectOfFolder(c)?.Id == ps.Id || state.Projects.Values.Any(p => Same(p.Folder, c)));
            else candidates.RemoveAll(c => ProjectOfFolder(c)?.Id != ps.Id || Same(c, ps.Folder));
            if (candidates.Count != 1 || (found is not null && !Same(found, candidates[0]))) return null;
            found = candidates[0];
        }
        return found;
    }

    // Folders waiting to go back where they were (a refused rename, a folder moved out of its
    // project, a name still taken): moved back as soon as nothing inside is open, with one notice.
    private void PutFoldersBack()
    {
        foreach (var op in state.FolderOps.Where(o => o.Kind == PutBackOp).ToArray()) TryPutBack(op);
    }

    private void TryPutBack(PendingFolderOp op)
    {
        var name = Leaf(op.LocalFrom);
        if (!FolderOnDisk(op.LocalTo))
        {
            // Nothing is left to move back here.
            state.FolderOps.Remove(op);
            if (!op.AtHome)
            {
                // The student moved it back themselves: its records follow.
                if (FolderOnDisk(op.LocalFrom)) RekeyFolder(op.LocalTo, op.LocalFrom, moveLocal: false);
            }
            else if (!FolderOnDisk(op.LocalFrom) && HoldsTeamFiles(op.LocalFrom))
            {
                // Moved out of its project and then gone from there: never a removal for the team.
                // Its files come back where they belong.
                RestoreFolder(op.LocalFrom, op.ReasonKind, op.Reason, op.Who);
            }
            SaveNow();
            return;
        }
        var collides = !op.AtHome && RekeyCollides(op.LocalTo, op.LocalFrom);
        if (!collides && MoveFolderDurably(new MovingFolder(PutBackMove, op.LocalTo, op.LocalFrom, op.ProjectId, op.AtHome))) return;
        // Waiting: the title says it has not gone back yet, and what it waits for.
        var open = OpenUnder(op.LocalTo);
        var taken = collides || (!Same(op.LocalFrom, op.LocalTo) && FolderOnDisk(op.LocalFrom));
        var title = (open is not null ? $"{name} goes back once {open} is closed" : taken ? $"{name} can't go back yet" : $"{name} is waiting to go back") +
            (op.Reason is { } reason ? ": " + reason : "");
        var detail = (open is not null ? $"Close {open} so Armory can put it back where it was."
            : taken ? $"Something named {name} is already in {Where(Parent(op.LocalFrom) ?? op.LocalFrom)}. Rename or move it, and Armory puts {Leaf(op.LocalTo)} back."
            : "Armory tries again by itself.") + " " + PutBackWhy(op.ReasonKind);
        Notice(NoticeKinds.FolderPutBack, null, op.LocalTo, detail, title, ItemOf(op.Reason), op.ReasonKind, op.Who);
    }

    private void RememberPutBack(PendingFolderOp op)
    {
        var name = Leaf(op.LocalFrom);
        Remember(NoticeKinds.FolderPutBack, null, op.LocalFrom, op.Reason is { } reason ? $"{name} was put back: {reason}." : $"{name} was put back where it was",
            PutBackWhy(op.ReasonKind), ItemOf(op.Reason), op.ReasonKind, op.Who);
    }

    // A put-back item's own sentence in a card of several: its reason, as a sentence.
    private static string ItemOf(string? reason) => reason is null ? "It was put back where it was." : char.ToUpperInvariant(reason[0]) + reason[1..] + ".";

    private static string PutBackWhy(string? kind) => kind switch
    {
        CheckedOutReason => "A folder is renamed or deleted only when nobody else has a file in it checked out. Ask them to check the files in, then try again.",
        OutsideProjectReason => "Folders can be renamed and moved inside a project, never into another one.",
        TargetExistsReason => "Choose another name, or rename the other folder first.",
        NameInUseReason => "A folder can take a name once the files that had it are gone from Armory. Try again in a moment.",
        _ => "Armory couldn't rename it for the team. Try again later.",
    };

    // A project's own folder (decision D16). Renamed or moved in Explorer: moved back, "Project
    // names are changed on ideabosco.com."; removed: made again and downloaded once online. Never
    // a second folder beside a renamed one, and never a removal of the project's files.
    private void CheckProjectFolder(ProjectState ps)
    {
        if (ps.PutBackFrom is { } moved)
        {
            if (FolderOnDisk(ps.Folder) || !FolderOnDisk(moved)) ps.PutBackFrom = null; // put back by hand, or gone
            else
            {
                if (MoveFolderDurably(new MovingFolder(ProjectPutBackMove, moved, ps.Folder, ps.Id, RecordsStay: true))) return;
                heldProjects.Add(ps.Id);
                var renamed = Parent(moved) is null;
                Notice(NoticeKinds.ProjectPutBack, null, moved,
                    $"{(OpenUnder(moved) is { } open ? $"Close {open}" : "Close the files in it")} so Armory can {(renamed ? $"rename {moved} back to {ps.Folder}" : $"move {ps.Folder} back to the top of your Armory folder")}. {ProjectNamesWords}",
                    $"The {ps.Folder} folder is waiting to be {(renamed ? "renamed" : "moved")} back");
                return;
            }
        }
        if (FolderOnDisk(ps.Folder) || !HadFiles(ps)) return;
        // Gone from this scan. Renamed or moved without a proven move: found by its files' bytes.
        if (FolderMovedByBytes(ps.Folder, ps, topLevel: true) is { } found)
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
        SaveNow();
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

    // Folder work made on this disk or in the window, oldest first: one server call each, with
    // the operation id persisted before it. Each is worked out from the server as it is after the
    // ones before it (read again after every call), and a project whose operation could not be
    // sent waits with the rest of its operations, so they always go in the order they happened.
    private async Task SendFolderOpsAsync(CancellationToken ct)
    {
        HashSet<Guid> waiting = [];
        foreach (var op in state.FolderOps.Where(o => o.Kind is RenameOp or DeleteOp or AppRenameOp or AppDeleteOp).ToArray())
        {
            if (online != true) return;
            if (!state.FolderOps.Contains(op) || waiting.Contains(op.ProjectId)) continue;
            var ps = state.Projects.GetValueOrDefault(op.ProjectId);
            if (ps is null || !ps.Usable || ps.Archived) continue;
            try
            {
                switch (op.Kind)
                {
                    case RenameOp: await SendFolderRenameAsync(op, ps, ct); break;
                    case DeleteOp: await SendFolderDeleteAsync(op, ps, ct); break;
                    default:
                        // The window's rename or delete, asked before a stop or a lost answer:
                        // finished now with the same operation id (the server answers a call that
                        // went through from its receipt).
                        var answer = op.Kind == AppRenameOp ? await SendAppRenameAsync(op, ps, ct) : await SendAppDeleteAsync(op, ps, ct);
                        if (!answer.Ok) Remember(NoticeKinds.CantSend, null, op.LocalFrom, $"Armory couldn't {(op.Kind == AppRenameOp ? "rename" : "delete")} {Leaf(op.LocalFrom)}", answer.Message);
                        break;
                }
            }
            catch (ArmoryOfflineException) { online = false; return; }
            catch (ArmoryClientException error)
            {
                // Not an answer about the folder (the server could not be asked properly): the
                // same operation id is sent again on the next pass, before anything after it.
                deps.Log?.Invoke($"folder {op.Kind} {op.LocalFrom}: {error.Message}");
                waiting.Add(op.ProjectId);
            }
        }
    }

    private async Task SendFolderRenameAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        await FreshAsync(ps, ct);
        var folders = ServerFolders(op.LocalFrom, ps);
        if (folders.Count == 0)
        {
            // Nothing is on the server under the old name (read just now): sent already (its
            // answer was lost), never in Armory, or the team renamed the folder first.
            TeamRenamedItFirst(op, ps);
            state.FolderOps.Remove(op);
            SaveNow();
            return;
        }
        var to = Relative(op.LocalTo, ps);
        foreach (var from in folders)
        {
            ArmoryRpcException? refused = null;
            // The team's files move with this one call: shown as moving until it answers.
            BeginMoving(local.Keys.Count(k => Inside(k, op.LocalTo)), op.LocalTo);
            try
            {
                CrashAt("before-folder");
                wrote = true;
                staleProjects.Add(ps.Id);
                await deps.Api.RenameFolderAsync(ps.Id, from, to, state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), from), ct);
                CrashAt("after-folder");
            }
            catch (ArmoryRpcException error) when (!error.IsTransient) { refused = error; }
            finally { EndMoving(); }
            if (refused is not null)
            {
                // Refused (someone else has a file in it checked out, or the project already has
                // that folder): put back where it was, with one notice naming who.
                var (reason, kind, who) = await RenameRefusalAsync(refused, op, ps, ct);
                var putBack = new PendingFolderOp(PutBackOp, op.Operation, op.ProjectId, op.LocalFrom, op.LocalTo, reason, kind, who);
                state.FolderOps[state.FolderOps.IndexOf(op)] = putBack;
                SaveNow();
                TryPutBack(putBack);
                return;
            }
        }
        state.FolderOps.Remove(op);
        lastActivity = deps.Clock.GetUtcNow();
        SaveNow();
    }

    private async Task<(string? Reason, string? Kind, string? Who)> RenameRefusalAsync(ArmoryRpcException error, PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        deps.Log?.Invoke($"rename folder {op.LocalFrom} refused: {error.Message}");
        if (FolderRefusal.TryParse(error.Details) is { IsTargetExists: true })
            return ($"{ps.Name} already has a folder named {Leaf(op.LocalTo)}", TargetExistsReason, null);
        if (error.IsInUse)
        {
            var (text, who) = await HoldersAsync(op.LocalFrom, ps, ct);
            return (text, CheckedOutReason, who);
        }
        return (null, null, null);
    }

    // A rename made here whose folder the team renamed first (its files are on the server under
    // one other folder now): the whole folder here goes to the team's name, files Armory doesn't
    // have yet included, as the team's rename (ApplyRemoteFolderMoves), with one notice.
    private void TeamRenamedItFirst(PendingFolderOp op, ProjectState ps)
    {
        string? teams = null;
        foreach (var st in state.Files.Values)
        {
            if (!Inside(st.Path, op.LocalTo) || st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) continue;
            if (PrefixChange(st.Path, remote.Path.Value) is not { } change || !Same(change.From, op.LocalTo) || (teams is not null && !Same(teams, change.To))) return;
            teams = change.To;
        }
        if (teams is null) return;
        state.RemoteFolderRenames.Add(new RemoteFolderRename(ps.Id, Relative(op.LocalTo, ps), Relative(teams, ps)));
        Remember(NoticeKinds.FolderPutBack, null, teams, $"{Leaf(op.LocalTo)} is now {Leaf(teams)}: someone renamed {Leaf(op.LocalFrom)} first",
            "Your rename didn't go through, so Armory moved the folder to the team's name with everything in it. Nothing was lost.",
            $"Someone renamed {Leaf(op.LocalFrom)} to {Leaf(teams)} first.");
    }

    private async Task SendFolderDeleteAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        await FreshAsync(ps, ct);
        // Looked at once more right before the removal goes to the whole team: still gone here...
        var tracked = state.Files.Values.Where(f => Inside(f.Path, op.LocalFrom) && f.FileId is { } id &&
            (!remoteById.TryGetValue(id, out var remote) || Inside(remote.Path.Value, op.LocalFrom))).ToList();
        if (tracked.Any(f => VaultPath.TryCreate(f.Path, out var p, out _, options.VaultRoot) && Exists(p)))
        {
            state.FolderOps.Remove(op);
            state.AbsentFolders.Remove(op.LocalFrom);
            SaveNow();
            return;
        }
        // ...and every file the team has in it now is one this computer had, as the team has it.
        // A folder gone from this disk never removes work this computer hasn't seen (a newer
        // version, a file it never had): the folder comes back instead, with one notice naming
        // who. (Core's rule for one file, that a removal of a file changed since is put back,
        // holds for a folder too.)
        var newer = remoteById.Values.Where(r => r.Project.Id == ps.Id && !r.File.Deleted && Inside(r.Path.Value, op.LocalFrom) && !HadAsTheTeamHasIt(r.File))
            .Select(r => r.File).ToList();
        if (newer.Count > 0)
        {
            state.FolderOps.Remove(op);
            var (text, who) = NewerWorkWords(newer);
            RestoreFolder(op.LocalFrom, NewerWorkReason, text, who);
            return;
        }
        foreach (var folder in ServerFolders(op.LocalFrom, ps))
        {
            try
            {
                CrashAt("before-folder");
                wrote = true;
                staleProjects.Add(ps.Id);
                await deps.Api.DeleteFolderAsync(ps.Id, folder, state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), folder), ct);
                CrashAt("after-folder");
            }
            catch (ArmoryRpcException error) when (!error.IsTransient)
            {
                // Refused: the folder is made again and its files downloaded, with one notice.
                deps.Log?.Invoke($"delete folder {op.LocalFrom} refused: {error.Message}");
                var (text, who) = error.IsInUse ? await HoldersAsync(op.LocalFrom, ps, ct) : (null, null);
                state.FolderOps.Remove(op);
                RestoreFolder(op.LocalFrom, error.IsInUse ? CheckedOutReason : null, text, who);
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
        SaveNow();
    }

    // This computer has the file as the team has it now: its record's base is the file's current version.
    private bool HadAsTheTeamHasIt(RemoteFile file)
        => file.Current is { } current && state.WithFileId(file.Id).Any(f => f.BaseId == current.Id.ToString());

    // "Maria Lopez has newer work in it", "Maria Lopez and Sam Lee have newer work in it".
    private (string Text, string? Who) NewerWorkWords(IReadOnlyCollection<RemoteFile> newer)
    {
        var authors = newer.Select(f => f.Current?.Author).OfType<string>().ToList();
        var others = authors.Where(a => !string.Equals(a, state.Email, StringComparison.OrdinalIgnoreCase))
            .GroupBy(DisplayName, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        if (others.Count == 0)
            return authors.Count > 0 ? ("you have newer work in it from another computer", null) : ("someone else has newer work in it", null);
        return ($"{Names(others)} {(others.Count == 1 ? "has" : "have")} newer work in it", string.Join('\n', others));
    }

    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and {names[2]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2:N0} others",
    };

    // A folder that can't be removed for the team: made again here, and every file the server
    // still has in it comes down again (Core plans a download once the record forgets its base).
    private void RestoreFolder(string folder, string? reasonKind, string? reason, string? who)
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
        var name = Leaf(folder);
        Remember(NoticeKinds.FolderPutBack, null, folder, reason is null ? $"{name} was put back" : $"{name} was put back: {reason}.",
            reasonKind switch
            {
                CheckedOutReason => "A folder is renamed or deleted only when nobody else has a file in it checked out. Its files are coming back now. Ask them to check the files in, then try again.",
                NewerWorkReason => "Armory removes a folder for the team only when this computer has every file in it as the team has it now. Its files are coming back now. Delete the folder again if it should still go.",
                OutsideProjectReason => "It was moved out of its project and then went missing, so its files are coming back where they belong.",
                _ => "Armory couldn't remove it for the team, so its files are coming back. Try again later.",
            }, ItemOf(reason), reasonKind, who);
        SaveNow();
    }

    // Who has files under a folder checked out, from this computer's own lock data (the
    // project's files as the server lists them, read again when this pass's copy names nobody).
    private async Task<(string Text, string? Who)> HoldersAsync(string folder, ProjectState ps, CancellationToken ct)
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
    // checked out", "you have 1 of its files checked out on LAB-PC-07"; Who is the people's names,
    // one per line.
    private (string Text, string? Who) HoldersWords(IReadOnlyCollection<RemoteLock> locks, string what)
    {
        if (locks.Count == 0) return ($"someone else has some of {what} checked out", null);
        var people = locks.Where(l => OwnershipOf(l) == LockOwnership.OtherPerson).ToList();
        if (people.Count == 0)
        {
            var devices = locks.Select(l => l.HolderDeviceName ?? "another computer").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return ($"you have {locks.Count:N0} of {what} checked out on {string.Join(" and ", devices)}", null);
        }
        var names = people.GroupBy(l => DisplayName(l.HolderEmail), StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        return ($"{Names(names)} {(names.Count == 1 ? "has" : "have")} {locks.Count:N0} of {what} checked out", string.Join('\n', names));
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

    // The project's files as the server has them now, when a folder call changed them since this
    // pass read them.
    private async Task FreshAsync(ProjectState ps, CancellationToken ct)
    {
        if (!staleProjects.Contains(ps.Id) && remoteProjects.ContainsKey(ps.Id)) return;
        var files = await deps.Api.ProjectFilesAsync(ps.Id, ct);
        KnowProject(ps, files);
        RememberHolders();
        PublishRemote();
    }

    // Every project a folder call changed, read again (only those: one call each, never the
    // whole refresh). False when the connection dropped.
    private async Task<bool> RefreshStaleAsync(CancellationToken ct)
    {
        if (staleProjects.Count == 0) return true;
        try
        {
            foreach (var id in staleProjects.ToArray())
                if (state.Projects.TryGetValue(id, out var ps)) await FreshAsync(ps, ct);
                else staleProjects.Remove(id);
            MarkDirty();
            return true;
        }
        catch (ArmoryOfflineException) { return false; }
    }

    // Known folders that are gone from the disk for a second scan, each the top of what is gone,
    // with every file the server has there missing too (looked at again right now): ONE
    // armory_delete_folder each. Unsent saves under them were kept already this pass
    // (ArchiveSupersededAsync).
    private async Task RemoveMissingFoldersAsync(CancellationToken ct)
    {
        foreach (var folder in missingFolders.OrderBy(f => f.Length).ToArray())
        {
            if (online != true) break;
            if (!missingFolders.Contains(folder)) continue; // went with a folder above it
            if (missingFolders.Any(m => !Same(m, folder) && Inside(folder, m))) continue;
            if (state.AbsentFolders.GetValueOrDefault(folder) < 2 || HeldByWork(folder) || FencedUnder(folder)) continue;
            var ps = ProjectOfFolder(folder);
            if (ps is null || ps.Archived) continue;
            var tracked = state.Files.Values.Where(f => Inside(f.Path, folder) && f.FileId is { } id && f.BaseHash is not null &&
                remoteById.TryGetValue(id, out var r) && !r.File.Deleted).ToList();
            if (tracked.Count == 0) continue;
            // Moved out of it on their own (an Explorer move each), or still being sent: not yet.
            if (tracked.Any(f => f.LocalMoveTo is not null || f.Inflight is not null || state.Moves.Any(m => m.FileId == f.FileId))) continue;
            if (tracked.Any(f => VaultPath.TryCreate(f.Path, out var p, out _, options.VaultRoot) && Exists(p))) continue;
            var op = new PendingFolderOp(DeleteOp, OperationIds.Derive(NextId("folder"), DeleteOp), ps.Id, folder, folder);
            state.FolderOps.Add(op);
            SaveNow();
            try { await SendFolderDeleteAsync(op, ps, ct); }
            catch (ArmoryOfflineException) { online = false; break; }
            catch (ArmoryClientException error) { deps.Log?.Invoke($"folder delete {folder}: {error.Message}"); }
        }
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
        if (any) MarkDirty();
        return any;
    }

    private bool TryMoveRemoteFolder(string from, string to, bool everyFile)
    {
        var source = localFolders.FirstOrDefault(f => Same(f, from));
        // A carried unit still writing in it: the folder moves on a pass after that unit ends.
        if (source is null || HeldByWork(source) || HeldByWork(to) || FencedUnder(source)) return false;
        var ps = ProjectOfFolder(source);
        if (ps is null || ProjectOfFolder(to)?.Id != ps.Id || Same(source, ps.Folder)) return false;
        var moving = 0;
        foreach (var st in state.Files.Values.Where(f => Inside(f.Path, source)))
        {
            if (st.FileId is not { } id || !remoteById.TryGetValue(id, out var remote) || remote.File.Deleted) continue; // removed ones go along, then to recovery
            if (!Same(remote.Path.Value, to + st.Path[source.Length..])) return false; // not one folder move
            moving++;
        }
        if (moving == 0) return false;
        // Without the server saying so, files of the student's own in it mean it is not simply
        // the team's folder renamed: each file moves on its own.
        if (everyFile && local.Keys.Any(k => Inside(k, source) && (!state.Files.TryGetValue(k, out var s) || s.FileId is null))) return false;
        if (!Same(source, to) && FolderOnDisk(to)) return false;
        if (RekeyCollides(source, to)) return false;
        return MoveFolderDurably(new MovingFolder(TeamMove, source, to, ps.Id));
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
        // Files the loop carried are still on their way into it: the next read of the server
        // moves it, once they have landed.
        if (CarriedUnder(from)) return;
        if (state.Projects.Values.Any(p => !ReferenceEquals(p, ps) && p.Usable && Same(p.Folder, to)))
        {
            Notice(NoticeKinds.ProjectRenaming, null, from, $"Another project still uses the folder {to}. Armory renames {from} as soon as it can.", $"{from} is now {to} on ideabosco.com");
            return;
        }
        if (!FolderOnDisk(from))
        {
            // Nothing here to move: the records follow the new name.
            RekeyFolder(from, to, moveLocal: true);
            ps.Folder = to;
            SaveNow();
            return;
        }
        if (MoveFolderDurably(new MovingFolder(ProjectMove, from, to, ps.Id))) return;
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
    }

    // ---- After the plans: known and empty folders (decision D17) ---------------------------

    // Folders that hold files the server has become known; a known folder with nothing left in
    // it (no file here, no live file on the server, nothing waiting, no folder a student made) is
    // removed, on every computer. Folders a student makes, empty or not, stay, and so does every
    // known folder they are in.
    private void TidyFolders()
    {
        foreach (var (file, project, path) in remoteById.Values)
        {
            if (file.Deleted || project.Archived || !project.Usable || !local.ContainsKey(path.Value)) continue;
            for (var folder = Parent(path.Value); folder is not null && !Same(folder, project.Folder); folder = Parent(folder))
                if (!state.KnownFolders.Add(folder)) break;
        }
        // Every folder that still holds something: a file here, a live file on the server, a file
        // with work waiting (an unsent save, a write in flight, a request), or a folder that is
        // not Armory's (a student made it, in the app or in File Explorer).
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Busy(string path) { for (var folder = Parent(path); folder is not null && busy.Add(folder); folder = Parent(folder)) { } }
        foreach (var remote in remoteById.Values) if (!remote.File.Deleted) Busy(remote.Path.Value);
        foreach (var key in local.Keys) Busy(key);
        foreach (var st in state.Files.Values)
            if (st.Inflight is not null || st.Entries.Count > 0 || st.Drafts.Count > 0 || st.LocalMoveTo is not null || st.CheckOut is not null || st.Request != CheckoutRequest.None)
                Busy(st.Path);
        foreach (var folder in localFolders) if (!state.KnownFolders.Contains(folder)) Busy(folder + "/");
        foreach (var folder in state.KnownFolders.OrderByDescending(f => f.Length).ToArray())
        {
            var ps = ProjectOfFolder(folder);
            if (ps is null || ps.Archived || heldProjects.Contains(ps.Id) || HeldByWork(folder) || missingFolders.Contains(folder) || FencedUnder(folder)) continue;
            if (Same(folder, ps.Folder)) { state.KnownFolders.Remove(folder); continue; }
            if (busy.Contains(folder) || !fs.DeleteEmptyFolder(folder)) continue;
            ForgetFolder(folder);
            lastActivity = deps.Clock.GetUtcNow();
        }
        MarkDirty();
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
    // import summary, however many there are. New files in the folder of an import that grew a
    // few minutes ago join it (an unzip seen over several passes is one import).
    private void DetectImports()
    {
        if (createdThisPass.Count == 0) return;
        var now = deps.Clock.GetUtcNow();
        var imported = state.Imports.SelectMany(i => i.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<string>();
        foreach (var path in createdThisPass)
            if (!imported.Contains(path) && state.Files.TryGetValue(path, out var st) && st.FileId is null && st.BaseId is null &&
                local.ContainsKey(path) && !remoteByPath.ContainsKey(path))
                fresh.Add(path);
        if (fresh.Count == 0) return;
        var changed = false;
        for (var i = 0; i < state.Imports.Count && fresh.Count > 0; i++)
        {
            var import = state.Imports[i];
            if (now - import.At > ImportJoinWithin) continue;
            var joining = fresh.Where(p => state.Files[p].ProjectId == import.ProjectId && Inside(p, import.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (joining.Count == 0) continue;
            state.Imports[i] = import with { Paths = [.. import.Paths, .. fresh.Where(joining.Contains)], At = now };
            fresh.RemoveAll(joining.Contains);
            changed = true;
        }
        if (fresh.Count >= ImportAtLeast)
        {
            var created = createdThisPass.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Folders that held anything before this pass.
            var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in state.Files.Keys)
            {
                if (created.Contains(key)) continue;
                for (var folder = Parent(key); folder is not null && occupied.Add(folder); folder = Parent(folder)) { }
            }
            var groups = new Dictionary<string, (Guid Project, List<string> Paths)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in fresh)
            {
                var root = ImportRoot(path, occupied);
                if (!groups.TryGetValue(root, out var group)) groups[root] = group = (state.Files[path].ProjectId, []);
                group.Paths.Add(path);
            }
            foreach (var (root, (project, paths)) in groups)
            {
                if (paths.Count < ImportAtLeast) continue;
                state.Imports.Add(new ImportRecord(Guid.NewGuid(), project, root, paths, now));
                changed = true;
            }
        }
        if (changed) MarkDirty();
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

    // What an import came to now: in Armory, sharing a name, still on its way, refused. A file
    // removed since, or gone from this disk with nothing left to send, is no longer part of it,
    // so an import whose folder was deleted shows nothing waiting.
    private (int Added, int Shared, int Waiting, int Other, int Total) ImportTally(ImportRecord import)
    {
        int added = 0, shared = 0, waiting = 0, other = 0;
        foreach (var path in import.Paths)
        {
            state.Files.TryGetValue(path, out var st);
            var here = TryLocal(path, out _);
            if (st?.FileId is not null && st.BaseHash is not null) added++;
            else if (st?.Base is { IsTombstone: true }) continue;
            else if (!here && (st is null || (st.FileId is null && (st.RefusalKind == NameTakenKind || (st.Inflight is null && st.Entries.Count == 0))))) continue;
            else if (st?.RefusalKind == NameTakenKind) shared++;
            else if (st?.Refusal is not null) other++;
            else waiting++;
        }
        return (added, shared, waiting, other, added + shared + waiting + other);
    }

    // ---- The window's folder actions (v2-design.md 4.3) ------------------------------------

    // New folder: made on this computer (the server keeps no empty folders; the folder is the
    // team's as soon as a file is in it).
    public async Task<ActionResult> CreateFolderAsync(Guid projectId, string parent, string name, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => CreateFolderAsync(projectId, parent, name, cancellationToken));
        name = name?.Trim() ?? "";
        if (!VaultPath.TryValidateName(name, out var problem)) return new(false, problem ?? "That name can't be used for a folder.");
        Working($"Making the folder {name}");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureScannedAsync(cancellationToken);
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
            for (var f = folder.Value; f is not null && !Same(f, ps.Folder); f = Parent(f)) localFolders.Add(f);
            PublishLocked();
            return new(true, $"Made the folder {name} in {Where(inside)}.");
        }
        finally { LeaveAction(); }
    }

    // Rename folder: for everyone first (one armory_rename_folder, refused while someone else has
    // a file in it checked out), then on this computer (one move). Durable before the call: a lost
    // answer is asked again with the same id and finished here from the same record.
    public async Task<ActionResult> RenameFolderAsync(Guid projectId, string folder, string newName, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => RenameFolderAsync(projectId, folder, newName, cancellationToken));
        newName = newName?.Trim() ?? "";
        if (!VaultPath.TryValidateName(newName, out var problem)) return new(false, problem ?? "That name can't be used for a folder.");
        if (!string.IsNullOrEmpty(folder)) Working($"Renaming {Leaf(folder)} to {newName}");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureScannedAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            if (string.IsNullOrEmpty(folder)) return new(false, ProjectNamesWords);
            var from = Join(ps.Folder, folder);
            var name = Leaf(from);
            var parent = Parent(from)!;
            if (!VaultPath.TryCreate(parent + "/" + newName, out var target, out var tooLong, options.VaultRoot)) return new(false, tooLong ?? "That name can't be used here.");
            var to = target.Value;
            if (string.Equals(from, to, StringComparison.Ordinal)) return new(false, "That is already its name.");
            var caseOnly = Same(from, to);
            if (!FolderOnDisk(from) && !remoteByPath.Keys.Any(k => Inside(k, from))) return new(false, $"{name} isn't there any more.");
            if (!caseOnly && (FolderOnDisk(to) || local.ContainsKey(to) || local.Keys.Any(k => Inside(k, to)) || remoteByPath.ContainsKey(to) || remoteByPath.Keys.Any(k => Inside(k, to)) || Exists(target)))
                return new(false, $"Something named {newName} is already in {Where(parent)}.");
            // The files on their way into it from the loop's transfers land first.
            await AwaitCarriedAsync(entry => entry.Unit.Any(p => Inside(p.Key, from)));
            if (HeldByWork(from)) return new(false, $"Armory is still working on {name}. Try again in a moment.");
            if (state.Files.Values.Any(f => Inside(f.Path, from) && (f.Inflight is not null || f.LocalMoveTo is not null)))
                return new(false, $"Armory is still sending files in {name}. Try again in a moment.");
            if (await OpenUnderAsync(from, cancellationToken) is { } open) return new(false, $"Close {open} in SolidWorks first.");
            var holders = HeldUnder(from, ps);
            if (holders.Count > 0) return new(false, $"{name} can't be renamed now: {HoldersWords(holders, "its files").Text}.");
            if (!caseOnly && RekeyCollides(from, to)) return new(false, $"{newName} still has files in Armory. Try again in a moment.");
            if (online != true) return Offline("Folders can be renamed once this computer is back online.");
            var op = new PendingFolderOp(AppRenameOp, OperationIds.Derive(NextId("folder"), AppRenameOp), ps.Id, from, to);
            state.FolderOps.Add(op);
            SaveNow();
            ActionResult answer;
            try { answer = await SendAppRenameAsync(op, ps, cancellationToken); }
            catch (ArmoryOfflineException)
            {
                online = false;
                PublishLocked();
                return new(true, $"You're offline. Armory renames {name} to {newName} as soon as this computer is back online.");
            }
            catch (ArmoryClientException error)
            {
                deps.Log?.Invoke($"rename folder {from}: {error.Message}");
                return new(true, $"Armory couldn't reach the server to rename {name}. It tries again by itself.");
            }
            if (answer.Ok) await PassLockedAsync(cancellationToken, PassScope.Under(to, from));
            return answer;
        }
        finally { LeaveAction(); }
    }

    // The window's rename, sent (or sent again with its own id) and then finished here in one
    // move, files Armory doesn't have yet included. If the move can't be made now, the next pass
    // moves the folder as the team's rename (file by file for anything open).
    private async Task<ActionResult> SendAppRenameAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        var name = Leaf(op.LocalFrom);
        var newName = Leaf(op.LocalTo);
        await FreshAsync(ps, ct);
        // The team's files move with the call, then the folder here: one operation, shown as
        // moving from the call to the end of the move here.
        BeginMoving(local.Keys.Count(k => Inside(k, op.LocalFrom)), op.LocalTo);
        try
        {
            foreach (var serverFolder in ServerFolders(op.LocalFrom, ps))
            {
                try
                {
                    CrashAt("before-folder");
                    wrote = true;
                    staleProjects.Add(ps.Id);
                    await deps.Api.RenameFolderAsync(ps.Id, serverFolder, Relative(op.LocalTo, ps), state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), serverFolder), ct);
                    CrashAt("after-folder");
                }
                catch (ArmoryRpcException error) when (!error.IsTransient)
                {
                    deps.Log?.Invoke($"rename folder {op.LocalFrom}: {error.Message}");
                    state.FolderOps.Remove(op);
                    SaveNow();
                    if (FolderRefusal.TryParse(error.Details) is { IsTargetExists: true }) return new(false, $"{ps.Name} already has a folder named {newName}.");
                    if (error.IsInUse) return new(false, $"{name} can't be renamed now: {(await HoldersAsync(op.LocalFrom, ps, ct)).Text}.");
                    return new(false, $"Armory couldn't rename {name}. Try again in a moment.");
                }
            }
            if (!FolderOnDisk(op.LocalFrom) || (!Same(op.LocalFrom, op.LocalTo) && RekeyCollides(op.LocalFrom, op.LocalTo)) ||
                !MoveFolderDurably(new MovingFolder(AppMove, op.LocalFrom, op.LocalTo, ps.Id)))
            {
                state.FolderOps.Remove(op);
                SaveNow();
            }
        }
        finally { EndMoving(); }
        lastActivity = deps.Clock.GetUtcNow();
        return new(true, $"Renamed {name} to {newName}.");
    }

    // Delete folder: for everyone first (one armory_delete_folder; refused while someone else
    // has a file in it checked out), then here: each file is kept in its history on the server
    // and moved to Armory's recovery folder, and the empty folder goes. Durable before the call,
    // like Rename folder.
    public async Task<ActionResult> DeleteFolderAsync(Guid projectId, string folder, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => DeleteFolderAsync(projectId, folder, cancellationToken));
        // "Deleting Gearbox (120 files)", with the team's files in it as last read.
        var inside = !string.IsNullOrEmpty(folder) && ProjectFor(projectId) is { } shown
            ? remoteById.Values.Count(r => !r.File.Deleted && Inside(r.Path.Value, Join(shown.Folder, folder))) : 0;
        if (!string.IsNullOrEmpty(folder)) Working(inside > 0 ? $"Deleting {Leaf(folder)} ({Count(inside, "file", "files")})" : $"Deleting the folder {Leaf(folder)}");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureScannedAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            if (string.IsNullOrEmpty(folder)) return new(false, "A project's top folder can't be deleted here. A mentor archives a project on ideabosco.com.");
            var path = Join(ps.Folder, folder);
            var name = Leaf(path);
            if (!FolderOnDisk(path) && !remoteByPath.Keys.Any(k => Inside(k, path))) return new(false, $"{name} isn't there any more.");
            // The files on their way into it from the loop's transfers land first.
            await AwaitCarriedAsync(entry => entry.Unit.Any(p => Inside(p.Key, path)));
            if (HeldByWork(path)) return new(false, $"Armory is still working on {name}. Try again in a moment.");
            var notInArmory = local.Keys.Count(k => Inside(k, path) && (!state.Files.TryGetValue(k, out var s) || s.FileId is null));
            if (notInArmory > 0)
                return new(false, $"{name} has {Count(notInArmory, "file that isn't", "files that aren't")} in Armory. Move or delete {(notInArmory == 1 ? "it" : "them")} first, then delete the folder.");
            if (state.Files.Values.Any(f => Inside(f.Path, path) && (f.Inflight is not null || f.LocalMoveTo is not null)))
                return new(false, $"Armory is still sending files in {name}. Try again in a moment.");
            if (await OpenUnderAsync(path, cancellationToken) is { } open) return new(false, $"Close {open} in SolidWorks first.");
            var holders = HeldUnder(path, ps);
            if (holders.Count > 0) return new(false, $"{name} can't be deleted now: {HoldersWords(holders, "its files").Text}.");
            if (online != true) return Offline("Folders can be deleted once this computer is back online.");
            var op = new PendingFolderOp(AppDeleteOp, OperationIds.Derive(NextId("folder"), AppDeleteOp), ps.Id, path, path);
            state.FolderOps.Add(op);
            SaveNow();
            ActionResult answer;
            try { answer = await SendAppDeleteAsync(op, ps, cancellationToken); }
            catch (ArmoryOfflineException)
            {
                online = false;
                PublishLocked();
                return new(true, $"You're offline. Armory deletes {name} as soon as this computer is back online.");
            }
            catch (ArmoryClientException error)
            {
                deps.Log?.Invoke($"delete folder {path}: {error.Message}");
                return new(true, $"Armory couldn't reach the server to delete {name}. It tries again by itself.");
            }
            if (!answer.Ok) return answer;
            // Nothing of it on this computer but empty folders: gone at once.
            if (FolderOnDisk(path) && !local.Keys.Any(k => Inside(k, path)) && fs.DeleteEmptyFolder(path)) ForgetFolder(path);
            SaveNow();
            // Kept copies first where needed, then recovery, then the empty folder (two passes at most).
            await PassLockedAsync(cancellationToken, PassScope.Under(path));
            if (local.Keys.Any(k => Inside(k, path))) await PassLockedAsync(cancellationToken, PassScope.Under(path));
            return Said(answer, many: true);
        }
        finally { LeaveAction(); }
    }

    // The window's delete, sent (or sent again with its own id). Its folder, and every folder in
    // it, is then Armory's to remove here once its files are aside (the student asked for all of it).
    private async Task<ActionResult> SendAppDeleteAsync(PendingFolderOp op, ProjectState ps, CancellationToken ct)
    {
        var name = Leaf(op.LocalFrom);
        await FreshAsync(ps, ct);
        var removed = 0;
        foreach (var serverFolder in ServerFolders(op.LocalFrom, ps))
        {
            try
            {
                CrashAt("before-folder");
                wrote = true;
                staleProjects.Add(ps.Id);
                removed += await deps.Api.DeleteFolderAsync(ps.Id, serverFolder, state.DeviceId!.Value, OperationIds.Derive(op.Operation.ToString(), serverFolder), ct);
                CrashAt("after-folder");
            }
            catch (ArmoryRpcException error) when (!error.IsTransient)
            {
                deps.Log?.Invoke($"delete folder {op.LocalFrom}: {error.Message}");
                state.FolderOps.Remove(op);
                SaveNow();
                if (error.IsInUse) return new(false, $"{name} can't be deleted now: {(await HoldersAsync(op.LocalFrom, ps, ct)).Text}.");
                return new(false, $"Armory couldn't delete {name}. Try again in a moment.");
            }
        }
        state.KnownFolders.Add(op.LocalFrom);
        foreach (var inside in localFolders.Where(f => Inside(f, op.LocalFrom))) state.KnownFolders.Add(inside);
        state.FolderOps.Remove(op);
        lastActivity = deps.Clock.GetUtcNow();
        SaveNow();
        return new(true, removed == 0 ? $"Deleted {name}." : $"Deleted {name} and its {Count(removed, "file", "files")}. Their history is kept.");
    }

    // Add files: files and whole folders from this computer, copied in (never over anything
    // already there), then added by a pass. One import summary, however many there are.
    public async Task<ActionResult> AddFilesAsync(Guid projectId, string folder, IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => AddFilesAsync(projectId, folder, sources, cancellationToken));
        if (sources.Count == 0) return new(false, "");
        Working(sources.Count == 1 ? $"Adding {Path.GetFileName(sources[0].TrimEnd('/', '\\'))}" : $"Adding {Count(sources.Count, "file or folder", "files and folders")}");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureScannedAsync(cancellationToken);
            if (ProjectFor(projectId) is not { } ps) return NoProject;
            if (ps.Archived) return ArchivedAnswer(ps);
            var target = Join(ps.Folder, folder);
            if (HeldByWork(target) || heldProjects.Contains(ps.Id)) return new(false, $"Armory is still working on {Leaf(target)}. Try again in a moment.");
            List<string> copied = [];
            int already = 0, leftOut = 0;
            // Every file to copy first, so the running lines can count them ("Copying 300 of
            // 1,000 files into Intake", 0.3.3: a big add said nothing while it copied).
            List<(string Source, string Destination)> toCopy = [];
            foreach (var source in sources)
            {
                if (Directory.Exists(source))
                {
                    var top = target + "/" + Path.GetFileName(source.TrimEnd('/', '\\'));
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).ToList(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { leftOut++; deps.Log?.Invoke($"add {source}: {error.Message}"); continue; }
                    foreach (var file in files) toCopy.Add((file, top + "/" + Path.GetRelativePath(source, file).Replace('\\', '/')));
                }
                else toCopy.Add((source, target + "/" + Path.GetFileName(source)));
            }
            var copying = new Tally(this, toCopy.Count, done => $"Copying {done:N0} of {Count(toCopy.Count, "file", "files")} into {Leaf(target)}");
            foreach (var (source, destination) in toCopy)
            {
                Copy(source, destination);
                copying.One();
            }
            void Copy(string source, string destination)
            {
                if (!VaultPath.TryCreate(destination, out var to, out _, options.VaultRoot)) { leftOut++; return; }
                var outcome = fs.CopyIn(source, to);
                if (outcome.Succeeded) { copied.Add(to.Value); return; }
                if (outcome.Problem?.StartsWith("Something named", StringComparison.Ordinal) == true) already++;
                else { leftOut++; deps.Log?.Invoke($"add {source}: {outcome.Problem}"); }
            }
            if (copied.Count > 0)
            {
                state.Imports.Add(new ImportRecord(Guid.NewGuid(), ps.Id, target, copied, deps.Clock.GetUtcNow()));
                SaveNow();
                await PassLockedAsync(cancellationToken, PassScope.Under([.. copied]));
            }
            var where = Where(target);
            var message = copied.Count > 0 ? $"Copied {Count(copied.Count, "file", "files")} into {where}." : $"Nothing was copied into {where}.";
            if (already > 0) message += $" {Count(already, "file was", "files were")} already there and {(already == 1 ? "was" : "were")} left as {(already == 1 ? "it is" : "they are")}.";
            if (leftOut > 0) message += $" {Count(leftOut, "file", "files")} couldn't be copied.";
            return Said(new(copied.Count > 0, message), many: toCopy.Count > 1);
        }
        finally { LeaveAction(); }
    }

    private static readonly ActionResult NoProject = new(false, "That project isn't on this computer.");
    // The page's own words for an archived project (decision D5 keeps "sync" out of the window).
    private static ActionResult ArchivedAnswer(ProjectState ps) => new(false, $"{ps.Name} is archived. It no longer updates.");
    private ProjectState? ProjectFor(Guid id) => state.Projects.TryGetValue(id, out var ps) && ps.Usable ? ps : null;

    // Files under a folder someone else (or my other computer) has checked out, as this pass read them.
    private List<RemoteLock> HeldUnder(string folder, ProjectState ps)
        => remoteById.Values.Where(r => r.Project.Id == ps.Id && !r.File.Deleted && Inside(r.Path.Value, folder) && r.File.Lock is { IsLive: true } held &&
            OwnershipOf(held) is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice).Select(r => r.File.Lock!).ToList();

    // ---- Paths -----------------------------------------------------------------------------

    // Everything this computer records under folder from now lives under to: file states,
    // pending moves, folders waiting to go back, known folders and imports; with moveLocal (the
    // engine moved the folder itself), this pass's picture of the disk too. A rename or removal
    // still to be sent keeps the paths it had when it happened (they are sent in order).
    private void RekeyFolder(string from, string to, bool moveLocal)
    {
        string Map(string path) => to + path[from.Length..];
        foreach (var st in state.Files.Values.Where(f => Inside(f.Path, from)).ToArray())
        {
            var target = Map(st.Path);
            if (state.Files.TryGetValue(target, out var occupant) && !ReferenceEquals(occupant, st))
            {
                // Only a record with nothing to keep gives way (a removed file's past, a name
                // never added). Callers put a folder back before it could take another file's
                // record (RekeyCollides), and one file's saves never pass to another.
                if (MustKeep(occupant))
                {
                    deps.Log?.Invoke($"kept the record at {occupant.Path}; {st.Path} stays where it was");
                    continue;
                }
                state.Files.Remove(target);
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
            if (op.Kind == PutBackOp && (Inside(op.LocalFrom, from) || Inside(op.LocalTo, from)))
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
            // An import summary is replaced, never changed in place (the state document writes each once).
            var import = state.Imports[i];
            if (!Inside(import.Folder, from) && !import.Paths.Any(p => Inside(p, from))) continue;
            state.Imports[i] = import with
            {
                Folder = Inside(import.Folder, from) ? Map(import.Folder) : import.Folder,
                Paths = import.Paths.Select(p => Inside(p, from) ? Map(p) : p).ToList(),
            };
        }
        for (var i = 0; i < createdThisPass.Count; i++) if (Inside(createdThisPass[i], from)) createdThisPass[i] = Map(createdThisPass[i]);
        RekeyDisk(from, to, moveLocal);

        void Remap(HashSet<string> set)
        {
            var moved = set.Where(k => Inside(k, from)).ToArray();
            foreach (var key in moved) set.Remove(key);
            foreach (var key in moved) set.Add(Map(key));
        }
    }

    // This pass's picture of the disk after the engine moved a folder itself.
    private void RekeyDisk(string from, string to, bool moveLocal)
    {
        if (!moveLocal) return;
        string Map(string path) => to + path[from.Length..];
        foreach (var key in local.Keys.Where(k => Inside(k, from)).ToArray())
        {
            local.Remove(key, out var file);
            if (VaultPath.TryCreate(Map(key), out var path, out _, options.VaultRoot)) local[path.Value] = file! with { Path = path };
        }
        foreach (var set in new[] { localFolders, markerDocuments })
        {
            var moved = set.Where(k => Inside(k, from)).ToArray();
            foreach (var key in moved) set.Remove(key);
            foreach (var key in moved) set.Add(Map(key));
        }
        foreach (var key in markerFirstSeen.Keys.Where(k => Inside(k, from)).ToArray()) { markerFirstSeen.Remove(key, out var at); markerFirstSeen[Map(key)] = at; }
        foreach (var key in markerSince.Keys.Where(k => Inside(k, from)).ToArray()) { markerSince.Remove(key, out var at); markerSince[Map(key)] = at; }
    }

    // A record that must never give way to another file's at its path: it has saves or work
    // waiting, or it is a file the server still has.
    private bool MustKeep(FileState st)
    {
        if (st.Entries.Count > 0 || st.Drafts.Count > 0 || st.Inflight is not null || st.CheckOut is not null || st.Request != CheckoutRequest.None ||
            st.AutoCheckIn || st.TransientLock || st.LocalMoveTo is not null || st.DeleteEntry is not null) return true;
        if (st.FileId is not { } id) return false;
        return remoteById.TryGetValue(id, out var remote) ? !remote.File.Deleted : st.Base is not { IsTombstone: true };
    }

    // Moving the records under from to under to would land one on a record that must be kept.
    private bool RekeyCollides(string from, string to)
    {
        foreach (var st in state.Files.Values)
        {
            if (!Inside(st.Path, from)) continue;
            if (state.Files.TryGetValue(to + st.Path[from.Length..], out var occupant) && !ReferenceEquals(occupant, st) && !Inside(occupant.Path, from) && MustKeep(occupant))
                return true;
        }
        return false;
    }

    // A path at or under a folder (vault-relative, compared as Windows compares names).
    private static bool Inside(string path, string folder)
        => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || (path.Length > folder.Length && path[folder.Length] == '/' &&
           path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : null;
    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Join(string projectFolder, string? inside) => string.IsNullOrEmpty(inside) ? projectFolder : projectFolder + "/" + inside.Trim('/');
    private static string Relative(string path, ProjectState ps) => path.Length > ps.Folder.Length ? path[(ps.Folder.Length + 1)..] : "";
    // "Robot 2027 › Pack › CopyDesignTemp" (addendum 7's separator).
    private static string Where(string folder) => folder.Replace("/", " › ", StringComparison.Ordinal);

    // Whether a folder is on this disk, as this pass's scan (and the engine's own moves) left it.
    // Without folders from the platform, a folder with any file under it.
    private bool FolderOnDisk(string folder) => folderScan ? localFolders.Contains(folder) : local.Keys.Any(k => Inside(k, folder) && k.Length > folder.Length);

    // The same with this exact spelling (a case-only rename).
    private bool FolderSpelledOnDisk(string folder)
        => folderScan ? localFolders.Any(f => string.Equals(f, folder, StringComparison.Ordinal)) : local.Keys.Any(k => k.StartsWith(folder + "/", StringComparison.Ordinal));

    // The project a folder belongs to (its first segment is the project's folder).
    private ProjectState? ProjectOfFolder(string folder)
    {
        var slash = folder.IndexOf('/', StringComparison.Ordinal);
        var top = slash < 0 ? folder : folder[..slash];
        return state.Projects.Values.FirstOrDefault(p => p.Usable && Same(p.Folder, top));
    }

    // The first file SolidWorks (or anything) has open under a folder, for "Close X" sentences.
    private string? OpenUnder(string folder)
    {
        foreach (var document in markerDocuments) if (Inside(document, folder)) return NameOf(document);
        var inside = local.Values.Where(f => Inside(f.Path.Value, folder)).Select(f => f.Path).ToList();
        var open = inside.Count == 0 ? null : fs.OpenAmong(inside);
        foreach (var path in inside) if (open!.Contains(path.Value)) return path.Name;
        return null;
    }

    // The same for a window action: a glance, off the engine thread and within a short budget
    // (0.3.3); the folder's move asks again (MoveFolder refuses a folder with a file open), and so
    // does each file's own move.
    private async Task<string?> OpenUnderAsync(string folder, CancellationToken ct)
    {
        foreach (var document in markerDocuments) if (Inside(document, folder)) return NameOf(document);
        var inside = local.Values.Where(f => Inside(f.Path.Value, folder)).Select(f => f.Path).ToList();
        var open = await AskOpenAsync(inside, ct, budget: GlanceBudget);
        foreach (var path in inside) if (OpenIn(open, path)) return path.Name;
        return null;
    }

    // Work in progress on a folder here: a rename or removal being sent, a folder waiting to be
    // put back, or a project folder waiting to be put back. Nothing under it is planned or moved
    // file by file meanwhile.
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

    // A folder on its way back where it was: a file Armory knows there keeps every save (under
    // the path it goes back to), and a new file there waits until the folder is back.
    private bool HeldForCapture(string path)
    {
        foreach (var op in state.FolderOps) if (op.Kind == PutBackOp && Inside(path, op.LocalTo)) return true;
        foreach (var ps in state.Projects.Values) if (ps.PutBackFrom is { } moved && Inside(path, moved)) return true;
        return false;
    }

    // Folders on their way back whose records stayed at home while the folder sits elsewhere on
    // this disk (moved out of its project, a project's own folder, a name still taken).
    private IEnumerable<(string Home, string Disk)> AwayFolders()
    {
        foreach (var op in state.FolderOps) if (op is { Kind: PutBackOp, AtHome: true }) yield return (op.LocalFrom, op.LocalTo);
        foreach (var ps in state.Projects.Values) if (ps.PutBackFrom is { } moved) yield return (ps.Folder, moved);
    }

    // A record's file on this disk: at its own path, or, while its folder is away, at the same
    // place in the folder where it is now. A record whose own spot is taken by a folder that is
    // away has nothing on this disk.
    private bool TryLocal(string recordPath, [MaybeNullWhen(false)] out LocalFile file)
    {
        foreach (var (home, disk) in AwayFolders())
            if (Inside(recordPath, home)) return local.TryGetValue(disk + recordPath[home.Length..], out file);
        foreach (var (_, disk) in AwayFolders())
            if (Inside(recordPath, disk)) { file = null; return false; }
        return local.TryGetValue(recordPath, out file);
    }

    // The record path of a file on disk in a folder that is away (null when it is not in one).
    private string? HomeOf(string diskPath)
    {
        foreach (var (home, disk) in AwayFolders()) if (Inside(diskPath, disk)) return home + diskPath[disk.Length..];
        return null;
    }

    // A file this computer must still be able to finish in an archived project: its own check
    // out, or a check in or undo it asked for (addendum 7).
    private bool MineToFinish(FileState? st)
        => st?.FileId is { } id && (st.Request != CheckoutRequest.None || st.AutoCheckIn || st.CheckOut is not null ||
           (remoteById.TryGetValue(id, out var remote) ? !remote.File.Deleted && OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice : KnownOwnership(st) == LockOwnership.ThisDevice));
}
