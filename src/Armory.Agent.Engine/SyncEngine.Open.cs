using System.Globalization;
using System.Threading.Channels;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

// SolidWorks, seen through the SolidWorks link (docs/agent/SOLIDWORKS.md) when there is one,
// and through its "~$" markers otherwise (docs/agent/ENGINE.md, "SolidWorks opened a file").
// The link's records arrive on one channel and are handled on the engine thread, in order:
// which documents are open in which SolidWorks, which the student opened (the questions asked
// about them, C5), saves (a path being saved is held until SolidWorks says it is saved, and the
// bytes it saved are stamped with the year SolidWorks reads in them), and what a save down of
// each would drop or can't do (B2). The engine answers with commands: make a document writable
// after its check out, save it again in the pinned year, the projects' pins, keep a file here.
public sealed partial class SyncEngine
{
    // How long a path SolidWorks is saving is held at most (LinkSaving without its LinkSaved).
    private static readonly TimeSpan SavingHeldFor = TimeSpan.FromMinutes(2);
    // Without the link, a file checked out with "Check out and reopen" while SolidWorks had it
    // open is opened again once it is closed, if that happens within this long.
    internal static readonly TimeSpan ReopenWhenClosedFor = TimeSpan.FromMinutes(5);
    // How long "Check out and reopen" waits for the link to make one document writable.
    internal static readonly TimeSpan MakeWritableBudget = TimeSpan.FromSeconds(15);

    private ISolidWorksLink? Link => deps.SolidWorks;
    // Attached SolidWorks sessions, in the order they attached (by process id).
    private readonly List<LinkAttached> linkSessions = [];
    // SolidWorks processes of this user the link couldn't attach to (started as administrator).
    private readonly HashSet<int> linkRefused = [];
    // The vault documents open in each attached SolidWorks, by "<pid>|<vault path>".
    private readonly Dictionary<string, LinkDocument> linkDocuments = new(StringComparer.OrdinalIgnoreCase);
    // How many SolidWorks sessions have each vault path open (the write-safety check reads it).
    private readonly Dictionary<string, int> linkOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly OpenAskOnce askedOnce = new();
    // Paths SolidWorks is saving now: no capture and no plan for them until it says it saved.
    private readonly Dictionary<string, (Guid SaveId, DateTimeOffset At)> savingHolds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> reopenWhenClosed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ProjectPin> pinsSent = [];
    private Task? linkPump;
    private long linkRecordsRead, linkRecordsHandled;
    private IReadOnlyList<OpenPrompt> openPrompts = [];
    private IReadOnlyList<SaveDownPrompt> saveDownPrompts = [];

    // One open document in one SolidWorks, as the link reported it.
    private sealed class LinkDocument(int pid, string path)
    {
        internal int Pid { get; } = pid;
        internal string Path { get; } = path;
        internal int DocType;
        internal bool ReadOnly, FutureVersion;
        // The student opened it in its own window, or worked in it (SolidWorks' active document).
        internal bool TopLevel;
        internal DateTimeOffset OpenedAt;
        // Asked about once in this SolidWorks session (OpenAskOnce), when.
        internal bool Asked;
        internal DateTimeOffset AskedAt;
        internal bool Changed;
        internal LinkCompatibility? Compatibility;
        internal bool KeepLocal;
        // The drop list the student answered (or saved with), so the same list never asks again.
        internal string? DropsAnswered;
        // The last save down SolidWorks canceled: it isn't saved yet (cleared by the next save).
        internal bool SaveDownCanceled;
        internal LinkSaveMode? Saving;
    }

    // ---- Host-facing --------------------------------------------------------------------------

    // The questions to ask, as Windows notifications, about files SolidWorks opened that this
    // computer has not checked out (C5; docs/agent/BRIDGE.md, "Host-facing"). With the link: only
    // documents the student opened, once per document per SolidWorks session, grouped when
    // opened within 1.5 seconds of each other. Without it: SolidWorks' markers, grouped when they
    // appear within 3 seconds of each other. A prompt stays while its file is open and not
    // checked out here; a host shows each (Path, Group) once.
    public IReadOnlyList<OpenPrompt> OpenPrompts => Volatile.Read(ref openPrompts);
    // Raised on the engine thread when OpenPrompts changes.
    public event Action<IReadOnlyList<OpenPrompt>>? OpenPromptsChanged;
    // The questions before a save down that drops something (research section 3.4 step 4).
    public IReadOnlyList<SaveDownPrompt> SaveDownPrompts => Volatile.Read(ref saveDownPrompts);

    // "Check out and reopen" (C5): checks the files out, then makes each one SolidWorks has open
    // read-only editable right there through the link (in place, keeping unsaved changes; never
    // closing a document or discarding a change). Without the link, a file still open is opened
    // again once the student closes it in SolidWorks (within five minutes). A file not open is
    // opened as "Check out and open" always did. The same as CheckOutAsync(paths, open: true).
    public Task<ActionResult> CheckOutAndReopenAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => CheckOutAsync(paths, open: true, cancellationToken);

    // The student's answer before a save down: keep these files on this computer only (their
    // saves write the running SolidWorks year and stay private drafts), or not.
    public Task<ActionResult> KeepLocalAsync(IReadOnlyList<string> paths, bool keep = true, CancellationToken cancellationToken = default)
        => engineThread.InvokeAsync(() => Task.FromResult(KeepLocalNow(paths, keep)));

    // A SaveDownPrompt's answer: "Save in 2025" (keepLocal false; no answer means the same) or
    // "Keep this file on this computer only" (keepLocal true).
    public Task<ActionResult> AnswerSaveDownAsync(string path, bool keepLocal, CancellationToken cancellationToken = default)
        => engineThread.InvokeAsync(() =>
        {
            if (keepLocal) return Task.FromResult(KeepLocalNow([path], true));
            var docs = OpenDocumentsUnder([path]);
            foreach (var doc in docs) doc.DropsAnswered = DropsText(doc.Compatibility);
            RequestPublish();
            if (docs.Count == 0) return Task.FromResult(new ActionResult(false, $"{NameOf(path)} isn't open in SolidWorks now."));
            var pin = PinOf(docs[0].Path);
            return Task.FromResult(new ActionResult(true, $"Armory saves {NameOf(docs[0].Path)} in SolidWorks {pin} when you save it."));
        });

    // "Save it in 2025 now": an open document is saved again in its project's pinned year.
    public Task<ActionResult> SaveDownNowAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => engineThread.InvokeAsync(async () =>
    {
        if (Link is not { } link || linkSessions.Count == 0) return new ActionResult(false, "SolidWorks isn't linked to Armory now. Open the file in SolidWorks and click Save.");
        var docs = OpenDocumentsUnder(paths).GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (docs.Count == 0) return new ActionResult(false, paths.Count == 1 ? $"Open {NameOf(paths[0])} in SolidWorks first, then try again." : "Open them in SolidWorks first, then try again.");
        List<string> saved = [], failed = [];
        var words = "";
        foreach (var doc in docs)
        {
            var name = NameOf(doc.Path);
            var pin = PinOf(doc.Path);
            if (doc.KeepLocal) { doc.KeepLocal = false; link.KeepLocal(FullPathOf(doc.Path), false); }
            SaveDownOutcome outcome;
            try { outcome = await link.SaveInPinnedReleaseAsync(FullPathOf(doc.Path), cancellationToken).WaitAsync(MakeWritableBudget, deps.Clock, cancellationToken); }
            catch (TimeoutException) { outcome = SaveDownOutcome.Failed; }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                deps.Log?.Invoke($"solidworks: save down of {doc.Path}: {error.Message}");
                outcome = SaveDownOutcome.LinkGone;
            }
            switch (outcome)
            {
                case SaveDownOutcome.Saved: saved.Add(name); break;
                case SaveDownOutcome.ReadOnly: words += $" Check out {name} first: SolidWorks has it read-only."; failed.Add(name); break;
                case SaveDownOutcome.NotOpen: words += $" Open {name} in SolidWorks first."; failed.Add(name); break;
                case SaveDownOutcome.NotNeeded: words += $" {name} doesn't need saving in another SolidWorks year."; failed.Add(name); break;
                case SaveDownOutcome.CantSaveDown: words += " " + (SaveDownReason(doc.Pid, pin, ProjectOfPath(doc.Path)?.Name) ?? $"SolidWorks on this computer can't save {name} in {pin}."); failed.Add(name); break;
                case SaveDownOutcome.LinkGone: words += $" SolidWorks isn't linked to Armory now. Click Save in SolidWorks to save {name}."; failed.Add(name); break;
                default: words += $" SolidWorks couldn't save {name} in {pin}. It stays on this computer only."; failed.Add(name); break;
            }
        }
        RequestPublish();
        var pins = docs.Select(d => PinOf(d.Path)).Distinct().ToList();
        var lead = saved.Count == 0 ? "" : saved.Count == 1 ? $"Saved {saved[0]} in SolidWorks {pins[0]}." : $"Saved {Count(saved.Count, "file", "files")} in their projects' SolidWorks year.";
        return new ActionResult(saved.Count > 0, (lead + words).Trim());
    });

    // ---- The record pump ------------------------------------------------------------------------

    // Started with the engine: each record is handled on the engine thread, in order.
    private void StartLinkPump()
    {
        if (Link is not { } link || linkPump is not null) return;
        linkPump = Task.Run(() => PumpLinkAsync(link.Records, stopping.Token));
        try { link.Replay(); }
        catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke("solidworks: replay: " + error.Message); }
    }

    private async Task PumpLinkAsync(ChannelReader<LinkRecord> records, CancellationToken ct)
    {
        try
        {
            while (await records.WaitToReadAsync(ct))
                while (records.TryPeek(out _))
                {
                    // Counted before it leaves the channel, so LinkSettledAsync never sees it nowhere.
                    Interlocked.Increment(ref linkRecordsRead);
                    if (!records.TryRead(out var record)) { Interlocked.Increment(ref linkRecordsHandled); continue; }
                    try { await engineThread.InvokeAsync(() => { OnLinkRecord(record); return Task.FromResult(true); }); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        flight?.Exception("solidworks link", error, fatal: false);
                        deps.Log?.Invoke("solidworks: " + error.GetType().Name + ": " + error.Message);
                    }
                    finally { Interlocked.Increment(ref linkRecordsHandled); }
                }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    // Every record the link sent so far has been handled (tests wait on it).
    internal async Task LinkSettledAsync()
    {
        if (Link is not { } link) return;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        bool Waiting(ChannelReader<LinkRecord> records) => records.CanCount ? records.Count > 0 : records.CanPeek && records.TryPeek(out _);
        while (Waiting(link.Records) || Interlocked.Read(ref linkRecordsRead) != Interlocked.Read(ref linkRecordsHandled))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The SolidWorks link's records were not handled within 30 seconds.");
            await Task.Delay(5);
        }
        await engineThread.InvokeAsync(() => Task.FromResult(true));
    }

    private void OnLinkRecord(LinkRecord record)
    {
        switch (record)
        {
            case LinkAttached attached:
                linkSessions.RemoveAll(s => s.ProcessId == attached.ProcessId);
                linkSessions.Add(attached);
                linkRefused.Remove(attached.ProcessId);
                SetRunningSolidWorks();
                pinsSent = [];
                SendPins();
                deps.Log?.Invoke($"solidworks: linked to {attached.Revision} (pid {attached.ProcessId}), save down {attached.SaveDown}");
                break;
            case LinkRefused refusedRecord:
                if (linkRefused.Add(refusedRecord.ProcessId))
                {
                    activity.Log("SolidWorks was started as administrator, so Armory can't link to it. Close SolidWorks and start it normally.");
                    deps.Log?.Invoke($"solidworks: can't link to pid {refusedRecord.ProcessId}: {refusedRecord.Reason}");
                }
                break;
            case LinkDetached detached:
                linkRefused.Remove(detached.ProcessId);
                if (linkSessions.RemoveAll(s => s.ProcessId == detached.ProcessId) > 0)
                    deps.Log?.Invoke($"solidworks: unlinked from pid {detached.ProcessId}: {detached.Reason}");
                foreach (var (key, doc) in linkDocuments.Where(d => d.Value.Pid == detached.ProcessId).ToArray()) ForgetDocument(key, doc);
                askedOnce.SessionEnded(detached.ProcessId);
                SetRunningSolidWorks();
                wake.Release();
                break;
            case LinkOpened opened when ToVault(opened.Path) is { } path:
            {
                var key = DocumentKey(opened.ProcessId, path.Value);
                if (linkDocuments.TryGetValue(key, out var existing)) ForgetDocument(key, existing);
                var doc = new LinkDocument(opened.ProcessId, path.Value)
                {
                    DocType = opened.DocType, ReadOnly = opened.ReadOnly, FutureVersion = opened.FutureVersion, OpenedAt = opened.At,
                };
                linkDocuments[key] = doc;
                linkOpen[path.Value] = linkOpen.GetValueOrDefault(path.Value) + 1;
                if (opened.TopLevel) StudentOpened(doc, opened.At);
                break;
            }
            case LinkActivated activated when ToVault(activated.Path) is { } path:
                if (linkDocuments.TryGetValue(DocumentKey(activated.ProcessId, path.Value), out var active)) StudentOpened(active, activated.At);
                break;
            case LinkModified modified when Document(modified.ProcessId, modified.Path) is { } changed:
                changed.Changed = true;
                break;
            case LinkSaving saving when ToVault(saving.Path) is { } path:
                savingHolds[path.Value] = (saving.SaveId, deps.Clock.GetUtcNow());
                if (Document(saving.ProcessId, saving.Path) is { } beingSaved) beingSaved.Saving = saving.Mode;
                break;
            case LinkSaved saved when ToVault(saved.Path) is { } path:
                OnSaved(saved, path);
                break;
            case LinkSaveCanceled canceled when ToVault(canceled.Path) is { } path:
                savingHolds.Remove(path.Value);
                if (Document(canceled.ProcessId, canceled.Path) is { } notSaved)
                {
                    notSaved.Saving = null;
                    // SolidWorks' own Previous Release Check stopped a save down: say so.
                    if (canceled.Mode == LinkSaveMode.SaveDown) notSaved.SaveDownCanceled = true;
                }
                break;
            case LinkCompatibility compatibility when Document(compatibility.ProcessId, compatibility.Path) is { } checkedDoc:
                checkedDoc.Compatibility = compatibility;
                break;
            case LinkClosed closed when ToVault(closed.Path) is { } path:
                if (linkDocuments.TryGetValue(DocumentKey(closed.ProcessId, path.Value), out var gone)) ForgetDocument(DocumentKey(closed.ProcessId, path.Value), gone);
                // A file waiting to open again once closed: the next pass sees it closed.
                if (reopenWhenClosed.ContainsKey(path.Value)) wake.Release();
                break;
            case LinkStamped stamped:
                KeepStamp(stamped.Stamp);
                break;
        }
        RequestPublish();
    }

    // The student opened this document themselves (it was SolidWorks' active document): it is
    // asked about once in this SolidWorks session.
    private void StudentOpened(LinkDocument doc, DateTimeOffset at)
    {
        if (!doc.TopLevel)
        {
            doc.TopLevel = true;
            doc.OpenedAt = at;
        }
        if (!doc.Asked && askedOnce.TryAsk(doc.Path, doc.Pid))
        {
            doc.Asked = true;
            doc.AskedAt = at;
        }
    }

    private void OnSaved(LinkSaved saved, VaultPath path)
    {
        if (saved.Stamp is { } stamp) KeepStamp(stamp);
        savingHolds.Remove(path.Value);
        if (Document(saved.ProcessId, saved.Path) is { } doc)
        {
            var mode = doc.Saving;
            doc.Saving = null;
            doc.Changed = false;
            doc.SaveDownCanceled = false;
            var pin = PinOf(doc.Path);
            if (mode == LinkSaveMode.SaveDown && saved.Stamp?.Year is { } year && year == pin)
            {
                // Saved down as meant: the drop list it was saved with doesn't ask again.
                var drops = DropsText(doc.Compatibility);
                doc.DropsAnswered = drops;
                activity.Log($"Saved {path.Name} in SolidWorks {pin}." + (drops is null ? "" : $" Not kept: {drops}."));
            }
        }
        lastActivity = deps.Clock.GetUtcNow();
        // The bytes may go now: the next pass decides with the stamp.
        wake.Release();
    }

    // A stamp the link made, kept as RecordReleaseStampAsync keeps one.
    private void KeepStamp(ReleaseStamp stamp)
    {
        if (!IsSha256(stamp.Sha256)) return;
        var hash = stamp.Sha256.ToLowerInvariant();
        stamp = stamp with { Sha256 = hash };
        state.ReleaseStamps.TryGetValue(hash, out var existing);
        state.ReleaseStamps[hash] = new StampRecord(SavedReleaseRule.Merge(existing?.Stamp, stamp), existing?.Committed);
        MarkDirty();
    }

    private void ForgetDocument(string key, LinkDocument doc)
    {
        linkDocuments.Remove(key);
        if (linkOpen.TryGetValue(doc.Path, out var count))
        {
            if (count <= 1) linkOpen.Remove(doc.Path);
            else linkOpen[doc.Path] = count - 1;
        }
        if (linkDocuments.Values.All(d => !string.Equals(d.Path, doc.Path, StringComparison.OrdinalIgnoreCase))) savingHolds.Remove(doc.Path);
    }

    // The running SolidWorks the words speak of: the one that attached last.
    private void SetRunningSolidWorks()
    {
        var newest = linkSessions.LastOrDefault();
        solidWorks = newest is not null && SolidWorksRevision.Parse(newest.Revision) is { } revision ? (revision, newest.SaveDown == SaveToVersionSupport.Available) : null;
        solidWorksSupport = newest?.SaveDown;
    }

    // ---- What the engine tells the link -----------------------------------------------------------

    // The projects' pins, as folders on this disk, sent when they change (after a read of the server).
    private void SendPins()
    {
        if (Link is not { } link) return;
        var pins = state.Projects.Values.Where(p => p.Usable && !p.Archived && p.Folder.Length > 0)
            .OrderBy(p => p.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProjectPin(FullPathOf(p.Folder), p.PinnedRelease, p.Enforce)).ToList();
        if (pins.SequenceEqual(pinsSent)) return;
        pinsSent = pins;
        try { link.SetPins(pins); }
        catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke("solidworks: pins: " + error.Message); }
    }

    private ActionResult KeepLocalNow(IReadOnlyList<string> paths, bool keep)
    {
        var docs = OpenDocumentsUnder(paths);
        if (docs.Count == 0) return new(false, paths.Count == 1 ? $"{NameOf(paths[0])} isn't open in SolidWorks now." : "None of them is open in SolidWorks now.");
        foreach (var doc in docs)
        {
            doc.KeepLocal = keep;
            if (!keep) doc.DropsAnswered = DropsText(doc.Compatibility);
            try { Link?.KeepLocal(FullPathOf(doc.Path), keep); }
            catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke("solidworks: keep local: " + error.Message); }
        }
        RequestPublish();
        var first = docs[0];
        var pin = PinOf(first.Path);
        var names = docs.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (!keep) return new(true, names == 1 ? $"Armory saves {NameOf(first.Path)} in SolidWorks {pin} when you save it." : $"Armory saves {Count(names, "file", "files")} in their projects' SolidWorks year when you save them.");
        return new(true, names == 1
            ? $"{NameOf(first.Path)} stays on this computer only when you save it. Nobody else gets those changes until it is saved in SolidWorks {pin}."
            : $"{Count(names, "file stays", "files stay")} on this computer only when you save them. Nobody else gets those changes until they are saved in their projects' SolidWorks year.");
    }

    // ---- Check out and reopen ---------------------------------------------------------------------

    // After the check out (the gate released): each checked-out file open in SolidWorks is made
    // writable there through the link, or opened again once it is closed; a single file that is
    // not open is opened (Check out and open). One sentence for all of it: message is what the
    // check out came to, mine the files this computer has checked out now.
    private async Task<ActionResult> ReopenAfterCheckOutAsync(string message, List<(FileState State, VaultPath Path)> mine, CancellationToken ct)
    {
        if (mine.Count == 0) return new(false, message);
        List<VaultPath> open;
        using (KnowOpen(mine.Select(m => m.Path))) open = mine.Where(m => IsOpenNow(m.Path)).Select(m => m.Path).ToList();
        if (open.Count == 0)
        {
            if (mine.Count == 1)
            {
                var launched = fs.Launch(mine[0].Path);
                if (!launched.Succeeded) message += " " + launched.Problem;
            }
            return new(true, message);
        }
        List<VaultPath> writable = [], unsaved = [], whenClosed = [], slow = [];
        foreach (var path in open)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = WritableOutcome.LinkGone;
            if (Link is { } link && linkOpen.ContainsKey(path.Value))
            {
                try { outcome = await link.MakeWritableAsync(FullPathOf(path.Value), ct).WaitAsync(MakeWritableBudget, deps.Clock, ct); }
                catch (TimeoutException) { slow.Add(path); continue; }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    deps.Log?.Invoke($"solidworks: make writable {path}: {error.GetType().Name}: {error.Message}");
                    outcome = WritableOutcome.LinkGone;
                }
                deps.Log?.Invoke($"solidworks: make writable {path}: {outcome}");
            }
            switch (outcome)
            {
                case WritableOutcome.AlreadyWritable or WritableOutcome.MadeWritableInPlace or WritableOutcome.Reopened:
                    writable.Add(path);
                    foreach (var doc in linkDocuments.Values.Where(d => string.Equals(d.Path, path.Value, StringComparison.OrdinalIgnoreCase))) doc.ReadOnly = false;
                    break;
                case WritableOutcome.HasUnsavedChanges:
                    unsaved.Add(path);
                    break;
                case WritableOutcome.NotOpen:
                    // Closed meanwhile: it opens writable from now on, as any closed file does.
                    writable.Add(path);
                    break;
                default:
                    // No link, or it couldn't: opened again once the student closes it.
                    reopenWhenClosed[path.Value] = deps.Clock.GetUtcNow();
                    whenClosed.Add(path);
                    break;
            }
        }
        RequestPublish();
        return new(true, message + ReopenWords(mine.Count, open.Count, writable, unsaved, whenClosed, slow));
    }

    // The second half of "Check out and reopen"'s sentence.
    internal static string ReopenWords(int checkedOut, int open, IReadOnlyList<VaultPath> writable, IReadOnlyList<VaultPath> unsaved,
        IReadOnlyList<VaultPath> whenClosed, IReadOnlyList<VaultPath> slow)
    {
        if (checkedOut == 1 && open == 1)
        {
            if (writable.Count == 1) return " You can save it in SolidWorks now.";
            if (unsaved.Count == 1)
                return $" SolidWorks still has it read-only. To save, close it in SolidWorks and open it again from Armory. Changes made before the check out can't be saved to it.";
            if (slow.Count == 1) return $" SolidWorks is still reopening {slow[0].Name}.";
            return " Close it in SolidWorks and Armory opens it again, ready to save.";
        }
        var words = "";
        if (writable.Count == open) words += open == checkedOut ? " You can save them in SolidWorks now." : " You can save the open ones in SolidWorks now.";
        else if (writable.Count > 0) words += $" You can save {NamesOf(writable)} in SolidWorks now.";
        if (unsaved.Count > 0)
            words += $" {(unsaved.Count == 1 ? "1 of them is" : $"{unsaved.Count:N0} of them are")} still read-only in SolidWorks: {NamesOf(unsaved)}. " +
                $"To save, close {(unsaved.Count == 1 ? "it" : "them")} in SolidWorks and open {(unsaved.Count == 1 ? "it" : "them")} again from Armory.";
        if (whenClosed.Count > 0)
            words += $" Close {NamesOf(whenClosed)} in SolidWorks and Armory opens {(whenClosed.Count == 1 ? "it" : "them")} again, ready to save.";
        if (slow.Count > 0) words += $" SolidWorks is still reopening {NamesOf(slow)}.";
        return words;
    }

    private static string NamesOf(IReadOnlyList<VaultPath> paths) => Names(paths.Select(p => p.Name).ToList());

    // After every pass: files checked out with "Check out and reopen" while SolidWorks had them
    // open, closed now, open again (once each, within five minutes of the click).
    private void ReopenClosed()
    {
        if (reopenWhenClosed.Count == 0) return;
        var now = deps.Clock.GetUtcNow();
        foreach (var (key, asked) in reopenWhenClosed.ToArray())
        {
            if (now - asked > ReopenWhenClosedFor || !VaultPath.TryCreate(key, out var path, out _, options.VaultRoot)) { reopenWhenClosed.Remove(key); continue; }
            if (!TryLocal(key, out var here) || IsOpenNow(here.Path)) continue;
            reopenWhenClosed.Remove(key);
            var target = here.Path;
            _ = Task.Run(() =>
            {
                var outcome = fs.Launch(target);
                if (!outcome.Succeeded) deps.Log?.Invoke($"reopen {target}: {outcome.Problem}");
            });
        }
    }

    // ---- Saving -----------------------------------------------------------------------------------

    // SolidWorks is saving this path: nothing of it is captured or decided until it says it
    // saved (or two minutes passed), so the bytes are decided with their stamp.
    private bool SavingHeld(string key)
    {
        if (savingHolds.Count == 0 || !savingHolds.TryGetValue(key, out var hold)) return false;
        if (deps.Clock.GetUtcNow() - hold.At <= SavingHeldFor) return true;
        savingHolds.Remove(key);
        return false;
    }

    // The gate's mode for these bytes: the project's, except that a save down SolidWorks did not
    // confirm stays here even in Warn (SavedReleaseRule.UnverifiedSaveDown).
    private ReleaseGateMode GateModeFor(ProjectState project, string? hash)
        => project.Enforce || (hash is not null && SavedReleaseRule.UnverifiedSaveDown(state.ReleaseStamps.GetValueOrDefault(hash)?.Stamp))
            ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn;

    // The refusal words the link knows better than the gate: a file that can't go back to the
    // pinned year (blocked), one the student keeps here, or a save down SolidWorks didn't do.
    private string? LinkGateWords(VaultPath path, string? hash, int pin)
    {
        if (hash is not null && SavedReleaseRule.UnverifiedSaveDown(state.ReleaseStamps.GetValueOrDefault(hash)?.Stamp))
            return $"SolidWorks on this computer couldn't save {path.Name} in {pin}, so it stays on this computer only. Nobody else gets these changes yet. Ask a CAD lead or a mentor what to do.";
        var doc = linkDocuments.Values.FirstOrDefault(d => string.Equals(d.Path, path.Value, StringComparison.OrdinalIgnoreCase));
        if (doc?.Compatibility is { Blocked.Count: > 0 } blocked && blocked.TargetYear == pin) return BlockedWords(path.Name, blocked, pin);
        if (doc?.KeepLocal == true) return KeptWords(pin);
        return null;
    }

    // ---- Words ------------------------------------------------------------------------------------

    // Research section 3.4 step 3: what the pinned year doesn't have, in SolidWorks' own words.
    internal static string BlockedWords(string name, LinkCompatibility compatibility, int pin)
    {
        var items = compatibility.Blocked.Select(b => Sentence(b.Message)).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var what = items.Count == 0 ? "something" : Names(items);
        var hint = compatibility.Blocked.Select(b => Sentence(b.Action ?? "")).FirstOrDefault(a => a.Length > 0);
        return $"It uses {what}, which SolidWorks {pin} doesn't have. Change it to something {pin} has" + (hint is null ? "" : $" (SolidWorks suggests: {hint})") +
            ", then save again. Until then nobody else gets these changes.";
    }

    private static string KeptWords(int pin) => $"You chose to keep it here. Until it is saved in SolidWorks {pin}, nobody else gets these changes.";

    // "3 appearances (colors), 2 explode steps, 1 simulation study" (research section 3.4 step
    // 4); null when nothing is dropped.
    internal static string? DropsText(LinkCompatibility? compatibility)
    {
        if (compatibility is null || compatibility.Drops.Count == 0) return null;
        List<string> parts = [];
        foreach (var drop in compatibility.Drops)
        {
            var part = drop.Kind switch
            {
                DropItem.Appearances when drop.Count > 0 => drop.Count == 1 ? "1 appearance (color)" : $"{drop.Count:N0} appearances (colors)",
                DropItem.Decals when drop.Count > 0 => Count(drop.Count, "decal", "decals"),
                DropItem.Lights when drop.Count > 0 => Count(drop.Count, "light", "lights"),
                DropItem.CustomProperties when drop.Count > 0 => Count(drop.Count, "custom property", "custom properties"),
                DropItem.ExplodeSteps when drop.Count > 0 => Count(drop.Count, "explode step", "explode steps"),
                DropItem.SimulationStudies => drop.Count > 0 ? Count(drop.Count, "simulation study", "simulation studies") : "simulation studies, if any",
                DropItem.Warning when !string.IsNullOrWhiteSpace(drop.Text) => Sentence(drop.Text),
                _ => null,
            };
            if (part is not null && !parts.Contains(part, StringComparer.Ordinal)) parts.Add(part);
        }
        // "Simulation studies, if any" alone is no reason to ask.
        if (parts.Count == 0 || (parts.Count == 1 && parts[0] == "simulation studies, if any")) return null;
        return string.Join(", ", parts);
    }

    // SolidWorks' own text, without a trailing period and surrounding space.
    private static string Sentence(string text) => text.Trim().TrimEnd('.').Trim();

    // Why this SolidWorks can't save down to a project's pin, or null when it can (or doesn't need to).
    private string? SaveDownReason(int pid, int pin, string? project)
    {
        var session = linkSessions.FirstOrDefault(s => s.ProcessId == pid) ?? linkSessions.LastOrDefault();
        if (session is null || SolidWorksRevision.Parse(session.Revision) is not { } revision) return null;
        return SaveDownReason(revision, session.SaveDown, pin, project);
    }

    // Research section 3.5, for the Settings line and the actions' answers.
    internal static string? SaveDownReason(SolidWorksRevision running, SaveToVersionSupport support, int pin, string? project)
    {
        var plan = SaveDown.Plan(running, pin);
        if (plan is SaveDownPlan.NotNeeded) return null;
        if (plan is SaveDownPlan.TooFarApart)
            return $"SolidWorks {running.Year} can't save files as {pin}. Files you save stay on this computer only. A mentor can raise " +
                $"{project ?? "the project"}'s SolidWorks year once everyone can use the new one.";
        if (plan is SaveDownPlan.OldServicePack || (plan.CanSave() && support is SaveToVersionSupport.NotConfigured or SaveToVersionSupport.OldServicePack))
            return $"Update SolidWorks {running.Year} to Service Pack 3 or newer so Armory can save team files in {pin}. Until then, files you save stay on this computer only.";
        if (plan.CanSave() && support == SaveToVersionSupport.NotLicensed)
            return $"SolidWorks on this computer couldn't save files in {pin}, so files you save stay on this computer only. Nobody else gets these changes yet. Ask a CAD lead or a mentor what to do.";
        if (!plan.CanSave()) return $"SolidWorks {running.Year} can't save files as {pin}. Files you save stay on this computer only.";
        return null;
    }

    // ---- The view's parts -------------------------------------------------------------------------

    // The Settings line for the SolidWorks link (null when this computer has no link).
    private SolidWorksView? SolidWorksStatus()
    {
        if (Link is null) return null;
        if (linkSessions.Count == 0)
            return linkRefused.Count > 0
                ? new SolidWorksView(SolidWorksStates.Administrator, "SolidWorks was started as administrator, so Armory can't link to it.",
                    "Close SolidWorks and start it normally, not as administrator.")
                : new SolidWorksView(SolidWorksStates.None, "SolidWorks isn't running.", null);
        var session = linkSessions[^1];
        if (SolidWorksRevision.Parse(session.Revision) is not { } revision) return new SolidWorksView(SolidWorksStates.Attached, "Linked to SolidWorks.", null);
        var linked = $"Linked to {revision.DisplayName}.";
        var pins = state.Projects.Values.Where(p => p.Usable && !p.Archived && p.PinnedRelease >= 1995 && p.PinnedRelease < revision.Year)
            .GroupBy(p => p.PinnedRelease).OrderByDescending(g => g.Key).ToList();
        if (pins.Count == 0) return new SolidWorksView(SolidWorksStates.Attached, linked, null);
        var reasons = pins.Select(g => (g.Key, Reason: SaveDownReason(revision, session.SaveDown, g.Key, g.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).First().Name))).ToList();
        var years = string.Join(" and ", pins.Select(g => g.Key.ToString(CultureInfo.InvariantCulture)));
        if (reasons.All(r => r.Reason is null)) return new SolidWorksView(SolidWorksStates.Attached, $"{linked} It saves team files in {years}.", null);
        var first = reasons.First(r => r.Reason is not null);
        return new SolidWorksView(SolidWorksStates.CantSaveDown, $"{linked} It can't save team files in {first.Key}.", first.Reason);
    }

    // The window's question (PromptView), OpenWithoutCheckOut for the tray, and OpenPrompts for
    // notifications, from the link's documents while a SolidWorks is linked, else from markers.
    private PromptView? Prompt()
    {
        PromptView? prompt = null;
        List<string> open = [];
        List<OpenPrompt> prompts = [];
        if (linkSessions.Count > 0)
        {
            // Documents the student opened, newest first; one per path (two SolidWorks may have it).
            var docs = linkDocuments.Values.Where(d => d.TopLevel).OrderByDescending(d => d.OpenedAt).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var asked = docs.Where(d => d.Asked).ToList();
            var groups = OpenBursts.LinkGroups(asked.Select(d => (d.Path, d.AskedAt)))
                .SelectMany(b => b.Paths.Select(p => (Path: p, Group: b.First.UtcTicks))).ToDictionary(x => x.Path, x => x.Group, StringComparer.OrdinalIgnoreCase);
            foreach (var doc in docs)
            {
                if (Asking(doc.Path, doc.ReadOnly) is not { } ask) continue;
                if (ask.Kind != OpenAskKind.Reopen) open.Add(doc.Path);
                if (doc.Asked && groups.TryGetValue(doc.Path, out var group))
                    prompts.Add(new OpenPrompt(ask.St.Path, NameOf(ask.St.Path), ask.Id, ask.Holder, true, group, ask.Kind));
                if (ask.Kind == OpenAskKind.Reopen) continue;
                var key = PromptKey(doc.Path, doc.OpenedAt);
                if (prompt is not null || dismissedPrompts.Contains(key)) continue;
                prompt = new PromptView(key, ask.Id.ToString(), ask.St.Path, NameOf(ask.St.Path), ask.Checkout, ask.Kind == OpenAskKind.CheckOut);
            }
        }
        else
        {
            List<(string Path, DateTimeOffset At)> markers = [];
            foreach (var (document, firstSeen) in markerFirstSeen.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!markerDocuments.Contains(document) || Asking(HomeOf(document) ?? document, true) is not { } ask || ask.Kind == OpenAskKind.Reopen) continue;
                open.Add(document);
                markers.Add((document, firstSeen));
                var key = PromptKey(document, firstSeen);
                if (prompt is not null || dismissedPrompts.Contains(key)) continue;
                // The file's own path (where it goes back to, when its folder is away), so Check out finds it.
                prompt = new PromptView(key, ask.Id.ToString(), ask.St.Path, NameOf(ask.St.Path), ask.Checkout, ask.Kind == OpenAskKind.CheckOut);
            }
            // SolidWorks makes a marker for every document it loads (an assembly's parts too):
            // markers that appear together are one burst, one question.
            foreach (var burst in OpenBursts.MarkerBursts(markers))
                foreach (var document in burst.Paths)
                    if (Asking(HomeOf(document) ?? document, true) is { } ask)
                        prompts.Add(new OpenPrompt(ask.St.Path, NameOf(ask.St.Path), ask.Id, ask.Holder, false, burst.First.UtcTicks, ask.Kind));
        }
        Volatile.Write(ref openWithoutCheckOut, open.ToArray());
        prompts.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        if (!prompts.SequenceEqual(openPrompts))
        {
            Volatile.Write(ref openPrompts, prompts.ToArray());
            try { OpenPromptsChanged?.Invoke(openPrompts); }
            catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke("open prompts: " + error.Message); }
        }
        return prompt;
    }

    // What to ask about one open document the team has, by who has it (OpenAsk); null for none.
    private (FileState St, Guid Id, OpenAskKind Kind, CheckoutView Checkout, string? Holder)? Asking(string path, bool openedReadOnly)
    {
        if (!state.Files.TryGetValue(path, out var st) || st.FileId is not { } id) return null;
        LockOwnership ownership;
        CheckoutView checkout;
        if (remoteById.TryGetValue(id, out var remote))
        {
            if (remote.File.Deleted || remote.File.Current is null) return null;
            ownership = OwnershipOf(remote.File.Lock);
            checkout = CheckoutOf(remote.File.Lock);
        }
        else if (st.BaseHash is not null) (ownership, checkout) = (KnownOwnership(st), KnownCheckout(st)); // offline: as last known
        else return null;
        var kind = OpenAsk.Decide(ownership, openedReadOnly, topLevel: true, tracked: true);
        if (kind == OpenAskKind.None) return null;
        var holder = kind is OpenAskKind.HeldByOther or OpenAskKind.HeldOnMyOtherComputer && checkout.Name is { } name
            ? $"{name} on {checkout.Device ?? "another computer"}" : null;
        return (st, id, kind, checkout, holder);
    }

    // A dismissed question is remembered while its open lasts (a marker's, or a link document's).
    private bool PromptStillOpen(string key)
        => markerFirstSeen.Any(m => key == PromptKey(m.Key, m.Value)) || linkDocuments.Values.Any(d => d.TopLevel && key == PromptKey(d.Path, d.OpenedAt));

    // The link's notices (kind solidWorks): per open vault document, the one thing to say before
    // or around a save in the pinned year, plus a SolidWorks started as administrator. Also
    // refreshes SaveDownPrompts.
    private IEnumerable<(string Id, string Path, string Title, string Detail, NoticeActionView? Action, string Flavor)> LinkNotices()
    {
        List<(string, string, string, string, NoticeActionView?, string)> items = [];
        List<SaveDownPrompt> asks = [];
        foreach (var pid in linkRefused.Order())
            items.Add(($"solidWorks:administrator:{pid}", "", "SolidWorks was started as administrator, so Armory can't link to it",
                "Close SolidWorks and start it normally, not as administrator. Until then, check files out here and open them again in SolidWorks to save.", null, "administrator"));
        foreach (var doc in linkDocuments.Values.GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(d => d.OpenedAt).First())
                     .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (ProjectOfPath(doc.Path) is not { } project || !VaultPath.TryCreate(doc.Path, out var path, out _, options.VaultRoot)) continue;
            var pin = project.PinnedRelease;
            var session = linkSessions.FirstOrDefault(s => s.ProcessId == doc.Pid);
            if (session is null || SolidWorksRevision.Parse(session.Revision) is not { } revision) continue;
            var canSave = SaveDown.Plan(revision, pin).CanSave() && session.SaveDown == SaveToVersionSupport.Available;
            var name = path.Name;
            if (doc.SaveDownCanceled)
            {
                items.Add(($"solidWorks:canceled:{doc.Path}", doc.Path, $"SolidWorks couldn't save {name} in {pin}, so it isn't saved yet",
                    "Click Save again to keep it on this computer (only you will have it), then fix what SolidWorks listed.", null, "canceled"));
                continue;
            }
            // B5 on a computer with the pinned year: SolidWorks says this file is newer than it.
            if (doc.FutureVersion && revision.Year == pin && FileYear(doc.Path) is null)
            {
                items.Add(($"solidWorks:future:{doc.Path}", doc.Path, $"{name} was saved in a newer SolidWorks than {pin}",
                    $"You can open parts and assemblies to look, but you can't change them here, and drawings won't open. Someone with a newer SolidWorks can fix it: " +
                    "check it out, open it, click Save, and check it in.", null, "future"));
                continue;
            }
            if (!canSave) continue;
            if (doc.Compatibility is { Blocked.Count: > 0 } blocked && blocked.TargetYear == pin)
            {
                items.Add(($"solidWorks:blocked:{doc.Path}", doc.Path, $"{name} is saved on this computer only", BlockedWords(name, blocked, pin), null, "blocked"));
                continue;
            }
            var saveNow = new NoticeActionView($"Save it in {pin} now", BridgeMessages.SaveDown, [doc.Path]);
            if (doc.KeepLocal)
            {
                items.Add(($"solidWorks:kept:{doc.Path}", doc.Path, $"{name} is saved on this computer only", KeptWords(pin), saveNow, "kept"));
                continue;
            }
            // B5 on a computer that can fix it: this copy was saved in a newer year, and the
            // student has it checked out, so Armory can save it in the pinned year right now.
            if (FileYear(doc.Path) is { } year && year > pin && !doc.ReadOnly)
            {
                items.Add(($"solidWorks:saveNow:{doc.Path}", doc.Path, $"{name} was saved in SolidWorks {year}",
                    $"{project.Name} uses SolidWorks {pin}. Armory can save it in {pin} for you now.", saveNow, "saveNow"));
                continue;
            }
            if (DropsText(doc.Compatibility) is { } drops && doc.Compatibility!.TargetYear == pin && drops != doc.DropsAnswered)
            {
                var title = $"When you save {name}, Armory saves it in SolidWorks {pin}";
                var detail = $"Your team uses {pin}, and {pin} can't keep: {drops}. Part numbers and descriptions are kept by Armory.";
                items.Add(($"solidWorks:drops:{doc.Path}:{drops}", doc.Path, title, detail,
                    new NoticeActionView("Keep this file on this computer only", BridgeMessages.KeepLocal, [doc.Path]), "drops"));
                asks.Add(new SaveDownPrompt($"savedown:{doc.Path}:{drops}", doc.Path, name, title, detail, pin));
            }
        }
        if (!asks.SequenceEqual(saveDownPrompts)) Volatile.Write(ref saveDownPrompts, asks.ToArray());
        return items;
    }

    // The SolidWorks year this computer knows for the bytes of a file here now (a stamp or the reader).
    private int? FileYear(string path)
        => TryLocal(path, out var file) ? KnownRelease(state.Files.GetValueOrDefault(path), file.Hash)?.Year : null;

    // ---- Paths ------------------------------------------------------------------------------------

    private static string DocumentKey(int pid, string path) => pid.ToString(CultureInfo.InvariantCulture) + "|" + path;

    private LinkDocument? Document(int pid, string fullPath)
        => ToVault(fullPath) is { } path && linkDocuments.TryGetValue(DocumentKey(pid, path.Value), out var doc) ? doc : null;

    // The open documents at or under these vault paths, in any linked SolidWorks.
    private List<LinkDocument> OpenDocumentsUnder(IReadOnlyList<string> paths)
        => linkDocuments.Values.Where(d => Under(d.Path, paths)).OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Pid).ToList();

    private ProjectState? ProjectOfPath(string path) => VaultPath.TryCreate(path, out var vault, out _, options.VaultRoot) ? ProjectOf(vault) : null;

    private int PinOf(string path) => ProjectOfPath(path)?.PinnedRelease ?? 0;

    // "C:\IDEA\Armory\Robot 2027\Plate.SLDPRT" for "Robot 2027/Plate.SLDPRT" (a folder works too).
    internal string FullPathOf(string vaultRelative) => options.VaultRoot.TrimEnd('\\', '/') + "\\" + vaultRelative.Replace('/', '\\');

    // A full path SolidWorks names, as a vault path; null when it is not in the vault.
    internal VaultPath? ToVault(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        var root = options.VaultRoot.Replace('/', '\\').TrimEnd('\\') + "\\";
        var full = fullPath.Replace('/', '\\');
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length) return null;
        return VaultPath.TryCreate(full[root.Length..].Replace('\\', '/'), out var path, out _, options.VaultRoot) ? path : null;
    }
}
