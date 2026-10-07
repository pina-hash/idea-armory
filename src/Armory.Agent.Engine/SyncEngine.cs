using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Engine;

public sealed record EngineOptions
{
    public required string VaultRoot { get; init; }
    public TimeSpan ActivePollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StaleMarkerAfter { get; init; } = TimeSpan.FromMinutes(10);
    // The contract's PUT limit (2 GiB); tests lower it.
    public long MaximumFileBytes { get; init; } = Armory.Client.BlobClient.MaximumPutBytes;
    // How many files a pass moves at once (uploads, downloads and the server calls around them).
    // Chosen by measurement on the school network profile (docs/agent/PROOF.md, ThroughputTests).
    public int TransferConcurrency { get; init; } = DefaultTransferConcurrency;
    public const int DefaultTransferConcurrency = 6;
}

public sealed class EngineDependencies
{
    public required IVaultFileSystem Files { get; init; }
    public required IJournalStore Journal { get; init; }
    public required ISnapshotStore Snapshots { get; init; }
    public required IEngineStateStore State { get; init; }
    public required SessionManager Sessions { get; init; }
    public required ArmoryApi Api { get; init; }
    public required BlobClient Blobs { get; init; }
    public ISavedReleaseReader? ReleaseReader { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    // Where the raw text of a problem goes (the agent's log). The window only ever shows a
    // plain sentence for it.
    public Action<string>? Log { get; init; }
}

public sealed record SyncReport(bool SignedIn, bool Online, int Uploaded, int Downloaded, int SideVersions, int Refused, IReadOnlyList<string> Problems);

// The sync loop (docs/agent/ENGINE.md). Core decides; the engine gathers inputs, executes
// Core's plans in order against the server and the disk, and records durable state. v2 check
// out: Core runs in Explicit mode, a file the server has is read-only unless this computer has
// it checked out, and the student's check out, check in and undo are durable requests the
// pass carries out (SyncEngine.Checkout.cs). All of it runs on one engine thread (EngineThread):
// every public method marshals onto it, and a pass moves files as interleaved async units on it.
public sealed partial class SyncEngine : IAsyncDisposable
{
    private readonly EngineOptions options;
    private readonly EngineDependencies deps;
    private readonly IVaultFileSystem fs;
    private readonly OfflineJournal journal;
    private readonly SaveRecorder recorder;
    private readonly EngineThread engineThread;
    private readonly ActivityTracker activity;
    private readonly SemaphoreSlim passGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<string, SolidWorksRelease?> releases = new(StringComparer.Ordinal);
    private EngineState state = null!;
    private Task? loop;
    private volatile bool paused;
    private DateTimeOffset lastActivity = DateTimeOffset.MinValue;
    private DateTimeOffset? lastOnline;
    private bool? online;
    private bool syncing;
    private string connectPhase = "idle";
    private string? connectMessage;
    private SettingsView settings;
    private string effectiveTheme = "idea";
    private AgentView view = null!;

    // Pass-scoped data.
    private readonly Dictionary<string, LocalFile> local = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> markerDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> localFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, IReadOnlyList<RemoteFile>> remoteProjects = [];
    private readonly Dictionary<string, (RemoteFile File, ProjectState Project)> remoteByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (RemoteFile File, ProjectState Project, VaultPath Path)> remoteById = [];
    private readonly List<string> problems = [];
    private readonly List<Note> notes = [];
    private int uploaded, downloaded, sideVersions, refused;
    private bool wrote;

    // Test seams. CrashPoint throws to simulate a process crash between named steps.
    internal Action<string>? CrashPoint { get; set; }

    // A named step inside a file's unit. After a crash in another unit (the pass's token is
    // canceled), no unit passes another step: none of them saves, sends or writes anything more.
    private void Checkpoint(string point, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CrashPoint?.Invoke(point);
    }

    public SyncEngine(EngineOptions options, EngineDependencies dependencies)
    {
        this.options = options;
        deps = dependencies;
        fs = dependencies.Files;
        journal = new OfflineJournal(dependencies.Journal);
        recorder = new SaveRecorder(dependencies.Snapshots, journal);
        settings = new SettingsView(options.VaultRoot, true, "system");
        activity = new ActivityTracker(dependencies.Clock);
        engineThread = new EngineThread(error => dependencies.Log?.Invoke("engine: " + error));
        // Even the state document is read on the engine thread.
        engineThread.Send(_ =>
        {
            state = EngineState.Load(dependencies.State);
            view = BuildView();
            lastViewBuilt = dependencies.Clock.GetTimestamp();
        }, null);
    }

    public AgentView View => Volatile.Read(ref view);
    // Raised on the engine thread with each new view.
    public event Action<AgentView>? ViewChanged;
    // Raised at most four times a second while files move (and once when they stop), from a
    // timer thread, with what is moving right now. The window patches its activity panel and
    // status line from it without a whole view.
    public event Action<ActivityView>? ActivityChanged;
    public bool IsPaused => paused;
    public void Pause() => engineThread.Enqueue(() => { paused = true; RequestPublish(); });
    public void Resume() => engineThread.Enqueue(() => { paused = false; RequestPublish(); wake.Release(); });
    public void Wake() => engineThread.Enqueue(() => wake.Release());

    public void Start() => engineThread.Enqueue(() => loop ??= LoopAsync(stopping.Token));

    public Task StopAsync() => engineThread.InvokeAsync(async () =>
    {
        await stopping.CancelAsync();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        return true;
    });

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await engineThread.InvokeAsync(async () =>
        {
            await SettleAsync();
            StopActivity();
            return true;
        });
        stopping.Dispose();
    }

    public void SetConnectState(string phase, string? message) => engineThread.Enqueue(() =>
    {
        connectPhase = phase;
        connectMessage = message;
        RequestPublish();
        if (phase == "idle" && deps.Sessions.IsSignedIn) wake.Release();
    });

    public void ApplySettings(SettingsView newSettings, string newEffectiveTheme) => engineThread.Enqueue(() =>
    {
        settings = newSettings;
        effectiveTheme = newEffectiveTheme;
        RequestPublish();
    });

    private async Task<bool> LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!paused)
            {
                try { await SyncOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return true; }
                catch (Exception error) { lastLoopError = "Sync stopped for a moment: " + error.GetType().Name; RequestPublish(); }
            }
            var idle = online != true || deps.Clock.GetUtcNow() - lastActivity > options.IdleAfter;
            var delay = idle ? options.IdlePollInterval : options.ActivePollInterval;
            try { await wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { return true; }
            while (wake.CurrentCount > 0) await wake.WaitAsync(0, ct);
        }
        return true;
    }

    public Task<SyncReport> SyncOnceAsync(CancellationToken cancellationToken = default) => engineThread.InvokeAsync(async () =>
    {
        await passGate.WaitAsync(cancellationToken);
        try { return await PassLockedAsync(cancellationToken); }
        finally { passGate.Release(); }
    });

    // One pass, by whoever holds the pass gate (the loop, a test, or an action from the window).
    // Every write it started is on disk before it returns; after a failure (a crash, in tests)
    // nothing more is saved: what was not on disk yet is lost, as it would be in a real crash.
    private async Task<SyncReport> PassLockedAsync(CancellationToken ct)
    {
        var failed = true;
        try
        {
            syncing = true;
            inPass = true;
            refreshesThisPass = 0;
            StartActivity();
            PublishLocked();
            var report = await PassAsync(ct);
            failed = false;
            return report;
        }
        finally
        {
            inPass = false;
            syncing = false;
            activity.Reset();
            if (failed) await DrainAsync();
            else await SettleAsync();
            PublishLocked();
            StopActivity();
        }
    }

    // A pass (docs/agent/ENGINE.md, "One pass"). Phase A, in order and one step at a time:
    // identity, scan, folder changes, capture, refresh, writes a crash left in flight, folder
    // operations, the team's moves, imports, Explorer moves, earlier saves, folder removals.
    // Phase B plans every path with Core. Phase C runs each file's plan as one unit, units
    // concurrently (EngineOptions.TransferConcurrency). Phase D reads the server again if
    // anything was written, finishes check ins, undos and check outs, and applies the read-only rule.
    private async Task<SyncReport> PassAsync(CancellationToken ct)
    {
        problems.Clear(); notes.Clear();
        uploaded = downloaded = sideVersions = refused = 0;
        wrote = false;
        state.Remembered.RemoveAll(n => deps.Clock.GetUtcNow() - n.At > TimeSpan.FromMinutes(30));
        state.Imports.RemoveAll(i => deps.Clock.GetUtcNow() - i.At > ImportShownFor);
        var session = deps.Sessions.Current;
        if (session is null) return Report(false);
        if (state.Email is not null && !string.Equals(state.Email, session.Email, StringComparison.OrdinalIgnoreCase)) return Report(true);
        if (state.Email is null || state.DeviceId != session.DeviceId)
        {
            // A reconnect of the same person registers a new device; its work and the locks
            // it holds (its check outs) stay this computer's.
            if (state.DeviceId is { } former && !state.FormerDevices.Contains(former)) state.FormerDevices.Add(former);
            state.Email = session.Email;
            state.DeviceId = session.DeviceId;
            SaveNow();
        }

        if (!recovered)
        {
            // Re-journal any capture a crash left unjournaled, once per start.
            try { recorder.Recover(); recovered = true; }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                Problem(NoticeKinds.CantRead, null, "Armory couldn't read its safe copies of your saves on this computer. Your files are untouched, and Armory tries again when it starts.",
                    "The save journal needs attention: " + error.Message);
            }
        }

        local.Clear(); markerDocuments.Clear(); localFolders.Clear(); createdThisPass.Clear();
        VaultScan scan;
        try { scan = fs.Scan(); }
        catch (IOException error)
        {
            Problem(NoticeKinds.CantRead, null, "Armory can't look through your Armory folder right now. It tries again by itself.", error.Message);
            return Report(true);
        }
        scanned = true;
        foreach (var file in scan.Files) local[file.Path.Value] = file;
        folderScan = scan.Folders is not null;
        if (scan.Folders is { } folders) localFolders.UnionWith(folders);
        ReadMarkers(scan);
        foreach (var problem in scan.Problems) ScanProblem(problem);
        // Folder changes on this disk come before anything is captured: a renamed folder's
        // files keep their records (never captured again as new files at the new path), and a
        // project folder renamed or removed in Explorer is put back before anything could take
        // its files for removed ones (SyncEngine.Folders.cs).
        DetectFolderChanges(scan);

        // Saves are captured before any network step, so nothing on the server side can stop
        // this computer from keeping every save.
        AttachEntries();
        Capture(session, notify: false);
        CrashPoint?.Invoke("after-capture");

        online = await RefreshAsync(ct);
        if (online == true)
        {
            lastOnline = deps.Clock.GetUtcNow();
            KeepCheckedOut();
            // A write a crash left in flight is sent again; the projects it wrote to are read again.
            if (await ResumeInflightAsync(ct) && online == true) online = await RefreshStaleAsync(ct);
            // A folder renamed or removed here: one server call each, after any file write a
            // crash left in flight (which lands in the folder as it was). Only the projects a
            // folder call changed are read again.
            if (online == true) await SendFolderOpsAsync(ct);
            if (online == true) online = await RefreshStaleAsync(ct);
        }
        if (online == true)
        {
            // The team's folder renames move here in one step each; anything else moves file by
            // file. Moves on this disk change nothing on the server: nothing is read again.
            ApplyRemoteFolderMoves();
            ApplyRemoteMoves();
            AdoptIdenticalBases();
            Capture(session, notify: true); // projects learned this pass
        }
        DetectImports();
        var entries = AttachEntries();
        if (online == true)
        {
            DetectLocalMoves(scan);
            await ExecutePendingMovesAsync(ct);
            await ArchiveSupersededAsync(entries, ct);
            // A known folder gone for a second scan: one removal for the team.
            if (online == true) await RemoveMissingFoldersAsync(ct);
            if (online == true) online = await RefreshStaleAsync(ct);
        }

        lastLoopError = null;
        // Phase B: every path planned with Core, grouped into units; phase C: the units.
        var units = await PlanAllAsync(online == true, ct);
        await RunUnitsAsync(units, ct);

        if (online == true)
        {
            // Check outs, check ins and undos are decided on the server's state after this
            // pass's own writes (the second and last read of the server in a pass). Read-only
            // then follows the locks as they are after this pass's own lock changes, which this
            // computer knows without reading again (KnowLock).
            if (wrote) online = await RefreshAsync(ct);
            if (online == true)
            {
                wrote = false;
                await FinishRequestsAsync(ct);
            }
        }
        // Known folders with nothing left in them go, on every computer (decision D17).
        if (online == true) TidyFolders();
        // The read-only rule holds offline too, from the last ownership this computer knew.
        ApplyReadOnly();
        // Every notice of this pass is known now, so dismissed items that are gone are forgotten.
        if (online == true) PruneDismissed();
        return Report(true);
    }

    private SyncReport Report(bool signedIn)
    {
        LogNewProblems();
        return new(signedIn, online == true, uploaded, downloaded, sideVersions, refused, problems.ToArray());
    }

    // The raw text of each problem goes to the log once, when it first appears.
    private HashSet<string> loggedProblems = new(StringComparer.Ordinal);
    private void LogNewProblems()
    {
        if (deps.Log is { } log)
            foreach (var problem in problems.Where(p => !loggedProblems.Contains(p))) log("sync: " + problem);
        loggedProblems = new(problems, StringComparer.Ordinal);
    }

    // ---- Refresh -------------------------------------------------------------------

    // A project's files as last read: when, and under which folder (paths are made from it).
    private sealed record ProjectRead(string Folder, long At);
    private readonly Dictionary<Guid, ProjectRead> projectReads = [];
    // Projects this computer wrote to since their files were last read.
    private readonly HashSet<Guid> projectsWritten = [];
    // Read again at least this often even when the change feed says nothing moved.
    private static readonly TimeSpan SafetyRefresh = TimeSpan.FromSeconds(60);
    private int refreshesThisPass;
    private bool refreshing;

    // armory_my_projects, then per project its change feed and, when the feed moved (or this
    // computer wrote there, or a minute went by), its files. Every write emits a change, so a
    // quiet feed means the files as last read are still the files; this computer's own lock
    // changes are known without reading (KnowLock).
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<RemoteProject> projects;
        try { projects = await deps.Api.MyProjectsAsync(ct); }
        catch (ArmoryOfflineException) { return false; }
        catch (ArmorySignedOutException) { return false; }
        if (inPass) refreshesThisPass++;
        refreshing = true;
        try
        {
            var names = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in state.Projects.Keys.Except(projects.Select(p => p.Id)).ToArray()) state.Projects[gone].Usable = false;
            foreach (var project in projects)
            {
                if (!state.Projects.TryGetValue(project.Id, out var ps)) state.Projects[project.Id] = ps = new ProjectState { Id = project.Id };
                ps.Name = project.Name;
                ps.PinnedRelease = project.PinnedRelease;
                ps.Enforce = project.ReleaseGate == ProjectReleaseGate.Enforce;
                ps.Role = RoleName(project.Role);
                ps.Archived = project.Archived;
                ps.Usable = VaultPath.TryValidateName(project.Name, out _) && names[project.Name] == 1;
                if (!ps.Usable)
                {
                    Notice(NoticeKinds.CantSend, null, project.Name, $"{project.Name} can't be a folder name on Windows, so its files stay off this computer. A lead must rename the project on ideabosco.com.");
                    continue;
                }
                if (ps.Folder.Length == 0)
                {
                    // New to this computer: its folder is its name, unless another project's folder
                    // still has that name (its rename waits for a file to close).
                    if (state.Projects.Values.Any(p => !ReferenceEquals(p, ps) && p.Usable && string.Equals(p.Folder, project.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        ps.Usable = false;
                        continue;
                    }
                    ps.Folder = project.Name;
                }
                // Archived (decision D8): skipped silently, its folder and files left as they are.
                if (ps.Archived) continue;
                // Renamed on the site (contract C2): the folder moves in place, once nothing in it is open.
                if (!string.Equals(ps.Folder, ps.Name, StringComparison.Ordinal) && ps.PutBackFrom is null && !heldProjects.Contains(ps.Id)) MoveProjectFolder(ps);
                // Removed on this disk: made again, its files downloaded (decision D16).
                if (restoreProjects.Contains(ps.Id)) { RestoreProjectFolder(ps); continue; }
                // Made only when it is not there and not waiting to be put back: never a second
                // folder beside one a student renamed.
                if (ps.PutBackFrom is not null || heldProjects.Contains(ps.Id) || FolderOnDisk(ps.Folder)) continue;
                try { fs.EnsureFolder(ps.Folder); localFolders.Add(ps.Folder); }
                catch (IOException error) { Problem(NoticeKinds.CantRead, ps.Folder, $"Armory couldn't make the folder for {project.Name} on this computer. It tries again by itself.", error.Message); }
            }
            HashSet<Guid> read = [];
            try
            {
                foreach (var ps in state.Projects.Values.Where(p => p.Usable).ToArray())
                {
                    // An archived project is not read, unless this computer still has check outs
                    // there to check in (addendum 7); its change cursor stays for when it is restored.
                    if (ps.Archived && !state.Files.Values.Any(f => f.ProjectId == ps.Id && MineToFinish(f))) continue;
                    var moved = ps.Archived;
                    if (!ps.Archived)
                    {
                        var changes = await deps.Api.ListChangesAsync(ps.Id, ps.Cursor, ct);
                        foreach (var change in changes)
                        {
                            if (change.Kind == "lock_broken" && change.Payload["former_device_id"]?.GetValue<string>() is { } former &&
                                Guid.TryParse(former, out var device) && state.IsMine(device))
                                foreach (var st in state.WithFileId(change.EntityId)) st.BreakNotice = true;
                            if (change.Kind == "file_revived") RecordRevival(change.EntityId, change.CreatedAt);
                            // Another computer renamed a folder: moved here in one step (ApplyRemoteFolderMoves).
                            if (change.Kind == "folder_renamed" && Text(change.Payload, "from") is { } from && Text(change.Payload, "to") is { } to &&
                                !(Guid.TryParse(Text(change.Payload, "device_id"), out var by) && state.IsMine(by)))
                                state.RemoteFolderRenames.Add(new RemoteFolderRename(ps.Id, from, to));
                            ps.Cursor = Math.Max(ps.Cursor, change.Cursor);
                        }
                        if (changes.Count > 0) lastActivity = deps.Clock.GetUtcNow();
                        moved = changes.Count > 0;
                    }
                    read.Add(ps.Id);
                    if (!moved && FilesStillKnown(ps)) continue;
                    KnowProject(ps, await deps.Api.ProjectFilesAsync(ps.Id, ct));
                }
                // A project not read now (no longer a member, archived, unusable): nothing of its is known.
                foreach (var gone in remoteProjects.Keys.Where(id => !read.Contains(id)).ToArray()) ForgetProject(gone);
                staleProjects.IntersectWith(read);
                RememberHolders();
                MarkDirty();
                return true;
            }
            catch (ArmoryOfflineException) { MarkDirty(); return false; }
        }
        finally
        {
            refreshing = false;
            PublishRemote();
            if (inPass) PublishSoon();
        }
    }

    // The project's files as last read are still the files: nothing moved in its change feed,
    // this computer wrote nothing there, its folder is the same, and they were read in the last minute.
    private bool FilesStillKnown(ProjectState ps)
        => remoteProjects.ContainsKey(ps.Id) && !projectsWritten.Contains(ps.Id) && !staleProjects.Contains(ps.Id) &&
           projectReads.TryGetValue(ps.Id, out var last) && string.Equals(last.Folder, ps.Folder, StringComparison.Ordinal) &&
           deps.Clock.GetElapsedTime(last.At) < SafetyRefresh;

    // A project's files as just read replace what was known of it.
    private void KnowProject(ProjectState ps, IReadOnlyList<RemoteFile> files)
    {
        ForgetProject(ps.Id);
        remoteProjects[ps.Id] = files;
        foreach (var file in files) Know(ps, file, publish: false);
        projectReads[ps.Id] = new ProjectRead(ps.Folder, deps.Clock.GetTimestamp());
        projectsWritten.Remove(ps.Id);
        staleProjects.Remove(ps.Id);
    }

    private void ForgetProject(Guid project)
    {
        remoteProjects.Remove(project);
        foreach (var (id, known) in remoteById.Where(r => r.Value.Project.Id == project).ToArray())
        {
            remoteById.Remove(id);
            if (remoteByPath.TryGetValue(known.Path.Value, out var byPath) && byPath.File.Id == id) remoteByPath.Remove(known.Path.Value);
        }
        liveNames.Remove(project);
        projectReads.Remove(project);
    }

    // What File detail reads (GetFileDetailAsync): the server's files as last known, published
    // whenever that changes, so a detail never waits for a pass or sees a read half done.
    private ImmutableDictionary<Guid, (RemoteFile File, ProjectState Project, VaultPath Path)> publishedRemote = ImmutableDictionary<Guid, (RemoteFile, ProjectState, VaultPath)>.Empty;
    private void PublishRemote() => publishedRemote = remoteById.ToImmutableDictionary();

    private static string? Text(JsonObject payload, string name) => payload[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Records one server file under its local path (and, unless a whole project is being read,
    // in what File detail reads).
    private void Know(ProjectState ps, RemoteFile file, bool publish = true)
    {
        var relative = ps.Folder + "/" + (file.Folder.Length == 0 ? "" : file.Folder + "/") + file.Name;
        if (!VaultPath.TryCreate(relative, out var path, out var problem, options.VaultRoot))
        {
            if (!file.Deleted)
                Notice(NoticeKinds.CantSend, file.Id, relative, "Its name can't be used on Windows, so it stays off this computer. A lead must rename it on ideabosco.com." +
                    (problem is null ? "" : " " + problem));
            return;
        }
        if (remoteById.TryGetValue(file.Id, out var old)) remoteByPath.Remove(old.Path.Value);
        remoteByPath[path.Value] = (file, ps);
        remoteById[file.Id] = (file, ps, path);
        if (publish && !refreshing) publishedRemote = publishedRemote.SetItem(file.Id, (file, ps, path));
    }

    // Every tracked file's check out as the server has it now, kept so the window still says who
    // has it while this computer is offline (even after a restart).
    private void RememberHolders()
    {
        foreach (var st in state.Files.Values)
            if (st.FileId is { } id && remoteById.TryGetValue(id, out var remote)) st.Holder = Known(remote.File.Lock);
    }

    private static KnownLock? Known(RemoteLock? held) => held is { IsLive: true } ? new(held.HolderEmail, held.HolderDeviceId, held.HolderDeviceName, held.AcquiredAt) : null;

    // This computer took or let go of a file's lock: what it knows of the server says so at once,
    // so the read-only rule and the window never act on the lock as it was before, even when the
    // connection drops before the server is read again.
    private void KnowLock(Guid fileId, RemoteLock? held)
    {
        foreach (var st in state.WithFileId(fileId)) st.Holder = Known(held);
        if (!remoteById.TryGetValue(fileId, out var known)) return;
        var file = known.File with { Lock = held };
        remoteById[fileId] = (file, known.Project, known.Path);
        if (remoteByPath.TryGetValue(known.Path.Value, out var byPath) && byPath.File.Id == fileId) remoteByPath[known.Path.Value] = (file, known.Project);
        // The project's list keeps the lock as read; the window and every decision read remoteById.
        if (!refreshing) publishedRemote = publishedRemote.SetItem(fileId, (file, known.Project, known.Path));
    }

    private void RecordRevival(Guid fileId, DateTimeOffset at)
    {
        if (!state.Revivals.TryGetValue(fileId, out var times)) state.Revivals[fileId] = times = [];
        if (!times.Contains(at)) times.Add(at);
    }

    private static string RoleName(MemberRole role) => role switch
    {
        MemberRole.CadLead => "cad_lead", MemberRole.Mentor => "mentor", MemberRole.Instructor => "instructor", _ => "student",
    };

    // A local file byte-identical to the server's current version is that version: this is
    // how a computer that already has copies starts, and how a download whose bookkeeping a
    // crash interrupted is recognized, without inventing a conflict. Identical bytes are
    // already preserved, so this decides nothing Core decides.
    private void AdoptIdenticalBases()
    {
        foreach (var (key, remote) in remoteByPath)
        {
            if (remote.File.Deleted || remote.File.Current is not { } current || !local.TryGetValue(key, out var file) || current.Hash != file.Hash) continue;
            state.Files.TryGetValue(key, out var st);
            if (st is not null && (st.Inflight is not null || (st.FileId is not null && st.FileId != remote.File.Id))) continue;
            if (state.FirstWithFileId(remote.File.Id, except: st) is not null) continue;
            st ??= FileFor(remote.Project, key);
            st.FileId ??= remote.File.Id;
            if (st.BaseId == current.Id.ToString()) continue;
            st.SetBase(new(current.Id.ToString(), current.Hash, current.Author));
            st.LastCaptured ??= current.Hash;
            Complete(st, current.Hash);
            MarkDirty();
        }
    }

    // ---- Capture -------------------------------------------------------------------

    private void Capture(ArmorySession session, bool notify)
    {
        notify |= online == false && state.Projects.Count > 0;
        foreach (var file in local.Values.OrderBy(f => f.Path))
        {
            var key = file.Path.Value;
            if (HeldForCapture(key))
            {
                // A folder on its way back where it was (a project folder renamed in Explorer, a
                // refused rename waiting for a file to close): a file Armory knows there keeps
                // every save, recorded under the path it goes back to, while it waits; a new file
                // there waits until the folder is back.
                var home = HomeOf(key) ?? key;
                if (state.Files.TryGetValue(home, out var held) && VaultPath.TryCreate(home, out var homePath, out _, options.VaultRoot))
                    CaptureSave(session, held, file, homePath);
                continue;
            }
            var project = ProjectOf(file.Path);
            if (project is null)
            {
                if (notify) Notice(NoticeKinds.CantSend, null, key, "It is outside every project. Move it into one of your project folders so Armory can keep it.");
                continue;
            }
            var known = state.Files.TryGetValue(key, out var existing);
            // Archived (decision D8): only this computer's own check outs there are kept up.
            if (project.Archived && !MineToFinish(existing)) continue;
            if (!known) createdThisPass.Add(key);
            CaptureSave(session, known ? existing! : FileFor(project, key), file, file.Path);
        }
    }

    // One save of a file kept here (journal and snapshot) before anything else happens to it,
    // recorded under the record's path; the bytes are read where the file is on disk now.
    private void CaptureSave(ArmorySession session, FileState st, LocalFile file, VaultPath recordPath)
    {
        if (state.Projects.GetValueOrDefault(st.ProjectId) is { Archived: true } && !MineToFinish(st)) return;
        if (file.Hash == st.BaseHash || file.Hash == st.LastCaptured) return;
        var id = NextId("save"); // saved (by its block) before it is used, so it is never reused
        try
        {
            SavedSnapshot snapshot;
            using (var source = fs.OpenRead(file.Path)) snapshot = Record(id, recordPath, session.Email, source);
            // The journal holds the save now; a crash before the next save of this document is
            // found again by AttachEntries.
            st.Entries.Add(id);
            st.LastCaptured = snapshot.Hash;
            lastActivity = deps.Clock.GetUtcNow();
            MarkDirty();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Problem(NoticeKinds.CantRead, file.Path.Value, "Armory couldn't read it to keep your save. Close any program that might be using it. Armory tries again by itself.", error.Message);
        }
    }

    // Every journaled save belongs to a file state. A capture that a crash left out of the
    // state document is attached here, so its bytes are preserved like any other save.
    private Dictionary<string, JournalEntry> AttachEntries()
    {
        var all = journal.Read().Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var attached = state.Files.Values.SelectMany(f => f.Entries).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var entry in all.Values.Where(e => e.Kind == IntentKind.Upload && !state.Completed.Contains(e.Id) && !attached.Contains(e.Id)))
        {
            if (!VaultPath.TryCreate(entry.Path, out var path, out _, options.VaultRoot)) continue;
            var project = ProjectOf(path);
            if (project is null) continue;
            var st = FileFor(project, path.Value);
            st.Entries.Add(entry.Id);
            st.LastCaptured = entry.Hash;
            changed = true;
        }
        if (changed) MarkDirty();
        return all;
    }

    // ---- Planning ------------------------------------------------------------------

    private IEnumerable<string> AllPaths()
        => local.Keys.Concat(state.Files.Keys).Concat(remoteByPath.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    // One path's Core plan with everything it was made from (phase B), carried out in phase C.
    private sealed record Planned(string Key, VaultPath Path, SyncInput Input, SyncPlan Plan, FileState State, ProjectState Project, RemoteFile? Remote);

    // Phase B: every path is planned with Core, in path order, and grouped into units: one file
    // each, except that files sharing a name (in any case) in a project are one unit, in path
    // order, so which of them gets the name never depends on timing. Offline, the plans only
    // journal Core's intents and nothing is left to run. The activity panel learns here what the
    // pass will move.
    private async Task<List<List<Planned>>> PlanAllAsync(bool isOnline, CancellationToken ct)
    {
        movingTo.Clear();
        foreach (var st in state.Files.Values) if (st.LocalMoveTo is { } target) movingTo.Add(target);
        List<List<Planned>> units = [];
        var byName = new Dictionary<(Guid Project, string Name), List<Planned>>();
        foreach (var key in AllPaths())
        {
            Planned? planned;
            try { planned = await PlanPathAsync(key, isOnline, ct); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                FileProblem(key, error);
                continue;
            }
            if (planned is null) continue;
            var name = (planned.Project.Id, planned.Path.Name.ToUpperInvariant());
            if (!byName.TryGetValue(name, out var unit))
            {
                byName[name] = unit = [];
                units.Add(unit);
            }
            unit.Add(planned);
            ExpectTransfers(planned);
        }
        return units;
    }

    // Paths a file is being renamed to here (an Explorer rename being sent): not planned on their own.
    private readonly HashSet<string> movingTo = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Planned?> PlanPathAsync(string key, bool isOnline, CancellationToken ct)
    {
        if (!VaultPath.TryCreate(key, out var path, out _, options.VaultRoot)) return null;
        var project = ProjectOf(path);
        if (project is null || !project.Usable) return null;
        // A folder being renamed or removed here, a project folder gone or waiting to be put
        // back, a known folder gone from the scan: nothing under it is planned file by file.
        if (Held(key)) return null;
        local.TryGetValue(key, out var localFile);
        state.Files.TryGetValue(key, out var st);
        // Archived (decision D8): only this computer's own check outs there are finished.
        if (project.Archived && !MineToFinish(st)) return null;
        // A file this computer already tracks is planned against its own server record
        // (found by id, wherever it now lives); a server path whose file another state owns
        // is that file's pending move, not a new file.
        (RemoteFile? File, ProjectState? Project) remote = default;
        if (st?.FileId is { } id) { if (remoteById.TryGetValue(id, out var byId)) remote = (byId.File, byId.Project); }
        else if (remoteByPath.TryGetValue(key, out var byPath))
        {
            if (state.FirstWithFileId(byPath.File.Id, except: st) is not null) return null;
            remote = byPath;
        }
        if (st is null && localFile is null && remote.File is null) return null;
        if (st?.LocalMoveTo is not null || movingTo.Contains(key)) return null;
        // A new file at a removed file's path is planned against that removed file: Core's
        // re-add, which the server answers by reviving the name with its history (contract
        // C4, EnsureServerFileAsync). Names are never refused here.
        st ??= FileFor(project, key);
        if (remote.File is not null) st.FileId ??= remote.File.Id;
        if (st.Inflight is not null) return null; // finished on the next online pass
        // A file never added because another file holds its name, gone from this disk: the
        // student removed it. Nothing of it can be sent (the name is taken), its saves stay in
        // this computer's safe copies, and nothing is left waiting for it.
        if (localFile is null && st.FileId is null && st.RefusalKind == NameTakenKind)
        {
            foreach (var entry in st.Entries.Concat(st.Drafts)) state.Completed.Add(entry);
            state.Files.Remove(key);
            MarkDirty();
            return null;
        }

        // One scan's absence is not a deletion: wait for a second scan before planning one.
        if (localFile is null && st.BaseHash is not null)
        {
            if (++st.AbsentScans < 2) return null;
        }
        else st.AbsentScans = 0;

        var remoteRevision = RevisionOf(remote.File);
        var ownership = OwnershipOf(remote.File?.Lock);
        // A lock taken only for a move or a removal is not a check out: Core plans the file as
        // nobody's, so bytes saved without a check out are kept as a kept copy and the shared
        // version is put back, never shared at a check in nobody asked for.
        if (ownership == LockOwnership.ThisDevice && st.TransientLock) ownership = LockOwnership.Free;
        var localHash = localFile?.Hash;
        var open = IsOpenNow(path);
        SolidWorksRelease? saved = null;
        if (Reconciler.IsSolidWorks(path) && localHash is not null && (localHash != st.BaseHash || st.BreakNotice))
            saved = await ReadReleaseAsync(st, path, localHash, ct);
        var input = new SyncInput(path, st.Base, localHash, remoteRevision, ownership, open, isOnline, st.BreakNotice,
            saved, new SolidWorksRelease(project.PinnedRelease), st.Preserved, project.Enforce ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn,
            CheckoutMode.Explicit, RequestOf(st, open));
        var plan = Reconciler.Plan(input);
        if (!isOnline)
        {
            JournalOffline(plan, st);
            return null;
        }
        return new Planned(key, path, input, plan, st, project, remote.File);
    }

    // Offline, a plan only records Core's intents (never an upload: the capture is the upload's).
    private void JournalOffline(SyncPlan plan, FileState st)
    {
        var input = plan.Expected;
        foreach (var intent in plan.Intents.Where(i => i.Kind != IntentKind.Upload))
            journal.Append(new JournalEntry($"{state.DeviceId}:intent:{intent.Kind}:{input.Path}:{input.LocalHash}:{st.BaseId}", intent.Kind, input.Path.Value, intent.Hash, null, state.Email!));
    }

    // What the panel will show moving: an upload for bytes sent, a download for bytes received.
    private void ExpectTransfers(Planned planned)
    {
        foreach (var action in planned.Plan.Actions)
        {
            switch (action.Kind)
            {
                case SyncActionKind.Upload or SyncActionKind.AcquireLockThenUpload or SyncActionKind.SaveSideVersion:
                    activity.Expect(Directions.Upload, planned.Key, local.TryGetValue(planned.Key, out var file) ? file.Size : 0);
                    break;
                case SyncActionKind.Download:
                    activity.Expect(Directions.Download, planned.Key, planned.Remote?.Current?.Bytes ?? 0);
                    break;
            }
        }
    }

    // Phase C: the units, at most TransferConcurrency at once, as interleaved async tasks on the
    // engine thread (state is only touched between awaits, on this thread). A unit runs its
    // files' plans in order, each action in order, so every crash point fires once per file in
    // the order it always did. Going offline in any unit stops new units from starting (the rest
    // are planned offline); any failure that is not one file's (a crash, in tests) cancels every
    // unit and is thrown only after all of them stopped, so nothing of this engine writes after it.
    private async Task RunUnitsAsync(List<List<Planned>> units, CancellationToken ct)
    {
        if (units.Count == 0) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var concurrency = Math.Max(1, options.TransferConcurrency);
        List<Task> running = [];
        ExceptionDispatchInfo? fatal = null;
        var next = 0;
        while (true)
        {
            while (fatal is null && online == true && next < units.Count && running.Count < concurrency)
            {
                var task = RunUnitAsync(units[next++], stop.Token);
                if (task.IsCompleted) Ended(task); // nothing to wait for (most files, most passes)
                else running.Add(task);
            }
            if (running.Count == 0) break;
            var done = await Task.WhenAny(running);
            running.Remove(done);
            Ended(done);
        }
        fatal?.Throw();
        // Offline: what was not started yet is planned offline, its intents journaled.
        for (; next < units.Count; next++)
            foreach (var planned in units[next]) JournalOffline(Reconciler.Plan(planned.Input with { IsOnline = false }), planned.State);

        void Ended(Task task)
        {
            if (task.IsCompletedSuccessfully || fatal is not null) return;
            fatal = ExceptionDispatchInfo.Capture(task.Exception?.InnerException ?? new OperationCanceledException(ct));
            stop.Cancel();
        }
    }

    private async Task RunUnitAsync(List<Planned> unit, CancellationToken ct)
    {
        foreach (var planned in unit)
        {
            ct.ThrowIfCancellationRequested();
            if (online != true)
            {
                JournalOffline(Reconciler.Plan(planned.Input with { IsOnline = false }), planned.State);
                continue;
            }
            try { await ExecutePlannedAsync(planned, ct); }
            catch (ArmoryOfflineException) { online = false; }
            catch (Exception error) when (error is ArmoryClientException or Armory.Storage.HashMismatchException or IOException or UnauthorizedAccessException or InvalidDataException)
            { FileProblem(planned.Key, error); }
            finally { activity.Drop(planned.Key); }
            MarkDirty();
            viewWanted = true;
            PublishSoon();
        }
    }

    private async Task ExecutePlannedAsync(Planned planned, CancellationToken ct)
    {
        var st = planned.State;
        // Its record left this computer's state meanwhile (a removed file's past, forgotten when
        // the name was revived in this same unit): nothing is left to do for it.
        if (!state.Files.TryGetValue(planned.Key, out var current) || !ReferenceEquals(current, st)) return;
        st.Refusal = null; st.RefusalKind = null; st.NewerWaiting = false; st.RemovedWaiting = false;
        foreach (var action in planned.Plan.Actions)
        {
            Checkpoint("before-" + action.Kind, ct);
            if (!await ExecuteAsync(action, planned.Input, st, planned.Project, planned.Remote, ct)) break;
        }
    }

    // What the student asked for this file, as Core reads it. A file added while open is
    // checked in once it is closed (decision D2).
    private static CheckoutRequest RequestOf(FileState st, bool open)
        => st.Request != CheckoutRequest.None ? st.Request : st.AutoCheckIn && !open ? CheckoutRequest.CheckIn : CheckoutRequest.None;

    // The engine's "never overwrite an open file" check: the platform's open-file answer,
    // or SolidWorks' ~$ lock file beside the document. Used for Core's input and again
    // immediately before any write to the file.
    private bool IsOpenNow(VaultPath path)
        => fs.IsOpen(path) || markerDocuments.Contains(path.Value);

    // SolidWorks' ~$ marker means "open" while the platform corroborates it (the document or
    // the marker itself is held open), and for a while after it first appears. A marker left
    // behind by a crash, never corroborated for StaleMarkerAfter, is stale and ignored. A
    // marker never takes the lock (decision D3): it raises the quiet check-out question.
    private void ReadMarkers(VaultScan scan)
    {
        var now = deps.Clock.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in scan.Markers)
        {
            if (!LockMarkers.TryGetDocument(marker, out var document) || !VaultPath.TryCreate(document, out var doc, out _, options.VaultRoot)) continue;
            seen.Add(document);
            if (!markerFirstSeen.ContainsKey(document)) markerFirstSeen[document] = now;
            var live = fs.IsOpen(doc) || (VaultPath.TryCreate(marker, out var markerPath, out _, options.VaultRoot) && fs.IsOpen(markerPath));
            if (live) { markerSince.Remove(document); markerDocuments.Add(document); continue; }
            if (!markerSince.TryGetValue(document, out var since)) markerSince[document] = since = now;
            if (now - since < options.StaleMarkerAfter) markerDocuments.Add(document);
            else Notice(NoticeKinds.CantRead, null, document,
                $"Armory is treating {doc.Name} as closed. If SolidWorks still has it open, save it there.", StaleMarkerTitle);
        }
        foreach (var gone in markerSince.Keys.Where(k => !seen.Contains(k)).ToArray()) markerSince.Remove(gone);
        foreach (var gone in markerFirstSeen.Keys.Where(k => !seen.Contains(k)).ToArray()) markerFirstSeen.Remove(gone);
        // A question dismissed for one open is forgotten once that open is over.
        dismissedPrompts.RemoveWhere(key => !markerFirstSeen.Any(m => key == PromptKey(m.Key, m.Value)));
    }

    internal const string StaleMarkerTitle = "SolidWorks may have closed unexpectedly";
    private readonly Dictionary<string, DateTimeOffset> markerSince = new(StringComparer.OrdinalIgnoreCase);
    // When this computer first saw each open document's ~$ marker: one check-out question per open.
    private readonly Dictionary<string, DateTimeOffset> markerFirstSeen = new(StringComparer.OrdinalIgnoreCase);
    private bool recovered;
    // A pass has read the disk since this start: the window's actions act on what it found.
    private bool scanned;

    private LockOwnership OwnershipOf(RemoteLock? held)
    {
        if (held is null || !held.IsLive) return LockOwnership.Free;
        if (!string.Equals(held.HolderEmail, state.Email, StringComparison.OrdinalIgnoreCase)) return LockOwnership.OtherPerson;
        return state.IsMine(held.HolderDeviceId) ? LockOwnership.ThisDevice : LockOwnership.MyOtherDevice;
    }

    private static Revision? RevisionOf(RemoteFile? file)
        => file is null ? null : file.Deleted ? new($"tombstone:{file.Id}", null, "")
            : file.Current is { } current ? new(current.Id.ToString(), current.Hash, current.Author) : null;

    private async Task<SolidWorksRelease?> ReadReleaseAsync(FileState st, VaultPath path, string hash, CancellationToken ct)
    {
        if (deps.ReleaseReader is null) return null;
        if (releases.TryGetValue(hash, out var known)) return known;
        var snapshot = SnapshotFor(st, hash);
        SolidWorksRelease? release = null;
        try
        {
            await using var stream = snapshot is not null ? deps.Snapshots.OpenRead(snapshot.Id) : fs.OpenRead(path);
            release = await deps.ReleaseReader.ReadAsync(stream, ct);
        }
        catch (IOException) { }
        releases[hash] = release;
        return release;
    }

    // ---- Helpers -------------------------------------------------------------------

    private ProjectState? ProjectOf(VaultPath path)
    {
        var slash = path.Value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return null;
        var name = path.Value[..slash];
        return state.Projects.Values.FirstOrDefault(p => p.Usable && string.Equals(p.Folder, name, StringComparison.OrdinalIgnoreCase));
    }

    private FileState FileFor(ProjectState project, string key)
    {
        if (!state.Files.TryGetValue(key, out var st))
        {
            state.Files[key] = st = new FileState { Path = key, ProjectId = project.Id };
            MarkDirty();
        }
        return st;
    }

    private static (string Folder, string Name) Split(VaultPath path)
    {
        var parts = path.Value.Split('/');
        return (string.Join('/', parts[1..^1]), parts[^1]);
    }

    // The file's saves of these bytes are on the server now: their journal entries are done.
    private void Complete(FileState st, string hash)
    {
        for (var i = st.Entries.Count - 1; i >= 0; i--)
        {
            var id = st.Entries[i];
            if (!journal.TryGet(id, out var entry) || entry.Hash != hash) continue;
            state.Completed.Add(id);
            st.Entries.RemoveAt(i);
            MarkDirty();
        }
    }

    private void Remember(string kind, Guid? fileId, string path, string title, string detail, string? itemDetail = null, string? reasonKind = null, string? who = null)
    {
        state.Remembered.RemoveAll(n => n.Path == path && n.Kind == kind);
        state.Remembered.Add(new RememberedNotice(kind, fileId, path, title, detail, deps.Clock.GetUtcNow(), itemDetail, reasonKind, who));
    }

    // A pass's own notice about one file or folder; the view groups them by kind into cards.
    private void Notice(string kind, Guid? fileId, string path, string detail, string? title = null, string? itemDetail = null, string? reasonKind = null, string? who = null)
        => notes.Add(new Note(kind, fileId, path, detail, title, itemDetail, reasonKind, who));

    // A problem with one file (path) or with this computer (no path): the window gets a plain
    // sentence as one notice item, and only the log gets the raw text.
    private void Problem(string kind, string? path, string plain, string raw, string? title = null)
    {
        problems.Add(string.IsNullOrEmpty(path) ? raw : $"{path}: {raw}");
        notes.Add(new Note(kind, null, path ?? "", plain, title));
    }

    // A failure while planning or carrying out one file's plan, in the window's words: the
    // server's refusals are things Armory can't send, the disk's are things it can't read.
    private void FileProblem(string path, Exception error)
    {
        var name = NameOf(path);
        switch (error)
        {
            case StorageTransferException:
                // File storage refused it or took too long this time: only this file waits.
                Problem(NoticeKinds.CantSend, path, "File storage didn't answer in time or turned it away. Armory tries again by itself.", error.Message, $"{name} didn't go through this time");
                break;
            case Armory.Storage.HashMismatchException:
                Problem(NoticeKinds.CantSend, path, "What arrived didn't match the team's version. Armory tries again by itself.", error.Message, $"Armory couldn't download {name}");
                break;
            case ArmoryClientException:
                Problem(NoticeKinds.CantSend, path, "The server didn't accept it this time. Armory tries again by itself.", error.Message, $"Armory couldn't finish a change to {name}");
                break;
            case InvalidDataException:
                Problem(NoticeKinds.CantRead, path, "Armory couldn't read its safe copy of a save of it. It tries again by itself.", error.Message);
                break;
            default:
                Problem(NoticeKinds.CantRead, path, "Armory couldn't read or change it. Close any program that might be using it. Armory tries again by itself.", error.Message);
                break;
        }
    }

    // A problem the scan met, "path: what went wrong" or a sentence about the folder, in plain words.
    private void ScanProblem(string raw)
    {
        var colon = raw.IndexOf(": ", StringComparison.Ordinal);
        var path = colon > 0 && VaultPath.TryCreate(raw[..colon], out var where, out _) ? where.Value : null;
        var what = path is null ? raw : raw[(colon + 2)..];
        string plain;
        if (what.Contains("exceeds", StringComparison.OrdinalIgnoreCase) || what.Contains("too long", StringComparison.OrdinalIgnoreCase))
            plain = "Its path is too long for Windows. Give it, or a folder it is in, a shorter name.";
        else if (what.Contains("reserved", StringComparison.OrdinalIgnoreCase) || what.Contains("control character", StringComparison.OrdinalIgnoreCase) ||
                 what.Contains("end with a dot", StringComparison.OrdinalIgnoreCase) || what.Contains("surrogate", StringComparison.OrdinalIgnoreCase))
            plain = "Its name can't be used on Windows. Rename it so Armory can keep it.";
        else if (raw.StartsWith("Reparse point", StringComparison.OrdinalIgnoreCase))
            plain = "A shortcut to another place is in your Armory folder. Armory leaves it alone.";
        else if (what.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            plain = "Armory couldn't make it read-only or writable yet. Close any program that might be using it. Armory tries again by itself.";
        else plain = path is null ? "Armory can't read part of your Armory folder. It tries again by itself."
            : "Armory can't read it. Close any program that might be using it. Armory tries again by itself.";
        Problem(NoticeKinds.CantRead, path, plain, raw);
    }

    // ---- Publishing --------------------------------------------------------------------

    private static readonly TimeSpan ViewEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ActivityEvery = TimeSpan.FromMilliseconds(250);
    private bool inPass, viewWanted, viewTimerSet;
    private long lastViewBuilt;

    // Something the window shows changed. With nothing running the view is built now; during a
    // pass at most every 500 ms (and at its end); during an action, when the action ends.
    private void RequestPublish()
    {
        viewWanted = true;
        if (passGate.CurrentCount > 0) PublishLocked();
        else if (inPass) PublishSoon();
    }

    // During a pass: now if the last view is 500 ms old, else once that much time has passed. Never
    // while the server is being read (the next look after the read builds it).
    private void PublishSoon()
    {
        if (!viewWanted || refreshing || !inPass) return;
        var since = deps.Clock.GetElapsedTime(lastViewBuilt);
        if (since >= ViewEvery)
        {
            PublishLocked();
            return;
        }
        if (viewTimerSet) return;
        viewTimerSet = true;
        _ = LaterAsync(ViewEvery - since);

        async Task LaterAsync(TimeSpan wait)
        {
            await Task.Delay(wait);
            viewTimerSet = false;
            PublishSoon();
        }
    }

    private void PublishLocked()
    {
        viewWanted = false;
        lastViewBuilt = deps.Clock.GetTimestamp();
        ApplyDismissals();
        var next = BuildView();
        Volatile.Write(ref view, next);
        ViewChanged?.Invoke(next);
    }

    // What is moving right now goes out on its own, at most four times a second, from a timer
    // that runs while a pass does (and once more after it, so the window sees it stop).
    private readonly object activityGate = new();
    private Timer? activityTimer;
    private bool activityStopping;
    private long activityRaisedAt, activityVersionRaised = -1;

    private void StartActivity()
    {
        lock (activityGate)
        {
            activityStopping = false;
            activityTimer ??= new Timer(_ => RaiseActivity(), null, ActivityEvery, ActivityEvery);
        }
    }

    private void StopActivity()
    {
        lock (activityGate) activityStopping = true;
    }

    private void RaiseActivity()
    {
        ActivityView snapshot;
        lock (activityGate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (activityRaisedAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(activityRaisedAt, now) < ActivityEvery) return;
            var version = activity.Version;
            var busy = activity.Busy;
            if (version == activityVersionRaised && !busy)
            {
                if (activityStopping)
                {
                    activityTimer?.Dispose();
                    activityTimer = null;
                }
                return;
            }
            snapshot = activity.Snapshot();
            activityVersionRaised = version;
            activityRaisedAt = now;
            try { ActivityChanged?.Invoke(snapshot); }
            catch (Exception error) when (error is not OutOfMemoryException) { deps.Log?.Invoke("activity: " + error.Message); }
        }
    }

    private volatile string? lastLoopError;
    private readonly ConcurrentQueue<string> pendingDismissals = new();

    internal static string? ParseExistingFolder(string? details, out Guid? fileId, out string? name)
    {
        fileId = null; name = null;
        if (details is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(details);
            var root = doc.RootElement;
            if (root.TryGetProperty("file_id", out var id) && Guid.TryParse(id.GetString(), out var parsed)) fileId = parsed;
            if (root.TryGetProperty("existing_name", out var n)) name = n.GetString();
            return root.TryGetProperty("existing_folder", out var folder) ? folder.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

// A pass's notice about one file or folder, before the view groups it into a card by kind.
// ItemDetail is the item's own sentence in a card of several; ReasonKind and Who let such a card
// name everyone in its title.
internal sealed record Note(string Kind, Guid? FileId, string Path, string Detail, string? Title = null, string? ItemDetail = null,
    string? ReasonKind = null, string? Who = null);
