using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Armory.Storage;
using Armory.Storage.Tests;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Armory.TestSupport;

/// <summary>Connect rate limits: at most N requests per sliding window, per key.</summary>
public sealed class ConnectRateLimits
{
    private int _startPerIp = 10, _startPerUser = 10, _exchangePerIp = 10;
    private long _windowTicks = TimeSpan.FromMinutes(1).Ticks;

    public int StartPerIp { get => Volatile.Read(ref _startPerIp); set => Volatile.Write(ref _startPerIp, value); }
    public int StartPerUser { get => Volatile.Read(ref _startPerUser); set => Volatile.Write(ref _startPerUser, value); }
    public int ExchangePerIp { get => Volatile.Read(ref _exchangePerIp); set => Volatile.Write(ref _exchangePerIp, value); }
    public TimeSpan Window { get => TimeSpan.FromTicks(Interlocked.Read(ref _windowTicks)); set => Interlocked.Exchange(ref _windowTicks, value.Ticks); }
}

/// <summary>
/// A fake ideabosco.com on its own 127.0.0.1 port, implementing docs/agent/CONTRACT.md
/// sections 2 (blob URLs) and 3 (connecting a computer). Access tokens are checked against
/// the shared <see cref="FakeSupabase"/> registry, membership and device registration run
/// against the real Armory SQL, and blob URLs point at <see cref="S3Host"/>, which a
/// <see cref="FakeNetworkHandler"/> routes to the shared <see cref="FakeS3"/>.
/// <para>
/// Checks run in this order. blob-url: 401, 400 (including a PUT over 2 GiB), 403 (not a
/// member, then for GET a hash outside the project), 503, 200. connect/start: 401, 429 (per
/// IP, then per user), 400, 303. connect/exchange: 429 (per IP), 400, then 401 unknown or
/// used code, 410 expired code, 401 wrong verifier (which also consumes the code), 200.
/// </para>
/// <para>
/// Test-only: the site's own Google sign-in is simulated by the request header
/// <c>X-Test-Site-User: &lt;email&gt;</c>, and <c>X-Test-Client-Ip</c> overrides the client
/// address the rate limits see. The real site has neither.
/// </para>
/// </summary>
public sealed partial class FakeIdeaBosco : FakeHttpServer
{
    public const string BlobUrlPath = "/api/armory/blob-url";
    public const string ConnectPagePath = "/armory/connect";
    public const string ConnectStartPath = "/api/armory/connect/start";
    public const string ConnectExchangePath = "/api/armory/connect/exchange";
    public const string SiteUserHeader = "X-Test-Site-User";
    public const string ClientIpHeader = "X-Test-Client-Ip";
    public const string S3Host = FakeNetworkHandler.S3Host;
    public const long MaxPutBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan BlobUrlLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, ConnectCode> _codes = new(StringComparer.Ordinal);
    private readonly object _codesGate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rateWindows = new(StringComparer.Ordinal);
    private readonly object _rateGate = new();
    private volatile bool _storageConfigured = true;

    public FakeIdeaBosco(FakeSupabase supabase, ArmoryTestDatabase database, FakeS3 s3)
    {
        Supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
        Database = database ?? throw new ArgumentNullException(nameof(database));
        S3 = s3 ?? throw new ArgumentNullException(nameof(s3));
    }

    public FakeSupabase Supabase { get; }
    public ArmoryTestDatabase Database { get; }
    public FakeS3 S3 { get; }

    /// <summary>When false, blob-url answers 503 {"error":"armory_storage_not_configured"}. Default true.</summary>
    public bool StorageConfigured { get => _storageConfigured; set => _storageConfigured = value; }

    public ConnectRateLimits RateLimits { get; } = new();

    /// <summary>Extra headers blob-url returns for the client to send with the S3 request. Empty by default.</summary>
    public IDictionary<string, string> BlobUrlHeaders { get; } = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 (lowercase hex) of every connect code issued; the codes themselves are never kept.</summary>
    public IReadOnlyCollection<string> StoredCodeHashes { get { lock (_codesGate) return [.. _codes.Keys]; } }

    /// <summary>The connect page URL the agent opens in the browser.</summary>
    public Uri ConnectUrl(int port, string state, string challenge, string device) =>
        new(BaseUri, $"{ConnectPagePath.TrimStart('/')}?port={port}&state={Uri.EscapeDataString(state)}&challenge={Uri.EscapeDataString(challenge)}&device={Uri.EscapeDataString(device)}");

    /// <summary>The object key and presigned URL the fake hands out for a hash.</summary>
    public static Uri BlobUrlFor(string hash, string method) =>
        new($"https://{S3Host}/{ContentObjectKey.FromHash(hash)}?op={method.ToLowerInvariant()}");

    private protected override Task<FakeResponse> HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;
        return path switch
        {
            BlobUrlPath when HttpMethods.IsPost(method) => BlobUrlAsync(context),
            ConnectPagePath when HttpMethods.IsGet(method) => Task.FromResult(ConnectPage(context)),
            ConnectStartPath when HttpMethods.IsPost(method) => ConnectStartAsync(context),
            ConnectExchangePath when HttpMethods.IsPost(method) => ConnectExchangeAsync(context),
            BlobUrlPath or ConnectPagePath or ConnectStartPath or ConnectExchangePath => Task.FromResult(Error(405, "method_not_allowed")),
            _ => Task.FromResult(Error(404, "not_found")),
        };
    }

    private static FakeResponse Error(int status, string error, string? message = null)
    {
        var body = new JsonObject { ["error"] = error };
        if (message is not null) body["message"] = message;
        return FakeResponse.Json(status, body);
    }

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex Base64UrlPattern();

    // ---- Contract section 2: POST /api/armory/blob-url ----

    private async Task<FakeResponse> BlobUrlAsync(HttpContext context)
    {
        if (Supabase.CheckAccessToken(BearerToken(context.Request), out var email) != FakeSupabase.TokenCheck.Live || email is null)
            return Error(401, "unauthorized", "A valid access token is required.");

        var (_, json) = await ReadJsonAsync(context.Request);
        if (json is not { ValueKind: JsonValueKind.Object } body) return Error(400, "bad_request", "The body must be a JSON object.");
        if (!body.TryGetProperty("projectId", out var projectElement) || projectElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(projectElement.GetString(), "D", out var projectId))
            return Error(400, "bad_request", "projectId must be a uuid.");
        if (!body.TryGetProperty("hash", out var hashElement) || hashElement.ValueKind != JsonValueKind.String
            || !HashPattern().IsMatch(hashElement.GetString()!))
            return Error(400, "bad_request", "hash must be 64 lowercase hex characters.");
        if (!body.TryGetProperty("bytes", out var bytesElement) || bytesElement.ValueKind != JsonValueKind.Number
            || !bytesElement.TryGetInt64(out var bytes) || bytes < 0)
            return Error(400, "bad_request", "bytes must be a non-negative integer.");
        if (!body.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String
            || methodElement.GetString() is not ("PUT" or "GET"))
            return Error(400, "bad_request", "method must be PUT or GET.");
        var hash = hashElement.GetString()!;
        var method = methodElement.GetString()!;
        if (method == "PUT" && bytes > MaxPutBytes) return Error(400, "too_large", "A PUT is allowed only for at most 2 GiB.");

        var member = await Database.RunAsCallerAsync(email, Supabase.AdminEmails, async (connection, cancellationToken) =>
        {
            await using var command = new NpgsqlCommand("select public.armory_is_member($1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = projectId });
            return await command.ExecuteScalarAsync(cancellationToken) is true;
        });
        if (!member) return Error(403, "forbidden", "The caller is not a member of this project.");

        if (method == "GET" && !await HashInProjectAsync(projectId, hash))
            return Error(403, "forbidden", "That hash is not part of this project.");

        if (!StorageConfigured) return FakeResponse.Json(503, new JsonObject { ["error"] = "armory_storage_not_configured" });

        var key = ContentObjectKey.FromHash(hash);
        var headers = new JsonObject();
        foreach (var (name, value) in BlobUrlHeaders) headers[name] = value;
        return FakeResponse.Json(200, new JsonObject
        {
            ["url"] = BlobUrlFor(hash, method).ToString(),
            ["headers"] = headers,
            ["expiresAt"] = IsoTime(Clock.GetUtcNow().Add(BlobUrlLifetime)),
            ["exists"] = S3.Objects.ContainsKey(key),
        });
    }

    private async Task<bool> HashInProjectAsync(Guid projectId, string hash)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select exists(select 1 from public.armory_versions v join public.armory_files f on f.id = v.file_id
                          where f.project_id = $1 and v.content_sha256 = $2)
                or exists(select 1 from public.armory_side_versions s join public.armory_files f on f.id = s.file_id
                          where f.project_id = $1 and s.content_sha256 = $2)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = projectId });
        command.Parameters.Add(new NpgsqlParameter { Value = hash });
        return await command.ExecuteScalarAsync() is true;
    }

    // JavaScript's Date.toISOString() form, which the real (TypeScript) site sends.
    private static string IsoTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ---- Contract section 3: connecting a computer ----

    private static string? SiteUser(HttpRequest request)
    {
        var user = request.Headers[SiteUserHeader].ToString().Trim();
        return user.Length == 0 ? null : EmailSet.Normalize(user);
    }

    private static string ClientIp(HttpContext context)
    {
        var overridden = context.Request.Headers[ClientIpHeader].ToString().Trim();
        return overridden.Length > 0 ? overridden : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed record ConnectRequest(int Port, string State, string Challenge, string Device);

    // The rules every connect input must meet. Null means valid; otherwise the reason.
    private static string? Validate(object? port, string? state, string? challenge, string? device, out ConnectRequest? request)
    {
        request = null;
        if (port is not int p || p is < 1024 or > 65535) return "port must be an integer from 1024 to 65535.";
        if (string.IsNullOrEmpty(state) || state.Length > 256 || !Base64UrlPattern().IsMatch(state)) return "state must be 1 to 256 base64url characters.";
        if (challenge is null || challenge.Length != 43 || !Base64UrlPattern().IsMatch(challenge)) return "challenge must be 43 base64url characters.";
        var trimmed = device?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 100) return "device must be a name of 1 to 100 characters.";
        request = new ConnectRequest(p, state, challenge, trimmed);
        return null;
    }

    private static object? ParsePort(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;

    // GET /armory/connect?port&state&challenge&device: the confirmation page (no scripts).
    private FakeResponse ConnectPage(HttpContext context)
    {
        var email = SiteUser(context.Request);
        if (email is null)
            return FakeResponse.Html(401, Page("Sign in required", "Sign in to ideabosco.com to connect a computer to Armory."));
        var query = context.Request.Query;
        var problem = Validate(ParsePort(query["port"].ToString()), query["state"].ToString(), query["challenge"].ToString(), query["device"].ToString(), out var request);
        if (problem is not null) return FakeResponse.Html(400, Page("This connect link is not valid", problem));
        return FakeResponse.Html(200, Page("Connect a computer", $"Connect {request!.Device} to Armory as {email}?"));
    }

    private static string Page(string title, string message) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head>" +
        $"<body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

    // POST /api/armory/connect/start {port, state, challenge, device}
    private async Task<FakeResponse> ConnectStartAsync(HttpContext context)
    {
        var email = SiteUser(context.Request);
        if (email is null) return Error(401, "unauthorized", "Sign in to ideabosco.com first.");
        var now = Clock.GetUtcNow();
        if (!TryCount("start-ip:" + ClientIp(context), RateLimits.StartPerIp, now)
            || !TryCount("start-user:" + email, RateLimits.StartPerUser, now))
            return Error(429, "rate_limited", "Too many connect attempts. Wait a minute and try again.");

        var (_, json) = await ReadJsonAsync(context.Request);
        if (json is not { ValueKind: JsonValueKind.Object } body) return Error(400, "bad_request", "The body must be a JSON object.");
        object? port = null;
        if (body.TryGetProperty("port", out var portElement))
        {
            if (portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out var number)) port = number;
            else if (portElement.ValueKind == JsonValueKind.String) port = ParsePort(portElement.GetString());
        }
        var problem = Validate(port, StringProperty(body, "state"), StringProperty(body, "challenge"), StringProperty(body, "device"), out var request);
        if (problem is not null) return Error(400, "bad_request", problem);

        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_codesGate)
        {
            _codes[Sha256Hex(code)] = new ConnectCode(email, request!.Challenge, request.State, request.Device, now.Add(CodeLifetime));
        }
        // Only 127.0.0.1 is ever the target host.
        return FakeResponse.Redirect($"http://127.0.0.1:{request.Port}/callback?state={Uri.EscapeDataString(request.State)}&code={Uri.EscapeDataString(code)}");
    }

    private static string? StringProperty(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // POST /api/armory/connect/exchange {code, verifier}
    private async Task<FakeResponse> ConnectExchangeAsync(HttpContext context)
    {
        var now = Clock.GetUtcNow();
        if (!TryCount("exchange-ip:" + ClientIp(context), RateLimits.ExchangePerIp, now))
            return Error(429, "rate_limited", "Too many connect attempts. Wait a minute and try again.");

        var (_, json) = await ReadJsonAsync(context.Request);
        if (json is not { ValueKind: JsonValueKind.Object } body
            || StringProperty(body, "code") is not { } code
            || StringProperty(body, "verifier") is not { } verifier)
            return Error(400, "bad_request", "code and verifier are required strings.");

        ConnectCode grant;
        lock (_codesGate)
        {
            var key = Sha256Hex(code);
            if (!_codes.TryGetValue(key, out var stored)) return Error(401, "invalid_code", "That connect code is not valid.");
            if (stored.Used) return Error(401, "invalid_code", "That connect code was already used.");
            if (now >= stored.ExpiresAt) return Error(410, "code_expired", "That connect code expired. Start again from the app.");
            // A wrong verifier consumes the code too, so it cannot be brute forced.
            _codes[key] = stored with { Used = true };
            if (!VerifierMatches(verifier, stored.Challenge)) return Error(401, "invalid_code", "The verifier does not match.");
            grant = stored;
        }

        var deviceId = await Database.RunAsCallerAsync(grant.Email, Supabase.AdminEmails, async (connection, cancellationToken) =>
        {
            await using var command = new NpgsqlCommand("select public.armory_register_device(p_name => $1, p_operation => $2)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = grant.Device });
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        });
        var session = Supabase.IssueSession(grant.Email);
        return FakeResponse.Json(200, new JsonObject
        {
            ["access_token"] = session.AccessToken,
            ["refresh_token"] = session.RefreshToken,
            ["expires_at"] = session.ExpiresAt.ToUnixTimeSeconds(),
            ["email"] = session.Email,
            ["device_id"] = deviceId.ToString(),
            ["supabase_url"] = Supabase.SupabaseUrl,
            ["anon_key"] = Supabase.AnonKey,
        });
    }

    /// <summary>RFC 7636 S256: base64url(SHA-256(ASCII bytes of the verifier)).</summary>
    public static string ChallengeFor(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static bool VerifierMatches(string verifier, string challenge) =>
        verifier.All(char.IsAscii) && FixedTimeEquals(ChallengeFor(verifier), challenge);

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // A sliding window per key; a refused request is not counted.
    private bool TryCount(string key, int limit, DateTimeOffset now)
    {
        lock (_rateGate)
        {
            if (!_rateWindows.TryGetValue(key, out var window)) _rateWindows[key] = window = new Queue<DateTimeOffset>();
            var start = now - RateLimits.Window;
            while (window.Count > 0 && window.Peek() <= start) window.Dequeue();
            if (window.Count >= limit) return false;
            window.Enqueue(now);
            return true;
        }
    }

    private sealed record ConnectCode(string Email, string Challenge, string State, string Device, DateTimeOffset ExpiresAt, bool Used = false);
}
