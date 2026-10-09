using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.SolidWorks;

// What every session shares: the link's thread, settings and channel.
internal sealed class LinkContext
{
    public required StaThread Thread { get; init; }
    public required LinkSettings Settings { get; init; }
    public required Action<LinkRecord> Emit { get; init; }
    public Action<string>? Log { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string VaultRoot { get; set; } = "";
    public IReadOnlyList<ProjectPin> Pins { get; set; } = [];
    // How long changes must be quiet before a document is checked again (research 3.4 step 2).
    public TimeSpan ModifySettle { get; init; } = TimeSpan.FromSeconds(3);
    // Another session of this SolidWorks release is linked in this Armory.
    public Func<int, int, bool> OtherSessionOfRelease { get; init; } = (_, _) => false;
    // The thread's message filter (null where none could be registered).
    public MessageFilter? Filter { get; set; }
}

// One SolidWorks process, linked out of process (docs/agent/SOLIDWORKS.md). Everything here runs
// on the link's STA thread. Event handlers answer from memory at once (SolidWorks waits for every
// reply) and post anything that calls back into SolidWorks. Every reference received is released;
// at the end (SolidWorks closed, Armory quit, the process died) every event is unadvised and
// every object let go, and a COM failure only ends this session, quietly.
internal sealed class LinkSession : ISaveDownCalls
{
    internal enum Phase { Finding, Starting, Attached, Gone }
    private static readonly TimeSpan StartingCallLimit = TimeSpan.FromSeconds(1), ClosingCallLimit = TimeSpan.FromSeconds(2);

    private readonly LinkContext context;
    private readonly string? executable;
    private readonly ConcurrentDictionary<string, Document> documents = new(StringComparer.Ordinal);
    private readonly List<(Guid Iid, int Dispid, Delegate Handler)> appSinks = [];
    private readonly Dictionary<int, int?> cancelDispids = [];
    private object? app;
    private SolidWorksRevision revision;
    private SaveDownManager? saveDown;
    private string? activePath;
    private int unsavedCount;
    private bool startingFailureLogged;

    // One open document this session watches: a vault document, or a new one not saved yet.
    private sealed class Document(object rcw, string path, int docType)
    {
        internal object Rcw { get; set; } = rcw;
        internal string Path { get; set; } = path;
        internal int DocType { get; } = docType;
        internal string Key { get; set; } = "";
        internal List<(Guid Iid, int Dispid, Delegate Handler)> Sinks { get; } = [];
        // A ModifyNotify since it was opened or saved; null: unknown (open before the link attached).
        internal bool? Changed { get; set; }
        internal bool TopLevel { get; set; }
        internal bool ReadOnly { get; set; }
        internal bool FutureVersion { get; set; }
        internal DateTimeOffset OpenedAt { get; set; }
        internal long ModifiedAt;
        // The save in progress: its id and what Save to Version made of it.
        internal Guid SaveId;
        internal LinkSaveMode SaveMode;
        internal string? SavingPath;
    }

    // The handlers' signatures as SolidWorks' dispinterfaces declare them; each returns 0.
    private delegate int NoArgs();
    private delegate int OneString(string fileName);
    private delegate int StringAndInt(string fileName, int reason);
    private delegate int OneInt(int value);
    private delegate int IntAndString(int saveType, string fileName);
    private delegate int NewDocument(object newDoc, int docType, string templateName);

    internal LinkSession(int pid, string? executable, LinkContext context)
    {
        Pid = pid;
        this.executable = executable;
        this.context = context;
    }

    internal int Pid { get; }
    internal Phase State { get; private set; } = Phase.Finding;
    internal SolidWorksRevision Revision => revision;
    internal SaveToVersionSupport Support => saveDown?.Support ?? SaveToVersionSupport.Unsupported;
    internal int DocumentCount => documents.Count;
    internal bool IsOpen(string fullPath) => documents.ContainsKey(LinkPolicy.Key(fullPath));

    private Action<string>? Log => context.Log;

    // ---- Attaching ----------------------------------------------------------------------------------

    // Each discovery tick: find the application object, then wait until SolidWorks finished
    // starting (StartupProcessCompleted, the property SolidWorks keeps for out-of-process
    // applications) before subscribing to anything.
    internal void Tick()
    {
        try
        {
            if (State == Phase.Finding)
            {
                app = RunningObjects.Find(Pid, Log);
                if (app is null) return;
                State = Phase.Starting;
            }
            if (State == Phase.Starting)
            {
                // A SolidWorks still starting is often busy: asked briefly, again on the next tick.
                using (context.Filter?.Hurry(StartingCallLimit))
                    if (Com.Get(app!, "StartupProcessCompleted") is not true) return;
                Attach();
            }
        }
        catch (Exception error) when (Com.IsComFailure(error) && State == Phase.Starting && !Com.IsGone(error))
        {
            // Busy while it starts: asked again on the next tick (said once in the log).
            if (!startingFailureLogged) Log?.Invoke($"solidworks: pid {Pid} isn't ready yet: {error.GetType().Name}: {(error.InnerException ?? error).Message}");
            startingFailureLogged = true;
        }
        catch (Exception error) when (Com.IsComFailure(error)) { Failed("attach", error); }
    }

    private void Attach()
    {
        var text = Com.CallString(app!, "RevisionNumber") ?? "";
        if (SolidWorksRevision.Parse(text) is not { } parsed)
        {
            Log?.Invoke($"solidworks: pid {Pid} says it is revision \"{text}\"; not linked");
            Release("unknown revision");
            return;
        }
        revision = parsed;
        try
        {
            Subscribe(app!, SwConstants.DSldWorksEvents, appSinks, [
                (SwConstants.FileOpenPostNotify, new OneString(OnFileOpenPost)),
                (SwConstants.FileNewNotify2, new NewDocument(OnFileNew)),
                (SwConstants.ActiveModelDocChangeNotify, new NoArgs(OnActiveDocChange)),
                (SwConstants.FileCloseNotify, new StringAndInt(OnFileClose)),
                (SwConstants.DestroyNotify, new NoArgs(OnDestroy)),
            ]);
        }
        catch
        {
            // Not followed yet: tried again from the start on the next tick.
            Unsubscribe(app!, appSinks);
            throw;
        }
        var ids = SaveToVersionEnums.Resolve(context.Settings, executable, Log);
        saveDown = new SaveDownManager(Pid, revision, this, ids, context.Settings, context.Emit, SupportChanged, HashOf, context.Clock, Log);
        saveDown.Attach(restoreLeftover: !context.OtherSessionOfRelease(Pid, revision.Major));
        State = Phase.Attached;
        context.Emit(new LinkAttached(Pid, text, revision.Year, saveDown.Support));
        Log?.Invoke($"solidworks link attached pid={Pid} revision={text} saveDown={saveDown.Support}");
        CatchUp();
        saveDown.SetPins(context.Pins);
        ActiveChanged();
    }

    // SolidWorks was already running with documents open: each one the student can see counts
    // as opened by the student; changes made before the link came are unknown.
    private void CatchUp()
    {
        var active = ActivePath();
        foreach (var doc in Com.Objects(Com.Call(app!, "GetDocuments")))
        {
            try
            {
                var path = Com.CallString(doc, "GetPathName") ?? "";
                if (!LinkPolicy.Watches(path, context.VaultRoot)) { Com.Release(doc); continue; }
                var visible = Com.Get(doc, "Visible") is true;
                Watch(doc, path, LinkPolicy.StudentOpened(active, path, visible), changed: null);
            }
            catch (Exception error) when (Com.IsComFailure(error)) { Com.Release(doc); if (Com.IsGone(error)) throw; }
        }
    }

    // ---- App events (memory only, then post) --------------------------------------------------------

    private int OnFileOpenPost(string fileName)
    {
        if (LinkPolicy.IsVaultDocument(fileName, context.VaultRoot)) context.Thread.Post(() => Safely("open", () => Opened(fileName)));
        return 0;
    }

    private int OnFileNew(object newDoc, int docType, string templateName)
    {
        // A new document has no path yet: watched in case it is saved into the vault.
        context.Thread.Post(() => Safely("new document", () =>
        {
            var path = Com.CallString(newDoc, "GetPathName") ?? "";
            if (LinkPolicy.Watches(path, context.VaultRoot)) Watch(newDoc, path, topLevel: true, changed: false);
            else Com.Release(newDoc);
        }));
        return 0;
    }

    private int OnActiveDocChange()
    {
        context.Thread.Post(() => Safely("active document", ActiveChanged));
        return 0;
    }

    private int OnFileClose(string fileName, int reason)
    {
        if (documents.TryGetValue(LinkPolicy.Key(fileName), out var doc)) Closed(doc);
        return 0;
    }

    private int OnDestroy()
    {
        // SolidWorks is closing: everything is let go once this reply is back with it.
        context.Thread.Post(() => Release("SolidWorks closed"));
        return 0;
    }

    // ---- Document events ------------------------------------------------------------------------

    private void SubscribeDocument(Document doc)
    {
        if (SwConstants.EventsFor(doc.DocType) is not { } events) return;
        List<(int, Delegate)> handlers =
        [
            (events.FileSaveNotify, new OneString(name => Saving(doc, name))),
            (events.FileSaveAsNotify2, new OneString(name => Saving(doc, name))),
            (events.FileSavePostNotify, new IntAndString((type, name) => SavedPost(doc, type, name))),
            (events.ModifyNotify, new NoArgs(() => Modified(doc))),
            (events.DestroyNotify2, new OneInt(type => Destroyed(doc, type))),
        ];
        if (CancelDispid(doc) is { } cancel) handlers.Add((cancel, new NoArgs(() => SaveCanceledEvent(doc))));
        Subscribe(doc.Rcw, events.Iid, doc.Sinks, handlers);
    }

    // FileSaveNotify or FileSaveAsNotify2, from memory: the engine holds the path until it is saved.
    private int Saving(Document doc, string fileName)
    {
        var path = string.IsNullOrEmpty(fileName) ? doc.Path : fileName;
        if (!LinkPolicy.IsVaultDocument(path, context.VaultRoot) || saveDown is null) return 0;
        doc.SaveId = Guid.NewGuid();
        doc.SaveMode = saveDown.Saving(path);
        doc.SavingPath = path;
        context.Emit(new LinkSaving(Pid, path, doc.SaveId, doc.SaveMode));
        return 0;
    }

    private int SavedPost(Document doc, int saveType, string fileName)
    {
        context.Thread.Post(() => Safely("save", () => Saved(doc, saveType, fileName)));
        return 0;
    }

    private int SaveCanceledEvent(Document doc)
    {
        if (doc.SavingPath is { } path)
        {
            context.Emit(new LinkSaveCanceled(Pid, path, doc.SaveId, doc.SaveMode));
            var mode = doc.SaveMode;
            doc.SavingPath = null;
            context.Thread.Post(() => Safely("canceled save", () =>
            {
                saveDown?.SaveCanceled(path, mode);
                Check(doc);
            }));
        }
        return 0;
    }

    private int Modified(Document doc)
    {
        // Lab step N5 reads this line: the first change since the document was opened or saved.
        if (doc.Changed != true) Log?.Invoke($"solidworks: {System.IO.Path.GetFileName(doc.Path)} changed");
        doc.Changed = true;
        if (LinkPolicy.IsVaultDocument(doc.Path, context.VaultRoot)) context.Emit(new LinkModified(Pid, doc.Path));
        // Checked again once changes have been quiet for a while.
        var at = Interlocked.Increment(ref doc.ModifiedAt);
        context.Thread.After(context.ModifySettle, () => { if (Interlocked.Read(ref doc.ModifiedAt) == at) Safely("check", () => Check(doc)); });
        return 0;
    }

    private int Destroyed(Document doc, int destroyType)
    {
        // Hidden: still loaded by an assembly or a drawing, so still open.
        if (LinkPolicy.Closed(destroyType)) Closed(doc);
        return 0;
    }

    // ---- Posted work (calls into SolidWorks) --------------------------------------------------------

    private void Opened(string fileName)
    {
        if (documents.ContainsKey(LinkPolicy.Key(fileName))) return;
        var doc = Com.Call(app!, "GetOpenDocumentByName", fileName);
        if (doc is null) return;
        Watch(doc, fileName, LinkPolicy.StudentOpened(ActivePath(), fileName), changed: false);
    }

    // Starts watching one document: its events, and what the engine is told about it.
    private void Watch(object rcw, string path, bool topLevel, bool? changed)
    {
        var docType = Com.Call(rcw, "GetType") is int type && type is >= 1 and <= 3 ? type : LinkPolicy.DocTypeOf(path);
        var doc = new Document(rcw, path, docType) { Changed = changed, TopLevel = topLevel, OpenedAt = context.Clock.GetUtcNow() };
        doc.Key = LinkPolicy.IsUnsaved(path) ? "new:" + Interlocked.Increment(ref unsavedCount) : LinkPolicy.Key(path);
        if (!documents.TryAdd(doc.Key, doc)) { Com.Release(rcw); return; }
        SubscribeDocument(doc);
        if (LinkPolicy.IsUnsaved(path)) return;
        Announce(doc);
    }

    // LinkOpened for a vault document, then its stamp and its check (B5, B2).
    private void Announce(Document doc)
    {
        doc.ReadOnly = Com.Call(doc.Rcw, "IsOpenedReadOnly") is true;
        doc.FutureVersion = FutureVersion(doc.Rcw);
        context.Emit(new LinkOpened(Pid, doc.Path, doc.DocType, doc.ReadOnly, doc.FutureVersion, doc.TopLevel, doc.OpenedAt));
        var path = doc.Path;
        context.Thread.Post(() => Safely("stamp", () =>
        {
            if (saveDown?.StampOnOpen(path) is { } stamp) context.Emit(new LinkStamped(Pid, path, stamp));
            Check(doc);
        }));
    }

    private static bool FutureVersion(object rcw)
    {
        object? extension = null;
        try
        {
            extension = Com.Get(rcw, "Extension");
            return extension is not null && Com.Call(extension, "IsFutureVersion") is true;
        }
        catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { return false; }
        finally { Com.Release(extension); }
    }

    private void Saved(Document doc, int saveType, string fileName)
    {
        var path = string.IsNullOrEmpty(fileName) ? doc.Path : fileName;
        if (LinkPolicy.SavedOwnPath(saveType) && !LinkPolicy.SamePath(path, doc.Path))
        {
            // Save As: the document is that file now.
            var wasVault = LinkPolicy.IsVaultDocument(doc.Path, context.VaultRoot);
            if (wasVault) context.Emit(new LinkClosed(Pid, doc.Path));
            documents.TryRemove(doc.Key, out _);
            doc.Path = path;
            if (!LinkPolicy.IsVaultDocument(path, context.VaultRoot))
            {
                Unwatch(doc);
                return;
            }
            doc.Key = LinkPolicy.Key(path);
            documents[doc.Key] = doc;
            doc.TopLevel = true;
            doc.OpenedAt = context.Clock.GetUtcNow();
            doc.ReadOnly = false;
            context.Emit(new LinkOpened(Pid, doc.Path, doc.DocType, false, false, true, doc.OpenedAt));
        }
        if (!LinkPolicy.IsVaultDocument(path, context.VaultRoot)) return;
        var mode = doc.SavingPath is not null && LinkPolicy.SamePath(doc.SavingPath, path) ? doc.SaveMode : saveDown?.Saving(path) ?? LinkSaveMode.Current;
        var saveId = doc.SavingPath is not null && LinkPolicy.SamePath(doc.SavingPath, path) ? doc.SaveId : Guid.NewGuid();
        doc.SavingPath = null;
        var stamp = saveDown?.Saved(path, mode);
        if (LinkPolicy.SavedOwnPath(saveType)) doc.Changed = false;
        context.Emit(new LinkSaved(Pid, path, saveId, saveType, stamp));
        Check(doc);
    }

    // What a save down of this document would do, for the engine's words.
    private void Check(Document doc)
    {
        if (saveDown is null || !documents.ContainsKey(doc.Key) || !LinkPolicy.IsVaultDocument(doc.Path, context.VaultRoot)) return;
        if (saveDown.CheckCompatibility(doc.Path, doc.DocType) is { } record) context.Emit(record);
    }

    private void ActiveChanged()
    {
        if (State != Phase.Attached) return;
        var path = ActivePath();
        activePath = path;
        if (path is not null && documents.TryGetValue(LinkPolicy.Key(path), out var doc) && !LinkPolicy.IsUnsaved(doc.Path))
        {
            doc.TopLevel = true;
            context.Emit(new LinkActivated(Pid, doc.Path, context.Clock.GetUtcNow()));
        }
        saveDown?.ActiveChanged(path);
    }

    private string? ActivePath()
    {
        var active = Com.Get(app!, "ActiveDoc");
        try { return active is null ? null : Com.CallString(active, "GetPathName") is { Length: > 0 } path ? path : null; }
        finally { Com.Release(active); }
    }

    private void Closed(Document doc)
    {
        if (!documents.TryRemove(doc.Key, out _)) return;
        if (LinkPolicy.IsVaultDocument(doc.Path, context.VaultRoot)) context.Emit(new LinkClosed(Pid, doc.Path));
        // Unadvising calls into SolidWorks: after this reply.
        context.Thread.Post(() => Unwatch(doc));
    }

    private void Unwatch(Document doc)
    {
        Unsubscribe(doc.Rcw, doc.Sinks);
        Com.FinalRelease(doc.Rcw);
    }

    // ---- Commands from the engine -------------------------------------------------------------------

    // After a check out (Armory.Core.WritablePlan): in place first; a reload or a drawing's
    // close-and-reopen only for a document nobody changed; never CloseDoc, never a discard.
    internal WritableOutcome MakeWritable(string path)
    {
        if (State != Phase.Attached) return WritableOutcome.LinkGone;
        var rcw = Com.Call(app!, "GetOpenDocumentByName", path);
        if (rcw is null) return WritableOutcome.NotOpen;
        try
        {
            documents.TryGetValue(LinkPolicy.Key(path), out var known);
            var docType = Com.Call(rcw, "GetType") is int type ? type : LinkPolicy.DocTypeOf(path);
            var state = new WritableState(true, Com.Call(rcw, "IsOpenedReadOnly") is true, docType == SwDocTypes.Drawing, known?.Changed, false, false);
            var startedReadOnly = state.ReadOnly;
            var reopened = false;
            while (true)
            {
                switch (WritablePlan.Next(state))
                {
                    case WritableStep.Done:
                        if (known is not null) known.ReadOnly = false;
                        return !startedReadOnly ? WritableOutcome.AlreadyWritable : reopened ? WritableOutcome.Reopened : WritableOutcome.MadeWritableInPlace;
                    case WritableStep.SetReadOnlyStateFalse:
                        Com.Call(rcw, "SetReadOnlyState", false);
                        state = state with { TriedInPlace = true, ReadOnly = Com.Call(rcw, "IsOpenedReadOnly") is true };
                        break;
                    case WritableStep.Reload:
                        var reload = Com.Call(rcw, "ReloadOrReplace", WritablePlan.ReloadReadOnly, null, WritablePlan.ReloadDiscardChanges);
                        Log?.Invoke($"solidworks: reload of {path}: {reload}");
                        reopened = true;
                        state = state with { TriedReopen = true, ReadOnly = Com.Call(rcw, "IsOpenedReadOnly") is true };
                        break;
                    case WritableStep.CloseAndReopenDrawing:
                        var args = new object?[] { rcw, WritablePlan.CloseAndReopenOptions, null };
                        var result = Com.CallRef(app!, "CloseAndReopen", args, 2);
                        Log?.Invoke($"solidworks: close and reopen of {path}: {result}");
                        reopened = true;
                        if (args[2] is { } newDoc && !ReferenceEquals(newDoc, rcw))
                        {
                            // A new document object: its events are watched instead (it owns
                            // that reference now), and it is asked about again by name.
                            if (known is not null) Rewatch(known, newDoc); else Com.Release(newDoc);
                            Com.Release(rcw);
                            rcw = Com.Call(app!, "GetOpenDocumentByName", path);
                            if (rcw is null) return WritableOutcome.Refused;
                        }
                        state = state with { TriedReopen = true, ReadOnly = Com.Call(rcw, "IsOpenedReadOnly") is true };
                        break;
                    case WritableStep.StopUnsaved:
                        return WritableOutcome.HasUnsavedChanges;
                    case WritableStep.NotOpen:
                        return WritableOutcome.NotOpen;
                    default:
                        return WritableOutcome.Refused;
                }
            }
        }
        catch (Exception error) when (Com.IsComFailure(error))
        {
            if (Com.IsGone(error)) { Failed("make writable", error); return WritableOutcome.LinkGone; }
            Log?.Invoke($"solidworks: make writable {path}: {error.Message}");
            return WritableOutcome.Refused;
        }
        finally { Com.Release(rcw); }
    }

    private void Rewatch(Document doc, object newRcw)
    {
        Unsubscribe(doc.Rcw, doc.Sinks);
        Com.FinalRelease(doc.Rcw);
        doc.Rcw = newRcw;
        SubscribeDocument(doc);
    }

    internal SaveDownOutcome SaveInPinnedRelease(string path)
        => State != Phase.Attached || saveDown is null ? SaveDownOutcome.LinkGone : saveDown.SaveInPinnedRelease(path);

    internal void KeepLocal(string path, bool keep) => saveDown?.KeepLocal(path, keep);

    internal void SetPins(IReadOnlyList<ProjectPin> pins) => saveDown?.SetPins(pins);

    internal void StatusText(string text)
    {
        if (State != Phase.Attached) return;
        var frame = Com.Call(app!, "Frame");
        try { if (frame is not null) Com.Call(frame, "SetStatusBarText", text); }
        finally { Com.Release(frame); }
    }

    // What is true now, again, for an engine that just started.
    internal void Replay()
    {
        if (State != Phase.Attached || saveDown is null) return;
        context.Emit(new LinkAttached(Pid, $"{revision.Major}.{revision.Minor}.{revision.Hotfix}", revision.Year, saveDown.Support));
        foreach (var doc in documents.Values.Where(d => !LinkPolicy.IsUnsaved(d.Path)))
            context.Emit(new LinkOpened(Pid, doc.Path, doc.DocType, doc.ReadOnly, doc.FutureVersion, doc.TopLevel, doc.OpenedAt));
    }

    private void SupportChanged(SaveToVersionSupport support)
        => context.Emit(new LinkAttached(Pid, $"{revision.Major}.{revision.Minor}.{revision.Hotfix}", revision.Year, support));

    // ---- The end ------------------------------------------------------------------------------------

    // Everything let go: each document's events unadvised and its object released, the student's
    // own Save to Version back (while SolidWorks still answers), the application's events and
    // object. Then the engine hears it is gone.
    internal void Release(string reason)
    {
        if (State == Phase.Gone) return;
        var wasAttached = State == Phase.Attached;
        State = Phase.Gone;
        // A SolidWorks closing may be busy: each call is retried briefly, so Armory's quit and
        // the other sessions never wait long on it.
        using var hurry = context.Filter?.Hurry(ClosingCallLimit);
        foreach (var doc in documents.Values.ToList())
        {
            documents.TryRemove(doc.Key, out _);
            Unsubscribe(doc.Rcw, doc.Sinks);
            Com.FinalRelease(doc.Rcw);
        }
        try { saveDown?.Restore(); }
        catch (Exception error) when (Com.IsComFailure(error)) { }
        if (app is not null)
        {
            Unsubscribe(app, appSinks);
            Com.FinalRelease(app);
            app = null;
        }
        if (wasAttached)
        {
            context.Emit(new LinkDetached(Pid, reason));
            Log?.Invoke($"solidworks link detached pid={Pid}: {reason}");
        }
    }

    private void Failed(string what, Exception error)
    {
        if (Com.IsGone(error)) Release("the connection to SolidWorks closed");
        else Log?.Invoke($"solidworks: {what} pid={Pid}: {error.GetType().Name}: {error.Message}");
    }

    // A posted step: a COM failure ends this session quietly when SolidWorks is gone, and is
    // logged otherwise; nothing reaches the loop.
    private void Safely(string what, Action work)
    {
        if (State == Phase.Gone) return;
        try { work(); }
        catch (Exception error) when (Com.IsComFailure(error)) { Failed(what, error); }
    }

    // ---- Events plumbing ------------------------------------------------------------------------------

    private void Subscribe(object rcw, Guid iid, List<(Guid, int, Delegate)> sinks, IEnumerable<(int Dispid, Delegate Handler)> handlers)
    {
        foreach (var (dispid, handler) in handlers)
        {
            var guarded = Guarded(handler);
            ComEventsHelper.Combine(rcw, iid, dispid, guarded);
            sinks.Add((iid, dispid, guarded));
        }
    }

    // Each handler, guarded: an exception never goes back to SolidWorks (it gets 0).
    private Delegate Guarded(Delegate handler) => handler switch
    {
        NoArgs h => new NoArgs(() => Answer(() => h())),
        OneString h => new OneString(a => Answer(() => h(a))),
        StringAndInt h => new StringAndInt((a, b) => Answer(() => h(a, b))),
        OneInt h => new OneInt(a => Answer(() => h(a))),
        IntAndString h => new IntAndString((a, b) => Answer(() => h(a, b))),
        NewDocument h => new NewDocument((a, b, c) => Answer(() => h(a, b, c))),
        _ => handler,
    };

    private int Answer(Func<int> handler)
    {
        try { return State == Phase.Gone ? 0 : handler(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log?.Invoke("solidworks: event handler: " + error.GetType().Name + ": " + error.Message);
            return 0;
        }
    }

    private void Unsubscribe(object rcw, List<(Guid Iid, int Dispid, Delegate Handler)> sinks)
    {
        foreach (var (iid, dispid, handler) in sinks)
        {
            try { ComEventsHelper.Remove(rcw, iid, dispid, handler); }
            catch (Exception error) when (Com.IsComFailure(error) || error is InvalidOperationException) { }
        }
        sinks.Clear();
    }

    // FileSavePostCancelNotify's number, from SolidWorks' own type information (it is not in the
    // measured table); null when SolidWorks doesn't publish it.
    private int? CancelDispid(Document doc)
    {
        if (cancelDispids.TryGetValue(doc.DocType, out var known)) return known;
        int? found = null;
        if (SwConstants.EventsFor(doc.DocType) is { } events)
        {
            ITypeInfo? info = null;
            ITypeLib? library = null;
            ITypeInfo? source = null;
            try
            {
                if (doc.Rcw is IDispatchInfo dispatch && dispatch.GetTypeInfo(0, 0, out info) >= 0 && info is not null)
                {
                    info.GetContainingTypeLib(out library, out _);
                    var iid = events.Iid;
                    library.GetTypeInfoOfGuid(ref iid, out source);
                    var ids = new int[1];
                    source.GetIDsOfNames([SwConstants.FileSavePostCancelNotify], 1, ids);
                    found = ids[0];
                }
            }
            catch (Exception error) when (Com.IsComFailure(error) || error is COMException) { found = null; }
            finally
            {
                if (source is not null) Marshal.ReleaseComObject(source);
                if (library is not null) Marshal.ReleaseComObject(library);
                if (info is not null) Marshal.ReleaseComObject(info);
            }
        }
        cancelDispids[doc.DocType] = found;
        Log?.Invoke($"solidworks: {SwConstants.FileSavePostCancelNotify} for document type {doc.DocType}: {(found is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not published")}");
        return found;
    }

    // ---- ISaveDownCalls ----------------------------------------------------------------------------

    public bool? GetToggle(int preference) => Com.Call(app!, "GetUserPreferenceToggle", preference) is bool on ? on : null;
    public bool SetToggle(int preference, bool value) => Com.Call(app!, "SetUserPreferenceToggle", preference, value) is not false;
    public int? GetInteger(int preference) => Com.Call(app!, "GetUserPreferenceIntegerValue", preference) is int value ? value : null;
    public bool SetInteger(int preference, int value) => Com.Call(app!, "SetUserPreferenceIntegerValue", preference, value) is not false;

    public CompatibilityResult? CheckCompatibility(string path, int saveToVersion)
    {
        var doc = Com.Call(app!, "GetOpenDocumentByName", path);
        if (doc is null) return null;
        object? extension = null;
        object[] incompatible = [], warnings = [];
        try
        {
            extension = Com.Get(doc, "Extension");
            if (extension is null) return null;
            var args = new object?[] { saveToVersion, SwConstants.CompatibilityNeverShow, null, null };
            if (Com.CallRef(extension, "CheckVersionCompatibility", args, 2, 3) is not int result) return null;
            incompatible = Com.Objects(args[2]);
            warnings = Com.Objects(args[3]);
            var items = incompatible.Select(i => new CompatibilityItem(Text(i, "Message") ?? "", Text(i, "Action"), ObjectName(i))).Where(i => i.Message.Length > 0).ToList();
            return new CompatibilityResult(result, items, warnings.Select(w => Text(w, "Message") ?? "").Where(w => w.Length > 0).ToList());
        }
        finally
        {
            Com.Release(incompatible);
            Com.Release(warnings);
            Com.Release(extension);
            Com.Release(doc);
        }
    }

    private static string? Text(object item, string name)
    {
        try { return Com.Get(item, name) as string; }
        catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { return null; }
    }

    private static string? ObjectName(object item)
    {
        object? target = null;
        try
        {
            target = Com.Get(item, "IncompatibleObject");
            return target is null ? null : Com.Get(target, "Name") as string;
        }
        catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { return null; }
        finally { Com.Release(target); }
    }

    // Research section 3.3: appearances, decals, lights, custom properties (the document's and
    // each configuration's), explode steps (assemblies), and simulation studies when SolidWorks
    // Simulation is loaded (else "simulation studies, if any"). Each count on its own: one that
    // fails is left out.
    public IReadOnlyList<DropItem> Inventory(string path, int docType)
    {
        var doc = Com.Call(app!, "GetOpenDocumentByName", path);
        if (doc is null) return [];
        object? extension = null;
        List<DropItem> drops = [];
        try
        {
            extension = Com.Get(doc, "Extension");
            if (extension is not null)
            {
                Count(drops, DropItem.Appearances, () => Com.Call(extension, "GetRenderMaterialsCount2", SwConstants.ThisDisplayState, null));
                Count(drops, DropItem.Decals, () => Com.Call(extension, "GetDecalsCount"));
            }
            Count(drops, DropItem.Lights, () => Com.Call(doc, "GetLightSourceCount"));
            var configurations = Com.Strings(Try(() => Com.Call(doc, "GetConfigurationNames")));
            if (extension is not null)
            {
                var properties = 0;
                foreach (var configuration in configurations.Prepend(""))
                {
                    var manager = Try(() => Com.Get(extension, "CustomPropertyManager", configuration));
                    try { if (manager is not null && Com.Get(manager, "Count") is int n) properties += n; }
                    catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { }
                    finally { Com.Release(manager); }
                }
                if (properties > 0) drops.Add(new DropItem(DropItem.CustomProperties, properties));
            }
            if (docType == SwDocTypes.Assembly)
            {
                var steps = 0;
                foreach (var configuration in configurations)
                {
                    var config = Try(() => Com.Call(doc, "GetConfigurationByName", configuration));
                    try { if (config is not null && Com.Call(config, "GetNumberOfExplodeSteps") is int n) steps += n; }
                    catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { }
                    finally { Com.Release(config); }
                }
                if (steps > 0) drops.Add(new DropItem(DropItem.ExplodeSteps, steps));
            }
            if (docType != SwDocTypes.Drawing) drops.Add(new DropItem(DropItem.SimulationStudies, SimulationStudies(path) ?? 0));
        }
        finally
        {
            Com.Release(extension);
            Com.Release(doc);
        }
        return drops;
    }

    private static void Count(List<DropItem> drops, string kind, Func<object?> read)
    {
        if (Try(read) is int n && n > 0) drops.Add(new DropItem(kind, n));
    }

    private static object? Try(Func<object?> read)
    {
        try { return read(); }
        catch (Exception error) when (Com.IsComFailure(error) && !Com.IsGone(error)) { return null; }
    }

    // The studies of the active document, when SolidWorks Simulation is loaded; null otherwise.
    private int? SimulationStudies(string path)
    {
        if (!LinkPolicy.SamePath(path, activePath)) return null;
        object? addIn = null, cosmos = null, active = null, manager = null;
        try
        {
            addIn = Try(() => Com.Call(app!, "GetAddInObject", SwConstants.SimulationAddIn));
            if (addIn is null) return null;
            cosmos = Try(() => Com.Get(addIn, "CosmosWorks"));
            active = cosmos is null ? null : Try(() => Com.Get(cosmos, "ActiveDoc"));
            manager = active is null ? null : Try(() => Com.Get(active, "StudyManager"));
            return manager is null ? null : Try(() => Com.Get(manager, "StudyCount")) as int?;
        }
        finally
        {
            Com.Release(manager);
            Com.Release(active);
            Com.Release(cosmos);
            Com.Release(addIn);
        }
    }

    public IReadOnlyList<string>? VersionHistory(string path) => Com.Strings(Com.Call(app!, "VersionHistory", path));

    public bool? IsReadOnly(string path)
    {
        var doc = Com.Call(app!, "GetOpenDocumentByName", path);
        if (doc is null) return null;
        try { return Com.Call(doc, "IsOpenedReadOnly") is true; }
        finally { Com.Release(doc); }
    }

    public bool Save(string path)
    {
        var doc = Com.Call(app!, "GetOpenDocumentByName", path);
        if (doc is null) return false;
        try
        {
            Com.Call(doc, "SetSaveFlag");
            var args = new object?[] { SwConstants.SaveAsSilent, 0, 0 };
            return Com.CallRef(doc, "Save3", args, 1, 2) is true;
        }
        finally { Com.Release(doc); }
    }

    // SHA-256 of the file as it is on disk now, read while SolidWorks may hold it open; null when
    // it can't be read.
    internal static string? HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
