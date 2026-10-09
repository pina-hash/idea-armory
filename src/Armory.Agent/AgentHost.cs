using System.Net;
using System.Text.Json.Nodes;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Platform.Windows;

namespace Armory.Agent;

// Composition root: the network clients, the sign-in, and one vault runtime (file system,
// journal, snapshots, state and the SyncEngine) for the configured vault root. A vault
// root change stops that runtime and starts a new one in-process. When the engine cannot
// run (an unusable folder, or a failure while starting), the host still answers with a
// plain view that says why, so the window and the tray keep working.
internal sealed partial class AgentHost : IAsyncDisposable
{
    private static readonly TimeSpan HintPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private readonly AgentPaths paths;
    private readonly AgentLog log;
    private readonly SettingsStore settingsStore;
    private readonly HttpClient restHttp;
    private readonly HttpClient siteHttp;
    private readonly HttpClient storageHttp;
    // What the host is made of (HostParts.Windows, or a test's), and the clients of the student
    // Armory works for: the one student's, or on a shared computer the one in use (the pointer a
    // switch swaps; AgentHost.Profiles.cs).
    private readonly HostParts parts;
    private readonly Uri site;
    private volatile ProfileClients clients;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private readonly System.Threading.Timer hintTimer;
    private VaultRuntime? runtime;
    private string? runtimeProblem;
    private AgentSettings settings;
    private string effectiveTheme;
    private string connectPhase = "idle";
    private string? connectMessage;
    private CancellationTokenSource? connecting;
    private TaskCompletionSource? firstSignedInView;
    private long lastHints = -1;
    private bool engineFailureLogged;
    private bool disposed;
    // Team status (v0.3): armory_heartbeat on its own task while the app runs.
    private readonly CancellationTokenSource running = new();
    private Task? beating;
    private static readonly TimeSpan GoodbyeDeadline = TimeSpan.FromSeconds(3);

    // parts: null for the app's own (Windows); a test passes its own.
    internal AgentHost(AgentPaths paths, AgentLog log, Uri site, AgentTelemetry telemetry, HostParts? parts = null)
    {
        this.paths = paths;
        this.log = log;
        this.site = site;
        Telemetry = telemetry;
        this.parts = parts ??= HostParts.Windows(log, telemetry, paths.SolidWorksFile);
        settingsStore = new SettingsStore(paths.SettingsFile);
        settings = settingsStore.Load();
        effectiveTheme = Themes.Effective(settings.Theme, parts.LightApps());
        restHttp = parts.Http(TimeSpan.FromSeconds(30));
        siteHttp = parts.Http(TimeSpan.FromSeconds(30));
        // Up to 2 GiB per transfer on a school network: a generous whole-request limit; a dead
        // connection is still noticed by the connect timeout and the engine's cancellation.
        storageHttp = parts.Http(TimeSpan.FromHours(2));
        // One student per computer: the sign-in in secrets\, as always. A computer shared by
        // several students: the student in use's own (AgentHost.Profiles.cs).
        clients = OpenClients();
        // Says whether this start found a saved sign-in (the email only; tokens never reach the
        // log). The upgrade cycle (tools/test-agent-install.ps1 -Kind Upgrade) reads it to prove
        // that a new version still uses the sign-in an older one saved.
        log.Info(Sessions.Current is { } saved ? "session loaded for " + saved.Email : "no saved session");
        hintTimer = new System.Threading.Timer(_ => PollHints(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        telemetry.Attach(() => Sessions.Current, DescribeAsync, DescribeNow, Api, () => Blobs.ActiveTransfers > 0, Feedback);
        AttachedTelemetry();
    }

    internal AgentTelemetry Telemetry { get; }
    // The student in use's network side (ProfileClients): every one of these follows a switch.
    internal SessionManager Sessions => clients.Sessions;
    internal ArmoryApi Api => clients.Api;
    internal BlobClient Blobs => clients.Blobs;
    internal ConnectFlow Connector => clients.Connector;
    internal TeamHeartbeat Heartbeat => clients.Heartbeat;
    // The window's Send feedback (FeedbackSender.SendAsync) and "Your feedback"
    // (Api.MyAppFeedbackAsync): docs/agent/CLIENT.md section 7.
    internal FeedbackSender Feedback => clients.Feedback;
    internal AgentSettings Settings { get { lock (gate) return settings; } }
    internal string EffectiveTheme { get { lock (gate) return effectiveTheme; } }
    internal bool IsPaused
    {
        get
        {
            var engine = Volatile.Read(ref runtime)?.Engine;
            try { return engine?.IsPaused ?? false; }
            catch (NotImplementedException) { return false; }
        }
    }

    // Raised on any thread; the window and the tray marshal to the UI thread.
    internal event Action<AgentView>? ViewChanged;
    // What is moving right now, at most four times a second while files move (the engine's
    // ActivityChanged): the window patches its activity panel and status line from it alone.
    internal event Action<ActivityView>? ActivityChanged;

    internal AgentView View
    {
        get
        {
            var engine = Volatile.Read(ref runtime)?.Engine;
            if (engine is null) return WithProfiles(Fallback(runtimeProblem ?? NoRuntimeLine()));
            try { return WithProfiles(WithSettings(engine.View)); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogEngineFailure("view", error);
                return WithProfiles(Fallback("Armory's sync engine is not running on this computer yet."));
            }
        }
    }

    internal async Task StartAsync()
    {
        try { StartupRegistrationApply(settings, startup: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { log.Error("could not update the sign-in start entry", error); }
        await RestartRuntimeAsync(settings);
        hintTimer.Change(HintPoll, HintPoll);
        // Beats until a clean stop; a failed beat is logged and never touches a sync pass.
        StartBeating();
        // File Explorer's right-click items and badges (AgentHost.Shell.cs).
        StartShell();
    }

    internal void Pause() => OnEngine("pause", e => e.Pause());
    internal void Resume() => OnEngine("resume", e => e.Resume());
    internal void Wake() => OnEngine("wake", e => e.Wake());

    // The window's actions (docs/agent/BRIDGE.md, "Page to host"; v2-design.md 4.2 and 4.3).
    // Paths are vault-relative and already checked by the Bridge; a folder means every file
    // under it. Each one goes to the engine, which answers with one plain sentence, so no
    // action is ever dropped without a word.
    internal Task<ActionResult> LaunchFileAsync(string path) => OnEngineAsync("open", e => e.LaunchAsync(path));
    internal Task<ActionResult> CheckOutAsync(IReadOnlyList<string> paths, bool open) => OnEngineAsync("check out", e => e.CheckOutAsync(paths, open));
    internal Task<ActionResult> CheckInAsync(IReadOnlyList<string> paths) => OnEngineAsync("check in", e => e.CheckInAsync(paths));
    internal Task<ActionResult> UndoCheckOutAsync(IReadOnlyList<string> paths) => OnEngineAsync("undo check out", e => e.UndoCheckOutAsync(paths));
    internal Task<ActionResult> TakeBackAsync(Guid fileId) => OnEngineAsync("take back", e => e.TakeBackAsync(fileId));
    // File detail's Put back on this computer: one of your kept copies, checked out to you (feedback N4).
    internal Task<ActionResult> PutBackKeptCopyAsync(Guid fileId, Guid versionId) => OnEngineAsync("put back a kept copy", e => e.PutBackKeptCopyAsync(fileId, versionId));
    // The picture File Explorer shows for a file in the vault (ShellThumbnails), or null.
    internal Task<byte[]?> ThumbnailAsync(string vaultPath)
        => Volatile.Read(ref runtime)?.Windows?.ExistingFile(vaultPath) is { } file ? thumbnails.GetAsync(file) : Task.FromResult<byte[]?>(null);
    private readonly ShellThumbnails thumbnails = new();
    internal Task<ActionResult> TakeBackAsync(IReadOnlyList<Guid> fileIds) => OnEngineAsync("take back " + fileIds.Count + " files", e => e.TakeBackAsync(fileIds));
    // One file, in the same folder: a file Armory doesn't have yet is renamed on disk; a file in
    // Armory is renamed for everyone (refused while someone else has it checked out, unless force:
    // a mentor or CAD lead force checks it in first, in the same action).
    internal Task<ActionResult> RenameFileAsync(string path, string newName, bool force = false)
        => OnEngineAsync("rename a file", e => e.RenameFileAsync(path, newName, force));
    // Folders (contract C5 and C6): a rename or a removal goes to the team in one call, then here.
    // Force: as for a file, for every check out in the folder that is in the way.
    internal Task<ActionResult> CreateFolderAsync(Guid project, string parent, string name) => OnEngineAsync("new folder", e => e.CreateFolderAsync(project, parent, name));
    internal Task<ActionResult> RenameFolderAsync(Guid project, string folder, string newName, bool force = false)
        => OnEngineAsync("rename a folder", e => e.RenameFolderAsync(project, folder, newName, force));
    internal Task<ActionResult> DeleteFolderAsync(Guid project, string folder, bool force = false)
        => OnEngineAsync("delete a folder", e => e.DeleteFolderAsync(project, folder, force));
    // sources: full paths on this computer (the file picker's, or the files and folders dropped on
    // the window); a folder is copied whole. Copied in, never over anything already there.
    internal Task<ActionResult> AddFilesAsync(Guid project, string folder, IReadOnlyList<string> sources)
        => sources.Count == 0 ? Task.FromResult(new ActionResult(false, "")) : OnEngineAsync("add " + sources.Count + " files or folders", e => e.AddFilesAsync(project, folder, sources));

    // "Report a problem" (docs/agent/TELEMETRY.md): the words and a fresh incident, saved here
    // and sent when the site can take them. Never an error for a site that isn't ready.
    // Windows is ending the session (a restart, a shut down, a sign out) and ends this process as
    // soon as its handler returns (TrayApp): the line that says so (the next start reads it: not a
    // crash), the last flight, then the stop, waited for at most `wait` on the caller's thread.
    internal void EndSession(string reason, TimeSpan wait)
    {
        log.Info(AgentLog.SessionEndingLine + " (" + reason + ")");
        Telemetry.LastFlight.Write();
        try
        {
            if (!StopAsync().Wait(wait)) log.Info("the session ended before Armory finished stopping");
        }
        catch (AggregateException error) { log.Error("stop at the end of the session failed", error.InnerException); }
    }

    // The computer went to sleep or woke (0.3.3): in the flight (a pass that spans it is not a
    // slow pass) and the log; on waking, the engine looks for the team's changes at once.
    internal void PowerChanged(bool resumed)
    {
        Telemetry.Recorder.Power(resumed ? "resume" : "suspend");
        log.Info(resumed ? "the computer woke up" : "the computer is going to sleep");
        if (resumed) Wake();
    }

    internal async Task<ActionResult> ReportProblemAsync(string? kind, string? body)
    {
        try
        {
            var (ok, message) = await Telemetry.ReportProblemAsync(kind, body).ConfigureAwait(false);
            return new ActionResult(ok, message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("report a problem failed", error);
            return new ActionResult(false, "Armory couldn't save your report. Try again in a moment.");
        }
    }

    // "Send feedback" and "Your feedback": AgentHost.Feedback.cs.

    // The engine's compact snapshot for an incident, with what the host knows (at once when the
    // engine is not running).
    private async Task<JsonNode?> DescribeAsync(CancellationToken ct)
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) return DescribeNow();
        var described = await engine.DescribeAsync(ct).ConfigureAwait(false);
        return WithHost(described);
    }

    private JsonObject DescribeNow() => WithHost(SyncEngine.DescribeView(View));

    private JsonObject WithHost(JsonObject described)
    {
        string phase;
        lock (gate) phase = connectPhase;
        described["host"] = new JsonObject
        {
            ["runtimeProblem"] = runtimeProblem,
            ["connectPhase"] = phase,
            ["signedIn"] = Sessions.IsSignedIn,
            ["transfersRunning"] = Blobs.ActiveTransfers,
        };
        return described;
    }

    // A notice card's Done or OK, or a check-out question's key ("prompt:<path>:<when>").
    internal void DismissNotice(string key) => OnEngine("dismiss notice", e => e.DismissNotice(key));

    // The open files this computer has not checked out (the tray's quiet balloons, D13).
    internal IReadOnlyCollection<string> OpenWithoutCheckOut
    {
        get
        {
            var engine = Volatile.Read(ref runtime)?.Engine;
            try { return engine?.OpenWithoutCheckOut ?? []; }
            catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("open files", error); return []; }
        }
    }

    // Runs one window action on the engine (every engine method marshals onto the engine's own
    // thread, so the window's thread only awaits). An engine that is not running, or a failure,
    // is a plain sentence, never silence.
    private async Task<ActionResult> OnEngineAsync(string what, Func<SyncEngine, Task<ActionResult>> action)
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) return new ActionResult(false, runtimeProblem ?? "Armory is starting. Try again in a moment.");
        try
        {
            var result = await action(engine).ConfigureAwait(false);
            log.Info("window: " + what + (result.Ok ? " done" : " refused"));
            return result;
        }
        // The engine was stopping for good (a switch of student, a folder change): nothing was done.
        catch (Exception error) when (error is EngineStoppedException || (error is OperationCanceledException && engine.IsStopping))
        {
            log.Info("window: " + what + " refused, the engine was stopping");
            return new ActionResult(false, SharedComputer ? "Armory is switching students. Try again in a moment." : "Armory is restarting. Try again in a moment.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogEngineFailure(what, error);
            return new ActionResult(false, "Armory couldn't do that. Try again in a moment.");
        }
    }

    internal void SignOut()
    {
        // A shared computer: nobody signs out, the next student picks themselves (Switch student).
        if (SharedComputer) { ShowPicker(Armory.Core.PickerTrigger.SwitchStudent); return; }
        CancelConnect();
        Sessions.SignOut();
        RaiseView();
    }

    // Switch account: this person signs out and the next one signs in at once, in the browser
    // (the Armory folder stays; it is handed over when the last person has nothing waiting).
    internal async Task SwitchAccountAsync()
    {
        if (SharedComputer) { ShowPicker(Armory.Core.PickerTrigger.SwitchStudent); return; }
        SignOut();
        await ConnectAsync();
    }

    internal Task<ActionResult> TakeOverFolderAsync() => OnEngineAsync("take over the folder", e => e.TakeOverFolderAsync());

    internal async Task<FileDetailView?> GetFileDetailAsync(Guid fileId)
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) return null;
        try { return await engine.GetFileDetailAsync(fileId); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogEngineFailure("file detail", error);
            return null;
        }
    }

    // Contract section 3 through ConnectFlow, with the window following each phase.
    internal async Task ConnectAsync()
    {
        // A shared computer: a new sign-in is a new student (Add a student).
        if (SharedComputer) { await AddProfileAsync(); return; }
        CancellationTokenSource cancel;
        lock (gate)
        {
            if (connecting is not null) return;
            connecting = cancel = new CancellationTokenSource();
        }
        try
        {
            SetConnect("waitingForBrowser", "Finish signing in with your school Google account in the browser. This window updates by itself.");
            log.Info("connect: waiting for the browser");
            var session = await Connector.ConnectAsync(Environment.MachineName, cancel.Token);
            log.Info("connect: signed in, " + session);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) firstSignedInView = settled;
            SetConnect("finishing", "Signed in as " + session.Email + ". Getting your files list.");
            Wake();
            // "Finishing" lasts until the engine shows the signed-in view (or 20 seconds).
            if (Volatile.Read(ref runtime) is not null)
                await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(20), cancel.Token)).ConfigureAwait(true);
            SetConnect("idle", null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            log.Info("connect: canceled");
            SetConnect("idle", null);
        }
        catch (ConnectException error)
        {
            log.Error("connect: failed" + (error.Status is { } status ? " (" + status + ")" : ""));
            SetConnect("failed", error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("connect: failed", error);
            SetConnect("failed", "That sign-in didn't finish. Check that you're online, then try again.");
        }
        finally
        {
            lock (gate)
            {
                firstSignedInView = null;
                if (ReferenceEquals(connecting, cancel)) connecting = null;
            }
            cancel.Dispose();
        }
    }

    internal void CancelConnect()
    {
        CancellationTokenSource? current;
        lock (gate) current = connecting;
        try { current?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // Saves settings.json, the sign-in start entry and the theme, and restarts the vault
    // runtime when the root changed. Returns a student-facing problem, or null.
    internal async Task<string?> SaveSettingsAsync(string? vaultRoot, bool startAtSignIn, string? theme)
    {
        if (!AgentSettings.TryNormalizeVaultRoot(vaultRoot, out var root, out var problem))
        {
            RaiseView();
            return problem;
        }
        // A shared computer's folders are chosen by the hand-over rule: its shared folder moves
        // only with shared mode off (docs/agent/PROFILES.md).
        if (SharedComputer && !string.Equals(root, Settings.VaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            RaiseView();
            return "To move the shared Armory folder, turn off \"This computer is shared by several students\" first.";
        }
        AgentSettings previous, next;
        lock (gate)
        {
            previous = settings;
            next = previous with { VaultRoot = root!, StartAtSignIn = startAtSignIn, Theme = Themes.Normalize(theme) };
            settings = next;
            effectiveTheme = Themes.Effective(next.Theme, parts.LightApps());
        }
        try { settingsStore.Save(next); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Error("could not save settings", error);
            problem = "Armory could not save its settings. Try again.";
        }
        if (previous.StartAtSignIn != next.StartAtSignIn)
        {
            try { StartupRegistrationApply(next, startup: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { log.Error("could not update the sign-in start entry", error); }
        }
        log.Info($"settings saved: vault {next.VaultRoot}, start at sign-in {next.StartAtSignIn}, theme {next.Theme}");
        if (!string.Equals(previous.VaultRoot, next.VaultRoot, StringComparison.OrdinalIgnoreCase)) await RestartRuntimeAsync(next);
        else ApplySettingsToEngine();
        RaiseView();
        return problem;
    }

    // Windows' app mode changed; only matters while the theme follows the system.
    internal void RefreshSystemTheme()
    {
        lock (gate)
        {
            var next = Themes.Effective(settings.Theme, parts.LightApps());
            if (next == effectiveTheme) return;
            effectiveTheme = next;
        }
        ApplySettingsToEngine();
        RaiseView();
    }

    // One stop, however many ask for it (Quit, and Windows ending the session, which waits on it
    // from its own thread: 0.3.3), run off the window's thread so that a thread blocked waiting
    // for it never holds it up.
    internal Task StopAsync()
    {
        lock (gate) return stopped ??= Task.Run(StopOnceAsync);
    }

    private Task? stopped;

    private async Task StopOnceAsync()
    {
        StopShell();
        hintTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        CancelConnect();
        // A clean stop says so to the team ("offline-soon"), within a few seconds at most, while
        // the engine stops (0.3.3: one after the other, a quit took up to 3 seconds longer).
        var goodbye = GoodbyeAsync();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = Interlocked.Exchange(ref runtime, null);
            if (old is not null) await StopRuntimeAsync(old).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
        await goodbye.ConfigureAwait(false);
    }

    private async Task GoodbyeAsync()
    {
        if (running.IsCancellationRequested) return;
        await running.CancelAsync().ConfigureAwait(false);
        if (beating is not null) { try { await beating.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (beating is not null) await Heartbeat.SayGoodbyeAsync(GoodbyeDeadline).ConfigureAwait(false);
    }

    // The folder the student Armory works for uses: settings' one, or on a shared computer the
    // student in use's (none while nobody is).
    private Task RestartRuntimeAsync(AgentSettings target) => RestartRuntimeAsync(RuntimeFolder(target));

    private async Task RestartRuntimeAsync(string? folder)
    {
        await lifecycle.WaitAsync();
        try
        {
            var old = Interlocked.Exchange(ref runtime, null);
            if (old is not null) await StopRuntimeAsync(old);
            if (folder is not null) await StartRuntimeAsync(folder);
            else runtimeProblem = null;
        }
        finally { lifecycle.Release(); }
        RaiseView();
    }

    // Under the lifecycle gate: a runtime for the folder, with the clients in use, started.
    private async Task<bool> StartRuntimeAsync(string folder, VaultRuntime? made = null)
    {
        runtimeProblem = null;
        engineFailureLogged = false;
        Interlocked.Exchange(ref lastHints, -1);
        var created = made;
        try
        {
            // Opening the journal, snapshots and read-only intents touches the disk: off the UI thread.
            created ??= await CreateRuntimeAsync(folder, clients);
            created.Engine.ViewChanged += OnEngineView;
            created.Engine.ActivityChanged += OnEngineActivity;
            created.Engine.OpenPromptsChanged += OnOpenPrompts;
            ApplySettingsTo(created.Engine);
            lock (gate) { if (connectPhase != "idle") created.Engine.SetConnectState(connectPhase, connectMessage); }
            created.Engine.Start();
            // The SolidWorks link looks for SolidWorks once the engine takes its records.
            created.SolidWorks?.Start();
            Volatile.Write(ref runtime, created);
            log.Info("vault runtime started at " + folder);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("vault runtime could not start at " + folder, error);
            if (created is not null) await StopRuntimeAsync(created);
            runtimeProblem = error switch
            {
                NotImplementedException => "Armory's sync engine is not running on this computer yet.",
                UnauthorizedAccessException => $"Armory is not allowed to use {folder}. Choose another folder in Settings.",
                IOException => $"Armory can't use the folder {folder}. Choose another folder in Settings.",
                _ => $"Armory could not start syncing {folder}. Restart Armory, or choose another folder in Settings.",
            };
            return false;
        }
    }

    // One runtime for a folder and a student's clients, not started (the disk is touched: off the
    // UI thread). A folder another runtime of this process still holds is refused (IOException).
    private Task<VaultRuntime> CreateRuntimeAsync(string folder, ProfileClients with) => Task.Run(() => parts.Runtime(folder, with));

    // Stops the engine for good (StopForGoodAsync: nothing of it runs or writes afterwards), then
    // closes its files. An engine that has not stopped within patience is parked: its files stay
    // open, so no other runtime can take its folder, until it does stop. False when parked.
    private async Task<bool> StopRuntimeAsync(VaultRuntime old, TimeSpan? patience = null)
    {
        old.Engine.ViewChanged -= OnEngineView;
        old.Engine.ActivityChanged -= OnEngineActivity;
        old.Engine.OpenPromptsChanged -= OnOpenPrompts;
        var wait = patience ?? StopTimeout;
        var stopped = false;
        try
        {
            var stop = old.Engine.StopForGoodAsync();
            stopped = await Task.WhenAny(stop, Task.Delay(wait)) == stop;
            if (stopped) await stop;
            else log.Error("the sync engine did not stop within " + wait.TotalSeconds + " seconds; its folder stays closed until it does");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("stop", error); }
        return await old.DisposeAsync(log, stopped ? StopTimeout : TimeSpan.Zero);
    }

    private void OnEngineView(AgentView view)
    {
        TaskCompletionSource? settled;
        lock (gate) settled = firstSignedInView;
        if (settled is not null && view.Connection is Connections.SignedIn or Connections.VaultOwnedByOther) settled.TrySetResult();
        TeamState(view.Activity);
        ViewChanged?.Invoke(WithProfiles(WithSettings(view)));
    }

    // The settings and theme the window shows are the host's, saved this moment, never the
    // engine's copy: the engine takes them on its own thread, which a long pass kept busy, so
    // a theme picked in 0.3.1 came back as the old one until the pass ended (and flickered).
    private AgentView WithSettings(AgentView view)
    {
        var current = SettingsNow();
        string theme;
        lock (gate) theme = effectiveTheme;
        return view.Settings == current && view.EffectiveTheme == theme ? view : view with { Settings = current, EffectiveTheme = theme };
    }

    private void OnEngineActivity(ActivityView activity)
    {
        TeamState(activity);
        // While the picker shows, the page learns nothing of a student's files (decision F7).
        ActivityChanged?.Invoke(PickerShowing ? Unnamed(activity) : activity);
    }

    // "syncing" while files move, "idle" otherwise: a change goes to the team at once.
    private void TeamState(ActivityView activity)
    {
        if (running.IsCancellationRequested) return;
        Heartbeat.SetState(TeamHeartbeat.StateFor(activity.Upload is not null || activity.Download is not null || activity.Move is not null));
    }

    private void RaiseView()
    {
        if (disposed) return;
        ViewChanged?.Invoke(View);
    }

    private void SetConnect(string phase, string? message)
    {
        lock (gate) { connectPhase = phase; connectMessage = message; }
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) { RaiseView(); return; }
        try { engine.SetConnectState(phase, message); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogEngineFailure("connect state", error);
            RaiseView();
        }
    }

    private void ApplySettingsToEngine()
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) return;
        try { ApplySettingsTo(engine); }
        catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("settings", error); }
    }

    private void ApplySettingsTo(SyncEngine engine)
    {
        string theme;
        lock (gate) theme = effectiveTheme;
        // SettingsNow carries the badges row too (AgentHost.Shell.cs).
        engine.ApplySettings(SettingsNow(), theme);
    }

    private void OnEngine(string what, Action<SyncEngine> action)
    {
        var engine = Volatile.Read(ref runtime)?.Engine;
        if (engine is null) { RaiseView(); return; }
        try { action(engine); }
        catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure(what, error); }
    }

    // The engine's own schedule runs the passes; disk notifications only make one come sooner.
    private void PollHints()
    {
        var current = Volatile.Read(ref runtime);
        if (current is null) return;
        var hints = current.Windows?.HintCount ?? 0;
        if (Interlocked.Exchange(ref lastHints, hints) is var previous && previous >= 0 && previous != hints) Wake();
    }

    private void LogEngineFailure(string what, Exception error)
    {
        if (engineFailureLogged && error is NotImplementedException) return;
        if (error is NotImplementedException) engineFailureLogged = true;
        log.Error("engine " + what + " failed", error);
    }

    private AgentView Fallback(string line)
    {
        AgentSettings current;
        string theme, phase;
        string? message;
        lock (gate) { current = settings; theme = effectiveTheme; phase = connectPhase; message = connectMessage; }
        var session = Sessions.Current;
        var connection = session is not null ? Connections.SignedIn
            : phase is "waitingForBrowser" or "finishing" ? Connections.Connecting : Connections.SignedOut;
        return new AgentView(connection, new ConnectView(phase, message),
            session is null ? null : new AccountView(session.Email, session.DeviceName),
            new SyncView(SyncStates.Attention, line, null, 0), new ActivityView(null, null, null, null, null, [], []), current.VaultRoot, [], null, [], [],
            SettingsNow(), theme);
    }

    private void StartupRegistrationApply(AgentSettings settings, bool startup)
    {
        // A test instance (ARMORY_DATA_DIR) never touches the real sign-in start entry.
        if (paths.IsOverridden) return;
        // Only the real IdeaArmory.exe registers itself (never "dotnet IdeaArmory.dll").
        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals("IdeaArmory.exe", StringComparison.OrdinalIgnoreCase)) return;
        // At startup only (re)write the entry, so a moved exe still starts; removing it is
        // only ever the student's choice in Settings.
        if (startup && !settings.StartAtSignIn) return;
        StartupRegistration.Apply(settings.StartAtSignIn, exe);
    }

    internal static HttpClient Http(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IDEA-Armory/" + AgentPaths.Version);
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync();
        disposed = true;
        await hintTimer.DisposeAsync();
        thumbnails.Dispose();
        running.Dispose();
        restHttp.Dispose();
        siteHttp.Dispose();
        storageHttp.Dispose();
        lifecycle.Dispose();
    }
}

// One vault root's disk adapters, its SolidWorks link and its engine. Its stores are closed in reverse order of
// creation, and only once the engine has stopped for good. One process holds one runtime per
// folder at a time (a parked one included): a second is refused, as the stores' own lock files
// (owner.lock, read-only.lock, FileShare.None) refuse one from another process.
internal sealed class VaultRuntime
{
    private static readonly HashSet<string> Held = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable[] stores;
    private int closed;

    internal VaultRuntime(string root, IVaultFileSystem files, SyncEngine engine, params IDisposable[] stores)
    {
        var key = Path.TrimEndingDirectorySeparator(root);
        lock (Held) if (!Held.Add(key)) throw new IOException("Armory is already running the folder " + root + ".");
        Root = key;
        Files = files;
        Engine = engine;
        this.stores = stores;
    }

    internal string Root { get; }
    internal IVaultFileSystem Files { get; }
    // The Windows file system's own extras (disk notifications, thumbnails); null for a test's.
    internal WindowsVaultFileSystem? Windows => Files as WindowsVaultFileSystem;
    internal SyncEngine Engine { get; }
    // The SolidWorks link (docs/agent/SOLIDWORKS.md); null when the runtime was made without one.
    internal Armory.SolidWorks.SolidWorksLink? SolidWorks { get; init; }

    // solidWorksFile: where the SolidWorks link keeps the student's own Save to Version setting;
    // null makes the runtime without a link.
    internal static VaultRuntime Create(string vaultRoot, SessionManager sessions, ArmoryApi api, BlobClient blobs, AgentLog? log = null,
        Armory.Telemetry.FlightRecorder? recorder = null, RealtimeFeed? live = null, string? solidWorksFile = null)
    {
        var disposables = new Stack<IDisposable>();
        Armory.SolidWorks.SolidWorksLink? solidWorks = null;
        try
        {
            var files = new WindowsVaultFileSystem(vaultRoot) { LaunchLog = log is null ? null : log.Info, OpenLog = log is null ? null : log.Info };
            disposables.Push(files);
            var journal = new DurableJournalStore(Path.Combine(files.Root, ".armory", "journal.bin"));
            disposables.Push(journal);
            var snapshots = new WindowsSnapshotStore(new DurableSnapshotStore(new WindowsPaths(files.Root)));
            disposables.Push(snapshots);
            var state = new FileStateStore(Path.Combine(files.Root, ".armory", "state.json"));
            if (solidWorksFile is not null) solidWorks = AgentHost.CreateSolidWorksLink(files.Root, solidWorksFile, log);
            var engine = new SyncEngine(new EngineOptions { VaultRoot = files.Root }, new EngineDependencies
            {
                Files = files,
                Journal = journal,
                Snapshots = snapshots,
                State = state,
                Sessions = sessions,
                Api = api,
                Blobs = blobs,
                // The SolidWorks year of every part, assembly and drawing, read from the file itself
                // (docs/core/solidworks-version-gate.md); the SolidWorks link adds its stamps.
                ReleaseReader = new Armory.Core.SolidWorksSavedReleaseReader(),
                // The raw text of a sync problem; the window shows it in plain words.
                Log = log is null ? null : log.Info,
                Recorder = recorder,
                // Live updates (v0.3): each synced project's change feed, filtered by project.
                Live = live,
                // What SolidWorks does with vault files: opens (C5), saves, save down (B2).
                SolidWorks = solidWorks,
            });
            return new VaultRuntime(files.Root, files, engine, files, journal, snapshots) { SolidWorks = solidWorks };
        }
        catch
        {
            // Nothing attached yet (the link starts after the engine): it only stops its thread.
            solidWorks?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            while (disposables.TryPop(out var item)) item.Dispose();
            throw;
        }
    }

    // True when the files were closed; false when the engine has not stopped yet, and its files
    // stay open (the folder closed to any other runtime) until it does.
    internal async Task<bool> DisposeAsync(AgentLog log, TimeSpan timeout)
    {
        // SolidWorks keeps running: every reference let go, the student's own setting put back.
        if (SolidWorks is not null)
        {
            try
            {
                var release = SolidWorks.DisposeAsync().AsTask();
                if (await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(30))) != release) log.Error("the SolidWorks link did not finish letting go of SolidWorks");
                else await release;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { log.Error("SolidWorks link dispose failed", error); }
        }
        Task dispose;
        try { dispose = Engine.DisposeAsync().AsTask(); }
        catch (Exception error) when (error is not OutOfMemoryException) { dispose = Task.FromException(error); }
        if (await Task.WhenAny(dispose, Task.Delay(timeout)) != dispose)
        {
            log.Error("the sync engine has not stopped; its files stay open until it does");
            _ = dispose.ContinueWith(_ => Close(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return false;
        }
        try { await dispose; }
        catch (Exception error) when (error is not OutOfMemoryException) { if (error is not NotImplementedException) log.Error("engine dispose failed", error); }
        Close();
        return true;
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        for (var i = stores.Length - 1; i >= 0; i--)
        {
            try { stores[i].Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        lock (Held) Held.Remove(Root);
    }
}
