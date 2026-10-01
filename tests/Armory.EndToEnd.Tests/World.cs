using System.Security.Cryptography;
using System.Text;
using Armory.Agent.Engine;
using Armory.Client;
using Armory.Core;
using Armory.Storage;
using Armory.Storage.Tests;
using Armory.TestSupport;
using Npgsql;

namespace Armory.EndToEnd.Tests;

internal sealed class SimulatedCrash(string point) : Exception($"simulated crash at {point}");

// One throwaway Armory: a real PostgreSQL database with server/sql and the test identity
// and is_admin stubs, the fake Supabase (PostgREST and token refresh), the fake
// ideabosco.com and the shared fake S3.
internal sealed class World : IAsyncDisposable
{
    public const string Root = @"C:\IDEA\Armory";
    private readonly List<IAsyncDisposable> owned = [];
    private readonly HeavyRunLock heavy;
    private World(HeavyRunLock heavy, ArmoryTestDatabase database, FakeSupabase supabase, FakeIdeaBosco site, FakeS3 s3)
    { this.heavy = heavy; Database = database; Supabase = supabase; Site = site; S3 = s3; }
    public ArmoryTestDatabase Database { get; }
    public FakeSupabase Supabase { get; }
    public FakeIdeaBosco Site { get; }
    public FakeS3 S3 { get; }
    public string Temp { get; } = Path.Combine(Path.GetTempPath(), "armory-e2e-" + Guid.NewGuid().ToString("N"));

    // Every world holds the heavy-run lock shared, so the server simulation (which takes it
    // exclusively) never shares the cluster with an end-to-end run; see HeavyRunLock.
    public static async Task<World> StartAsync()
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
            return new World(heavy, database, supabase, site, s3);
        }
        catch
        {
            await heavy.DisposeAsync();
            throw;
        }
    }

    public async Task<Person> PersonAsync(string email, bool admin = false)
    {
        if (admin) Supabase.AdminEmails.Add(email);
        var person = new Person(this, email);
        await person.SignInAsync();
        return person;
    }

    public async Task<Computer> ComputerAsync(string name, string email)
    {
        var computer = new Computer(this, name, Path.Combine(Temp, name.Replace(' ', '-')));
        owned.Add(computer);
        await computer.ConnectAsync(email);
        return computer;
    }

    public async Task<long> CountAsync(string sql, params (string, object)[] parameters)
    {
        await using var c = await Database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Whether these bytes are anywhere in server history (a version or a side version).
    public bool HashOnServer(string hash)
    {
        using var c = new NpgsqlConnection(Database.ConnectionString);
        c.Open();
        using var command = new NpgsqlCommand("select exists(select 1 from armory_versions where content_sha256=@h) or exists(select 1 from armory_side_versions where content_sha256=@h)", c);
        command.Parameters.AddWithValue("h", hash);
        return (bool)command.ExecuteScalar()!;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string, object)[] parameters)
    {
        await using var c = await Database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        List<T> rows = [];
        while (await reader.ReadAsync()) rows.Add(read(reader));
        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in owned) await item.DisposeAsync();
        await Site.DisposeAsync();
        await Supabase.DisposeAsync();
        await Database.DisposeAsync();
        try { if (Directory.Exists(Temp)) Directory.Delete(Temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        await heavy.DisposeAsync();
    }
}

// A person using the website or a mentor's tools: a session and a registered device, no vault.
internal sealed class Person(World world, string email)
{
    public string Email { get; } = email;
    public ArmoryApi Api { get; private set; } = null!;
    public BlobClient Blobs { get; private set; } = null!;
    public Guid Device { get; private set; }
    public async Task SignInAsync()
    {
        var issued = world.Supabase.IssueSession(Email);
        var http = new HttpClient(new FakeNetworkHandler(world.S3));
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession(world.Supabase.SupabaseUrl, world.Supabase.AnonKey, issued.AccessToken, issued.RefreshToken, issued.ExpiresAt, Email, Guid.Empty, "website"));
        Api = new ArmoryApi(new PostgrestClient(http, sessions));
        Blobs = new BlobClient(http, http, world.Site.BaseUri, sessions);
        Device = await Api.RegisterDeviceAsync("mentor laptop", Guid.NewGuid());
    }

    // A full shared write the way any tool would make it: lock, bytes, commit.
    public async Task<CommitResult> CommitAsync(Guid project, Guid file, byte[] bytes)
    {
        Assert.True(await Api.AcquireLockAsync(file, Device, Guid.NewGuid()));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await Blobs.UploadAsync(project, hash, bytes.Length, () => new MemoryStream(bytes));
        var parent = (await Api.ProjectFilesAsync(project)).Single(f => f.Id == file).Current?.Id;
        var result = await Api.CommitVersionWithReleaseAsync(file, parent, ContentObjectKey.FromHash(hash), hash, bytes.Length, Device, Guid.NewGuid(), null);
        Assert.True(await Api.ReleaseLockAsync(file, Device, Guid.NewGuid()));
        return result;
    }
}

// A student's Windows PC: its own vault folder, durable stores, secret store and network
// switch. Crash() drops the engine and builds a new one over the same durable stores.
internal sealed class Computer : IAsyncDisposable
{
    private readonly World world;
    private readonly OfflineHandler network;
    private readonly HttpClient http;
    public Computer(World world, string name, string root)
    {
        this.world = world;
        Name = name;
        Disk = new PortableVaultFileSystem(root) { IsPreserved = world.HashOnServer };
        network = new OfflineHandler(new FakeNetworkHandler(world.S3));
        http = new HttpClient(network);
    }
    public string Name { get; }
    public PortableVaultFileSystem Disk { get; }
    public MemoryJournalStore Journal { get; } = new();
    public MemorySnapshotStore Snapshots { get; } = new();
    public MemoryStateStore State { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public ISavedReleaseReader? ReleaseReader { get; set; }
    public Action<string>? CrashPoint { get; set; }
    public TestClock Clock { get; } = new();
    public long MaximumFileBytes { get; set; } = BlobClient.MaximumPutBytes;
    public SyncEngine Engine { get; private set; } = null!;
    public SessionManager Sessions { get; private set; } = null!;
    public bool Offline { get => network.Offline; set => network.Offline = value; }
    public int Engines { get; private set; }

    public async Task ConnectAsync(string email)
    {
        var sessions = new SessionManager(http, Secrets);
        using var browser = new FakeBrowser();
        Task<FakeBrowserResult>? approval = null;
        var flow = new ConnectFlow(http, world.Site.BaseUri, new Launcher(uri => approval = browser.SignInAndApproveAsync(uri, email)), sessions);
        var session = await flow.ConnectAsync(Name);
        var result = await approval!;
        Assert.Equal(System.Net.HttpStatusCode.OK, result.Status);
        Assert.Equal(email, session.Email);
        Restart();
    }

    public void Restart()
    {
        Sessions = new SessionManager(http, Secrets);
        var api = new ArmoryApi(new PostgrestClient(http, Sessions));
        Engine = new SyncEngine(new EngineOptions { VaultRoot = World.Root, MaximumFileBytes = MaximumFileBytes }, new EngineDependencies
        {
            Files = Disk, Journal = Journal, Snapshots = Snapshots, State = State, Sessions = Sessions, Api = api,
            Blobs = new BlobClient(http, http, world.Site.BaseUri, Sessions), ReleaseReader = ReleaseReader, Clock = Clock,
        })
        { CrashPoint = CrashPoint };
        Engines++;
    }

    public Task<SyncReport> SyncAsync() => Engine.SyncOnceAsync();
    public async Task SyncTimesAsync(int times) { for (var i = 0; i < times; i++) await SyncAsync(); }

    public void Write(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));
    public void Write(string path, byte[] bytes)
    {
        var full = Disk.Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
    public byte[]? Read(string path) => File.Exists(Disk.Full(path)) ? File.ReadAllBytes(Disk.Full(path)) : null;
    public string? Text(string path) => Read(path) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
    public void Delete(string path) => File.Delete(Disk.Full(path));
    public void Open(string path) => Disk.Open(path);
    public void Close(string path) => Disk.Close(path);

    public ValueTask DisposeAsync() { http.Dispose(); return ValueTask.CompletedTask; }

    private sealed class Launcher(Action<Uri> open) : IBrowserLauncher { public void Open(Uri uri) => open(uri); }
}

internal sealed class OfflineHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public volatile bool Offline;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Offline ? throw new HttpRequestException("This computer is offline (test).") : base.SendAsync(request, cancellationToken);
}

// The saved-release reader is a test fake: no standalone reader exists yet
// (docs/spike/saved-release.md). Content beginning "SW<year>" reads as that release.
internal sealed class FakeReleaseReader : ISavedReleaseReader
{
    public async ValueTask<SolidWorksRelease?> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var head = new byte[6];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        var text = Encoding.ASCII.GetString(head, 0, read);
        return text.StartsWith("SW", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), out var year) ? new SolidWorksRelease(year) : null;
    }
}
