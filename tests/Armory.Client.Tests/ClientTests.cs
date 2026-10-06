using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Armory.Client;
using Armory.Storage;
using Armory.Storage.Tests;
using Armory.TestSupport;
using Npgsql;

namespace Armory.Client.Tests;

// Armory.Client against the fake ideabosco.com and fake Supabase (PostgREST and token
// refresh) over a real PostgreSQL database: docs/agent/CONTRACT.md sections 1-3.
public sealed partial class ClientTests
{
    private sealed class Env : IAsyncDisposable
    {
        public required ArmoryTestDatabase Db { get; init; }
        public required FakeSupabase Supabase { get; init; }
        public required FakeIdeaBosco Site { get; init; }
        public required FakeS3 S3 { get; init; }
        public required HttpClient Http { get; init; }
        public FakeS3 Storage => S3;
        public static async Task<Env> StartAsync()
        {
            var db = await ArmoryTestDatabase.CreateAsync();
            var supabase = new FakeSupabase(db);
            await supabase.StartAsync();
            var s3 = new FakeS3();
            var site = new FakeIdeaBosco(supabase, db, s3);
            await site.StartAsync();
            return new Env { Db = db, Supabase = supabase, Site = site, S3 = s3, Http = new HttpClient(new FakeNetworkHandler(s3)) };
        }
        public (SessionManager Sessions, ArmoryApi Api, BlobClient Blobs, InMemorySecretStore Secrets) SignedIn(string email, TimeSpan? lifetime = null, bool admin = false)
        {
            if (admin) Supabase.AdminEmails.Add(email);
            var issued = Supabase.IssueSession(email, lifetime);
            var secrets = new InMemorySecretStore();
            var sessions = new SessionManager(Http, secrets);
            sessions.SignIn(new ArmorySession(Supabase.SupabaseUrl, Supabase.AnonKey, issued.AccessToken, issued.RefreshToken, issued.ExpiresAt, email, Guid.Empty, "test PC"));
            return (sessions, new ArmoryApi(new PostgrestClient(Http, sessions)), new BlobClient(Http, Http, Site.BaseUri, sessions), secrets);
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await Site.DisposeAsync(); await Supabase.DisposeAsync(); await Db.DisposeAsync(); }
    }

    private sealed class Launcher(Func<Uri, Task> open) : IBrowserLauncher
    {
        public Task? Running { get; private set; }
        public void Open(Uri uri) => Running = Task.Run(() => open(uri));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [PostgresFact]
    public async Task Connect_flow_uses_loopback_state_and_pkce_and_stores_the_session()
    {
        await using var env = await Env.StartAsync();
        var secrets = new InMemorySecretStore();
        var sessions = new SessionManager(env.Http, secrets);
        using var browser = new FakeBrowser();
        Uri? opened = null;
        var launcher = new Launcher(async uri => { opened = uri; await browser.SignInAndApproveAsync(uri, "Alex.Kim@Students.test"); });
        var session = await new ConnectFlow(env.Http, env.Site.BaseUri, launcher, sessions).ConnectAsync("Lab PC 12");
        Assert.Equal("alex.kim@students.test", session.Email);
        Assert.Equal("Lab PC 12", session.DeviceName);
        Assert.Equal(env.Supabase.SupabaseUrl, session.SupabaseUrl);
        Assert.Equal(env.Supabase.AnonKey, session.AnonKey);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
        // The connect URL carries no secret: only the port, state, challenge and device.
        var query = System.Web.HttpUtility.ParseQueryString(opened!.Query);
        Assert.Equal(new[] { "port", "state", "challenge", "device" }, query.AllKeys.Select(k => k!).ToArray());
        Assert.InRange(int.Parse(query["port"]!, System.Globalization.CultureInfo.InvariantCulture), 1024, 65535);
        Assert.Equal(43, query["state"]!.Length);
        Assert.Equal(43, query["challenge"]!.Length);
        Assert.DoesNotContain(session.RefreshToken, opened.ToString(), StringComparison.Ordinal);
        Assert.Equal(FakeIdeaBosco.ChallengeFor("verifier-text"), ConnectFlow.Challenge("verifier-text"));
        // Stored for the next start, and registered as this user's device.
        var restored = new SessionManager(env.Http, secrets).Current!;
        Assert.Equal(session.RefreshToken, restored.RefreshToken);
        Assert.Equal(session.DeviceId, restored.DeviceId);
        await using var c = await env.Db.OpenAsync();
        Assert.Equal("Lab PC 12", (string)(await new NpgsqlCommand($"select name from armory_devices where id='{session.DeviceId}' and owner_email='alex.kim@students.test'", c).ExecuteScalarAsync())!);
        Assert.DoesNotContain(session.AccessToken, session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(session.RefreshToken, session.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_callback_with_the_wrong_state_is_ignored_and_the_real_one_still_connects()
    {
        await using var env = await Env.StartAsync();
        var sessions = new SessionManager(env.Http, new InMemorySecretStore());
        using var browser = new FakeBrowser();
        using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        HttpStatusCode? forged = null, stray = null;
        var launcher = new Launcher(async uri =>
        {
            var port = System.Web.HttpUtility.ParseQueryString(uri.Query)["port"];
            forged = (await plain.GetAsync($"http://127.0.0.1:{port}/callback?state=forged&code=stolen")).StatusCode;
            stray = (await plain.GetAsync($"http://127.0.0.1:{port}/favicon.ico")).StatusCode;
            await browser.SignInAndApproveAsync(uri, "maria.lopez@students.test");
        });
        var session = await new ConnectFlow(env.Http, env.Site.BaseUri, launcher, sessions).ConnectAsync("laptop");
        Assert.Equal(HttpStatusCode.BadRequest, forged);
        Assert.Equal(HttpStatusCode.NotFound, stray);
        Assert.Equal("maria.lopez@students.test", session.Email);
    }

    // The browser half of the flow done by hand, so a test can delay or replay it.
    private static async Task<Uri> StartAsync(Env env, Uri connect, string email)
    {
        var query = System.Web.HttpUtility.ParseQueryString(connect.Query);
        using var site = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(env.Site.BaseUri, FakeIdeaBosco.ConnectStartPath))
        {
            Content = JsonContent.Create(new { port = int.Parse(query["port"]!, System.Globalization.CultureInfo.InvariantCulture), state = query["state"], challenge = query["challenge"], device = query["device"] }),
        };
        request.Headers.Add(FakeIdeaBosco.SiteUserHeader, email);
        using var response = await site.SendAsync(request);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        return response.Headers.Location!;
    }

    [PostgresFact]
    public async Task An_expired_code_is_a_plain_410_message()
    {
        await using var env = await Env.StartAsync();
        var clock = new TestClock();
        env.Site.Clock = clock;
        using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var launcher = new Launcher(async uri =>
        {
            var callback = await StartAsync(env, uri, "alex.kim@students.test");
            clock.Advance(TimeSpan.FromMinutes(3));
            await plain.GetAsync(callback);
        });
        var error = await Assert.ThrowsAsync<ConnectException>(() => new ConnectFlow(env.Http, env.Site.BaseUri, launcher, new SessionManager(env.Http, new InMemorySecretStore())).ConnectAsync("laptop"));
        Assert.Equal(410, error.Status);
        Assert.Contains("expired", error.Message);
    }

    [PostgresFact]
    public async Task A_used_code_is_a_plain_401_message()
    {
        await using var env = await Env.StartAsync();
        using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var launcher = new Launcher(async uri =>
        {
            var callback = await StartAsync(env, uri, "alex.kim@students.test");
            // Someone else exchanges the code first (without the verifier it is consumed).
            var code = System.Web.HttpUtility.ParseQueryString(callback.Query)["code"];
            await plain.PostAsJsonAsync(new Uri(env.Site.BaseUri, FakeIdeaBosco.ConnectExchangePath), new { code, verifier = "wrong-verifier" });
            await plain.GetAsync(callback);
        });
        var error = await Assert.ThrowsAsync<ConnectException>(() => new ConnectFlow(env.Http, env.Site.BaseUri, launcher, new SessionManager(env.Http, new InMemorySecretStore())).ConnectAsync("laptop"));
        Assert.Equal(401, error.Status);
    }

    [PostgresFact]
    public async Task Rate_limits_and_timeouts_are_plain_messages()
    {
        await using var env = await Env.StartAsync();
        env.Site.RateLimits.ExchangePerIp = 0;
        using var browser = new FakeBrowser();
        var launcher = new Launcher(uri => browser.SignInAndApproveAsync(uri, "alex.kim@students.test"));
        var limited = await Assert.ThrowsAsync<ConnectException>(() => new ConnectFlow(env.Http, env.Site.BaseUri, launcher, new SessionManager(env.Http, new InMemorySecretStore())).ConnectAsync("laptop"));
        Assert.Equal(429, limited.Status);
        var never = new Launcher(_ => Task.CompletedTask);
        var timeout = await Assert.ThrowsAsync<ConnectException>(() => new ConnectFlow(env.Http, env.Site.BaseUri, never, new SessionManager(env.Http, new InMemorySecretStore())) { Timeout = TimeSpan.FromMilliseconds(300) }.ConnectAsync("laptop"));
        Assert.Contains("in time", timeout.Message);
    }

    [PostgresFact]
    public async Task Refresh_rotates_the_token_saves_it_first_and_is_single_flight()
    {
        await using var env = await Env.StartAsync();
        var (sessions, api, _, secrets) = env.SignedIn("alex.kim@students.test", lifetime: TimeSpan.FromSeconds(30));
        var before = sessions.Current!;
        var calls = Enumerable.Range(0, 10).Select(_ => sessions.GetFreshAsync()).ToArray();
        var after = await Task.WhenAll(calls);
        Assert.Equal(1, env.Supabase.TokenRequestCount);
        Assert.All(after, s => Assert.Equal(after[0].RefreshToken, s.RefreshToken));
        Assert.NotEqual(before.RefreshToken, after[0].RefreshToken);
        Assert.Equal(after[0].RefreshToken, new SessionManager(env.Http, secrets).Current!.RefreshToken);
        // An access token that expires mid-session is renewed and the call is retried.
        env.Supabase.ExpireAccessToken(sessions.Current!.AccessToken);
        Assert.Empty(await api.MyProjectsAsync());
        Assert.Equal(2, env.Supabase.TokenRequestCount);
    }

    [PostgresFact]
    public async Task A_refused_refresh_token_signs_this_computer_out()
    {
        await using var env = await Env.StartAsync();
        var (sessions, api, _, secrets) = env.SignedIn("alex.kim@students.test", lifetime: TimeSpan.FromSeconds(30));
        var signedOut = false;
        sessions.SignedOut += () => signedOut = true;
        env.Supabase.RevokeRefreshToken(sessions.Current!.RefreshToken);
        await Assert.ThrowsAsync<ArmorySignedOutException>(() => api.MyProjectsAsync());
        Assert.True(signedOut);
        Assert.Null(sessions.Current);
        Assert.Null(secrets.Read("armory-session"));
    }

    [PostgresFact]
    public async Task Server_errors_map_to_typed_exceptions_and_offline_is_offline()
    {
        await using var env = await Env.StartAsync();
        var (_, admin, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var project = await admin.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        var (_, student, _, _) = env.SignedIn("alex.kim@students.test");
        var forbidden = await Assert.ThrowsAsync<ArmoryRpcException>(() => student.ProjectFilesAsync(project));
        Assert.True(forbidden.IsForbidden);
        Assert.Equal(403, forbidden.Status);
        await admin.AddMemberAsync(project, "alex.kim@students.test", MemberRole.Student, Guid.NewGuid());
        var device = await student.RegisterDeviceAsync("laptop", Guid.NewGuid());
        await student.CreateFileAsync(project, "Drivetrain", "Plate.SLDPRT", device, Guid.NewGuid());
        var taken = await Assert.ThrowsAsync<ArmoryRpcException>(() => student.CreateFileAsync(project, "Intake", "plate.sldprt", device, Guid.NewGuid()));
        Assert.True(taken.IsNameTaken);
        Assert.Equal(409, taken.Status);
        Assert.Contains("\"existing_folder\": \"Drivetrain\"", taken.Details);
        var invalid = await Assert.ThrowsAsync<ArmoryRpcException>(() => student.CreateFileAsync(project, "", "CON.txt", device, Guid.NewGuid()));
        Assert.True(invalid.IsInvalidInput);
        env.Supabase.Offline = true;
        await Assert.ThrowsAsync<ArmoryOfflineException>(() => student.MyProjectsAsync());
    }

    [PostgresFact]
    public async Task Every_typed_rpc_round_trips_and_every_write_replays_from_its_receipt()
    {
        await using var env = await Env.StartAsync();
        var (_, mentor, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var (_, alex, blobs, _) = env.SignedIn("alex.kim@students.test");
        async Task<T> Twice<T>(Func<Guid, Task<T>> call) { var op = Guid.NewGuid(); var first = await call(op); Assert.Equal(first, await call(op)); return first; }
        var project = await Twice(op => mentor.CreateProjectAsync("Robot 2027", 2027, op));
        Assert.True(await Twice(op => mentor.AddMemberAsync(project, "alex.kim@students.test", MemberRole.CadLead, op)));
        Assert.True(await Twice(op => mentor.AddMemberAsync(project, "maria.lopez@students.test", MemberRole.Student, op)));
        Assert.True(await Twice(op => mentor.RemoveMemberAsync(project, "maria.lopez@students.test", op)));
        var mentorDevice = await Twice(op => mentor.RegisterDeviceAsync("mentor laptop", op));
        var device = await Twice(op => alex.RegisterDeviceAsync("laptop", op));
        Assert.True(await alex.IsMemberAsync(project));
        var mine = Assert.Single(await alex.MyProjectsAsync());
        Assert.Equal((MemberRole.CadLead, 2025, ProjectReleaseGate.Warn), (mine.Role, mine.PinnedRelease, mine.ReleaseGate));
        var file = await Twice(op => alex.CreateFileAsync(project, "Drivetrain", "Plate.SLDPRT", device, op));
        Assert.True(await Twice(op => alex.AcquireLockAsync(file, device, op)));
        var bytes = Encoding.UTF8.GetBytes("plate");
        var hash = Hash(bytes);
        Assert.True(await blobs.UploadAsync(project, hash, bytes.Length, () => new MemoryStream(bytes)));
        var v1 = await Twice(op => alex.CommitVersionWithReleaseAsync(file, null, ContentObjectKey.FromHash(hash), hash, bytes.Length, device, op, null));
        Assert.True(v1.Advanced);
        var v2 = await Twice(op => alex.CommitVersionAsync(file, v1.VersionId, ContentObjectKey.FromHash(hash), hash, bytes.Length, device, op));
        Assert.True(v2.Advanced);
        var side = await Twice(op => alex.SaveSideVersionAsync(file, v2.VersionId, ContentObjectKey.FromHash(hash), hash, bytes.Length, "conflict", device, op));
        var side2 = await Twice(op => alex.SaveSideVersionWithReleaseAsync(file, v2.VersionId, ContentObjectKey.FromHash(hash), hash, bytes.Length, "conflict", device, op, 2025));
        Assert.NotEqual(side, side2);
        Assert.True(await Twice(op => alex.MoveFileAsync(file, "Drivetrain/Gearbox", "Plate-Left.SLDPRT", device, op)));
        var (part, full) = await Twice(op => alex.AllocatePartNumberAsync(project, 1, 2027, op));
        Assert.Equal(("5669-27-0100", false), (part, full));
        var files = await alex.ProjectFilesAsync(project);
        var row = Assert.Single(files);
        Assert.Equal(("Drivetrain/Gearbox", "Plate-Left.SLDPRT", v2.VersionId, "alex.kim@students.test"), (row.Folder, row.Name, row.Current!.Id, row.Lock!.HolderEmail));
        Assert.Equal(4, (await alex.FileHistoryAsync(file)).Count);
        Assert.True(await Twice(op => alex.ReleaseLockAsync(file, device, op)));
        Assert.True(await mentor.AcquireLockAsync(file, mentorDevice, Guid.NewGuid()));
        Assert.True(await Twice(op => mentor.BreakLockAsync(file, mentorDevice, op)));
        Assert.True(await mentor.AcquireLockAsync(file, mentorDevice, Guid.NewGuid()));
        Assert.True(await Twice(op => mentor.TombstoneAsync(file, v2.VersionId, mentorDevice, op)));
        Assert.True(await Twice(op => mentor.SetReleaseGateAsync(project, ProjectReleaseGate.Enforce, op)));
        Assert.True(await Twice(op => mentor.RaisePinnedReleaseAsync(project, 2026, op)));
        var changes = await alex.ListChangesAsync(project, 0);
        Assert.Contains(changes, c => c.Kind == "file_moved" && c.Payload["old_name"]!.GetValue<string>() == "Plate.SLDPRT");
        Assert.Contains(changes, c => c.Kind == "lock_broken");
        Assert.Empty(await alex.ListChangesAsync(project, changes[^1].Cursor));
        Assert.True((await alex.ProjectFilesAsync(project)).Single().Deleted);
    }

    [PostgresFact]
    public async Task Blob_transfers_skip_stored_bytes_verify_downloads_and_report_refusals()
    {
        await using var env = await Env.StartAsync();
        var (_, mentor, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var project = await mentor.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        await mentor.AddMemberAsync(project, "alex.kim@students.test", MemberRole.Student, Guid.NewGuid());
        var (_, alex, blobs, _) = env.SignedIn("alex.kim@students.test");
        var bytes = Encoding.UTF8.GetBytes("gearbox bytes");
        var hash = Hash(bytes);
        Assert.True(await blobs.UploadAsync(project, hash, bytes.Length, () => new MemoryStream(bytes)));
        Assert.False(await blobs.UploadAsync(project, hash, bytes.Length, () => throw new InvalidOperationException("must not reopen stored bytes")));
        Assert.Equal(1, env.Storage.Puts);
        // A GET is refused until the hash is in this project's history.
        var refused = await Assert.ThrowsAsync<BlobRefusedException>(() => blobs.DownloadAsync(project, hash, bytes.Length, new MemoryStream()));
        Assert.Equal(403, refused.Status);
        var device = await alex.RegisterDeviceAsync("laptop", Guid.NewGuid());
        var file = await alex.CreateFileAsync(project, "", "Gearbox.SLDASM", device, Guid.NewGuid());
        await alex.AcquireLockAsync(file, device, Guid.NewGuid());
        await alex.CommitVersionWithReleaseAsync(file, null, ContentObjectKey.FromHash(hash), hash, bytes.Length, device, Guid.NewGuid(), null);
        var copy = new MemoryStream();
        await blobs.DownloadAsync(project, hash, bytes.Length, copy);
        Assert.Equal(bytes, copy.ToArray());
        env.Storage.CorruptGets = true;
        await Assert.ThrowsAsync<HashMismatchException>(() => blobs.DownloadAsync(project, hash, bytes.Length, new MemoryStream()));
        env.Storage.CorruptGets = false;
        env.Site.StorageConfigured = false;
        await Assert.ThrowsAsync<ArmoryOfflineException>(() => blobs.DownloadAsync(project, hash, bytes.Length, new MemoryStream()));
        env.Site.StorageConfigured = true;
        var (_, _, stranger, _) = env.SignedIn("stranger@students.test");
        Assert.Equal(403, (await Assert.ThrowsAsync<BlobRefusedException>(() => stranger.UploadAsync(project, hash, 1, () => new MemoryStream()))).Status);
        await Assert.ThrowsAsync<BlobRefusedException>(() => blobs.UploadAsync(project, hash, BlobClient.MaximumPutBytes + 1, () => new MemoryStream()));
    }
}
