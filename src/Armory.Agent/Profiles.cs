using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Armory.Agent.Engine;
using Armory.Client;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Agent;

// A computer shared by several students (docs/agent/PROFILES.md, 0.3.3 part F): each student
// keeps a profile of their own on it, with their own sign-in, so switching never needs the
// browser again. Kept under %LOCALAPPDATA%\IDEA Armory\profiles only while the computer is
// shared (settings.json "sharedComputer": true); one student per computer never makes or reads
// anything here.
//
//   profiles\profiles.json            the index below: who, which folder, what waited; no secret
//   profiles\<id>\secrets\            the profile's own protected secrets (DPAPI on Windows):
//       armory-session.secret         their sign-in, the same format and name as secrets\'s
//       armory-pin.secret             their PIN's hash, wrong tries and wait (PinRecord)
//
// <id> is 32 random hex digits, never made from the address.

// What a student left waiting in a folder when Armory last stopped for them (the picker's
// "Alex has 2 files checked out here"); the engine's own answer at the hand-over is the authority.
internal sealed record WaitingRecord(string Folder, int CheckedOut, int Unsent, int Changed, int Added, int FolderChanges, string Words, DateTimeOffset At)
{
    internal int Total => CheckedOut + Unsent + Changed + Added + FolderChanges;
    internal static WaitingRecord Of(string folder, FolderWaiting waiting, DateTimeOffset at)
        => new(folder, waiting.CheckedOut, waiting.Unsent, waiting.Changed, waiting.Added, waiting.FolderChanges, waiting.Words, at);
}

// One student. Folder: the computer's shared folder, or a folder of their own beside it.
internal sealed record ProfileRecord(string Id, string Email, string Name, DateTimeOffset AddedAt, DateTimeOffset? LastUsedAt, string Folder, WaitingRecord? Waiting);

// The index. Current: the student Armory works for (null: nobody yet). PinsRequired: "Ask for a
// PIN when switching students", with who changed it and when. Migrating: "on" or "off" while
// shared mode is being turned on or off (finished at the next start). LastPicked: the local day
// someone last picked a student, for the first open of the day.
internal sealed record ProfileIndex(int Version, string? Current, bool PinsRequired, string? PinsChangedBy, DateTimeOffset? PinsChangedAt,
    string? Migrating, DateOnly? LastPicked, IReadOnlyList<ProfileRecord> Profiles)
{
    internal const int CurrentVersion = 1;
    internal static ProfileIndex Empty { get; } = new(CurrentVersion, null, true, null, null, null, null, []);
    internal ProfileRecord? Find(string? id) => id is null ? null : Profiles.FirstOrDefault(p => p.Id == id);
    internal ProfileRecord? FindEmail(string email) => Profiles.FirstOrDefault(p => SharedComputer.SameEmail(p.Email, email));
    internal ProfileIndex With(ProfileRecord record)
        => this with { Profiles = Profiles.Any(p => p.Id == record.Id) ? Profiles.Select(p => p.Id == record.Id ? record : p).ToList() : [.. Profiles, record] };
    internal ProfileIndex Without(string id) => this with { Profiles = Profiles.Where(p => p.Id != id).ToList(), Current = Current == id ? null : Current };
}

internal sealed class ProfileStore
{
    internal const string SessionSecret = "armory-session", PinSecret = "armory-pin";
    // DpapiSecretStore keeps a secret named n as n + this, in its folder (and the test store too),
    // which is what lets shared mode move a sign-in from secrets\ into a profile, never copy it.
    internal const string SecretExtension = ".secret";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string, ISecretStore> secretsIn;
    private readonly Action<string>? problem;

    internal ProfileStore(string folder, Func<string, ISecretStore> secretsIn, Action<string>? problem = null)
    {
        Folder = Path.GetFullPath(folder);
        this.secretsIn = secretsIn;
        this.problem = problem;
    }

    internal string Folder { get; }
    internal string IndexFile => Path.Combine(Folder, "profiles.json");
    // A newer Armory set this computer's students up: this one runs nobody and never rewrites it.
    internal bool TooNew { get; private set; }

    // A missing index is no students yet; an unreadable one too (said in the log); a newer one is
    // TooNew. Never throws for what the file holds.
    internal ProfileIndex Load()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(IndexFile); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return ProfileIndex.Empty; }
        try
        {
            using (var document = JsonDocument.Parse(bytes))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var v) &&
                    v.TryGetInt32(out var version) && version > ProfileIndex.CurrentVersion)
                {
                    TooNew = true;
                    problem?.Invoke($"profiles.json is version {version}, newer than this Armory knows; nobody is run and it is left as it is");
                    return ProfileIndex.Empty;
                }
            }
            var index = JsonSerializer.Deserialize<ProfileIndex>(bytes, Options) ?? ProfileIndex.Empty;
            // Only well-formed records: an id of the right shape and an address.
            var kept = (index.Profiles ?? []).Where(p => p is not null && IsId(p.Id) && !string.IsNullOrWhiteSpace(p.Email) && !string.IsNullOrWhiteSpace(p.Folder)).ToList();
            return index with { Profiles = kept, Current = kept.Any(p => p.Id == index.Current) ? index.Current : null };
        }
        catch (JsonException error)
        {
            problem?.Invoke("profiles.json could not be read and was treated as no students: " + error.Message);
            return ProfileIndex.Empty;
        }
    }

    // Write-through temp file, flushed, then renamed over the old one (as settings.json).
    internal void Save(ProfileIndex index)
    {
        if (TooNew) throw new InvalidOperationException("This computer's students were set up by a newer Armory.");
        Directory.CreateDirectory(Folder);
        var temp = IndexFile + ".pending";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, index, Options);
            output.Flush(true);
        }
        File.Move(temp, IndexFile, overwrite: true);
    }

    internal string FolderOf(string id) => Path.Combine(Folder, CheckedId(id));
    internal string SecretsFolderOf(string id) => Path.Combine(FolderOf(id), "secrets");
    internal ISecretStore SecretsOf(string id) => secretsIn(SecretsFolderOf(id));
    internal static string SecretFile(string folder, string name) => Path.Combine(folder, name + SecretExtension);

    internal PinRecord? ReadPin(string id)
    {
        var bytes = SecretsOf(id).Read(PinSecret);
        if (bytes is null) return null;
        try
        {
            var pin = JsonSerializer.Deserialize<PinRecord>(bytes, Options);
            return pin is { Salt.Length: > 0, Hash.Length: > 0, Iterations: > 0 } ? pin : null;
        }
        catch (JsonException) { return null; }
    }

    internal void WritePin(string id, PinRecord pin) => SecretsOf(id).Write(PinSecret, JsonSerializer.SerializeToUtf8Bytes(pin, Options));

    // Forgets one profile: its folder under profiles\ (sign-in, PIN) and nothing else, never a
    // file in any Armory folder.
    internal void Forget(string id)
    {
        var folder = FolderOf(id);
        for (var attempt = 0; attempt < 3 && Directory.Exists(folder); attempt++)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) problem?.Invoke($"profile {id} could not be removed: {error.Message}");
                else Thread.Sleep(100);
            }
        }
    }

    // Every profile folder and the index (shared mode turned off).
    internal void ForgetAll()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem?.Invoke("profiles could not be removed: " + error.Message); }
    }

    internal static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    internal static bool IsId(string? text) => text is { Length: 32 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string CheckedId(string id) => IsId(id) ? id : throw new ArgumentException("Not a profile id.", nameof(id));
}

// The host's three HTTP clients, shared by every profile (cheap to share: they hold no sign-in).
internal sealed record HostHttp(HttpClient Rest, HttpClient Site, HttpClient Storage);

// One student's network side, over the host's shared HttpClients (decision F2): their own
// SessionManager over their own secrets, so their refresh token has exactly one owner, and every
// object that reads a session reads only theirs: a note, a heartbeat or a file being sent as one
// student can never go out as another. Built the way the host built its own before profiles; with
// one student per computer it is exactly that (over secrets\).
internal sealed class ProfileClients
{
    private CancellationTokenSource? beat;

    private ProfileClients(string? profileId, SessionManager sessions, ArmoryApi api, BlobClient blobs, ConnectFlow connector, TeamHeartbeat heartbeat, FeedbackSender feedback)
    {
        ProfileId = profileId;
        Sessions = sessions;
        Api = api;
        Blobs = blobs;
        Connector = connector;
        Heartbeat = heartbeat;
        Feedback = feedback;
    }

    internal static ProfileClients Create(string? profileId, ISecretStore secrets, HostHttp http, Uri site, IBrowserLauncher browser, AgentTelemetry telemetry, AgentLog log)
    {
        var sessions = new SessionManager(http.Rest, secrets);
        var api = new ArmoryApi(new PostgrestClient(http.Rest, sessions, telemetry.Recorder));
        var blobs = new BlobClient(http.Site, http.Storage, site, sessions, telemetry.Recorder);
        var connector = new ConnectFlow(http.Site, site, browser, sessions);
        var heartbeat = new TeamHeartbeat(api, sessions, AgentPaths.Version, log: log.Info);
        // Send feedback, the same as the website's (v0.3.2): the window's one entry point, and the
        // path the saved notes go through too, sharing the uploader's waits.
        var feedback = new FeedbackSender(api, new FeedbackScreenshots(http.Rest, sessions, telemetry.Recorder), sessions, AgentPaths.Version,
            telemetry.Limiter, log: log.Info);
        return new ProfileClients(profileId, sessions, api, blobs, connector, heartbeat, feedback);
    }

    // Null: the one student of a computer that is not shared (or nobody in use yet).
    internal string? ProfileId { get; }
    internal SessionManager Sessions { get; }
    internal ArmoryApi Api { get; }
    internal BlobClient Blobs { get; }
    internal ConnectFlow Connector { get; }
    internal TeamHeartbeat Heartbeat { get; }
    internal FeedbackSender Feedback { get; }

    // Team status (v0.3): only the student in use beats.
    internal Task StartBeating(CancellationToken appRunning)
    {
        beat?.Dispose();
        beat = CancellationTokenSource.CreateLinkedTokenSource(appRunning);
        var token = beat.Token;
        return Task.Run(() => Heartbeat.RunAsync(token));
    }

    internal void StopBeating()
    {
        try { beat?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

// What the host is made of that a test replaces (Armory.Agent.Tests runs the real host on Linux
// over portable folders, a fake site and plain secrets). Windows builds the app's own.
internal sealed class HostParts
{
    // The secrets in a folder (DPAPI on Windows).
    internal required Func<string, ISecretStore> SecretsIn { get; init; }
    internal required Func<TimeSpan, HttpClient> Http { get; init; }
    internal required IBrowserLauncher Browser { get; init; }
    // One runtime (disk adapters and an engine, not started) for a folder and a student's clients.
    internal required Func<string, ProfileClients, VaultRuntime> Runtime { get; init; }
    // A folder's state document, read without an engine (whose the folder is).
    internal required Func<string, IEngineStateStore> StateOf { get; init; }
    // Whether SolidWorks has a document open from a folder (a "~$" marker in it).
    internal required Func<string, bool> OpenInSolidWorks { get; init; }
    internal Func<bool> LightApps { get; init; } = WindowsTheme.AppsUseLightTheme;
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal string MachineName { get; init; } = Environment.MachineName;
    internal int PinIterations { get; init; } = PinHash.Iterations;
    // How long a switch waits for the last student's engine to stop before it parks it.
    internal TimeSpan StopPatience { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan StillFinishingAfter { get; init; } = TimeSpan.FromSeconds(15);

    // solidWorksFile: where the SolidWorks link keeps the student's own Save to Version setting
    // (AgentPaths.SolidWorksFile); null makes runtimes without a link.
    internal static HostParts Windows(AgentLog log, AgentTelemetry telemetry, string? solidWorksFile = null) => new()
    {
        SecretsIn = folder => new DpapiSecretStore(folder, problem => log.Error(problem)),
        Http = AgentHost.Http,
        Browser = new DefaultBrowserLauncher(),
        Runtime = (root, c) => VaultRuntime.Create(root, c.Sessions, c.Api, c.Blobs, log, telemetry.Recorder, new RealtimeFeed(c.Sessions, log: log.Info), solidWorksFile),
        StateOf = root => new FileStateStore(Path.Combine(root, ".armory", "state.json")),
        OpenInSolidWorks = AnyMarkerIn,
    };

    // A SolidWorks "~$" marker anywhere in the folder (outside .armory): a document is open from it.
    private static bool AnyMarkerIn(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return false;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(root, "~$*", options).Any(f => !f.Contains(Path.DirectorySeparatorChar + ".armory" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
}
