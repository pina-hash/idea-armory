using System.Text.Json;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;

namespace Armory.Agent.Tests;

// The parts of several students on one computer that need no server (docs/agent/PROFILES.md):
// one student per computer is exactly as before, a folder holds one runtime at a time, the
// students' index keeps no secret and is never rewritten by an older Armory, settings.json says
// shared mode only while it is on, a PIN never reaches a log, and the bridge checks every picker
// message before the host sees it. These run on every platform, offline.
public sealed class SharedComputerHostTests
{
    // The real host, offline: no site can be reached, folders are portable, secrets plain files.
    private sealed class Offline : IAsyncDisposable
    {
        private readonly TempFolder temp = new();
        private readonly Dictionary<string, LabFolder> folders = new(StringComparer.OrdinalIgnoreCase);

        public Offline(bool shared, ArmorySession? signedIn = null)
        {
            Paths = new AgentPaths(Path.Combine(temp.Root, "data"), true);
            Directory.CreateDirectory(Paths.LogFolder);
            if (shared) File.WriteAllText(Paths.SettingsFile, "{\"vaultRoot\":\"C:\\\\IDEA\\\\Armory\",\"startAtSignIn\":true,\"theme\":\"system\",\"sharedComputer\":true}");
            if (signedIn is not null)
                new PlainSecretStore(Paths.SecretsFolder).Write("armory-session", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    signedIn.SupabaseUrl, signedIn.AnonKey, signedIn.AccessToken, signedIn.RefreshToken, signedIn.ExpiresAt, signedIn.Email, signedIn.DeviceId, signedIn.DeviceName,
                }));
            Log = new AgentLog(Paths.LogFile, Paths.CrashFile);
            Telemetry = new AgentTelemetry(Paths, Log);
            Host = new AgentHost(Paths, Log, new Uri("http://127.0.0.1:9"), Telemetry, Parts);
        }

        public AgentPaths Paths { get; }
        public AgentLog Log { get; }
        public AgentTelemetry Telemetry { get; }
        public AgentHost Host { get; }
        public List<string> Violations { get; } = [];
        public string LogText => AgentExe.ReadShared(Paths.LogFile);

        public HostParts Parts => new()
        {
            SecretsIn = folder => new PlainSecretStore(folder),
            Http = timeout => new HttpClient(new Unreachable()) { Timeout = timeout },
            Browser = new NoBrowser(),
            Runtime = MakeRuntime,
            StateOf = root => Folder(root).State,
            OpenInSolidWorks = _ => false,
            LightApps = () => false,
            PinIterations = 1000,
        };

        public LabFolder Folder(string root)
        {
            lock (folders)
            {
                if (!folders.TryGetValue(root, out var folder))
                    folders[root] = folder = new LabFolder(Path.Combine(temp.Root, "folders", root.Replace(':', '_').Replace('\\', '_')));
                return folder;
            }
        }

        public VaultRuntime MakeRuntime(string root, ProfileClients clients)
        {
            var folder = Folder(root);
            var closing = new Closing("the runtime in " + root, Violations);
            var files = new GuardedFiles(folder.Disk, closing);
            var engine = new SyncEngine(new EngineOptions { VaultRoot = root }, new EngineDependencies
            {
                Files = files, Journal = new GuardedJournal(folder.Journal, closing), Snapshots = new GuardedSnapshots(folder.Snapshots, closing),
                State = new GuardedState(folder.State, closing), Sessions = clients.Sessions, Api = clients.Api, Blobs = clients.Blobs,
            });
            return new VaultRuntime(root, files, engine, closing);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Telemetry.DisposeAsync();
            temp.Dispose();
        }

        private sealed class Unreachable : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("offline (test)");
        }

        private sealed class NoBrowser : IBrowserLauncher { public void Open(Uri uri) { } }
    }

    // The window, as the bridge sees it: every message it posts, kept.
    private sealed class Window : IBridgeWindow
    {
        public List<JsonElement> Posted { get; } = [];
        public void Post(string json) { lock (Posted) Posted.Add(JsonDocument.Parse(json).RootElement.Clone()); }
        public string? ChooseFolder(string current) => null;
        public IReadOnlyList<string>? ChooseFiles(string title) => null;
        public void ShowProblem(string message) { }
        public Task<WindowCapture?> CaptureWindowAsync(int cssWidth, int cssHeight) => Task.FromResult<WindowCapture?>(null);
        public (bool Ok, string Message) Answer(string requestId)
        {
            var answer = Posted.Single(p => p.GetProperty("type").GetString() == "actionResult" && p.GetProperty("requestId").GetString() == requestId);
            return (answer.GetProperty("ok").GetBoolean(), answer.GetProperty("message").GetString()!);
        }
    }

    private static ArmorySession Alex => new("http://127.0.0.1:9/supabase", "anon-key", "access-token-of-alex", "refresh-token-of-alex",
        DateTimeOffset.UtcNow.AddHours(1), "alex.kim@students.test", Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "LAB-PC-14");

    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Single_user_mode_is_unchanged()
    {
        await using var lab = new Offline(shared: false, signedIn: Alex);
        await lab.Host.StartAsync();
        // The same sign-in, where it always was; nothing under profiles\, ever.
        Assert.Contains("session loaded for alex.kim@students.test", lab.LogText);
        Assert.Equal("alex.kim@students.test", lab.Host.Sessions.Current!.Email);
        Assert.True(File.Exists(Path.Combine(lab.Paths.SecretsFolder, "armory-session.secret")));
        Assert.False(lab.Host.SharedComputer);
        Assert.False(lab.Host.PickerShowing);
        Assert.Null(lab.Host.View.Profiles);
        Assert.False(lab.Host.View.Settings.SharedComputer);
        Assert.Equal(AgentSettings.DefaultVaultRoot, lab.Host.VaultFolder);
        // Opening the window from hidden shows Home, not a picker.
        lab.Host.ShowPicker(PickerTrigger.ShownFromHidden);
        lab.Host.ShowPicker(PickerTrigger.WindowsLocked);
        Assert.False(lab.Host.PickerShowing);
        Assert.Null(lab.Host.View.Profiles);

        // Every picker message is answered plainly and changes nothing.
        var window = new Window();
        var bridge = new Bridge(lab.Host, window, lab.Log);
        var sent = new (string Type, string Json)[]
        {
            ("pickProfile", $$"""{"type":"pickProfile","profileId":"{{Id}}","requestId":"r1"}"""),
            ("enterPin", $$"""{"type":"enterPin","profileId":"{{Id}}","pin":"2580","requestId":"r2"}"""),
            ("setPin", $$"""{"type":"setPin","profileId":"{{Id}}","pin":"2580","requestId":"r3"}"""),
            ("addProfile", """{"type":"addProfile","requestId":"r4"}"""),
            ("forgotPin", $$"""{"type":"forgotPin","profileId":"{{Id}}","requestId":"r5"}"""),
            ("chooseFolder", $$"""{"type":"chooseFolder","profileId":"{{Id}}","choice":"own","requestId":"r6"}"""),
            ("removeProfile", $$"""{"type":"removeProfile","profileId":"{{Id}}","requestId":"r7"}"""),
            ("setPinsRequired", """{"type":"setPinsRequired","on":false,"requestId":"r8"}"""),
        };
        foreach (var (_, json) in sent) await bridge.HandleAsync(json);
        await bridge.HandleAsync("""{"type":"showPicker"}""");
        await bridge.HandleAsync("""{"type":"cancelPicker"}""");
        for (var i = 1; i <= sent.Length; i++) Assert.Equal((false, AgentHost.NotShared), window.Answer("r" + i));
        await bridge.HandleAsync("""{"type":"setSharedComputer","on":false,"pin":"","requestId":"r9"}""");
        Assert.Equal((true, "This computer is used by one student."), window.Answer("r9"));
        Assert.False(Directory.Exists(lab.Paths.ProfilesFolder));
        Assert.False(File.Exists(lab.Paths.SettingsFile));
        Assert.Null(lab.Host.View.Profiles);
        Assert.Equal("alex.kim@students.test", lab.Host.Sessions.Current!.Email);
        // Sign out and Switch account are still what they were.
        lab.Host.SignOut();
        Assert.Null(lab.Host.Sessions.Current);
        Assert.False(File.Exists(Path.Combine(lab.Paths.SecretsFolder, "armory-session.secret")));
        Assert.Contains("signed out", lab.LogText);
        Assert.Empty(lab.Violations);
    }

    [Fact]
    public async Task A_second_runtime_on_the_same_folder_is_refused()
    {
        await using var lab = new Offline(shared: false);
        var clients = ProfileClients.Create(null, new InMemorySecretStore(), new HostClientsForTest().Http, new Uri("http://127.0.0.1:9"), new NoLaunch(), lab.Telemetry, lab.Log);
        var first = lab.MakeRuntime(@"C:\IDEA\Armory", clients);
        Assert.Throws<IOException>(() => lab.MakeRuntime(@"C:\IDEA\Armory", clients));
        Assert.Throws<IOException>(() => lab.MakeRuntime(@"c:\idea\armory", clients));
        // Another folder is fine at the same time (a student's own beside the shared one).
        var other = lab.MakeRuntime(@"C:\IDEA\Armory-jordan", clients);
        // Once the first has stopped for good and closed its files, the folder can be run again.
        Assert.True(await first.DisposeAsync(lab.Log, TimeSpan.FromSeconds(10)));
        var again = lab.MakeRuntime(@"C:\IDEA\Armory", clients);
        Assert.True(await again.DisposeAsync(lab.Log, TimeSpan.FromSeconds(10)));
        Assert.True(await other.DisposeAsync(lab.Log, TimeSpan.FromSeconds(10)));
        Assert.Empty(lab.Violations);
    }

    private sealed class HostClientsForTest
    {
        public HostHttp Http { get; } = new(new HttpClient(), new HttpClient(), new HttpClient());
    }

    private sealed class NoLaunch : IBrowserLauncher { public void Open(Uri uri) { } }

    // Windows: the stores' own lock files refuse a second runtime on a folder, in this process
    // or another (DurableSnapshotStore's owner.lock, ReadOnlyPolicy's read-only.lock).
    [WindowsFact]
    public void A_second_runtime_from_the_real_stores_is_refused()
    {
        using var temp = new TempFolder();
        var root = temp.File("Armory");
        Directory.CreateDirectory(root);
        var sessions = new SessionManager(new HttpClient(), new InMemorySecretStore());
        var api = new ArmoryApi(new PostgrestClient(new HttpClient(), sessions));
        var blobs = new BlobClient(new HttpClient(), new HttpClient(), new Uri("http://127.0.0.1:9"), sessions);
        var log = new AgentLog(temp.File("agent.log"), temp.File("crash.log"));
        var first = VaultRuntime.Create(root, sessions, api, blobs, log);
        try { Assert.ThrowsAny<IOException>(() => VaultRuntime.Create(root, sessions, api, blobs, log)); }
        finally { Assert.True(first.DisposeAsync(log, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()); }
        var again = VaultRuntime.Create(root, sessions, api, blobs, log);
        Assert.True(again.DisposeAsync(log, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task The_picker_answers_only_well_formed_messages_and_starts_with_add_a_student()
    {
        await using var lab = new Offline(shared: true);
        await lab.Host.StartAsync();
        Assert.True(lab.Host.SharedComputer);
        Assert.True(lab.Host.PickerShowing);
        var view = lab.Host.View;
        Assert.True(view.Profiles!.Showing);
        Assert.Empty(view.Profiles.Profiles);
        Assert.Equal(PickerSteps.Choose, view.Profiles.Step.Kind);
        Assert.Equal(@"C:\IDEA\Armory", view.Profiles.SharedFolder);
        Assert.True(view.Settings.SharedComputer);
        Assert.Empty(view.Projects);
        Assert.Contains("profiles: shared computer on, 0 students, current nobody", lab.LogText);
        var window = new Window();
        var bridge = new Bridge(lab.Host, window, lab.Log);
        await bridge.HandleAsync("""{"type":"pickProfile","profileId":"../../secrets","requestId":"a"}""");
        await bridge.HandleAsync("""{"type":"pickProfile","profileId":"0123456789ABCDEF0123456789ABCDEF","requestId":"b"}""");
        await bridge.HandleAsync($$"""{"type":"enterPin","profileId":"{{Id}}","pin":"12a4","requestId":"c"}""");
        await bridge.HandleAsync($$"""{"type":"enterPin","profileId":"{{Id}}","pin":"１２３４","requestId":"d"}""");
        await bridge.HandleAsync($$"""{"type":"setPin","profileId":"{{Id}}","pin":"123","requestId":"e"}""");
        await bridge.HandleAsync($$"""{"type":"chooseFolder","profileId":"{{Id}}","choice":"C:\\Windows","requestId":"f"}""");
        await bridge.HandleAsync($$"""{"type":"pickProfile","profileId":"{{Id}}","requestId":"g"}""");
        await bridge.HandleAsync("""{"type":"setSharedComputer","on":true,"pin":"12","requestId":"h"}""");
        Assert.Equal((false, "That student isn't on this computer."), window.Answer("a"));
        Assert.Equal((false, "That student isn't on this computer."), window.Answer("b"));
        Assert.Equal((false, "Type 4 digits."), window.Answer("c"));
        Assert.Equal((false, "Type 4 digits."), window.Answer("d"));
        Assert.Equal((false, "Type 4 digits."), window.Answer("e"));
        Assert.Equal((false, "Choose Wait or a folder of your own."), window.Answer("f"));
        Assert.Equal((false, "That student isn't on this computer any more."), window.Answer("g"));
        Assert.Equal((false, "Type 4 digits."), window.Answer("h"));
        // A PIN is never written to the log, not even a refused one.
        Assert.DoesNotContain("12a4", lab.LogText);
        // Without anyone in use, nothing runs and the window stays on the picker.
        lab.Host.CancelPicker();
        Assert.True(lab.Host.PickerShowing);
        Assert.Empty(lab.Violations);
    }

    [Fact]
    public void Profile_index_round_trips_and_holds_no_secret()
    {
        using var temp = new TempFolder();
        var store = new ProfileStore(temp.File("profiles"), folder => new PlainSecretStore(folder));
        Assert.Equal(ProfileIndex.Empty, store.Load());
        var id = ProfileStore.NewId();
        Assert.True(ProfileStore.IsId(id));
        Assert.NotEqual(id, ProfileStore.NewId());
        var at = new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
        var record = new ProfileRecord(id, "alex.kim@students.test", "Alex Kim", at, at.AddMinutes(5), @"C:\IDEA\Armory",
            new WaitingRecord(@"C:\IDEA\Armory", 2, 0, 0, 0, 0, "2 files checked out", at));
        var index = ProfileIndex.Empty with { Current = id, PinsRequired = false, PinsChangedBy = "pina@ideabosco.test", PinsChangedAt = at, LastPicked = new DateOnly(2026, 10, 9), Profiles = [record] };
        store.Save(index);
        store.SecretsOf(id).Write("armory-session", "{\"refresh_token\":\"refresh-secret\"}"u8.ToArray());
        store.WritePin(id, PinHash.Create("2580", 1000));
        var back = store.Load();
        Assert.Equal((id, false, "pina@ideabosco.test", new DateOnly(2026, 10, 9)), (back.Current, back.PinsRequired, back.PinsChangedBy, back.LastPicked));
        Assert.Equal(record, back.Profiles.Single());
        var text = File.ReadAllText(store.IndexFile);
        Assert.DoesNotContain("refresh-secret", text);
        Assert.DoesNotContain("2580", text);
        Assert.DoesNotContain("salt", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PinCheck.Right, PinHash.Check(store.ReadPin(id)!, "2580", at, out _));
        // A record with an id that isn't one is dropped; Forget takes only that profile's folder.
        File.WriteAllText(store.IndexFile, text.Replace(id, "not-an-id"));
        Assert.Empty(store.Load().Profiles);
        store.Forget(id);
        Assert.False(Directory.Exists(store.FolderOf(id)));
        Assert.True(File.Exists(store.IndexFile));
        Assert.Throws<ArgumentException>(() => store.FolderOf(".."));
    }

    [Fact]
    public void An_unreadable_index_loads_empty_and_a_newer_one_is_never_rewritten()
    {
        using var temp = new TempFolder();
        var problems = new List<string>();
        var store = new ProfileStore(temp.File("profiles"), folder => new PlainSecretStore(folder), problems.Add);
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(store.IndexFile, "{ not json");
        Assert.Equal(ProfileIndex.Empty, store.Load());
        Assert.Single(problems);
        Assert.False(store.TooNew);
        File.WriteAllText(store.IndexFile, "{\"version\":2,\"current\":null,\"profiles\":[]}");
        Assert.Empty(store.Load().Profiles);
        Assert.True(store.TooNew);
        Assert.Throws<InvalidOperationException>(() => store.Save(ProfileIndex.Empty));
        Assert.Contains("\"version\":2", File.ReadAllText(store.IndexFile));
    }

    [Fact]
    public void Shared_mode_is_in_settings_only_while_it_is_on()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        store.Save(AgentSettings.Default);
        Assert.DoesNotContain("sharedComputer", File.ReadAllText(store.Path));
        Assert.False(store.Load().SharedComputer);
        store.Save(AgentSettings.Default with { SharedComputer = true });
        Assert.Contains("\"sharedComputer\": true", File.ReadAllText(store.Path));
        Assert.True(store.Load().SharedComputer);
        Assert.True(store.Load().ToView().SharedComputer);
        // Only a true turns it on.
        foreach (var value in new[] { "false", "\"true\"", "1", "null" })
        {
            File.WriteAllText(store.Path, "{\"vaultRoot\":\"C:\\\\IDEA\\\\Armory\",\"sharedComputer\":" + value + "}");
            Assert.False(store.Load().SharedComputer, value);
        }
    }

    [Fact]
    public void A_pin_never_reaches_a_log()
    {
        foreach (var line in new[] { "{\"type\":\"enterPin\",\"pin\":\"2580\"}", "pin=2580", "pin: 2580", "{'pin': '2580'}" })
        {
            var scrubbed = Redactor.Scrub(line);
            Assert.DoesNotContain("2580", scrubbed);
            Assert.Contains(Redactor.Mask, scrubbed);
        }
        Assert.Equal("picker: a wrong PIN for alex.kim@students.test (3 in a row)", Redactor.Scrub("picker: a wrong PIN for alex.kim@students.test (3 in a row)"));
        Assert.Equal("spinning: 2 files", Redactor.Scrub("spinning: 2 files"));
    }
}
