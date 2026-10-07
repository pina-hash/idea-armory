using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Builds the window's AgentView (docs/agent/BRIDGE.md, v2-design.md 4.4 to 4.6) in plain
// student words. Notices are grouped by kind into at most one card each; waiting to upload is
// activity, never rows; "SolidWorks year not checked" is never a notice, only a tag on the
// file's detail.
public sealed partial class SyncEngine
{
    private const int NoticeItemsShown = 200;
    private static readonly TimeSpan KeptCopiesShownFor = TimeSpan.FromDays(1);
    private static readonly string[] NoticeOrder =
    [
        NoticeKinds.CantSend, NoticeKinds.CantRead, NoticeKinds.NameShared, NoticeKinds.TakenBack, NoticeKinds.FolderPutBack, NoticeKinds.ProjectPutBack,
        NoticeKinds.ProjectRenaming, NoticeKinds.CheckInPartial, NoticeKinds.KeptCopy, NoticeKinds.NewerWaiting, NoticeKinds.Import,
    ];
    private IReadOnlyCollection<string> openWithoutCheckOut = [];

    // The open files (SolidWorks' ~$ marker) this computer has not checked out, for the tray's
    // one quiet balloon per opened file (decision D13).
    public IReadOnlyCollection<string> OpenWithoutCheckOut => Volatile.Read(ref openWithoutCheckOut);

    private AgentView BuildView()
    {
        var session = deps.Sessions.Current;
        var connection = session is null
            ? (connectPhase is "waitingForBrowser" or "finishing" ? Connections.Connecting : Connections.SignedOut)
            : state.Email is not null && !string.Equals(state.Email, session.Email, StringComparison.OrdinalIgnoreCase) ? Connections.VaultOwnedByOther
            : Connections.SignedIn;
        var account = session is null ? null : new AccountView(session.Email, session.DeviceName);
        var files = state.Files.Values.ToArray();
        var pending = files.Count(Unsent);
        var notices = Notices(files);
        var sync = paused ? new SyncView(SyncStates.Paused, "Paused. Nothing uploads or downloads until you resume.", null, pending)
            : online == false ? new SyncView(SyncStates.Offline, "You're offline. Your work is safe on this computer.", pending > 0 ? null : LastChecked(), pending)
            : syncing ? new SyncView(SyncStates.Syncing, "Checking for changes.", null, pending)
            : notices.Any(n => n.Tone != NoticeTones.Info) ? new SyncView(SyncStates.Attention, "Everything else is saved. A few files need you.", LastChecked(), pending)
            : pending > 0 ? new SyncView(SyncStates.Syncing, "Uploading your saves.", null, pending)
            : new SyncView(SyncStates.Synced, "Everything is saved to Armory.", LastChecked(), 0);
        return new AgentView(connection, new ConnectView(connectPhase, connectMessage), account, sync, Activity(pending), options.VaultRoot,
            notices, Prompt(), MyFiles(files), Projects(), settings, effectiveTheme);
    }

    private string? LastChecked() => lastOnline is { } at ? "Last checked " + Relative(at) + "." : null;
    private string Relative(DateTimeOffset at)
    {
        var age = deps.Clock.GetUtcNow() - at;
        return age < TimeSpan.FromMinutes(1) ? "just now" : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago" : at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    // Saves of this file that have not reached the server yet (refused ones are notices).
    private bool Unsent(FileState st)
    {
        if (st.Refusal is not null) return false;
        if (st.Inflight is { Kind: "create" or "commit" or "side" or "archive" } || st.Entries.Count > 0) return true;
        return local.TryGetValue(st.Path, out var file) && file.Hash != st.BaseHash && file.Hash != st.Preserved;
    }

    // ---- Activity ------------------------------------------------------------------------

    // Stage E3 fills in what is moving right now; the waiting line for offline and paused works now.
    private ActivityView Activity(int pending)
    {
        WaitingView? waiting = null;
        if (pending > 0 && (paused || online == false))
            waiting = new WaitingView(pending, $"{Count(pending, "file is", "files are")} waiting to upload. {(pending == 1 ? "It uploads" : "They upload")} " +
                (paused ? "when you resume." : "when this computer is back online."));
        return new ActivityView(null, null, null, null, waiting, []);
    }

    // ---- Notices -------------------------------------------------------------------------

    private sealed record RawItem(string Id, Guid? FileId, string Path, string? Detail, string? Title = null, string? Flavor = null);
    private sealed record RawGroup(string Key, string Kind, List<RawItem> Items);

    private IReadOnlyList<NoticeGroupView> Notices(FileState[] files)
        => RawNotices(files).OrderBy(g => Array.IndexOf(NoticeOrder, g.Kind)).Select(Group).ToArray();

    // Every notice item there is now, one group per kind, without the ones the student dismissed.
    private List<RawGroup> RawNotices(FileState[] files)
    {
        var now = deps.Clock.GetUtcNow();
        var groups = new Dictionary<string, RawGroup>(StringComparer.Ordinal);
        void Add(string kind, RawItem item)
        {
            if (!groups.TryGetValue(kind, out var group)) groups[kind] = group = new RawGroup(kind, kind, []);
            if (!group.Items.Any(i => i.Id == item.Id)) group.Items.Add(item);
        }
        foreach (var n in notes)
            Add(n.Kind, new RawItem($"{n.Kind}:{n.Path}:{n.Title}", n.FileId, n.Path, n.Detail, n.Title,
                n.Title == StaleMarkerTitle ? "stale" : n.Title?.Contains("renamed", StringComparison.Ordinal) == true ? "rename" : null));
        foreach (var n in state.Remembered.Where(n => now - n.At < TimeSpan.FromMinutes(30)))
            Add(n.Kind, new RawItem($"{n.Kind}:{n.Path}:{n.At.UtcTicks}", n.FileId, n.Path, n.Detail, n.Title));
        foreach (var problem in problems)
        {
            // "path: what went wrong", or a sentence about this computer.
            var colon = problem.IndexOf(": ", StringComparison.Ordinal);
            var path = colon > 0 && VaultPath.TryCreate(problem[..colon], out var where, out _) ? where.Value : "";
            Add(NoticeKinds.CantRead, new RawItem("read:" + problem, null, path, path.Length > 0 ? problem[(colon + 2)..] : problem));
        }
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var name = NameOf(st.Path);
            local.TryGetValue(st.Path, out var file);
            var remote = st.FileId is { } id && remoteById.TryGetValue(id, out var r) ? r.File : null;
            if (st.Refusal is not null)
            {
                var kind = st.RefusalKind == NameTakenKind ? NoticeKinds.NameShared : NoticeKinds.CantSend;
                Add(kind, new RawItem($"{kind}:{st.Path}", st.FileId, st.Path, st.Refusal, Flavor: st.RefusalKind));
            }
            // Bytes saved without a check out, kept, waiting for the file to close so the
            // checked-in version can come back: a kept copy, not a newer version.
            var putBack = file is not null && st.Preserved == file.Hash && remote?.Current is { } current && current.Hash == st.BaseHash;
            if (st.NewerWaiting && !putBack)
                Add(NoticeKinds.NewerWaiting, new RawItem($"newer:{st.Path}:{remote?.Current?.Id}", st.FileId, st.Path,
                    $"Close {name} in SolidWorks to get it. Your copy stays as it is until then.",
                    st.NewerAuthor is { } author ? $"A newer {name} from {DisplayName(author)} is waiting" : null));
            if (st.BreakNotice)
                Add(NoticeKinds.TakenBack, new RawItem($"taken:{st.Path}", st.FileId, st.Path,
                    "A mentor or CAD lead took it back. Armory is keeping your changes that weren't checked in in its history, so nothing is lost."));
            foreach (var side in st.Sides.Where(s => now - s.At < KeptCopiesShownFor))
            {
                switch (side.Reason)
                {
                    case LockBrokenReason:
                        Add(NoticeKinds.TakenBack, new RawItem($"taken:{side.VersionId}", st.FileId, st.Path,
                            "A mentor or CAD lead took it back. Your changes that weren't checked in are kept in its history."));
                        break;
                    case ChangedWithoutCheckOutReason:
                        Add(NoticeKinds.KeptCopy, new RawItem($"kept:{side.VersionId}", st.FileId, st.Path,
                            putBack && side.Hash == file!.Hash ? $"Saved without a check out. The checked-in version comes back when you close {name}."
                                : "Saved without a check out, so the checked-in version was put back. Your change is in its history.", Flavor: "forced"));
                        break;
                    case ConflictReason:
                        Add(NoticeKinds.KeptCopy, new RawItem($"kept:{side.VersionId}", st.FileId, st.Path,
                            "Someone else checked it in first. Your change is in its history.", Flavor: "conflict"));
                        break;
                }
            }
        }
        foreach (var group in groups.Values)
        {
            if (!state.Dismissed.TryGetValue(group.Key, out var hidden)) continue;
            // An item that is gone may come back later as news; forget it.
            hidden.IntersectWith(group.Items.Select(i => i.Id));
            group.Items.RemoveAll(i => hidden.Contains(i.Id));
        }
        foreach (var gone in state.Dismissed.Keys.Where(k => !groups.ContainsKey(k)).ToArray()) state.Dismissed.Remove(gone);
        return groups.Values.Where(g => g.Items.Count > 0).ToList();
    }

    private static NoticeGroupView Group(RawGroup g)
    {
        var items = g.Items;
        var n = items.Count;
        var first = items[0];
        var name = NameOf(first.Path);
        var expand = new NoticeActionView("Show them", "expand", []);
        var (tone, title, detail, action) = g.Kind switch
        {
            NoticeKinds.NameShared => (NoticeTones.Look,
                n == 1 ? "1 file shares a name with another file in this project" : $"{n:N0} files share a name with other files in this project",
                "A project keeps one file per name, because SolidWorks finds parts by name. Rename these to add them.", expand),
            NoticeKinds.CantSend => (NoticeTones.Bad,
                n == 1 ? $"{name} can't be uploaded" : $"{n:N0} files can't be uploaded",
                n == 1 ? first.Detail ?? "" : items.All(i => i.Flavor == GateKind)
                    ? "They were saved in a SolidWorks year the project can't take. Each one says what to do, then it uploads by itself."
                    : "Each one says why. They stay on this computer, and everything else keeps uploading.", (NoticeActionView?)null),
            NoticeKinds.CantRead when items.All(i => i.Flavor == "stale") => (NoticeTones.Look,
                n == 1 ? StaleMarkerTitle : $"{StaleMarkerTitle} with {n:N0} files open",
                n == 1 ? first.Detail ?? "" : "Armory is treating them as closed. If SolidWorks still has one open, save it there.", null),
            NoticeKinds.CantRead => (NoticeTones.Bad,
                n == 1 ? "Armory can't read a file on this computer" : $"Armory can't read {n:N0} files on this computer",
                n == 1 ? first.Detail ?? "" : "Close any program that might be using them. Armory tries again by itself.", null),
            NoticeKinds.NewerWaiting => (NoticeTones.Look,
                n == 1 ? first.Title ?? $"A newer {name} is waiting" : $"Newer versions of {n:N0} files are waiting",
                n == 1 ? first.Detail ?? "" : "Close them in SolidWorks to get them. Your copies stay as they are until then.",
                n == 1 && first.FileId is not null && first.Flavor != "rename" ? new NoticeActionView("Open it", BridgeMessages.LaunchFile, [first.Path]) : null),
            NoticeKinds.KeptCopy => (NoticeTones.Look,
                n == 1 ? $"Your change to {name} was kept as your own copy" : $"{n:N0} of your changes were kept as your own copies",
                n == 1 ? first.Detail ?? ""
                    : items.All(i => i.Flavor == "forced") ? "They were saved without a check out, so the checked-in versions were put back. Nothing was lost: each change is in its file's history."
                    : items.All(i => i.Flavor == "conflict") ? "Someone else checked these in first, so your changes were kept in each file's history. Nothing was lost. Ask your CAD lead which one to keep."
                    : "Nothing was lost: each change is in its file's history.", n == 1 ? null : expand),
            NoticeKinds.TakenBack => (NoticeTones.Look,
                n == 1 ? $"{name} was taken back" : $"{n:N0} of your files were taken back",
                n == 1 ? first.Detail ?? "" : "A mentor or CAD lead took them back. Your changes that weren't checked in are kept in their history, so nothing was lost.",
                new NoticeActionView("OK", BridgeMessages.DismissNotice, [])),
            NoticeKinds.FolderPutBack => (NoticeTones.Look,
                n == 1 ? first.Title ?? $"{name} was put back where it was" : $"{n:N0} renames were put back",
                n == 1 ? first.Detail ?? "" : "Someone else has these files checked out, so they can't be renamed now. Try again after they're checked in.", null),
            _ => (NoticeTones.Info, first.Title ?? name, first.Detail ?? "", null),
        };
        return new NoticeGroupView(g.Key, g.Kind, tone, title, detail, n, action,
            items.Take(NoticeItemsShown).Select(i => new NoticeItemView(i.FileId?.ToString(), i.Path, NameOf(i.Path), i.Detail)).ToArray());
    }

    // ---- The check-out question ------------------------------------------------------------

    // SolidWorks opened a file the server has and this computer has not checked out: the most
    // recently opened one asks, once per open (decision D13). Someone else's file says who.
    private PromptView? Prompt()
    {
        PromptView? prompt = null;
        List<string> open = [];
        foreach (var (document, firstSeen) in markerFirstSeen.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!markerDocuments.Contains(document) || !state.Files.TryGetValue(document, out var st) || st.FileId is not { } id) continue;
            if (!remoteById.TryGetValue(id, out var remote) || remote.File.Deleted || remote.File.Current is null) continue;
            var ownership = OwnershipOf(remote.File.Lock);
            if (ownership == LockOwnership.ThisDevice) continue;
            open.Add(document);
            var key = PromptKey(document, firstSeen);
            if (prompt is not null || dismissedPrompts.Contains(key)) continue;
            prompt = new PromptView(key, id.ToString(), document, NameOf(document), CheckoutOf(remote.File.Lock), ownership == LockOwnership.Free);
        }
        Volatile.Write(ref openWithoutCheckOut, open.ToArray());
        return prompt;
    }

    // ---- My files and the team's files ------------------------------------------------------

    // The files this computer has checked out (addendum 7), in every project, archived ones too.
    private IReadOnlyList<MyFileView> MyFiles(FileState[] files)
    {
        List<MyFileView> mine = [];
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (st.FileId is not { } id) continue;
            CheckoutView checkout;
            RemoteFile? remote = null;
            if (remoteById.TryGetValue(id, out var known))
            {
                remote = known.File;
                if (remote.Deleted || OwnershipOf(remote.Lock) != LockOwnership.ThisDevice) continue;
                checkout = CheckoutOf(remote.Lock);
            }
            else if (st.AppliedOwnership == LockOwnership.ThisDevice && st.BaseHash is not null)
                checkout = new CheckoutView(CheckoutStates.Mine, "Checked out by you", state.Email is { } email ? DisplayName(email) : null, state.Email,
                    deps.Sessions.Current?.DeviceName, null);
            else continue;
            local.TryGetValue(st.Path, out var file);
            var note = st.Request == CheckoutRequest.CheckIn ? (online == true ? "Checking in." : "Checks in when this computer is back online.")
                : st.Request == CheckoutRequest.Undo ? (online == true ? "Undoing the check out." : "The check out is undone when this computer is back online.")
                : st.AutoCheckIn ? "You added it while it was open. It is checked in by itself when you close it."
                : null;
            mine.Add(new MyFileView(id.ToString(), st.Path, NameOf(st.Path), ProjectName(st), StatusOf(st, remote, file, LockOwnership.ThisDevice), note, checkout));
        }
        return mine;
    }

    private IReadOnlyList<ProjectView> Projects()
    {
        List<ProjectView> projects = [];
        foreach (var project in state.Projects.Values.Where(p => p.Usable).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var folders = new Dictionary<string, List<FileRowView>>(StringComparer.OrdinalIgnoreCase) { [""] = [] };
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var remote in remoteProjects.GetValueOrDefault(project.Id) ?? [])
            {
                if (remote.Deleted || !remoteById.TryGetValue(remote.Id, out var known)) continue;
                state.Files.TryGetValue(known.Path.Value, out var st);
                local.TryGetValue(known.Path.Value, out var file);
                var ownership = OwnershipOf(remote.Lock);
                Add(remote.Folder, new FileRowView(remote.Id.ToString(), remote.Name, known.Path.Value, StatusOf(st, remote, file, ownership), CheckoutOf(remote.Lock),
                    file is not null && st is not null && file.Hash != st.BaseHash, remote.Current?.ReleaseChecked == false,
                    remote.Current?.CreatedAt.ToString("O", CultureInfo.InvariantCulture), remote.Current is { } c ? DisplayName(c.Author) : null));
                shown.Add(known.Path.Value);
            }
            // Files in the project's folder that Armory does not have (yet).
            foreach (var (key, file) in local)
            {
                if (shown.Contains(key) || ProjectOf(file.Path)?.Id != project.Id) continue;
                state.Files.TryGetValue(key, out var st);
                if (st?.FileId is { } id && remoteById.TryGetValue(id, out var elsewhere) && !elsewhere.File.Deleted) continue; // shown where the server has it
                Add(Split(file.Path).Folder, new FileRowView(null, file.Path.Name, key, StatusOf(st, null, file, LockOwnership.Free), Available, false, false, null, null));
            }
            // Folders on this computer, empty ones too.
            var prefix = project.Folder + "/";
            foreach (var folder in localFolders)
                if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !folders.ContainsKey(folder[prefix.Length..])) folders[folder[prefix.Length..]] = [];
            projects.Add(new ProjectView(project.Id.ToString(), project.Name, project.Archived, project.Role, project.CanTakeBack,
                folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => new FolderView(f.Key, f.Key.Length == 0 ? project.Name : f.Key[(f.Key.LastIndexOf('/') + 1)..],
                    f.Value.Count, f.Value.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray()));

            void Add(string folder, FileRowView row)
            {
                if (!folders.TryGetValue(folder, out var list)) folders[folder] = list = [];
                list.Add(row);
            }
        }
        return projects;
    }

    // FileStatus v2 for one file. Waiting to upload is activity; a row says what the file is.
    private string StatusOf(FileState? st, RemoteFile? remote, LocalFile? file, LockOwnership ownership)
    {
        if (remote is null || remote.Deleted)
        {
            if (st?.Refusal is not null || file is null) return FileStatuses.NotInArmory;
            return online == true ? FileStatuses.Uploading : FileStatuses.Waiting;
        }
        if (st?.Inflight is { Kind: "create" or "commit" or "side" or "archive" }) return FileStatuses.Uploading;
        if (st?.NewerWaiting == true)
            return file is not null && st.Preserved == file.Hash && remote.Current?.Hash == st.BaseHash ? FileStatuses.KeptCopy : FileStatuses.NewerWaiting;
        if (file is null) return remote.Current is null ? FileStatuses.Uploading : FileStatuses.NotOnThisComputer;
        if (st is null) return FileStatuses.Synced;
        if (file.Hash != st.BaseHash)
            return ownership != LockOwnership.ThisDevice && st.Preserved == file.Hash ? FileStatuses.KeptCopy : FileStatuses.Changed;
        if (st.Entries.Count > 0) return online == true ? FileStatuses.Uploading : FileStatuses.Waiting;
        return FileStatuses.Synced;
    }

    private static readonly CheckoutView Available = new(CheckoutStates.Available, "Available", null, null, null, null);

    // Who has the file checked out, in the words every row shows.
    private CheckoutView CheckoutOf(RemoteLock? held)
    {
        if (held is not { IsLive: true }) return Available;
        var name = DisplayName(held.HolderEmail);
        var device = held.HolderDeviceName ?? "another computer";
        var since = held.AcquiredAt.ToString("O", CultureInfo.InvariantCulture);
        return OwnershipOf(held) switch
        {
            LockOwnership.ThisDevice => new(CheckoutStates.Mine, "Checked out by you", name, held.HolderEmail, held.HolderDeviceName ?? deps.Sessions.Current?.DeviceName, since),
            LockOwnership.MyOtherDevice => new(CheckoutStates.MyOtherComputer, $"Checked out by you on {device}", name, held.HolderEmail, device, since),
            _ => new(CheckoutStates.Other, $"Checked out by {name} on {device}", name, held.HolderEmail, device, since),
        };
    }

    // ---- File detail ---------------------------------------------------------------------

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
        state.Files.TryGetValue(remote.Path.Value, out var st);
        local.TryGetValue(remote.Path.Value, out var file);
        var currentId = remote.File.Current?.Id;
        var ordered = history.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).ToArray();
        var versionNotes = new Dictionary<Guid, string>();
        var firstVersion = true;
        var afterRemoval = false;
        foreach (var h in ordered)
        {
            if (h.Kind == "tombstone") { afterRemoval = true; continue; }
            if (h.Kind != "version") continue;
            versionNotes[h.Id] = firstVersion ? "Added to Armory" : afterRemoval ? "Added again, with its history" : "Checked in";
            firstVersion = false;
            afterRemoval = false;
        }
        var entries = ordered.Reverse().Select(h => new HistoryEntryView(h.Id.ToString(),
            h.Kind == "side_version" ? HistoryKinds.KeptCopy : h.Kind == "tombstone" ? HistoryKinds.Removed : HistoryKinds.Version,
            DisplayName(h.Author), h.CreatedAt.ToString("O", CultureInfo.InvariantCulture), h.Bytes,
            h.Kind == "tombstone" ? $"Removed from {remote.Project.Name}" : versionNotes.GetValueOrDefault(h.Id) ?? KeptCopyNote(h),
            h.ReleaseChecked == false, h.Id == currentId)).ToArray();
        return new FileDetailView(fileId.ToString(), remote.File.Name, remote.Path.Value, remote.Project.Name, remote.File.Folder,
            StatusOf(st, remote.File, file, OwnershipOf(remote.File.Lock)), CheckoutOf(remote.File.Lock), remote.File.Current?.ReleaseChecked == false,
            remote.Project.CanTakeBack, entries);
    }

    // A kept copy's note, from the reason the server keeps with it.
    private static string KeptCopyNote(RemoteHistoryEntry h) => h.Reason switch
    {
        EarlierSaveReason => "An earlier save, kept",
        SavedWhileCheckedOutReason => "Saved while checked out",
        UndoReason => "Kept when the check out was undone",
        ChangedWithoutCheckOutReason => $"Changed without a check out, kept as {DisplayName(h.Author)}'s own copy",
        LockBrokenReason => $"Kept as {DisplayName(h.Author)}'s own copy: the file was taken back",
        _ => $"Kept as {DisplayName(h.Author)}'s own copy: someone else checked in first",
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
