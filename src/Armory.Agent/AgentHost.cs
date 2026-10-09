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

    internal AgentHost(AgentPaths paths, AgentLog log, Uri site, AgentTelemetry telemetry)
    {
        this.paths = paths;
        this.log = log;
        Telemetry = telemetry;
        settingsStore = new SettingsStore(paths.SettingsFile);
        settings = settingsStore.Load();
        effectiveTheme = Themes.Effective(settings.Theme, WindowsTheme.AppsUseLightTheme());
        restHttp = Http(TimeSpan.FromSeconds(30));
        siteHttp = Http(TimeSpan.FromSeconds(30));
        // Up to 2 GiB per transfer on a school network: a generous whole-request limit; a dead
        // connection is still noticed by the connect timeout and the engine's cancellation.
        storageHttp = Http(TimeSpan.FromHours(2));
        var secrets = new DpapiSecretStore(paths.SecretsFolder, problem => log.Error(problem));
        Sessions = new SessionManager(restHttp, secrets);
        // Says whether this start found a saved sign-in (the email only; tokens never reach the
        // log). The upgrade cycle (tools/test-agent-install.ps1 -Kind Upgrade) reads it to prove
        // that a new version still uses the sign-in an older one saved.
        log.Info(Sessions.Current is { } saved ? "session loaded for " + saved.Email : "no saved session");
        Api = new ArmoryApi(new PostgrestClient(restHttp, Sessions, telemetry.Recorder));
        Blobs = new BlobClient(siteHttp, storageHttp, site, Sessions, telemetry.Recorder);
        Connector = new ConnectFlow(siteHttp, site, new DefaultBrowserLauncher(), Sessions);
        Heartbeat = new TeamHeartbeat(Api, Sessions, AgentPaths.Version, log: log.Info);
        // Send feedback, the same as the website's (v0.3.2): the window's one entry point, and the
        // path the saved notes go through too, sharing the uploader's waits.
        Feedback = new FeedbackSender(Api, new FeedbackScreenshots(restHttp, Sessions, telemetry.Recorder), Sessions, AgentPaths.Version,
            telemetry.Limiter, log: log.Info);
        Sessions.SignedOut += () => { log.Info("signed out"); Wake(); };
        hintTimer = new System.Threading.Timer(_ => PollHints(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        telemetry.Attach(() => Sessions.Current, DescribeAsync, DescribeNow, Api, () => Blobs.ActiveTransfers > 0, Feedback);
    }

    internal AgentTelemetry Telemetry { get; }
    internal SessionManager Sessions { get; }
    internal ArmoryApi Api { get; }
    internal BlobClient Blobs { get; }
    internal ConnectFlow Connector { get; }
    internal TeamHeartbeat Heartbeat { get; }
    // The window's Send feedback (FeedbackSender.SendAsync) and "Your feedback"
    // (Api.MyAppFeedbackAsync): docs/agent/CLIENT.md section 7.
    internal FeedbackSender Feedback { get; }
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
            if (engine is null) return Fallback(runtimeProblem ?? "Armory is starting.");
            try { return WithSettings(engine.View); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogEngineFailure("view", error);
                return Fallback("Armory's sync engine is not running on this computer yet.");
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
        beating = Task.Run(() => Heartbeat.RunAsync(running.Token));
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
    // The picture File Explorer shows for a file in the vault (ShellThumbnails), or null.
    internal Task<byte[]?> ThumbnailAsync(string vaultPath)
        => Volatile.Read(ref runtime)?.Files.ExistingFile(vaultPath) is { } file ? thumbnails.GetAsync(file) : Task.FromResult<byte[]?>(null);
    private readonly ShellThumbnails thumbnails = new();
    internal Task<ActionResult> TakeBackAsync(IReadOnlyList<Guid> fileIds) => OnEngineAsync("take back " + fileIds.Count + " files", e => e.TakeBackAsync(fileIds));
    // One file, in the same folder: a file Armory doesn't have yet is renamed on disk; a file in
    // Armory is renamed for everyone (refused while someone else has it checked out).
    internal Task<ActionResult> RenameFileAsync(string path, string newName) => OnEngineAsync("rename a file", e => e.RenameFileAsync(path, newName));
    // Folders (contract C5 and C6): a rename or a removal goes to the team in one call, then here.
    internal Task<ActionResult> CreateFolderAsync(Guid project, string parent, string name) => OnEngineAsync("new folder", e => e.CreateFolderAsync(project, parent, name));
    internal Task<ActionResult> RenameFolderAsync(Guid project, string folder, string newName) => OnEngineAsync("rename a folder", e => e.RenameFolderAsync(project, folder, newName));
    internal Task<ActionResult> DeleteFolderAsync(Guid project, string folder) => OnEngineAsync("delete a folder", e => e.DeleteFolderAsync(project, folder));
    // sources: full paths on this computer (the file picker's, or the files and folders dropped on
    // the window); a folder is copied whole. Copied in, never over anything already there.
    internal Task<ActionResult> AddFilesAsync(Guid project, string folder, IReadOnlyList<string> sources)
        => sources.Count == 0 ? Task.FromResult(new ActionResult(false, "")) : OnEngineAsync("add " + sources.Count + " files or folders", e => e.AddFilesAsync(project, folder, sources));

    // "Report a problem" (docs/agent/TELEMETRY.md): the words and a fresh incident, saved here
    // and sent when the site can take them. Never an error for a site that isn't ready.
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

    // "Send feedback" (v0.3): the words as a note on its own, saved here and sent when the site
    // can take them. One sentence back, never an error for a site that isn't ready.
    internal async Task<ActionResult> SendFeedbackAsync(string? kind, string? body)
    {
        try
        {
            var (ok, message) = await Telemetry.SendFeedbackAsync(kind, body).ConfigureAwait(false);
            return new ActionResult(ok, message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Error("send feedback failed", error);
            return new ActionResult(false, "Armory couldn't save your feedback. Try again in a moment.");
        }
    }

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
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogEngineFailure(what, error);
            return new ActionResult(false, "Armory couldn't do that. Try again in a moment.");
        }
    }

    internal void SignOut()
    {
        CancelConnect();
        Sessions.SignOut();
        RaiseView();
    }

    // Switch account: this person signs out and the next one signs in at once, in the browser
    // (the Armory folder stays; it is handed over when the last person has nothing waiting).
    internal async Task SwitchAccountAsync()
    {
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
        AgentSettings previous, next;
        lock (gate)
        {
            previous = settings;
            next = new AgentSettings(root!, startAtSignIn, Themes.Normalize(theme));
            settings = next;
            effectiveTheme = Themes.Effective(next.Theme, WindowsTheme.AppsUseLightTheme());
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
            var next = Themes.Effective(settings.Theme, WindowsTheme.AppsUseLightTheme());
            if (next == effectiveTheme) return;
            effectiveTheme = next;
        }
        ApplySettingsToEngine();
        RaiseView();
    }

    internal async Task StopAsync()
    {
        hintTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        CancelConnect();
        // A clean stop says so to the team ("offline-soon"), within a few seconds at most.
        if (!running.IsCancellationRequested)
        {
            await running.CancelAsync();
            if (beating is not null) { try { await beating.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            if (beating is not null) await Heartbeat.SayGoodbyeAsync(GoodbyeDeadline).ConfigureAwait(false);
        }
        await lifecycle.WaitAsync();
        try
        {
            var old = Interlocked.Exchange(ref runtime, null);
            if (old is not null) await StopRuntimeAsync(old);
        }
        finally { lifecycle.Release(); }
    }

    private async Task RestartRuntimeAsync(AgentSettings target)
    {
        await lifecycle.WaitAsync();
        try
        {
            var old = Interlocked.Exchange(ref runtime, null);
            if (old is not null) await StopRuntimeAsync(old);
            runtimeProblem = null;
            engineFailureLogged = false;
            Interlocked.Exchange(ref lastHints, -1);
            VaultRuntime? created = null;
            try
            {
                // Opening the journal, snapshots and read-only intents touches the disk: off the UI thread.
                created = await Task.Run(() => VaultRuntime.Create(target.VaultRoot, Sessions, Api, Blobs, log, Telemetry.Recorder, new RealtimeFeed(Sessions, log: log.Info)));
                created.Engine.ViewChanged += OnEngineView;
                created.Engine.ActivityChanged += OnEngineActivity;
                ApplySettingsTo(created.Engine);
                lock (gate) { if (connectPhase != "idle") created.Engine.SetConnectState(connectPhase, connectMessage); }
                created.Engine.Start();
                Volatile.Write(ref runtime, created);
                log.Info("vault runtime started at " + target.VaultRoot);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Error("vault runtime could not start at " + target.VaultRoot, error);
                if (created is not null) await StopRuntimeAsync(created);
                runtimeProblem = error switch
                {
                    NotImplementedException => "Armory's sync engine is not running on this computer yet.",
                    UnauthorizedAccessException => $"Armory is not allowed to use {target.VaultRoot}. Choose another folder in Settings.",
                    IOException => $"Armory can't use the folder {target.VaultRoot}. Choose another folder in Settings.",
                    _ => $"Armory could not start syncing {target.VaultRoot}. Restart Armory, or choose another folder in Settings.",
                };
            }
        }
        finally { lifecycle.Release(); }
        RaiseView();
    }

    private async Task StopRuntimeAsync(VaultRuntime old)
    {
        old.Engine.ViewChanged -= OnEngineView;
        old.Engine.ActivityChanged -= OnEngineActivity;
        try
        {
            var stop = old.Engine.StopAsync();
            if (await Task.WhenAny(stop, Task.Delay(StopTimeout)) != stop) log.Error("the sync engine did not stop within " + StopTimeout.TotalSeconds + " seconds");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { LogEngineFailure("stop", error); }
        await old.DisposeAsync(log, StopTimeout);
    }

    private void OnEngineView(AgentView view)
    {
        TaskCompletionSource? settled;
        lock (gate) settled = firstSignedInView;
        if (settled is not null && view.Connection is Connections.SignedIn or Connections.VaultOwnedByOther) settled.TrySetResult();
        TeamState(view.Activity);
        ViewChanged?.Invoke(WithSettings(view));
    }

    // The settings and theme the window shows are the host's, saved this moment, never the
    // engine's copy: the engine takes them on its own thread, which a long pass kept busy, so
    // a theme picked in 0.3.1 came back as the old one until the pass ended (and flickered).
    private AgentView WithSettings(AgentView view)
    {
        SettingsView current;
        string theme;
        lock (gate) { current = settings.ToView(); theme = effectiveTheme; }
        return view.Settings == current && view.EffectiveTheme == theme ? view : view with { Settings = current, EffectiveTheme = theme };
    }

    private void OnEngineActivity(ActivityView activity)
    {
        TeamState(activity);
        ActivityChanged?.Invoke(activity);
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
        AgentSettings current;
        string theme;
        lock (gate) { current = settings; theme = effectiveTheme; }
        engine.ApplySettings(current.ToView(), theme);
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
        var hints = current.Files.HintCount;
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
            current.ToView(), theme);
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

    private static HttpClient Http(TimeSpan timeout)
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

// One vault root's disk adapters and its engine. Disposed in reverse order of creation.
internal sealed class VaultRuntime
{
    private VaultRuntime(WindowsVaultFileSystem files, DurableJournalStore journal, WindowsSnapshotStore snapshots, SyncEngine engine)
    {
        Files = files;
        Journal = journal;
        Snapshots = snapshots;
        Engine = engine;
    }

    internal WindowsVaultFileSystem Files { get; }
    internal DurableJournalStore Journal { get; }
    internal WindowsSnapshotStore Snapshots { get; }
    internal SyncEngine Engine { get; }

    internal static VaultRuntime Create(string vaultRoot, SessionManager sessions, ArmoryApi api, BlobClient blobs, AgentLog? log = null,
        Armory.Telemetry.FlightRecorder? recorder = null, RealtimeFeed? live = null)
    {
        var disposables = new Stack<IDisposable>();
        try
        {
            var files = new WindowsVaultFileSystem(vaultRoot) { LaunchLog = log is null ? null : log.Info, OpenLog = log is null ? null : log.Info };
            disposables.Push(files);
            var journal = new DurableJournalStore(Path.Combine(files.Root, ".armory", "journal.bin"));
            disposables.Push(journal);
            var snapshots = new WindowsSnapshotStore(new DurableSnapshotStore(new WindowsPaths(files.Root)));
            disposables.Push(snapshots);
            var state = new FileStateStore(Path.Combine(files.Root, ".armory", "state.json"));
            var engine = new SyncEngine(new EngineOptions { VaultRoot = files.Root }, new EngineDependencies
            {
                Files = files,
                Journal = journal,
                Snapshots = snapshots,
                State = state,
                Sessions = sessions,
                Api = api,
                Blobs = blobs,
                // No standalone saved-release reader exists yet (docs/platform/audit.md).
                ReleaseReader = null,
                // The raw text of a sync problem; the window shows it in plain words.
                Log = log is null ? null : log.Info,
                Recorder = recorder,
                // Live updates (v0.3): each synced project's change feed, filtered by project.
                Live = live,
            });
            return new VaultRuntime(files, journal, snapshots, engine);
        }
        catch
        {
            while (disposables.TryPop(out var item)) item.Dispose();
            throw;
        }
    }

    internal async Task DisposeAsync(AgentLog log, TimeSpan timeout)
    {
        try
        {
            var dispose = Engine.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(timeout)) != dispose) log.Error("the sync engine did not finish stopping; closing its files anyway");
            else await dispose;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (error is not NotImplementedException) log.Error("engine dispose failed", error); }
        Snapshots.Dispose();
        Journal.Dispose();
        Files.Dispose();
    }
}
