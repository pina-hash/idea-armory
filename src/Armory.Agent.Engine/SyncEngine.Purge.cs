using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// v0.3 (ARMORY.md, "The v0.3 server contract", "What the Windows app must do"): a membership that
// ended, and Delete forever.
//
// - A project gone from armory_my_projects, or answering "not a project member" (P0001 from
//   armory_list_changes, armory_acquire_lock and armory_save_side_version; 42501 from
//   armory_project_files, armory_file_history, armory_create_file and armory_move_file, treated
//   alike), is asked about once: armory_project_purged.
//   - A time: it was deleted forever. Its records go, unsent saves included (never sent: there is
//     nothing left on the server to send them to), its files go to Armory's recovery folder once
//     closed (moved, never deleted), then its folder and the project itself. One line says so.
//   - Null: this person was removed. As before 0.3: not synced, its folder left as it is. Asked
//     again once per start, in case it is deleted forever later.
// - A folder_purged change ({folder, files, file_ids, by}): those files and their history are
//   gone. Their records go the same way, quietly (the log says so), and nothing of them is ever
//   taken for local work to upload again.
public sealed partial class SyncEngine
{
    // Projects that answered "not a project member" since the last read of the server.
    private readonly HashSet<Guid> notMember = [];
    // Projects asked about with armory_project_purged since this start.
    private readonly HashSet<Guid> purgeAsked = [];

    // A refusal that means "no longer a member of this project" is remembered, and the loop
    // woken so the next read of the server asks about the project.
    private void NoteNotMember(Guid? project, Exception error)
    {
        if (error is not ArmoryRpcException { IsNotMember: true } refusal || project is not { } id || id == Guid.Empty) return;
        if (!notMember.Add(id)) return;
        deps.Log?.Invoke($"membership: {state.Projects.GetValueOrDefault(id)?.Name ?? id.ToString()} answered \"{refusal.Message}\" ({refusal.SqlState}); the next read asks whether it was deleted forever");
        wake.Release();
    }

    // A project no longer listed, or answering "not a project member": deleted forever, or this
    // person removed? Asked once per disappearance (and once per start for a removed one).
    private async Task CheckGoneAsync(ProjectState ps, CancellationToken ct)
    {
        ps.Usable = false;
        if (ps.PurgedAt is not null || !purgeAsked.Add(ps.Id)) return;
        DateTimeOffset? purged;
        try { purged = await deps.Api.ProjectPurgedAsync(ps.Id, ct); }
        catch (ArmoryRpcException error)
        {
            // A site older than 0233 (404 PGRST202), or a refusal: handled as removed, as before 0.3.
            deps.Log?.Invoke($"membership: armory_project_purged for {ps.Name} answered {error.SqlState} ({error.Message}); handled as removed");
            purged = null;
        }
        catch (ArmoryOfflineException)
        {
            purgeAsked.Remove(ps.Id); // asked again once back online
            throw;
        }
        if (purged is { } at) Purge(ps, at);
        else if (!ps.Departed)
        {
            ps.Departed = true;
            deps.Log?.Invoke($"membership: no longer a member of {ps.Name} ({ps.Id}); it is no longer synced, and its folder stays as it is");
        }
        MarkDirty();
    }

    // Deleted forever: everything of the project here goes (DropPurged finishes it, pass by pass).
    private void Purge(ProjectState ps, DateTimeOffset at)
    {
        ps.PurgedAt = at;
        ps.Usable = false;
        ps.Departed = false;
        var records = 0;
        HashSet<Guid> ids = [];
        foreach (var st in state.Files.Values.Where(f => f.ProjectId == ps.Id).ToArray())
        {
            if (st.FileId is { } id) ids.Add(id);
            MarkPurged(st);
            records++;
        }
        state.Moves.RemoveAll(m => ids.Contains(m.FileId));
        state.FolderOps.RemoveAll(op => op.ProjectId == ps.Id);
        state.RemoteFolderRenames.RemoveAll(r => r.ProjectId == ps.Id);
        state.MovingFolders.RemoveAll(m => m.ProjectId == ps.Id);
        state.Imports.RemoveAll(i => i.ProjectId == ps.Id);
        foreach (var id in ids) { state.Revivals.Remove(id); openWhenHere.Remove(id); }
        ForgetProject(ps.Id);
        staleProjects.Remove(ps.Id);
        projectsWritten.Remove(ps.Id);
        heldProjects.Remove(ps.Id);
        restoreProjects.Remove(ps.Id);
        Remember(NoticeKinds.ProjectDeleted, null, ps.Folder, $"{ps.Name} was deleted forever on ideabosco.com, so Armory took it off this computer.", "");
        deps.Log?.Invoke($"project deleted forever: {ps.Name} ({ps.Id}) at {at:O}; {records} records here and its folder leave this computer (files go to the recovery folder)");
        MarkDirty();
    }

    // folder_purged: those files and their history are gone from the server.
    private void FolderPurged(ProjectState ps, FolderPurge purge)
    {
        var records = 0;
        foreach (var id in purge.FileIds)
        {
            foreach (var st in state.WithFileId(id)) { MarkPurged(st); records++; }
            state.Moves.RemoveAll(m => m.FileId == id);
            state.Revivals.Remove(id);
            openWhenHere.Remove(id);
        }
        deps.Log?.Invoke($"folder deleted forever: {ps.Name} › {(purge.Folder.Length == 0 ? "(top folder)" : purge.Folder)}, {purge.Files} files" +
            $"{(purge.By is null ? "" : " by " + purge.By)}; {records} records here leave this computer");
        MarkDirty();
    }

    // Never planned or sent again: unsent saves are done with (there is nothing left on the server
    // to send them to), and every request on the file is over.
    private void MarkPurged(FileState st)
    {
        if (st.Purged) return;
        st.Purged = true;
        foreach (var entry in st.Entries.Concat(st.Drafts)) state.Completed.Add(entry);
        st.Entries.Clear();
        st.Drafts.Clear();
        st.Inflight = null;
        st.CheckOut = null;
        st.Request = CheckoutRequest.None;
        st.AutoCheckIn = false;
        st.TransientLock = false;
        st.Refusal = null;
        st.RefusalKind = null;
        st.NewerWaiting = false;
        st.RemovedWaiting = false;
        st.BreakNotice = false;
        st.Holder = null;
        st.LocalMoveTo = null;
        MarkDirty();
    }

    // Every pass, after the server was read (and offline too): what was deleted forever leaves
    // this computer. A purged record's copy goes to Armory's recovery folder once it is closed,
    // then the record goes. A project deleted forever leaves with every file in its folder (each
    // to the recovery folder, closed ones first), then its empty folders, then the project.
    private void DropPurged()
    {
        foreach (var st in state.Files.Values.Where(f => f.Purged).ToArray())
        {
            if (TryLocal(st.Path, out var file) && !LeaveForRecovery(file)) continue;
            state.Files.Remove(st.Path);
            MarkDirty();
        }
        foreach (var ps in state.Projects.Values.Where(p => p.PurgedAt is not null).ToArray())
        {
            var left = false;
            if (ps.Folder.Length > 0)
                foreach (var file in local.Values.Where(f => Inside(f.Path.Value, ps.Folder)).ToArray())
                    if (!LeaveForRecovery(file)) left = true;
            if (left || state.Files.Values.Any(f => f.ProjectId == ps.Id)) continue;
            if (ps.Folder.Length > 0 && FolderOnDisk(ps.Folder) && !fs.DeleteEmptyFolder(ps.Folder)) continue;
            if (ps.Folder.Length > 0)
            {
                state.KnownFolders.RemoveWhere(f => Inside(f, ps.Folder));
                foreach (var gone in state.AbsentFolders.Keys.Where(f => Inside(f, ps.Folder)).ToArray()) state.AbsentFolders.Remove(gone);
                localFolders.RemoveWhere(f => Inside(f, ps.Folder));
            }
            state.Projects.Remove(ps.Id);
            deps.Log?.Invoke($"project deleted forever: {ps.Name} is off this computer");
            MarkDirty();
        }
    }

    // A file of something deleted forever, out of the vault into Armory's recovery folder (moved,
    // never deleted), once nothing has it open. False while it stays.
    private bool LeaveForRecovery(LocalFile file)
    {
        if (IsOpenNow(file.Path)) return false;
        var moved = fs.MoveToRecovery(file.Path, file.Hash);
        if (!moved.Succeeded)
        {
            deps.Log?.Invoke($"deleted forever: {file.Path} stays for now ({moved.Problem})");
            return false;
        }
        local.Remove(file.Path.Value);
        return true;
    }

    // A file on disk in the folder of a project deleted forever (on its way to the recovery folder).
    private bool InPurgedProject(string path)
        => state.Projects.Values.Any(p => p.PurgedAt is not null && p.Folder.Length > 0 && Inside(path, p.Folder));
}
