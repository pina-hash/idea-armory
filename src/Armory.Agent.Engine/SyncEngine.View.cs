using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// Builds the window's AgentView (docs/agent/BRIDGE.md, v2-design.md 4.4 to 4.6) in plain
// student words. Notices are grouped by kind into at most one card each; waiting to upload is
// activity, never rows; "SolidWorks year not checked" is never a notice, only a tag on the
// file's detail. A year known to be newer than the project's pin is one (newerRelease, B5).
public sealed partial class SyncEngine
{
    private const int NoticeItemsShown = 200;
    private static readonly TimeSpan KeptCopiesShownFor = TimeSpan.FromDays(1);
    private static readonly string[] NoticeOrder =
    [
        NoticeKinds.CantSend, NoticeKinds.CantRead, NoticeKinds.NewerRelease, NoticeKinds.NameShared, NoticeKinds.TakenBack, NoticeKinds.FolderPutBack, NoticeKinds.ProjectPutBack,
        NoticeKinds.ProjectRenaming, NoticeKinds.ProjectDeleted, NoticeKinds.CheckInPartial, NoticeKinds.KeptCopy, NoticeKinds.NewerWaiting, NoticeKinds.Import,
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
        // A record of something deleted forever (v0.3) is on its way out: never shown.
        var files = state.Files.Values.Where(f => !f.Purged).ToArray();
        var pending = files.Count(Unsent);
        var notices = Notices(files);
        var moving = Activity(files, pending);
        var sync = paused ? new SyncView(SyncStates.Paused, "Paused. Nothing uploads or downloads until you resume.", null, pending)
            : online == false ? new SyncView(SyncStates.Offline, "You're offline. Your work is safe on this computer.", pending > 0 ? null : LastChecked(), pending)
            // While files move, the status line is the activity's line ("Downloading 412 of 1,280 files, ...").
            : syncing ? new SyncView(SyncStates.Syncing, moving.Line ?? "Checking for changes.", null, pending)
            : notices.Any(n => n.Tone != NoticeTones.Info) ? new SyncView(SyncStates.Attention, "Everything else is saved. A few files need you.", LastChecked(), pending)
            : pending > 0 ? new SyncView(SyncStates.Syncing, "Uploading your saves.", null, pending)
            : new SyncView(SyncStates.Synced, "Everything is saved to Armory.", LastChecked(), 0);
        return new AgentView(connection, new ConnectView(connectPhase, connectMessage), account, sync, moving, options.VaultRoot,
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
        // An archived project's files wait for nothing (decision D8), unless they are mine to finish.
        if (state.Projects.GetValueOrDefault(st.ProjectId) is { Archived: true } && !MineToFinish(st)) return false;
        if (st.Inflight is { Kind: "create" or "commit" or "side" or "archive" } || st.Entries.Count > 0) return true;
        return TryLocal(st.Path, out var file) && file.Hash != st.BaseHash && file.Hash != st.Preserved;
    }

    // ---- Activity ------------------------------------------------------------------------

    // What is moving right now (ActivityTracker), with what waits: files that upload once this
    // computer is back online or resumed, or checked-out files whose changes are shared only at
    // check in (v2-design.md 4.4).
    private ActivityView Activity(FileState[] files, int pending)
    {
        WaitingView? waiting = null;
        if (pending > 0 && (paused || online == false))
            waiting = new WaitingView(pending, $"{Count(pending, "file is", "files are")} waiting to upload. {(pending == 1 ? "It uploads" : "They upload")} " +
                (paused ? "when you resume." : "when this computer is back online."));
        else if (CheckedOutWithChanges(files) is var changed and > 0)
            waiting = new WaitingView(changed, $"{(changed == 1 ? "1 checked-out file has" : $"{changed:N0} checked-out files have")} changes. Check {(changed == 1 ? "it" : "them")} in to share {(changed == 1 ? "it" : "them")}.");
        activity.SetWaiting(waiting);
        return activity.Snapshot();
    }

    // Files this computer has checked out whose bytes here differ from the shared version (not in
    // an archived project, which says nothing, decision D8).
    private int CheckedOutWithChanges(FileState[] files)
    {
        var count = 0;
        foreach (var st in files)
        {
            if (st.FileId is not { } id || st.BaseHash is null || st.Request != CheckoutRequest.None || st.TransientLock) continue;
            if (state.Projects.GetValueOrDefault(st.ProjectId) is { Archived: true }) continue;
            var mine = remoteById.TryGetValue(id, out var remote)
                ? !remote.File.Deleted && OwnershipOf(remote.File.Lock) == LockOwnership.ThisDevice
                : KnownOwnership(st) == LockOwnership.ThisDevice;
            if (mine && TryLocal(st.Path, out var file) && file.Hash != st.BaseHash) count++;
        }
        return count;
    }

    // ---- Notices -------------------------------------------------------------------------

    // ItemDetail is the item's own sentence in a card of several (who has its files checked out,
    // why it went back); ReasonKind and Who let such a card name everyone in its title.
    // Release is a newerRelease item's project and years.
    private sealed record RawItem(string Id, Guid? FileId, string Path, string? Detail, string? Title = null, string? Flavor = null, (int Added, int Total)? Tally = null,
        string? ItemDetail = null, string? ReasonKind = null, string? Who = null, (string Project, int Saved, int Pin)? Release = null);
    private sealed record RawGroup(string Key, string Kind, List<RawItem> Items);

    // The items each card showed when the view was last built: a dismissal hides exactly those.
    private Dictionary<string, string[]> shownNoticeItems = new(StringComparer.Ordinal);

    private IReadOnlyList<NoticeGroupView> Notices(FileState[] files)
    {
        var groups = RawNotices(files);
        shownNoticeItems = groups.ToDictionary(g => g.Key, g => g.Items.Select(i => i.Id).ToArray(), StringComparer.Ordinal);
        return groups.OrderBy(g => Array.IndexOf(NoticeOrder, g.Kind)).Select(Group).ToArray();
    }

    // Every notice item there is now, one group per kind, without the ones the student dismissed.
    // Building a view never forgets a dismissal: a view built while a pass is still gathering its
    // notices (or right after a start) would otherwise forget the ones it hasn't gathered yet.
    private List<RawGroup> RawNotices(FileState[] files, bool keepDismissed = false)
    {
        var now = deps.Clock.GetUtcNow();
        var groups = new Dictionary<string, RawGroup>(StringComparer.Ordinal);
        void Add(string kind, RawItem item)
        {
            // One card per kind, however many files: 14 files that share a name are one card
            // with 14 items, and a Pack and Go is one import summary.
            var key = kind; // MUTATION: notices are grouped by kind
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new RawGroup(key, kind, []);
            if (!group.Items.Any(i => i.Id == item.Id)) group.Items.Add(item);
        }
        // A pass's notes, its problems among them (in plain words; the raw text goes to the log).
        foreach (var n in notes)
            Add(n.Kind, new RawItem($"{n.Kind}:{n.Path}:{n.Title}" + (n.Path.Length == 0 ? ":" + n.Detail : ""), n.FileId, n.Path, n.Detail, n.Title,
                n.Title == StaleMarkerTitle ? "stale" : n.Title?.Contains("renamed", StringComparison.Ordinal) == true || n.Title?.EndsWith(" was moved", StringComparison.Ordinal) == true ? "rename" : null,
                ItemDetail: n.ItemDetail, ReasonKind: n.ReasonKind, Who: n.Who));
        foreach (var n in state.Remembered.Where(n => now - n.At < TimeSpan.FromMinutes(30)))
            Add(n.Kind, new RawItem($"{n.Kind}:{n.Path}:{n.At.UtcTicks}", n.FileId, n.Path, n.Detail, n.Title, ItemDetail: n.ItemDetail, ReasonKind: n.ReasonKind, Who: n.Who));
        // One summary per bulk add (an unzip, a paste, a Pack and Go, Add files).
        foreach (var import in state.Imports.Where(i => now - i.At < ImportShownFor))
        {
            var (added, shared, waiting, other, total) = ImportTally(import);
            if (total == 0) continue;
            var detail = new List<string>();
            if (shared > 0) detail.Add($"{Count(shared, "file needs", "files need")} you: {(shared == 1 ? "it shares a name with another file" : "they share a name with other files")} in this project.");
            if (waiting > 0) detail.Add($"{Count(waiting, "file is", "files are")} {(online == false ? "waiting to upload" : "still uploading")}.");
            if (other > 0) detail.Add($"{Count(other, "file", "files")} can't be uploaded. Each one says why.");
            if (detail.Count == 0) detail.Add("They're all in Armory now.");
            Add(NoticeKinds.Import, new RawItem($"import:{import.Id}", null, import.Folder, string.Join(' ', detail),
                $"Added {added:N0} of {total:N0} files to {Where(import.Folder)}", Tally: (added, total)));
        }
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var name = NameOf(st.Path);
            TryLocal(st.Path, out var file);
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
            // Removed by the team while it is open here: nothing newer, nothing uploading.
            if (st.RemovedWaiting)
                Add(NoticeKinds.NewerWaiting, new RawItem($"removed:{st.Path}", st.FileId, st.Path,
                    $"Close {name} in SolidWorks, and Armory moves your copy aside. Nothing is lost.",
                    $"{name} was removed from {ProjectName(st)}", Flavor: "removed"));
            // One item per file: the newest take back, and the newest kept copy.
            var recent = st.Sides.Where(s => now - s.At < KeptCopiesShownFor).ToList();
            if (recent.LastOrDefault(s => s.Reason == LockBrokenReason) is { } taken && !st.BreakNotice)
                Add(NoticeKinds.TakenBack, new RawItem($"taken:{taken.VersionId}", st.FileId, st.Path,
                    "A mentor or CAD lead took it back. Your changes that weren't checked in are kept in its history."));
            else if (st.BreakNotice)
                Add(NoticeKinds.TakenBack, new RawItem($"taken:{st.Path}", st.FileId, st.Path,
                    "A mentor or CAD lead took it back. Armory is keeping your changes that weren't checked in in its history, so nothing is lost."));
            if (recent.LastOrDefault(s => s.Reason is ChangedWithoutCheckOutReason or ConflictReason) is { } kept)
            {
                if (kept.Reason == ConflictReason)
                    Add(NoticeKinds.KeptCopy, new RawItem($"kept:{kept.VersionId}", st.FileId, st.Path,
                        "Someone else checked it in first. Your change is in its history.", Flavor: "conflict"));
                else if (putBack && kept.Hash == file!.Hash)
                    Add(NoticeKinds.KeptCopy, new RawItem($"kept:{kept.VersionId}", st.FileId, st.Path,
                        $"Saved without a check out. The checked-in version comes back when you close {name}.", Flavor: "waiting"));
                else
                    Add(NoticeKinds.KeptCopy, new RawItem($"kept:{kept.VersionId}", st.FileId, st.Path,
                        "Saved without a check out, so the checked-in version was put back. Your change is in its history.", Flavor: "forced"));
            }
        }
        // B5: the team's version of a file saved in a newer SolidWorks than its project uses. On a
        // computer that can fix it, the student is needed; elsewhere it is news.
        foreach (var (remote, project, path, current) in UncheckedTeamVersions().OrderBy(v => v.Path.Value, StringComparer.OrdinalIgnoreCase))
        {
            state.Files.TryGetValue(path.Value, out var st);
            if (TeamRelease(remote, path, st) is not { } saved || saved <= project.PinnedRelease) continue;
            var (detail, needsYou) = NewerThanPinNotice(saved, project.PinnedRelease, solidWorks?.Revision, solidWorks?.SaveDownWorks ?? false);
            Add(NoticeKinds.NewerRelease, new RawItem($"newerRelease:{path.Value}:{current.Id}", remote.Id, path.Value, detail, Flavor: needsYou ? "needsYou" : "news",
                ItemDetail: $"Saved in SolidWorks {saved}. {project.Name} uses SolidWorks {project.PinnedRelease}.", Release: (project.Name, saved, project.PinnedRelease)));
        }
        foreach (var group in groups.Values)
            if (!keepDismissed && state.Dismissed.TryGetValue(group.Key, out var hidden)) group.Items.RemoveAll(i => hidden.Contains(i.Id));
        return groups.Values.Where(g => g.Items.Count > 0).ToList();
    }

    // At the end of a whole online pass every notice is known: a dismissed item that is gone is
    // forgotten (it may come back later as news), and so is a card with nothing left.
    private void PruneDismissed()
    {
        if (state.Dismissed.Count == 0) return;
        var now = RawNotices(state.Files.Values.ToArray(), keepDismissed: true).ToDictionary(g => g.Key, g => g.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (key, hidden) in state.Dismissed.ToArray())
        {
            if (now.TryGetValue(key, out var items)) hidden.IntersectWith(items);
            if (!now.ContainsKey(key) || hidden.Count == 0) state.Dismissed.Remove(key);
        }
        MarkDirty();
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
            NoticeKinds.NewerRelease => (items.Any(i => i.Flavor == "needsYou") ? NoticeTones.Look : NoticeTones.Info,
                NewerReleaseTitle(items),
                items.Select(i => i.Detail).Distinct(StringComparer.Ordinal).Count() == 1 ? first.Detail ?? ""
                    : "Each one says which SolidWorks it was saved in. Someone with that SolidWorks can fix it: check it out in Armory, open it, click Save so Armory saves it in the project's SolidWorks year, then check it in.",
                n == 1 ? null : expand),
            NoticeKinds.NameShared => (NoticeTones.Look,
                n == 1 ? "1 file shares a name with another file in this project" : $"{n:N0} files share a name with other files in this project",
                "A project keeps one file per name, because SolidWorks finds parts by name. Rename these to add them.", expand),
            NoticeKinds.CantSend => (NoticeTones.Bad,
                n == 1 ? first.Title ?? $"{name} can't be uploaded" : $"{n:N0} files can't be uploaded",
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
                n == 1 ? first.Title ?? $"A newer {name} is waiting" : items.All(i => i.Flavor == "removed") ? $"{n:N0} files were removed from the project"
                    : items.Any(i => i.Flavor == "removed") ? $"{n:N0} files changed for the team" : $"Newer versions of {n:N0} files are waiting",
                n == 1 ? first.Detail ?? "" : items.All(i => i.Flavor == "removed") ? "Close them in SolidWorks, and Armory moves your copies aside. Nothing is lost."
                    : items.Any(i => i.Flavor == "removed") ? "Close them in SolidWorks to finish. Each one says what changed. Your copies stay as they are until then."
                    : "Close them in SolidWorks to get them. Your copies stay as they are until then.",
                n == 1 && first.FileId is not null && first.Flavor is not ("rename" or "removed") ? new NoticeActionView("Open it", BridgeMessages.LaunchFile, [first.Path]) : null),
            // Only a kept copy still waiting for its file to close, or one someone else's check in
            // overtook, needs the student; one whose checked-in version is back is news.
            NoticeKinds.KeptCopy => (items.Any(i => i.Flavor is "conflict" or "waiting") ? NoticeTones.Look : NoticeTones.Info,
                n == 1 ? $"Your change to {name} was kept as your own copy" : $"Your changes to {n:N0} files were kept as your own copies",
                n == 1 ? first.Detail ?? ""
                    : items.All(i => i.Flavor == "forced") ? "They were saved without a check out, so the checked-in versions were put back. Nothing was lost: each change is in its file's history."
                    : items.All(i => i.Flavor is "forced" or "waiting") ? "They were saved without a check out. The checked-in versions come back as you close them. Nothing was lost: each change is in its file's history."
                    : items.All(i => i.Flavor == "conflict") ? "Someone else checked these in first, so your changes were kept in each file's history. Nothing was lost. Ask your CAD lead which one to keep."
                    : "Nothing was lost: each change is in its file's history.", new NoticeActionView("OK", BridgeMessages.DismissNotice, [])),
            NoticeKinds.TakenBack => (NoticeTones.Look,
                n == 1 ? $"{name} was force checked in" : $"{n:N0} of your files were force checked in",
                n == 1 ? first.Detail ?? "" : "A mentor or CAD lead took them back. Your changes that weren't checked in are kept in their history, so nothing was lost.",
                new NoticeActionView("OK", BridgeMessages.DismissNotice, [])),
            NoticeKinds.FolderPutBack => (NoticeTones.Look,
                n == 1 ? first.Title ?? $"{name} was put back where it was" : PutBackTitle(items),
                n == 1 ? first.Detail ?? "" : items.All(i => i.ReasonKind == CheckedOutReason)
                    ? "A folder is renamed or deleted only when nobody else has a file in it checked out. Ask them to check the files in, then try again."
                    : "Each one says why.", n == 1 ? null : expand),
            NoticeKinds.Import => (NoticeTones.Info,
                n == 1 ? first.Title ?? $"Added files to {name}" : $"Added {items.Sum(i => i.Tally?.Added ?? 0):N0} of {items.Sum(i => i.Tally?.Total ?? 0):N0} files to {n:N0} folders",
                n == 1 ? first.Detail ?? "" : "Each folder says what came in.", new NoticeActionView("Done", BridgeMessages.DismissNotice, [])),
            NoticeKinds.ProjectPutBack => (NoticeTones.Info,
                n == 1 ? first.Title ?? $"The {name} folder was put back" : $"{n:N0} project folders were put back",
                n == 1 ? first.Detail ?? "" : ProjectNamesWords, new NoticeActionView("OK", BridgeMessages.DismissNotice, [])),
            // One line (v0.3): the project is gone from the website, so it is gone from here.
            NoticeKinds.ProjectDeleted => (NoticeTones.Info,
                n == 1 ? first.Title ?? $"{name} was deleted forever" : $"{n:N0} projects were deleted forever on ideabosco.com, so Armory took them off this computer.",
                "", new NoticeActionView("OK", BridgeMessages.DismissNotice, [])),
            NoticeKinds.ProjectRenaming => (NoticeTones.Info,
                n == 1 ? first.Title ?? $"{name} is being renamed" : $"{n:N0} projects are being renamed",
                n == 1 ? first.Detail ?? "" : "A mentor renamed them on ideabosco.com. Armory renames their folders on this computer as soon as nothing in them is open.", null),
            _ => (NoticeTones.Info, first.Title ?? name, first.Detail ?? "", null),
        };
        return new NoticeGroupView(g.Key, g.Kind, tone, title, detail, n, action,
            items.Take(NoticeItemsShown).Select(i => new NoticeItemView(i.FileId?.ToString(), i.Path, NameOf(i.Path), i.ItemDetail ?? i.Detail)).ToArray());
    }

    // "3 files in Robot 2027 were saved in SolidWorks 2026"; files of several projects or years
    // are counted together.
    private static string NewerReleaseTitle(List<RawItem> items)
    {
        var first = items[0];
        if (first.Release is not { } release || items.Any(i => i.Release != release))
            return $"{items.Count:N0} files were saved in a newer SolidWorks than their project uses";
        return items.Count == 1 ? $"{NameOf(first.Path)} in {release.Project} was saved in SolidWorks {release.Saved}"
            : $"{items.Count:N0} files in {release.Project} were saved in SolidWorks {release.Saved}";
    }

    // Several folders (or files) put back at once: what they are, and who has files in them
    // checked out when that is why. "2 folders were put back: Maria Lopez and Sam Lee have files
    // in them checked out".
    private static string PutBackTitle(List<RawItem> items)
    {
        var files = items.Count(i => i.FileId is not null);
        var folders = items.Count - files;
        var what = files == 0 ? $"{folders:N0} folders were put back" : folders == 0 ? $"{files:N0} renames were put back" : $"{folders:N0} folders and {files:N0} files were put back";
        if (files > 0 || items.Any(i => i.ReasonKind != CheckedOutReason)) return what;
        var who = items.SelectMany(i => (i.Who ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToList();
        if (who.Count == 0 || items.Any(i => i.Who is null)) return what + ": someone else has files in them checked out";
        return $"{what}: {Names(who)} {(who.Count == 1 ? "has" : "have")} files in them checked out";
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
            if (!markerDocuments.Contains(document) || !state.Files.TryGetValue(HomeOf(document) ?? document, out var st) || st.FileId is not { } id) continue;
            LockOwnership ownership;
            CheckoutView checkout;
            if (remoteById.TryGetValue(id, out var remote))
            {
                if (remote.File.Deleted || remote.File.Current is null) continue;
                ownership = OwnershipOf(remote.File.Lock);
                checkout = CheckoutOf(remote.File.Lock);
            }
            else if (st.BaseHash is not null) (ownership, checkout) = (KnownOwnership(st), KnownCheckout(st)); // offline: as last known
            else continue;
            if (ownership == LockOwnership.ThisDevice) continue;
            open.Add(document);
            var key = PromptKey(document, firstSeen);
            if (prompt is not null || dismissedPrompts.Contains(key)) continue;
            // The file's own path (where it goes back to, when its folder is away), so Check out finds it.
            prompt = new PromptView(key, id.ToString(), st.Path, NameOf(st.Path), checkout, ownership == LockOwnership.Free);
        }
        Volatile.Write(ref openWithoutCheckOut, open.ToArray());
        return prompt;
    }

    // ---- My files and the team's files ------------------------------------------------------

    // The files this computer has checked out (addendum 7), in every project, archived ones too.
    // A lock taken only for an add of a closed file (the same pass checks it in) or only for a
    // move or a removal is no check out of the student's: never listed, so a 5,000-file import
    // lists nothing here while its files go in. A file added while it was open is, until it
    // closes.
    private IReadOnlyList<MyFileView> MyFiles(FileState[] files)
    {
        List<MyFileView> mine = [];
        foreach (var st in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (st.FileId is not { } id) continue;
            if (st.Request == CheckoutRequest.None && (st.TransientLock || (st.AutoCheckIn && !OpenHere(st)))) continue;
            CheckoutView checkout;
            RemoteFile? remote = null;
            TryLocal(st.Path, out var file);
            string status;
            if (remoteById.TryGetValue(id, out var known))
            {
                remote = known.File;
                if (remote.Deleted || OwnershipOf(remote.Lock) != LockOwnership.ThisDevice) continue;
                checkout = CheckoutOf(remote.Lock);
                status = StatusOf(st, remote, file, LockOwnership.ThisDevice);
            }
            // Offline since the start: the check outs this computer last knew it had.
            else if (st.BaseHash is not null && (KnownOwnership(st) == LockOwnership.ThisDevice || st.AutoCheckIn))
                (checkout, status) = (KnownCheckout(st), KnownStatus(st, file, LockOwnership.ThisDevice));
            else continue;
            var note = st.Request == CheckoutRequest.CheckIn ? (online == true ? "Checking in." : "Checks in when this computer is back online.")
                : st.Request == CheckoutRequest.Undo ? (online == true ? "Undoing the check out." : "The check out is undone when this computer is back online.")
                : st.AutoCheckIn ? "You added it while it was open. It is checked in by itself when you close it."
                : null;
            mine.Add(new MyFileView(id.ToString(), st.Path, NameOf(st.Path), ProjectName(st), status, note, checkout));
        }
        return mine;
    }

    // The file is open here now (where it is on disk; a file not on disk is not open).
    private bool OpenHere(FileState st) => TryLocal(st.Path, out var file) && IsOpenNow(file.Path);

    private IReadOnlyList<ProjectView> Projects()
    {
        List<ProjectView> projects = [];
        foreach (var project in state.Projects.Values.Where(p => p.Usable).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var folders = new Dictionary<string, List<FileRowView>>(StringComparer.OrdinalIgnoreCase) { [""] = [] };
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Not read from the server since this start (offline): the team's files as this
            // computer last knew them, each with its label and status, never "waiting" by default.
            if (!remoteProjects.ContainsKey(project.Id))
                foreach (var st in state.Files.Values.Where(f => f.ProjectId == project.Id && f.FileId is not null && f.BaseId?.StartsWith("tombstone:", StringComparison.Ordinal) != true))
                {
                    if (!VaultPath.TryCreate(st.Path, out var known, out _, options.VaultRoot) || ProjectOf(known)?.Id != project.Id) continue;
                    TryLocal(st.Path, out var file);
                    if (file is null && st.BaseHash is null) continue; // never here, and nothing known of it
                    var ownership = KnownOwnership(st);
                    // Offline: the year this computer last knew for the version it has.
                    var year = Reconciler.IsSolidWorks(known) && st.BaseHash is { } baseHash ? KnownRelease(st, baseHash)?.Year : null;
                    Add(Split(known).Folder, new FileRowView(st.FileId.ToString(), known.Name, known.Value, KnownStatus(st, file, ownership), KnownCheckout(st),
                        file is not null && file.Hash != st.BaseHash, st.ReleaseNotChecked, null, null, year, year > project.PinnedRelease));
                    shown.Add(known.Value);
                }
            foreach (var listed in remoteProjects.GetValueOrDefault(project.Id) ?? [])
            {
                // The file as this computer knows it now (its own lock changes since the read included).
                if (listed.Deleted || !remoteById.TryGetValue(listed.Id, out var known)) continue;
                var remote = known.File;
                state.Files.TryGetValue(known.Path.Value, out var st);
                TryLocal(known.Path.Value, out var file);
                var ownership = OwnershipOf(remote.Lock);
                var year = TeamRelease(remote, known.Path, st);
                Add(remote.Folder, new FileRowView(remote.Id.ToString(), remote.Name, known.Path.Value, StatusOf(st, remote, file, ownership), CheckoutOf(remote.Lock),
                    file is not null && st is not null && file.Hash != st.BaseHash, remote.Current?.ReleaseChecked == false,
                    remote.Current?.CreatedAt.ToString("O", CultureInfo.InvariantCulture), remote.Current is { } c ? DisplayName(c.Author) : null,
                    year, year > project.PinnedRelease));
                shown.Add(known.Path.Value);
            }
            // Files in the project's folder that Armory does not have (yet).
            foreach (var (key, file) in local)
            {
                if (shown.Contains(key) || ProjectOf(file.Path)?.Id != project.Id) continue;
                if (HomeOf(key) is { } home && shown.Contains(home)) continue; // shown where it goes back to
                state.Files.TryGetValue(key, out var st);
                RemoteFile? gone = null;
                if (st?.FileId is { } id && remoteById.TryGetValue(id, out var elsewhere))
                {
                    if (!elsewhere.File.Deleted) continue; // shown where the server has it
                    gone = elsewhere.File;
                }
                Add(Split(file.Path).Folder, new FileRowView(null, file.Path.Name, key, StatusOf(st, gone, file, LockOwnership.Free), Available, false, false, null, null, null, false));
            }
            // Folders on this computer, empty ones too.
            var prefix = project.Folder + "/";
            foreach (var folder in localFolders)
                if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !folders.ContainsKey(folder[prefix.Length..])) folders[folder[prefix.Length..]] = [];
            projects.Add(new ProjectView(project.Id.ToString(), project.Name, project.Archived, project.Role, project.CanTakeBack,
                folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => new FolderView(f.Key, f.Key.Length == 0 ? project.Name : f.Key[(f.Key.LastIndexOf('/') + 1)..],
                    f.Value.Count, f.Value.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray(),
                project.PinnedRelease, folders.Values.Sum(rows => rows.Count(r => r.NewerThanPin))));

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
        // Removed by the team, its copy here waiting to go aside (open, or until the next pass).
        if (remote is { Deleted: true } && st?.Base is { IsTombstone: false } && file is not null && (st.RemovedWaiting || file.Hash == st.BaseHash))
            return FileStatuses.NotInArmory;
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

    // A file's status from what this computer last knew of the server (offline since the start):
    // the same words as StatusOf, and "waiting" only for saves that have not reached the server.
    private string KnownStatus(FileState st, LocalFile? file, LockOwnership ownership)
    {
        if (st.Inflight is { Kind: "create" or "commit" or "side" or "archive" }) return FileStatuses.Waiting;
        if (st.NewerWaiting) return file is not null && st.Preserved == file.Hash ? FileStatuses.KeptCopy : FileStatuses.NewerWaiting;
        if (file is null) return FileStatuses.NotOnThisComputer;
        if (file.Hash != st.BaseHash) return ownership != LockOwnership.ThisDevice && st.Preserved == file.Hash ? FileStatuses.KeptCopy : FileStatuses.Changed;
        if (st.Entries.Count > 0) return FileStatuses.Waiting;
        return FileStatuses.Synced;
    }

    // Who has it checked out as this computer last knew, in the words every row shows.
    private CheckoutView KnownCheckout(FileState st)
    {
        var ownership = KnownOwnership(st);
        if (st.Holder is not { } held)
            return ownership == LockOwnership.ThisDevice
                ? new(CheckoutStates.Mine, "Checked out by you", state.Email is { } email ? DisplayName(email) : null, state.Email, deps.Sessions.Current?.DeviceName, null)
                : Available;
        var name = DisplayName(held.Email);
        var device = held.DeviceName ?? "another computer";
        var since = held.Since.ToString("O", CultureInfo.InvariantCulture);
        return ownership switch
        {
            LockOwnership.ThisDevice => new(CheckoutStates.Mine, "Checked out by you", name, held.Email, held.DeviceName ?? deps.Sessions.Current?.DeviceName, since),
            LockOwnership.MyOtherDevice => new(CheckoutStates.MyOtherComputer, $"Checked out by you on {device}", name, held.Email, device, since),
            _ => new(CheckoutStates.Other, $"Checked out by {name} on {device}", name, held.Email, device, since),
        };
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

    // On the engine thread, never behind a pass: the server's files as last published
    // (publishedRemote), this computer's record and disk as they are between two steps of a pass.
    public Task<FileDetailView?> GetFileDetailAsync(Guid fileId, CancellationToken cancellationToken = default)
        => engineThread.InvokeAsync(() => DetailAsync(fileId, cancellationToken));

    private async Task<FileDetailView?> DetailAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (!publishedRemote.TryGetValue(fileId, out var remote)) return null;
        IReadOnlyList<RemoteHistoryEntry> history;
        try { history = await deps.Api.FileHistoryAsync(fileId, cancellationToken); }
        catch (ArmoryClientException) { history = []; }
        state.Files.TryGetValue(remote.Path.Value, out var st);
        TryLocal(remote.Path.Value, out var file);
        var currentId = remote.File.Current?.Id;
        var ordered = history.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).ToArray();
        // The server drops a revived file's removal from its history (0232 deletes the tombstone
        // row), so a revival is known from the change feed: the first version after it is the
        // file added again.
        var revivals = new Queue<DateTimeOffset>((state.Revivals.GetValueOrDefault(fileId) ?? []).Order());
        var versionNotes = new Dictionary<Guid, string>();
        var firstVersion = true;
        var afterRemoval = false;
        foreach (var h in ordered)
        {
            while (revivals.TryPeek(out var revived) && revived <= h.CreatedAt) { revivals.Dequeue(); afterRemoval = !firstVersion; }
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
            h.ReleaseChecked == false, h.Id == currentId,
            // Saves kept while checked out (and earlier saves) are the ordinary record of work, not news.
            h.Kind == "side_version" && h.Reason is SavedWhileCheckedOutReason or EarlierSaveReason)).ToArray();
        var year = TeamRelease(remote.File, remote.Path, st);
        return new FileDetailView(fileId.ToString(), remote.File.Name, remote.Path.Value, remote.Project.Name, remote.File.Folder,
            StatusOf(st, remote.File, file, OwnershipOf(remote.File.Lock)), CheckoutOf(remote.File.Lock), remote.File.Current?.ReleaseChecked == false,
            remote.Project.CanTakeBack, entries, year, year > remote.Project.PinnedRelease);
    }

    // A kept copy's note, from the reason the server keeps with it.
    private static string KeptCopyNote(RemoteHistoryEntry h) => h.Reason switch
    {
        EarlierSaveReason => "An earlier save, kept",
        SavedWhileCheckedOutReason => "Saved while checked out",
        UndoReason => "Kept when the check out was undone",
        ChangedWithoutCheckOutReason => $"Changed without a check out, kept as {DisplayName(h.Author)}'s own copy",
        LockBrokenReason => $"Kept as {DisplayName(h.Author)}'s own copy: the file was force checked in",
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
