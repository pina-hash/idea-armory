using System.Text;
using System.Text.Json;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.EndToEnd.Tests;
using Armory.Storage.Tests;
using Armory.TestSupport;

namespace Armory.Agent.Tests;

// A secret store that keeps each secret as a plain file named like DpapiSecretStore's (DPAPI is
// Windows only): what a test reads back is exactly what is on disk, a refresh token included.
internal sealed class PlainSecretStore(string folder) : ISecretStore
{
    private string FileOf(string name) => Path.Combine(folder, name + ProfileStore.SecretExtension);
    public byte[]? Read(string name) => File.Exists(FileOf(name)) ? File.ReadAllBytes(FileOf(name)) : null;
    public void Write(string name, byte[] value)
    {
        Directory.CreateDirectory(folder);
        var temp = FileOf(name) + "." + Guid.NewGuid().ToString("N") + ".pending";
        File.WriteAllBytes(temp, value);
        File.Move(temp, FileOf(name), overwrite: true);
    }
    public void Delete(string name) { if (File.Exists(FileOf(name))) File.Delete(FileOf(name)); }
}

// One Armory folder of the lab computer (C:\IDEA\Armory, or a student's own): its files and its
// .armory stores, the same objects for every runtime that ever runs on it, as the real folder is.
internal sealed class LabFolder(string root)
{
    public PortableVaultFileSystem Disk { get; } = new(root);
    public MemoryJournalStore Journal { get; } = new();
    public MemorySnapshotStore Snapshots { get; } = new();
    public MemoryStateStore State { get; } = new();
    public string Text(string path) => File.ReadAllText(Disk.Full(path));
    public bool Has(string path) => File.Exists(Disk.Full(path));
    public int Files(string folder) => Directory.Exists(Disk.Full(folder)) ? Directory.EnumerateFiles(Disk.Full(folder)).Count() : 0;
}

// Closed by its VaultRuntime once its engine has stopped for good: from then on any write that
// runtime's engine makes is a violation (the next student's runtime may be on the same folder).
internal sealed class Closing(string what, List<string> violations) : IDisposable
{
    private volatile bool closed;
    public void Dispose() => closed = true;
    public void Write(string kind)
    {
        if (!closed) return;
        lock (violations) violations.Add($"{what}: {kind} after its runtime closed");
    }
}

internal sealed class GuardedFiles(IVaultFileSystem inner, Closing closing) : IVaultFileSystem
{
    public string Root => inner.Root;
    public VaultScan Scan() => inner.Scan();
    public bool IsOpen(VaultPath path) => inner.IsOpen(path);
    public IReadOnlySet<string> OpenAmong(IReadOnlyCollection<VaultPath> paths) => inner.OpenAmong(paths);
    public Stream OpenRead(VaultPath path) => inner.OpenRead(path);
    public ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content, bool readOnly = false) { closing.Write("replace " + path); return inner.Replace(path, expectedHash, content, readOnly); }
    public ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash) { closing.Write("recover " + path); return inner.MoveToRecovery(path, expectedHash); }
    public ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash) { closing.Write("move " + from); return inner.Move(from, to, expectedHash); }
    public ReplaceOutcome MoveFolder(string from, string to) { closing.Write("move folder " + from); return inner.MoveFolder(from, to); }
    public bool DeleteEmptyFolder(string folder) { closing.Write("delete folder " + folder); return inner.DeleteEmptyFolder(folder); }
    public ReplaceOutcome CopyIn(string sourceFullPath, VaultPath to) { closing.Write("copy in " + to); return inner.CopyIn(sourceFullPath, to); }
    public ReplaceOutcome Launch(VaultPath path) => inner.Launch(path);
    public void ApplyLockAttribute(VaultPath path, LockOwnership ownership) { closing.Write("read-only " + path); inner.ApplyLockAttribute(path, ownership); }
    public void ApplyLockAttributes(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> attributes) { closing.Write("read-only of " + attributes.Count); inner.ApplyLockAttributes(attributes); }
    public void EnsureFolder(string vaultRelativeFolder) { closing.Write("folder " + vaultRelativeFolder); inner.EnsureFolder(vaultRelativeFolder); }
    public bool FolderExists(string vaultRelativeFolder) => inner.FolderExists(vaultRelativeFolder);
    public Stream CreateStaging(out string stagingName) { closing.Write("staging"); return inner.CreateStaging(out stagingName); }
    public void DeleteStaging(string stagingName) { closing.Write("delete staging"); inner.DeleteStaging(stagingName); }
}

internal sealed class GuardedState(IEngineStateStore inner, Closing closing) : IEngineStateStore
{
    public byte[]? Load() => inner.Load();
    public void Save(byte[] state) { closing.Write("state"); inner.Save(state); }
    public void Save(IReadOnlyList<ReadOnlyMemory<byte>> parts) { closing.Write("state"); inner.Save(parts); }
}

internal sealed class GuardedJournal(IJournalStore inner, Closing closing) : IJournalStore
{
    public byte[] ReadAll() => inner.ReadAll();
    public void Append(ReadOnlySpan<byte> bytes) { closing.Write("journal"); inner.Append(bytes); }
    public void Flush() => inner.Flush();
    public void TruncateIncompleteTail(int validLength) { closing.Write("journal tail"); inner.TruncateIncompleteTail(validLength); }
    public long? Generation => inner.Generation;
}

internal sealed class GuardedSnapshots(ISnapshotStore inner, Closing closing) : ISnapshotStore
{
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source) { closing.Write("kept copy " + path); return inner.Capture(id, path, author, source); }
    public IReadOnlyList<SavedSnapshot> Enumerate() => inner.Enumerate();
    public Stream OpenRead(string id) => inner.OpenRead(id);
}

// Who signs in when Armory opens the browser (null: nobody yet, and the sign-in waits).
internal sealed class LabBrowser : IBrowserLauncher, IDisposable
{
    private readonly FakeBrowser browser = new();
    public string? Next { get; set; }
    public int Opened;
    public List<Task<FakeBrowserResult>> Approvals { get; } = [];
    public void Open(Uri uri)
    {
        Interlocked.Increment(ref Opened);
        if (Next is { } email) lock (Approvals) Approvals.Add(browser.SignInAndApproveAsync(uri, email));
    }
    public void Dispose() => browser.Dispose();
}

// File storage that can be slow for one test, and counts what it serves.
internal sealed class LabNetwork(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public Func<HttpRequestMessage, TimeSpan>? StorageDelay { get; set; }
    // A call the site can't be reached for, for one test (the computer is offline for it).
    public Func<HttpRequestMessage, bool>? Refuse { get; set; }
    private long gets;
    public long StorageGets => Interlocked.Read(ref gets);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Refuse?.Invoke(request) == true) throw new HttpRequestException("offline (test)");
        if (string.Equals(request.RequestUri?.Host, FakeNetworkHandler.S3Host, StringComparison.OrdinalIgnoreCase))
        {
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref gets);
            if (StorageDelay?.Invoke(request) is { } wait && wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

// A lab computer with one shared Windows login: the real AgentHost (shared computer or not) over a
// private data folder, the fake ideabosco.com and Supabase over a throwaway PostgreSQL database,
// plain secrets, and portable folders standing in for C:\IDEA\Armory and each student's own.
internal sealed class Lab : IAsyncDisposable
{
    public const string Shared = @"C:\IDEA\Armory";
    public const string Alex = "alex.kim@students.test", Jordan = "jordan.reyes@students.test", Mentor = "pina@ideabosco.test";
    public const string Plate = "Robot 2027/Drivetrain/Plate.SLDPRT";
    private readonly HeavyRunLock heavy;
    private readonly Dictionary<string, LabFolder> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly TempFolder temp = new();

    private Lab(HeavyRunLock heavy, ArmoryTestDatabase database, FakeSupabase supabase, FakeS3 s3, FakeIdeaBosco site)
    {
        this.heavy = heavy;
        Database = database;
        Supabase = supabase;
        S3 = s3;
        Site = site;
        Paths = new AgentPaths(Path.Combine(temp.Root, "data"), true);
        Directory.CreateDirectory(Paths.LogFolder);
        Log = new AgentLog(Paths.LogFile, Paths.CrashFile);
        Network = new LabNetwork(new FakeNetworkHandler(s3));
    }

    public ArmoryTestDatabase Database { get; }
    public FakeSupabase Supabase { get; }
    public FakeS3 S3 { get; }
    public FakeIdeaBosco Site { get; }
    public AgentPaths Paths { get; }
    public AgentLog Log { get; }
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero));
    public LabBrowser Browser { get; } = new();
    public LabNetwork Network { get; }
    public Guid Project { get; private set; }
    public ArmoryApi MentorApi { get; private set; } = null!;
    public AgentHost Host { get; private set; } = null!;
    public AgentTelemetry Telemetry { get; private set; } = null!;
    // Writes by an engine after its runtime closed (must stay empty).
    public List<string> Violations { get; } = [];
    public int RuntimesMade { get; private set; }
    private static readonly string Sep = Path.DirectorySeparatorChar.ToString();

    public static async Task<Lab> StartAsync(bool shared)
    {
        var heavy = await HeavyRunLock.SharedAsync();
        try
        {
            var database = await ArmoryTestDatabase.CreateAsync();
            var supabase = new FakeSupabase(database);
            await supabase.StartAsync();
            var s3 = new FakeS3();
            var site = new FakeIdeaBosco(supabase, database, s3);
            await site.StartAsync();
            site.RateLimits.StartPerIp = site.RateLimits.StartPerUser = site.RateLimits.ExchangePerIp = 100_000;
            // Team status with a state per computer (idea-app 0233): heartbeats are checked.
            await ArmoryV3StandIn.ApplyCoreAsync(database);
            var lab = new Lab(heavy, database, supabase, s3, site);
            await lab.MakeTeamAsync();
            if (shared) File.WriteAllText(lab.Paths.SettingsFile, "{\"vaultRoot\":\"C:\\\\IDEA\\\\Armory\",\"startAtSignIn\":true,\"theme\":\"system\",\"sharedComputer\":true}");
            await lab.StartHostAsync();
            return lab;
        }
        catch
        {
            await heavy.DisposeAsync();
            throw;
        }
    }

    private async Task MakeTeamAsync()
    {
        Supabase.AdminEmails.Add(Mentor);
        var issued = Supabase.IssueSession(Mentor);
        var http = new HttpClient(new FakeNetworkHandler(S3));
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession(Supabase.SupabaseUrl, Supabase.AnonKey, issued.AccessToken, issued.RefreshToken, issued.ExpiresAt, Mentor, Guid.Empty, "website"));
        MentorApi = new ArmoryApi(new PostgrestClient(http, sessions));
        Project = await MentorApi.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        await MentorApi.AddMemberAsync(Project, Alex, MemberRole.Student, Guid.NewGuid());
        await MentorApi.AddMemberAsync(Project, Jordan, MemberRole.Student, Guid.NewGuid());
    }

    public HostParts Parts => new()
    {
        SecretsIn = folder => new PlainSecretStore(folder),
        Http = timeout => new HttpClient(Network, disposeHandler: false) { Timeout = timeout },
        Browser = Browser,
        Runtime = MakeRuntime,
        StateOf = root => Folder(root).State,
        OpenInSolidWorks = root => Directory.Exists(Folder(root).Disk.Root) &&
            Directory.EnumerateFiles(Folder(root).Disk.Root, "~$*", SearchOption.AllDirectories).Any(f => !f.Contains(Sep + ".armory" + Sep, StringComparison.Ordinal)),
        LightApps = () => false,
        Clock = Clock,
        MachineName = "LAB-PC-14",
        PinIterations = 1000,
        StopPatience = TimeSpan.FromSeconds(20),
        StillFinishingAfter = TimeSpan.FromSeconds(2),
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

    private VaultRuntime MakeRuntime(string root, ProfileClients clients)
    {
        var folder = Folder(root);
        var closing = new Closing("the runtime made for " + (clients.Sessions.Current?.Email ?? "nobody") + " in " + root + " (" + (++RuntimesMade) + ")", Violations);
        var files = new GuardedFiles(folder.Disk, closing);
        var engine = new SyncEngine(new EngineOptions { VaultRoot = root, ActivePollInterval = TimeSpan.FromMilliseconds(300), IdlePollInterval = TimeSpan.FromMilliseconds(300) },
            new EngineDependencies
            {
                Files = files, Journal = new GuardedJournal(folder.Journal, closing), Snapshots = new GuardedSnapshots(folder.Snapshots, closing),
                State = new GuardedState(folder.State, closing), Sessions = clients.Sessions, Api = clients.Api, Blobs = clients.Blobs, Clock = Clock,
            });
        return new VaultRuntime(root, files, engine, closing);
    }

    public async Task StartHostAsync()
    {
        Telemetry = new AgentTelemetry(Paths, Log, Clock);
        Host = new AgentHost(Paths, Log, Site.BaseUri, Telemetry, Parts);
        await Host.StartAsync();
    }

    // Armory quits and starts again (the data folder and every Armory folder stay).
    public async Task RestartAsync()
    {
        await Host.DisposeAsync();
        await Telemetry.DisposeAsync();
        await StartHostAsync();
    }

    public string LogText => File.Exists(Paths.LogFile) ? AgentExe.ReadShared(Paths.LogFile) : "";
    public AgentView View => Host.View;
    public ProfilesView Profiles => Host.View.Profiles!;
    public PickerStepView Step => Profiles.Step;
    public ProfileView Tile(string email) => Profiles.Profiles.Single(p => p.Email == email);
    public string IdOf(string email) => Tile(email).Id;
    public string? InUse => Profiles.Profiles.SingleOrDefault(p => p.Current)?.Email;

    // Armory runs for this student, in this folder, and has read the team's files once.
    public async Task UntilRunningAsync(string email, string folder)
    {
        try
        {
            await Until(() => !Profiles.Showing && InUse == email && Host.VaultFolder == folder && Host.Sessions.Current?.Email == email, $"{email} in use in {folder}");
            await Until(() => Host.View.Connection == Connections.SignedIn && Host.View.Account?.Email == email, $"{email}'s engine signed in");
        }
        catch (Xunit.Sdk.XunitException error)
        {
            throw new Xunit.Sdk.XunitException(error.Message + "\n" + Describe());
        }
    }

    // What the host shows and says, for a failure's message.
    public string Describe()
        => "showing " + Host.View.Profiles?.Showing + ", step " + JsonSerializer.Serialize(Host.View.Profiles?.Step) + ", in use " + InUseOrNone() +
           ", folder " + Host.VaultFolder + ", session " + Host.Sessions.Current?.Email + ", connection " + Host.View.Connection + ", sync " + Host.View.Sync.Line +
           "\nlog:\n" + string.Join("\n", LogText.Split('\n').TakeLast(30));

    private string? InUseOrNone() => Host.View.Profiles?.Profiles.SingleOrDefault(p => p.Current)?.Email;

    public static async Task Until(Func<bool> condition, string what, int seconds = 60)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), "waited " + seconds + " s for " + what);
            await Task.Delay(50);
        }
    }

    // Add a student: the browser signs in as them; with a PIN when PINs are on.
    public async Task<string> AddAsync(string email, string? pin)
    {
        Browser.Next = email;
        Assert.True((await Host.AddProfileAsync()).Ok);
        if (pin is null)
        {
            await Until(() => InUse == email && !Profiles.Showing, email + " added and in use");
            return IdOf(email);
        }
        await Until(() => Step.Kind == PickerSteps.NewPin, email + "'s new PIN step");
        var id = Step.ProfileId!;
        var set = await Host.SetPinAsync(id, pin);
        Assert.True(set.Ok, set.Message + " / " + Step.Message);
        return id;
    }

    // Pick a student (and type their PIN when the picker asks for it).
    public async Task<ActionResult> PickAsync(string email, string? pin = null)
    {
        Host.ShowPicker(PickerTrigger.SwitchStudent);
        var id = IdOf(email);
        var picked = await Host.PickProfileAsync(id);
        if (Step.Kind == PickerSteps.Pin && Step.ProfileId == id && pin is not null) return await Host.EnterPinAsync(id, pin);
        return picked;
    }

    // Every secret file under the data folder, as text (plain here).
    public IReadOnlyList<(string File, string Text)> Secrets() => Directory.Exists(Paths.DataFolder)
        ? Directory.EnumerateFiles(Paths.DataFolder, "*" + ProfileStore.SecretExtension, SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(Paths.DataFolder, f), Encoding.UTF8.GetString(File.ReadAllBytes(f)))).ToList()
        : [];

    // Every file of every Armory folder, with its bytes' hash (proof that nothing was deleted).
    public string AllFiles()
    {
        var all = new StringBuilder();
        lock (folders)
            foreach (var (root, folder) in folders.OrderBy(f => f.Key, StringComparer.Ordinal))
                if (Directory.Exists(folder.Disk.Root))
                    foreach (var file in Directory.EnumerateFiles(folder.Disk.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Sep + ".armory" + Sep, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                        all.Append(root).Append(':').Append(Path.GetRelativePath(folder.Disk.Root, file)).Append('=')
                            .Append(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
        return all.ToString();
    }

    public Task<string?> DeviceState(string email) => Scalar("select state from armory_devices where owner_email=@e order by last_seen desc nulls last limit 1", email);
    public async Task<long> Devices(string email) => long.Parse(await Scalar("select count(*)::text from armory_devices where owner_email=@e", email) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string?> Scalar(string sql, string email)
    {
        await using var c = await Database.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, c);
        command.Parameters.AddWithValue("e", email);
        return await command.ExecuteScalarAsync() as string;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Host.DisposeAsync(); } catch (Exception error) when (error is not OutOfMemoryException) { }
        try { await Telemetry.DisposeAsync(); } catch (Exception error) when (error is not OutOfMemoryException) { }
        Browser.Dispose();
        await Site.DisposeAsync();
        await Supabase.DisposeAsync();
        await Database.DisposeAsync();
        temp.Dispose();
        await heavy.DisposeAsync();
    }
}
