using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Storage.Tests;
using Npgsql;

namespace Armory.TestSupport.Tests;

/// <summary>One throwaway database per test class; skipped tests never touch it.</summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private ArmoryTestDatabase? _database;
    public ArmoryTestDatabase Database => _database ?? throw new InvalidOperationException("ARMORY_TEST_POSTGRES is not set.");

    public async Task InitializeAsync()
    {
        if (ArmoryTestDatabase.IsAvailable) _database = await ArmoryTestDatabase.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}

/// <summary>Both fakes, one fake S3 and a raw HttpClient that never follows redirects.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private Harness(ArmoryTestDatabase database, TestClock clock, FakeSupabase supabase, FakeIdeaBosco site, FakeS3 s3)
    {
        Database = database;
        Clock = clock;
        Supabase = supabase;
        Site = site;
        S3 = s3;
    }

    public ArmoryTestDatabase Database { get; }
    public TestClock Clock { get; }
    public FakeSupabase Supabase { get; }
    public FakeIdeaBosco Site { get; }
    public FakeS3 S3 { get; }
    public HttpClient Http { get; } = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });

    public static async Task<Harness> StartAsync(ArmoryTestDatabase database)
    {
        var clock = new TestClock();
        var supabase = new FakeSupabase(database) { Clock = clock };
        await supabase.StartAsync();
        var s3 = new FakeS3();
        var site = new FakeIdeaBosco(supabase, database, s3) { Clock = clock };
        await site.StartAsync();
        return new Harness(database, clock, supabase, site, s3);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Site.DisposeAsync();
        await Supabase.DisposeAsync();
        S3.Dispose();
    }

    public static string Email(string who) => $"{who}-{Guid.NewGuid():N}@example.com";

    public static HttpContent JsonBody(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    public Task<HttpResponseMessage> Rpc(string function, string json, string? accessToken, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Supabase.BaseUri, "rest/v1/rpc/" + function)) { Content = JsonBody(json) };
        request.Headers.Add("apikey", apiKey ?? Supabase.AnonKey);
        if (accessToken is not null) request.Headers.Add("Authorization", "Bearer " + accessToken);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> Rpc(string function, JsonObject body, string? accessToken) => Rpc(function, body.ToJsonString(), accessToken);

    public Task<HttpResponseMessage> Refresh(string json, string? apiKey, string grantType = "refresh_token")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Supabase.BaseUri, "auth/v1/token?grant_type=" + grantType)) { Content = JsonBody(json) };
        if (apiKey is not null) request.Headers.Add("apikey", apiKey);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> RefreshToken(string refreshToken) =>
        Refresh(new JsonObject { ["refresh_token"] = refreshToken }.ToJsonString(), Supabase.AnonKey);

    public Task<HttpResponseMessage> BlobUrl(string json, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Site.BaseUri, "api/armory/blob-url")) { Content = JsonBody(json) };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> BlobUrl(Guid project, string hash, long bytes, string method, string accessToken) =>
        BlobUrl(new JsonObject { ["projectId"] = project.ToString(), ["hash"] = hash, ["bytes"] = bytes, ["method"] = method }.ToJsonString(), "Bearer " + accessToken);

    public Task<HttpResponseMessage> Start(string json, string? siteUser, string? clientIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Site.BaseUri, "api/armory/connect/start")) { Content = JsonBody(json) };
        if (siteUser is not null) request.Headers.Add(FakeIdeaBosco.SiteUserHeader, siteUser);
        if (clientIp is not null) request.Headers.Add(FakeIdeaBosco.ClientIpHeader, clientIp);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> Start(int port, string state, string challenge, string device, string siteUser, string? clientIp = null) =>
        Start(new JsonObject { ["port"] = port, ["state"] = state, ["challenge"] = challenge, ["device"] = device }.ToJsonString(), siteUser, clientIp);

    public Task<HttpResponseMessage> Exchange(string json, string? clientIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Site.BaseUri, "api/armory/connect/exchange")) { Content = JsonBody(json) };
        if (clientIp is not null) request.Headers.Add(FakeIdeaBosco.ClientIpHeader, clientIp);
        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> ExchangeCode(string code, string verifier) =>
        Exchange(new JsonObject { ["code"] = code, ["verifier"] = verifier }.ToJsonString());

    /// <summary>Starts a connect for <paramref name="email"/> and returns the one-time code from the 303 Location.</summary>
    public async Task<(string Code, string Verifier)> IssueCodeAsync(string email, string device = "Lab PC 3")
    {
        var verifier = Pkce.NewSecret();
        using var response = await Start(49152, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(verifier), device, email);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        return (query["code"]!, verifier);
    }

    /// <summary>A project whose mentor is a site admin, with one student.</summary>
    public async Task<(Guid Project, string Mentor, string Student)> SeedProjectAsync()
    {
        var mentor = Email("mentor");
        var student = Email("student");
        await using var connection = await Database.OpenAs(mentor, isAdmin: true);
        var project = (Guid)(await Command(connection, "select armory_create_project($1,2027::smallint,$2)", "Robot " + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid()).ExecuteScalarAsync())!;
        await Command(connection, "select armory_add_member($1,$2,'student'::armory_member_role,$3)", project, student, Guid.NewGuid()).ExecuteNonQueryAsync();
        return (project, mentor, student);
    }

    /// <summary>Adds a file with one version (hash) and one side version (sideHash), written directly as the superuser.</summary>
    public async Task AddVersionsAsync(Guid project, string hash, string sideHash)
    {
        await using var connection = await Database.OpenAsync();
        var file = (Guid)(await Command(connection, "insert into armory_files(project_id,name) values($1,$2) returning id", project, "Part " + Guid.NewGuid().ToString("N")[..8] + ".SLDPRT").ExecuteScalarAsync())!;
        await Command(connection, "insert into armory_versions(file_id,object_key,content_sha256,byte_length,author_email) values($1,'k',$2,3,'a@example.com')", file, hash).ExecuteNonQueryAsync();
        await Command(connection, "insert into armory_side_versions(file_id,object_key,content_sha256,byte_length,author_email,reason) values($1,'k',$2,3,'a@example.com','stale parent')", file, sideHash).ExecuteNonQueryAsync();
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }

    public static async Task<JsonNode?> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync());

    public static string RandomHash() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}

internal static class Pkce
{
    public static string NewSecret() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A stand-in for the agent's loopback listener: answers one GET /callback and records it.</summary>
internal sealed class LoopbackCallback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackCallback()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Received = AcceptOnceAsync();
    }

    public int Port { get; }
    public Task<Uri> Received { get; }

    private async Task<Uri> AcceptOnceAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        var target = text.ToString().Split(' ')[1];
        var body = Encoding.UTF8.GetBytes("This computer is connected. You can close this tab.");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
        return new Uri($"http://127.0.0.1:{Port}{target}");
    }

    public void Dispose() => _listener.Stop();
}
