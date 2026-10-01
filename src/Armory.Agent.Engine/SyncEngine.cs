using System.Text.Json;
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
}

public sealed record SyncReport(bool SignedIn, bool Online, int Uploaded, int Downloaded, int SideVersions, int Refused, IReadOnlyList<string> Problems);

// The sync loop (docs/agent/ENGINE.md). Core decides; the engine gathers inputs, executes
// Core's plans in order against the server and the disk, and records durable state.
public sealed partial class SyncEngine : IAsyncDisposable
{
    private readonly EngineOptions options;
    private readonly EngineDependencies deps;
    private readonly IVaultFileSystem fs;
    private readonly OfflineJournal journal;
    private readonly SaveRecorder recorder;
    private readonly SemaphoreSlim passGate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<string, SolidWorksRelease?> releases = new(StringComparer.Ordinal);
    private EngineState state;
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
    private AgentView view;

    // Pass-scoped data.
    private readonly Dictionary<string, LocalFile> local = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> markerDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, IReadOnlyList<RemoteFile>> remoteProjects = [];
    private readonly Dictionary<string, (RemoteFile File, ProjectState Project)> remoteByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (RemoteFile File, ProjectState Project, VaultPath Path)> remoteById = [];
    private readonly List<string> problems = [];
    private readonly List<AttentionView> notices = [];
    private int uploaded, downloaded, sideVersions, refused;
    private bool wrote;

    // Test seams. CrashPoint throws to simulate a process crash between named steps.
    internal Action<string>? CrashPoint { get; set; }

    public SyncEngine(EngineOptions options, EngineDependencies dependencies)
    {
        this.options = options;
        deps = dependencies;
        fs = dependencies.Files;
        journal = new OfflineJournal(dependencies.Journal);
        recorder = new SaveRecorder(dependencies.Snapshots, journal);
        state = EngineState.Load(dependencies.State);
        settings = new SettingsView(options.VaultRoot, true, "system");
        view = BuildView();
    }

    // Pass-scoped helper so a long pass still shows progress after a slow step.
    private void PublishIfPending() { if (publishPending) PublishLocked(); }

    public AgentView View => Volatile.Read(ref view);
    public event Action<AgentView>? ViewChanged;
    public bool IsPaused => paused;
    public void Pause() { paused = true; Publish(); }
    public void Resume() { paused = false; Publish(); Wake(); }
    public void Wake() => wake.Release();

    public void Start()
    {
        loop ??= Task.Run(() => LoopAsync(stopping.Token));
    }

    public async Task StopAsync()
    {
        await stopping.CancelAsync();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        stopping.Dispose();
    }

    public void SetConnectState(string phase, string? message)
    {
        connectPhase = phase;
        connectMessage = message;
        Publish();
        if (phase == "idle" && deps.Sessions.IsSignedIn) Wake();
    }

    public void ApplySettings(SettingsView newSettings, string newEffectiveTheme)
    {
        settings = newSettings;
        effectiveTheme = newEffectiveTheme;
        Publish();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!paused)
            {
                try { await SyncOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception error) { lastLoopError = "Sync stopped for a moment: " + error.GetType().Name; Publish(); }
            }
            var idle = online != true || deps.Clock.GetUtcNow() - lastActivity > options.IdleAfter;
            var delay = idle ? options.IdlePollInterval : options.ActivePollInterval;
            try { await wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { return; }
            while (wake.CurrentCount > 0) await wake.WaitAsync(0, ct);
        }
    }

    public async Task<SyncReport> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        await passGate.WaitAsync(cancellationToken);
        try
        {
            syncing = true;
            PublishLocked();
            return await PassAsync(cancellationToken);
        }
        finally
        {
            syncing = false;
            try { PublishLocked(); }
            finally { passGate.Release(); }
        }
    }

    private async Task<SyncReport> PassAsync(CancellationToken ct)
    {
        problems.Clear(); notices.Clear();
        uploaded = downloaded = sideVersions = refused = 0;
        wrote = false;
        var session = deps.Sessions.Current;
        if (session is null) return Report(false);
        if (state.Email is not null && !string.Equals(state.Email, session.Email, StringComparison.OrdinalIgnoreCase)) return Report(true);
        if (state.Email is null || state.DeviceId != session.DeviceId)
        {
            // A reconnect of the same person registers a new device; its work stays theirs.
            state.Email = session.Email;
            state.DeviceId = session.DeviceId;
            Save();
        }

        recorder.Recover();
        var entries = AttachEntries();

        local.Clear(); markerDocuments.Clear();
        VaultScan scan;
        try { scan = fs.Scan(); }
        catch (IOException error) { problems.Add(error.Message); return Report(true); }
        foreach (var file in scan.Files) local[file.Path.Value] = file;
        foreach (var marker in scan.Markers)
            if (LockMarkers.TryGetDocument(marker, out var document)) markerDocuments.Add(document);
        problems.AddRange(scan.Problems);

        online = await RefreshAsync(ct);
        if (online == true)
        {
            lastOnline = deps.Clock.GetUtcNow();
            await ResumeInflightAsync(ct);
            if (await ApplyRemoteMovesAsync(ct)) await RefreshAsync(ct);
            AdoptIdenticalBases();
        }

        Capture(session);
        CrashPoint?.Invoke("after-capture");
        entries = AttachEntries();
        if (online == true)
        {
            await ExecutePendingMovesAsync(ct);
            await ArchiveSupersededAsync(entries, ct);
        }

        lastLoopError = null;
        foreach (var path in AllPaths()) { await PlanAndExecuteAsync(path, online == true, ct); PublishIfPending(); }

        if (online == true)
        {
            // Locks are decided on the server's state after this pass's own writes.
            if (wrote) await RefreshAsync(ct);
            await AcquireForMarkersAsync(ct);
            await ReleaseFinishedLocksAsync(ct);
            ApplyReadOnly();
        }
        return Report(true);
    }

    private SyncReport Report(bool signedIn) => new(signedIn, online == true, uploaded, downloaded, sideVersions, refused, problems.ToArray());

    // ---- Refresh -------------------------------------------------------------------

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<RemoteProject> projects;
        try { projects = await deps.Api.MyProjectsAsync(ct); }
        catch (ArmoryOfflineException) { return false; }
        catch (ArmorySignedOutException) { return false; }
        remoteProjects.Clear(); remoteByPath.Clear(); remoteById.Clear();
        var names = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in state.Projects.Keys.Except(projects.Select(p => p.Id)).ToArray()) state.Projects[gone].Usable = false;
        foreach (var project in projects)
        {
            if (!state.Projects.TryGetValue(project.Id, out var ps)) state.Projects[project.Id] = ps = new ProjectState { Id = project.Id };
            ps.Name = project.Name;
            ps.PinnedRelease = project.PinnedRelease;
            ps.Enforce = project.ReleaseGate == ProjectReleaseGate.Enforce;
            ps.Role = project.Role.ToString();
            ps.Usable = VaultPath.TryValidateName(project.Name, out _) && names[project.Name] == 1;
            if (!ps.Usable) { Notice(AttentionKinds.Refused, null, project.Name, "This project's name can't be a folder", "A lead must rename the project on ideabosco.com."); continue; }
            try { fs.EnsureFolder(project.Name); } catch (IOException error) { problems.Add(error.Message); }
        }
        try
        {
            foreach (var ps in state.Projects.Values.Where(p => p.Usable))
            {
                var changes = await deps.Api.ListChangesAsync(ps.Id, ps.Cursor, ct);
                foreach (var change in changes)
                {
                    if (change.Kind == "lock_broken" && change.Payload["former_device_id"]?.GetValue<string>() is { } former &&
                        Guid.TryParse(former, out var device) && device == state.DeviceId)
                        foreach (var st in state.Files.Values.Where(f => f.FileId == change.EntityId)) st.BreakNotice = true;
                    ps.Cursor = Math.Max(ps.Cursor, change.Cursor);
                }
                if (changes.Count > 0) lastActivity = deps.Clock.GetUtcNow();
                var files = await deps.Api.ProjectFilesAsync(ps.Id, ct);
                remoteProjects[ps.Id] = files;
                foreach (var file in files)
                {
                    var relative = ps.Name + "/" + (file.Folder.Length == 0 ? "" : file.Folder + "/") + file.Name;
                    if (!VaultPath.TryCreate(relative, out var path, out var problem, options.VaultRoot))
                    {
                        Notice(AttentionKinds.Refused, file.Id, relative, "A lead must fix this file's name", problem ?? "The name can't be used on Windows.");
                        continue;
                    }
                    remoteByPath[path.Value] = (file, ps);
                    remoteById[file.Id] = (file, ps, path);
                }
            }
            Save();
            return true;
        }
        catch (ArmoryOfflineException) { Save(); return false; }
    }

    // A local file byte-identical to the server's current version is that version: this
    // is how a computer that already has copies starts, without inventing a conflict.
    private void AdoptIdenticalBases()
    {
        foreach (var (key, remote) in remoteByPath)
        {
            if (!local.TryGetValue(key, out var file) || remote.File.Current is not { } current || current.Hash != file.Hash) continue;
            var st = FileFor(remote.Project, key);
            st.FileId ??= remote.File.Id;
            if (st.Base is null && st.Inflight is null) { st.SetBase(new(current.Id.ToString(), current.Hash, current.Author)); st.LastCaptured ??= current.Hash; }
        }
        Save();
    }

    // ---- Capture -------------------------------------------------------------------

    private void Capture(ArmorySession session)
    {
        foreach (var file in local.Values.OrderBy(f => f.Path))
        {
            var project = ProjectOf(file.Path);
            if (project is null)
            {
                Notice(AttentionKinds.Refused, null, file.Path.Value, "This file is outside every project", "Move it into one of your project folders so Armory can keep it.");
                continue;
            }
            var st = FileFor(project, file.Path.Value);
            if (file.Hash == st.BaseHash || file.Hash == st.LastCaptured) continue;
            var id = state.NextId("save");
            Save(); // the id is spent before it is used, so it is never reused
            try
            {
                SavedSnapshot snapshot;
                using (var source = fs.OpenRead(file.Path)) snapshot = recorder.Record(id, file.Path, session.Email, source);
                st.Entries.Add(id);
                st.LastCaptured = snapshot.Hash;
                lastActivity = deps.Clock.GetUtcNow();
                Save();
            }
            catch (IOException error) { problems.Add($"{file.Path}: {error.Message}"); }
            catch (UnauthorizedAccessException error) { problems.Add($"{file.Path}: {error.Message}"); }
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
        if (changed) Save();
        return all;
    }

    // ---- Planning ------------------------------------------------------------------

    private IEnumerable<string> AllPaths()
        => local.Keys.Concat(state.Files.Keys).Concat(remoteByPath.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task PlanAndExecuteAsync(string key, bool isOnline, CancellationToken ct)
    {
        if (!VaultPath.TryCreate(key, out var path, out _, options.VaultRoot)) return;
        var project = ProjectOf(path);
        if (project is null || !project.Usable) return;
        remoteByPath.TryGetValue(key, out var remote);
        local.TryGetValue(key, out var localFile);
        state.Files.TryGetValue(key, out var st);
        if (st is null && localFile is null && remote.File is null) return;
        st ??= FileFor(project, key);
        if (remote.File is not null) st.FileId ??= remote.File.Id;
        if (st.Inflight is not null) return; // finished on the next online pass

        var remoteRevision = RevisionOf(remote.File);
        var ownership = OwnershipOf(remote.File?.Lock);
        var localHash = localFile?.Hash;
        SolidWorksRelease? saved = null;
        if (Reconciler.IsSolidWorks(path) && localHash is not null && (localHash != st.BaseHash || st.BreakNotice))
            saved = await ReadReleaseAsync(st, path, localHash, ct);
        var input = new SyncInput(path, st.Base, localHash, remoteRevision, ownership, IsOpenNow(path), isOnline, st.BreakNotice,
            saved, new SolidWorksRelease(project.PinnedRelease), st.Preserved, project.Enforce ? ReleaseGateMode.Enforce : ReleaseGateMode.Warn);
        var plan = Reconciler.Plan(input);
        if (!isOnline)
        {
            foreach (var intent in plan.Intents.Where(i => i.Kind != IntentKind.Upload))
                journal.Append(new JournalEntry($"{state.DeviceId}:intent:{intent.Kind}:{path}:{localHash}:{st.BaseId}", intent.Kind, path.Value, intent.Hash, null, state.Email!));
            return;
        }
        st.Refusal = null; st.RefusalKind = null; st.NewerWaiting = false;
        foreach (var action in plan.Actions)
        {
            CrashPoint?.Invoke("before-" + action.Kind);
            if (!await ExecuteAsync(action, input, st, project, remote.File, ct)) break;
        }
        Save();
    }

    // The engine's "never overwrite an open file" check: the platform's open-file answer,
    // or SolidWorks' ~$ lock file beside the document. Used for Core's input and again
    // immediately before any write to the file.
    private bool IsOpenNow(VaultPath path)
        => fs.IsOpen(path) || markerDocuments.Contains(path.Value);

    private LockOwnership OwnershipOf(RemoteLock? held)
    {
        if (held is null || !held.IsLive) return LockOwnership.Free;
        if (!string.Equals(held.HolderEmail, state.Email, StringComparison.OrdinalIgnoreCase)) return LockOwnership.OtherPerson;
        return held.HolderDeviceId == state.DeviceId ? LockOwnership.ThisDevice : LockOwnership.MyOtherDevice;
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
        return state.Projects.Values.FirstOrDefault(p => p.Usable && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private FileState FileFor(ProjectState project, string key)
    {
        if (!state.Files.TryGetValue(key, out var st))
            state.Files[key] = st = new FileState { Path = key, ProjectId = project.Id };
        return st;
    }

    private static (string Folder, string Name) Split(VaultPath path)
    {
        var parts = path.Value.Split('/');
        return (string.Join('/', parts[1..^1]), parts[^1]);
    }

    private SavedSnapshot? SnapshotFor(FileState st, string hash)
    {
        var all = deps.Snapshots.Enumerate();
        var ids = st.Entries.ToHashSet(StringComparer.Ordinal);
        return all.LastOrDefault(s => s.Hash == hash && ids.Contains(s.Id))
            ?? all.LastOrDefault(s => s.Hash == hash && string.Equals(s.Path, st.Path, StringComparison.OrdinalIgnoreCase));
    }

    private void Complete(FileState st, string hash)
    {
        var entries = journal.Read().Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var id in st.Entries.ToArray())
            if (entries.TryGetValue(id, out var entry) && entry.Hash == hash) { state.Completed.Add(id); st.Entries.Remove(id); }
    }

    private void Notice(string kind, Guid? fileId, string path, string title, string detail)
        => notices.Add(new AttentionView(kind, fileId?.ToString(), path, path[(path.LastIndexOf('/') + 1)..], title, detail, deps.Clock.GetUtcNow().ToString("O")));

    private void Save() => deps.State.Save(state.Serialize());

    // The view is built only by whoever holds the pass gate: a pass publishes as it goes,
    // and a call from another thread (pause, settings, connect state) during a pass leaves
    // the publishing to that pass.
    private void Publish()
    {
        if (!passGate.Wait(0)) { publishPending = true; return; }
        try { PublishLocked(); }
        finally { passGate.Release(); }
    }

    private void PublishLocked()
    {
        publishPending = false;
        var next = BuildView();
        Volatile.Write(ref view, next);
        ViewChanged?.Invoke(next);
    }

    private volatile bool publishPending;
    private volatile string? lastLoopError;

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
