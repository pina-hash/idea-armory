using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Builds the window's AgentView (docs/agent/BRIDGE.md) in plain student language.
public sealed partial class SyncEngine
{
    private AgentView BuildView()
    {
        var session = deps.Sessions.Current;
        var connection = session is null
            ? (connectPhase is "waitingForBrowser" or "finishing" ? Connections.Connecting : Connections.SignedOut)
            : state.Email is not null && !string.Equals(state.Email, session.Email, StringComparison.OrdinalIgnoreCase) ? Connections.VaultOwnedByOther
            : Connections.SignedIn;
        var account = session is null ? null : new AccountView(session.Email, session.DeviceName);
        var files = state.Files.Values.ToArray();
        var pending = files.Count(f => f.Entries.Count > 0 || f.Inflight is not null || (local.TryGetValue(f.Path, out var l) && l.Hash != f.BaseHash && l.Hash != f.Preserved));
        var needsMe = NeedsMe(files);
        var sync = paused ? new SyncView(SyncStates.Paused, "Syncing is paused. Your saves wait on this computer.", PendingDetail(pending), pending)
            : online == false ? new SyncView(SyncStates.Offline, "You're offline. Your work is safe on this computer.", PendingDetail(pending) ?? LastChecked(), pending)
            : syncing ? new SyncView(SyncStates.Syncing, "Checking for changes and sending your saves.", PendingDetail(pending), pending)
            : needsMe.Any(n => n.Kind is AttentionKinds.Refused or AttentionKinds.NameTaken or AttentionKinds.NewerWaiting or AttentionKinds.LockBroken)
                ? new SyncView(SyncStates.Attention, "Everything else is saved. A few files need you.", LastChecked(), pending)
            : pending > 0 ? new SyncView(SyncStates.Syncing, "Sending your saves to Armory.", PendingDetail(pending), pending)
            : new SyncView(SyncStates.Synced, "Everything is saved to Armory.", LastChecked(), 0);
        return new AgentView(connection, new ConnectView(connectPhase, connectMessage), account, sync, options.VaultRoot,
            MyFiles(files), needsMe, Projects(files), settings, effectiveTheme);
    }

    private string? PendingDetail(int pending)
        => pending == 0 ? null : pending == 1 ? "1 file is waiting to send." : $"{pending} files are waiting to send.";
    private string? LastChecked() => lastOnline is { } at ? "Last checked " + Relative(at) + "." : null;
    private string Relative(DateTimeOffset at)
    {
        var age = deps.Clock.GetUtcNow() - at;
        return age < TimeSpan.FromMinutes(1) ? "just now" : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago" : at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private string StatusOf(FileState st, RemoteFile? remote)
    {
        local.TryGetValue(st.Path, out var file);
        if (st.Refusal is not null) return FileStatuses.Refused;
        if (st.Inflight is not null) return FileStatuses.Syncing;
        if (st.Preserved is not null && file?.Hash == st.Preserved) return FileStatuses.Conflict;
        if (st.NewerWaiting) return FileStatuses.NewerWaiting;
        var ownership = OwnershipOf(remote?.Lock);
        if (ownership is LockOwnership.OtherPerson or LockOwnership.MyOtherDevice) return FileStatuses.EditingByOther;
        if (file is not null && file.Hash != st.BaseHash) return online == true ? FileStatuses.Syncing : FileStatuses.WaitingToSend;
        if (st.Entries.Count > 0) return FileStatuses.WaitingToSend;
        if (ownership == LockOwnership.ThisDevice) return FileStatuses.EditingByMe;
        if (file is null && remote is { Deleted: false, Current: not null }) return FileStatuses.NotOnThisComputer;
        return FileStatuses.Synced;
    }

    private IReadOnlyList<MyFileView> MyFiles(FileState[] files)
    {
        List<MyFileView> mine = [];
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var remote = st.FileId is { } id && remoteById.TryGetValue(id, out var r) ? r.File : null;
            local.TryGetValue(st.Path, out var file);
            var held = OwnershipOf(remote?.Lock) == LockOwnership.ThisDevice;
            var unsynced = (file is not null && file.Hash != st.BaseHash && file.Hash != st.Preserved) || st.Entries.Count > 0 || st.Inflight is not null;
            if (!held && !unsynced && st.Refusal is null) continue;
            var status = StatusOf(st, remote);
            var note = status switch
            {
                FileStatuses.Refused => st.Refusal,
                FileStatuses.WaitingToSend => "Saved on this computer. It sends when Armory can reach the internet.",
                FileStatuses.Syncing => "Sending to Armory now.",
                FileStatuses.Conflict => "Your version is kept in this file's history.",
                _ => held ? "You're editing this. Others see it as read-only until you close it." : null,
            };
            mine.Add(new MyFileView(st.FileId?.ToString(), st.Path, NameOf(st.Path), ProjectName(st), held && status == FileStatuses.Synced ? FileStatuses.EditingByMe : status, note));
        }
        return mine;
    }

    private IReadOnlyList<AttentionView> NeedsMe(FileState[] files)
    {
        var now = deps.Clock.GetUtcNow();
        List<AttentionView> items = [.. notices, .. state.Remembered.Where(n => now - n.At < TimeSpan.FromMinutes(30))
            .Select(n => new AttentionView(n.Kind, n.FileId?.ToString(), n.Path, n.Path[(n.Path.LastIndexOf('/') + 1)..], n.Title, n.Detail, n.At.ToString("O")))];
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var name = NameOf(st.Path);
            var id = st.FileId?.ToString();
            if (st.Refusal is not null)
                items.Add(st.RefusalKind == AttentionKinds.NameTaken
                    ? new(AttentionKinds.NameTaken, id, st.Path, name, "That name is already used", st.Refusal, null)
                    : new(AttentionKinds.Refused, id, st.Path, name, $"Can't send {name}", st.Refusal, null));
            if (st.NewerWaiting)
                items.Add(new(AttentionKinds.NewerWaiting, id, st.Path, name,
                    st.NewerAuthor is { } author ? $"A newer version from {DisplayName(author)} is waiting" : "A newer version is waiting",
                    $"Close {name} to get it. Your copy stays as it is until then.", null));
            if (st.BreakNotice)
                items.Add(new(AttentionKinds.LockBroken, id, st.Path, name, "A mentor took over this file",
                    "Armory is keeping your version in the file's history, so nothing is lost.", null));
            foreach (var side in st.Sides.Where(s => s.Reason != EarlierSaveReason).OrderByDescending(s => s.At).Take(3))
                items.Add(new(AttentionKinds.SideVersion, id, st.Path, name, "Your version was kept as your own copy",
                    side.Reason == "lock broken" ? $"A mentor took over {name}. Your version is saved in its history."
                        : $"Someone else saved {name} first. Your version is saved in its history.", side.At.ToString("O")));
            if (st.ReleaseNotChecked)
                items.Add(new(AttentionKinds.ReleaseNotChecked, id, st.Path, name, "SolidWorks year not checked",
                    $"Armory couldn't check which SolidWorks year saved {name}. It was saved to Armory anyway.", null));
        }
        return items;
    }

    private IReadOnlyList<ProjectView> Projects(FileState[] files)
    {
        List<ProjectView> projects = [];
        foreach (var project in state.Projects.Values.Where(p => p.Usable).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var rows = new Dictionary<string, List<FileRowView>>(StringComparer.OrdinalIgnoreCase);
            var remoteFiles = remoteProjects.GetValueOrDefault(project.Id) ?? [];
            foreach (var remote in remoteFiles.Where(f => !f.Deleted))
            {
                if (!remoteById.TryGetValue(remote.Id, out var known)) continue;
                var st = state.Files.GetValueOrDefault(known.Path.Value) ?? new FileState { Path = known.Path.Value, ProjectId = project.Id, FileId = remote.Id };
                Add(remote.Folder, new FileRowView(remote.Id.ToString(), remote.Name, known.Path.Value, StatusOf(st, remote), Holder(remote.Lock, remote),
                    remote.Current?.ReleaseChecked == false, remote.Current?.CreatedAt.ToString("O"), remote.Current is { } c ? DisplayName(c.Author) : null));
            }
            projects.Add(new ProjectView(project.Id.ToString(), project.Name,
                rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => new FolderView(r.Key, r.Key.Length == 0 ? project.Name : r.Key[(r.Key.LastIndexOf('/') + 1)..],
                    r.Value.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray()));
            void Add(string folder, FileRowView row)
            {
                if (!rows.TryGetValue(folder, out var list)) rows[folder] = list = [];
                list.Add(row);
            }
        }
        return projects;
    }

    private HolderView? Holder(RemoteLock? held, RemoteFile? file)
    {
        if (held is null || !held.IsLive) return null;
        var ownership = OwnershipOf(held);
        var saved = file?.Current is { } current && string.Equals(current.Author, held.HolderEmail, StringComparison.OrdinalIgnoreCase) && current.CreatedAt >= held.AcquiredAt;
        return new HolderView(DisplayName(held.HolderEmail), held.HolderEmail, held.HolderDeviceName ?? "another computer", held.AcquiredAt.ToString("O"),
            ownership == LockOwnership.ThisDevice, ownership == LockOwnership.MyOtherDevice, saved);
    }

    public async Task<FileDetailView?> GetFileDetailAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await passGate.WaitAsync(cancellationToken);
        try { return await DetailLockedAsync(fileId, cancellationToken); }
        finally { passGate.Release(); }
    }

    private async Task<FileDetailView?> DetailLockedAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (!remoteById.TryGetValue(fileId, out var remote)) return null;
        IReadOnlyList<RemoteHistoryEntry> history;
        try { history = await deps.Api.FileHistoryAsync(fileId, cancellationToken); }
        catch (ArmoryClientException) { history = []; }
        var st = state.Files.GetValueOrDefault(remote.Path.Value) ?? new FileState { Path = remote.Path.Value, ProjectId = remote.Project.Id, FileId = fileId };
        var currentId = remote.File.Current?.Id;
        var first = history.Where(h => h.Kind == "version").MinBy(h => h.CreatedAt)?.Id;
        var entries = history.Select(h => new HistoryEntryView(h.Id.ToString(), h.Kind == "side_version" ? "sideVersion" : h.Kind == "tombstone" ? "removed" : "version",
            DisplayName(h.Author), h.CreatedAt.ToString("O"), h.Bytes, NoteFor(h, first), h.ReleaseChecked == false, h.Id == currentId)).ToArray();
        return new FileDetailView(fileId.ToString(), remote.File.Name, remote.Path.Value, remote.Project.Name, remote.File.Folder, StatusOf(st, remote.File),
            Holder(remote.File.Lock, remote.File), remote.File.Current?.ReleaseChecked == false, entries);
    }

    private static string NoteFor(RemoteHistoryEntry h, Guid? first) => h.Kind switch
    {
        "tombstone" => "Removed from the project. Every earlier version is still here.",
        "side_version" when h.Reason == EarlierSaveReason => "An earlier save, kept.",
        "side_version" when h.Reason == "lock broken" => $"Kept as {DisplayName(h.Author)}'s own copy: a mentor took over the file.",
        "side_version" => $"Kept as {DisplayName(h.Author)}'s own copy: someone else saved first.",
        _ => h.Id == first ? "Added to Armory." : "Saved.",
    };

    private string ProjectName(FileState st) => state.Projects.GetValueOrDefault(st.ProjectId)?.Name ?? st.Path.Split('/')[0];
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    // "maria.lopez@school.org" -> "Maria Lopez". Emails are the only names the server keeps.
    internal static string DisplayName(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var local = at > 0 ? email[..at] : email;
        var words = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length == 0 ? email : name;
    }
}
