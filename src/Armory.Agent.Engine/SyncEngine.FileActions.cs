using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

public sealed partial class SyncEngine
{
    private readonly Dictionary<Guid, HashSet<string>> sharedFolders = [];
    private readonly Dictionary<Guid, (string Folder, long At)> sharedFolderReads = [];

    private async Task ReadSharedFoldersAsync(ProjectState project, CancellationToken ct)
    {
        if (sharedFolderReads.TryGetValue(project.Id, out var last) && Same(last.Folder, project.Folder) &&
            deps.Clock.GetElapsedTime(last.At) < options.FilesSafetyRefresh) return;
        if (await deps.Api.FolderPathsAsync(project.Id, ct) is not { } paths) return;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in paths)
        {
            if (!VaultPath.TryCreate(project.Folder + "/" + relative, out var folder, out var problem, options.VaultRoot))
            {
                Problem(NoticeKinds.CantRead, project.Folder, "A shared folder's name can't be used on this computer.", problem ?? relative);
                continue;
            }
            for (var name = folder.Value; name is not null && !Same(name, project.Folder); name = Parent(name))
            {
                known.Add(name);
                state.KnownFolders.Add(name);
            }
        }
        sharedFolders[project.Id] = known;
        sharedFolderReads[project.Id] = (project.Folder, deps.Clock.GetTimestamp());
    }

    private void EnsureSharedFolders()
    {
        foreach (var folder in sharedFolders.Where(p => state.Projects.TryGetValue(p.Key, out var project) && project.Usable && !project.Archived)
            .SelectMany(p => p.Value).OrderBy(p => p.Length))
        {
            if (localFolders.Contains(folder)) continue;
            try { fs.EnsureFolder(folder); localFolders.Add(folder); }
            catch (IOException error) { Problem(NoticeKinds.CantRead, folder, "Armory couldn't make a shared folder here yet.", error.Message); }
        }
    }

    // The website's authorization decides. The tombstone reconciler preserves changed
    // bytes and moves closed copies to recovery, never deleting a local copy on an RPC ack.
    public async Task<ActionResult> TrashFileAsync(string relativePath, Guid? expectedFileId = null, CancellationToken cancellationToken = default)
    {
        if (!engineThread.IsCurrent) return await engineThread.InvokeAsync(() => TrashFileAsync(relativePath, expectedFileId, cancellationToken));
        if (!VaultPath.TryCreate(relativePath, out var path, out var problem, options.VaultRoot)) return new(false, problem ?? "Choose a file in Armory.");
        await EnterActionAsync(cancellationToken);
        try
        {
            if (Unready() is { } why) return why;
            await EnsureScannedAsync(cancellationToken);
            await AwaitCarriedAsync(entry => entry.Unit.Any(p => Same(p.Key, path.Value)));
            if (!await RefreshAsync(cancellationToken)) return Offline("Move to Trash once this computer is back online.");
            if (!remoteByPath.TryGetValue(path.Value, out var known) || known.File.Deleted) return new(false, "That file is no longer in Armory.");
            if (expectedFileId is { } selected && known.File.Id != selected)
                return new(false, "That file moved or changed. Open its page again before moving it to Trash.");
            if (known.Project.Archived) return ArchivedAnswer(known.Project);
            if (IsOpenNow(path)) return new(false, $"Close {path.Name} in SolidWorks first.");
            if (OwnershipOf(known.File.Lock) != LockOwnership.Free) return new(false, "Check in this file before moving it to Trash.");
            if (HeldByWork(path.Value) || state.Files.TryGetValue(path.Value, out var st) &&
                (st.Inflight is not null || st.Entries.Count > 0 || st.LocalMoveTo is not null))
                return new(false, "Armory is still saving this file. Wait for it to finish, then try again.");
            try { await deps.Api.TrashFileAsync(known.File.Id, false, Guid.NewGuid(), cancellationToken); }
            catch (ArmoryRpcException error) when (error.IsFunctionMissing) { return new(false, "The website needs an update before files can move to Trash."); }
            catch (ArmoryRpcException error) { return new(false, error.Message); }
            projectsWritten.Add(known.Project.Id);
            await PassLockedAsync(cancellationToken);
            await PassLockedAsync(cancellationToken);
            return new(true, $"Moved {path.Name} to Trash for everyone. Restore it within 30 days from Trash on the project's website. Local copies are preserved and move to recovery when ready.");
        }
        catch (Exception error) when (Stopped(error))
        { return Offline("Armory couldn't confirm the move to Trash. Look again when this computer is online before trying again. Your work stays safe."); }
        finally { LeaveAction(); }
    }
}
