using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Armory.TestSupport;

/// <summary>A session the fake issued. <see cref="ExpiresAt"/> is whole seconds, like Supabase's <c>expires_at</c>.</summary>
public sealed record FakeSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public required string Email { get; init; }
    public required Guid UserId { get; init; }
}

/// <summary>
/// A fake Supabase project on 127.0.0.1: the auth refresh endpoint and PostgREST RPCs, the
/// way docs/agent/CLIENT.md describes them. RPCs run against the real Armory SQL in an
/// <see cref="ArmoryTestDatabase"/>, one transaction per request, under the caller's identity
/// as role <c>authenticated</c> (or <c>anon</c> without a token). Tokens are opaque random
/// strings held in memory; FakeIdeaBosco shares this registry.
/// <para>
/// Refresh is stricter than GoTrue on purpose: GoTrue also accepts a reused refresh token
/// within its reuse interval (10 s by default) or when it is the parent of the active token,
/// and answers with the active token; this fake refuses every reuse and, like GoTrue's reuse
/// detection, revokes the whole token family. A client proven here never depends on that
/// leniency. A refresh whose answer is lost (DropAfterHandling) therefore signs the client out.
/// </para>
/// </summary>
public sealed partial class FakeSupabase : FakeHttpServer
{
    public const string TokenPath = "/auth/v1/token";
    public const string RpcPathPrefix = "/rest/v1/rpc/";

    private readonly ConcurrentDictionary<string, AccessTokenState> _accessTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshTokenState> _refreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> _userIds = new(StringComparer.Ordinal);
    private readonly object _refreshGate = new();
    private long _accessTokenLifetimeTicks = TimeSpan.FromHours(1).Ticks;
    private volatile string _expiredTokenCode = "PGRST303";

    public FakeSupabase(ArmoryTestDatabase database)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ArmoryTestDatabase Database { get; }

    /// <summary>The project's public anon key. Every request must send it as the <c>apikey</c> header.</summary>
    public string AnonKey { get; } = "anon-" + RandomToken(24);

    /// <summary>Emails the test is_admin() stub treats as site admins (sent as armory.test_admins).</summary>
    public EmailSet AdminEmails { get; } = new();

    /// <summary>The lifetime of access tokens minted by a refresh or a connect exchange. Default one hour.</summary>
    public TimeSpan AccessTokenLifetime
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _accessTokenLifetimeTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            Interlocked.Exchange(ref _accessTokenLifetimeTicks, value.Ticks);
        }
    }

    /// <summary>
    /// The PostgREST code for an expired access token: <c>PGRST303</c> (PostgREST 13 and later,
    /// the default) or <c>PGRST301</c> (earlier versions). CLIENT.md allows both.
    /// </summary>
    public string ExpiredTokenCode
    {
        get => _expiredTokenCode;
        set => _expiredTokenCode = value is "PGRST301" or "PGRST303" ? value : throw new ArgumentException("PostgREST answers PGRST301 or PGRST303 for an expired JWT.", nameof(value));
    }

    /// <summary>The base URL a client is given (<c>supabase_url</c>), without a trailing slash.</summary>
    public string SupabaseUrl => BaseUri.ToString().TrimEnd('/');

    /// <summary>When true, every RPC's caller and arguments are kept in <see cref="RpcArguments"/> (arguments only, never a token).</summary>
    public bool RecordRpcArguments { get; set; }

    /// <summary>What each RPC was called with, by whom, while <see cref="RecordRpcArguments"/> was on.</summary>
    public IReadOnlyList<(string Function, string? Email, JsonElement Arguments)> RpcArguments => _rpcArguments.ToArray();
    private readonly ConcurrentQueue<(string Function, string? Email, JsonElement Arguments)> _rpcArguments = new();

    /// <summary>Requests to <c>/rest/v1/rpc/{function}</c> so far.</summary>
    public int RpcCount(string function) => RequestCount(RpcPathPrefix + function);

    /// <summary>Requests to <c>/auth/v1/token</c> so far.</summary>
    public int TokenRequestCount => RequestCount(TokenPath);

    /// <summary>The stable fake Supabase user id for an email.</summary>
    public Guid UserIdFor(string email) => _userIds.GetOrAdd(EmailSet.Normalize(email), static _ => Guid.NewGuid());

    /// <summary>Mints an independent session for <paramref name="email"/>. The lifetime defaults to <see cref="AccessTokenLifetime"/>.</summary>
    public FakeSession IssueSession(string email, TimeSpan? lifetime = null)
    {
        var normalized = EmailSet.Normalize(email);
        if (normalized.Length == 0) throw new ArgumentException("An email is required.", nameof(email));
        return IssueSession(normalized, lifetime ?? AccessTokenLifetime, family: Guid.NewGuid());
    }

    // A refresh continues its token's family (GoTrue's session); a sign-in starts a new one.
    private FakeSession IssueSession(string normalized, TimeSpan lifetime, Guid family)
    {
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(Clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());
        var access = AccessTokenFor(normalized, expiresAt);
        var refresh = "refresh-" + RandomToken(32);
        _accessTokens[access] = new AccessTokenState(normalized, expiresAt, Expired: false);
        lock (_refreshGate) _refreshTokens[refresh] = new RefreshTokenState(normalized, family, Used: false, Revoked: false);
        return new FakeSession(access, refresh, expiresAt) { Email = normalized, UserId = UserIdFor(normalized) };
    }

    // A token shaped like GoTrue's: a JWT whose payload carries sub (the user id), email, role, aud
    // and exp, so a client can read its own auth uid. The signature is random bytes: this fake
    // never verifies one, it looks every token up in its registry (an unknown token is invalid).
    private string AccessTokenFor(string email, DateTimeOffset expiresAt)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = "authenticated", ["exp"] = expiresAt.ToUnixTimeSeconds(), ["sub"] = UserIdFor(email).ToString(), ["email"] = email,
            ["role"] = "authenticated", ["session_id"] = Guid.NewGuid().ToString(),
        }));
        return header + "." + payload + "." + RandomToken(32);
    }

    // The claims PostgREST hands the database for a caller (request.jwt.claims), so auth.uid() works.
    private string ClaimsFor(string email) => new JsonObject
    {
        ["sub"] = UserIdFor(email).ToString(), ["email"] = email, ["role"] = "authenticated", ["aud"] = "authenticated",
    }.ToJsonString();

    private readonly ConcurrentDictionary<string, byte> _hidden = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(string Code, string Message)>> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes an RPC answer 404 PGRST202, as a site without its migration does: every overload, or
    /// only the call with <paramref name="argumentCount"/> named arguments (for example the
    /// eight-argument armory_submit_app_feedback while its five-argument form still answers).
    /// </summary>
    public void HideFunction(string function, int? argumentCount = null) => _hidden[HiddenKey(function, argumentCount)] = 0;

    /// <summary>Undoes <see cref="HideFunction"/>.</summary>
    public void ShowFunction(string function, int? argumentCount = null) => _hidden.TryRemove(HiddenKey(function, argumentCount), out _);

    private static string HiddenKey(string function, int? argumentCount) => argumentCount is { } n ? function + "/" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) : function;

    private bool IsHidden(string function, int argumentCount) => _hidden.ContainsKey(function) || _hidden.ContainsKey(HiddenKey(function, argumentCount));

    /// <summary>
    /// The next <paramref name="times"/> calls to an RPC answer this SQLSTATE without running (as a
    /// call the database rolled back does: a 40P01 deadlock, a 40001 serialization failure), with
    /// PostgREST's status for it.
    /// </summary>
    public void FailRpc(string function, string sqlState, string message, int times = 1)
    {
        var queue = _failures.GetOrAdd(function, static _ => new());
        for (var i = 0; i < times; i++) queue.Enqueue((sqlState, message));
    }

    /// <summary>Makes an access token answer 401 "JWT expired" (<see cref="ExpiredTokenCode"/>) from now on.</summary>
    public void ExpireAccessToken(string accessToken)
    {
        if (!_accessTokens.ContainsKey(accessToken)) throw new ArgumentException("Unknown access token.", nameof(accessToken));
        _accessTokens.AddOrUpdate(accessToken, static _ => throw new InvalidOperationException(), static (_, state) => state with { Expired = true });
    }

    /// <summary>Forgets a refresh token, so using it answers 400 "Invalid Refresh Token: Refresh Token Not Found".</summary>
    public void RevokeRefreshToken(string refreshToken)
    {
        lock (_refreshGate) _refreshTokens.Remove(refreshToken);
    }

    /// <summary>The email of a live (known and unexpired) access token, or null.</summary>
    public string? EmailForAccessToken(string accessToken) => CheckAccessToken(accessToken, out var email) == TokenCheck.Live ? email : null;

    /// <summary>Loses the answer of the <paramref name="callNumber"/>th call to an RPC after its transaction commits.</summary>
    public void DropRpcAcknowledgement(string function, int callNumber) =>
        ScheduleFault(RpcPathPrefix + function, callNumber, FakeFault.DropAfterHandling);

    /// <summary>
    /// PostgREST's HTTP status for a SQLSTATE: its documented table, which agrees with every
    /// code docs/agent/CLIENT.md section 1 names. CLIENT.md's "anything else 400" is the
    /// table's last row; the classes it does not list (for example 40xxx, 55xxx, 57xxx and
    /// XXxxx, which PostgREST answers 500) follow PostgREST.
    /// </summary>
    public static int PostgrestStatusFor(string sqlState, bool anonymous)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        var group = sqlState.Length >= 2 ? sqlState[..2] : sqlState;
        // PostgREST answers a PTxyz SQLSTATE with HTTP xyz (Armory's PT429: 429).
        if (sqlState.Length == 5 && group == "PT" && int.TryParse(sqlState[2..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var custom) && custom is >= 100 and <= 599)
            return custom;
        return sqlState switch
        {
            "42501" => anonymous ? 401 : 403,
            "23505" or "23503" => 409,
            "P0001" => 400,
            "42883" or "42P01" => 404,
            "42P17" => 500,
            "25006" => 405,
            "53400" => 500,
            _ => group switch
            {
                "08" or "53" => 503,
                "0L" or "0P" or "28" => 403,
                "09" or "25" or "2D" or "38" or "39" or "3B" or "40" or "54" or "55" or "57" or "58" or "F0" or "HV" or "P0" or "XX" => 500,
                _ => 400,
            },
        };
    }

    internal enum TokenCheck { Live, Expired, Invalid }

    internal TokenCheck CheckAccessToken(string? token, out string? email)
    {
        email = null;
        if (string.IsNullOrEmpty(token) || !_accessTokens.TryGetValue(token, out var state)) return TokenCheck.Invalid;
        if (state.Expired || Clock.GetUtcNow() >= state.ExpiresAt) return TokenCheck.Expired;
        email = state.Email;
        return TokenCheck.Live;
    }

    private protected override Task<FakeResponse> HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path == TokenPath) return TokenEndpointAsync(context);
        if (path.StartsWith(RpcPathPrefix, StringComparison.Ordinal)) return RpcAsync(context, path[RpcPathPrefix.Length..]);
        if (path.StartsWith(StoragePathPrefix, StringComparison.Ordinal)) return StorageAsync(context, path[StoragePathPrefix.Length..]);
        return Task.FromResult(FakeResponse.Json(404, new JsonObject { ["message"] = "no Route matched with those values" }));
    }

    private bool ApiKeyMatches(HttpRequest request) =>
        request.Headers.TryGetValue("apikey", out var key) && FixedTimeEquals(key.ToString(), AnonKey);

    private static FakeResponse InvalidApiKey() => FakeResponse.Json(401, new JsonObject { ["message"] = "Invalid API key" });

    // POST /auth/v1/token?grant_type=refresh_token (CLIENT.md section 2).
    private async Task<FakeResponse> TokenEndpointAsync(HttpContext context)
    {
        var request = context.Request;
        if (!ApiKeyMatches(request)) return InvalidApiKey();
        if (!HttpMethods.IsPost(request.Method)) return FakeResponse.Json(405, new JsonObject { ["message"] = "method not allowed" });
        if (request.Query["grant_type"].ToString() != "refresh_token")
            return AuthError(request, 400, "validation_failed", "unsupported_grant_type");

        var (_, json) = await ReadJsonAsync(request);
        if (json is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("refresh_token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(tokenElement.GetString()))
            return AuthError(request, 400, "validation_failed", "refresh_token required");

        var presented = tokenElement.GetString()!;
        string email;
        Guid family;
        lock (_refreshGate)
        {
            if (!_refreshTokens.TryGetValue(presented, out var state))
                return AuthError(request, 400, "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found");
            if (state.Used || state.Revoked)
            {
                // GoTrue's reuse detection: the whole family (its session) is revoked.
                foreach (var (token, other) in _refreshTokens.Where(t => t.Value.Family == state.Family).ToList())
                    _refreshTokens[token] = other with { Revoked = true };
                return AuthError(request, 400, "refresh_token_already_used", "Invalid Refresh Token: Already Used");
            }
            _refreshTokens[presented] = state with { Used = true };
            email = state.Email;
            family = state.Family;
        }

        var lifetime = AccessTokenLifetime;
        var session = IssueSession(email, lifetime, family);
        return FakeResponse.Json(200, new JsonObject
        {
            ["access_token"] = session.AccessToken,
            ["token_type"] = "bearer",
            ["expires_in"] = (long)lifetime.TotalSeconds,
            ["expires_at"] = session.ExpiresAt.ToUnixTimeSeconds(),
            ["refresh_token"] = session.RefreshToken,
            ["user"] = new JsonObject
            {
                ["id"] = session.UserId.ToString(),
                ["aud"] = "authenticated",
                ["role"] = "authenticated",
                ["email"] = session.Email,
            },
        });
    }

    // GoTrue's error body: {"code": status, "error_code", "msg"}, or {"code": error_code,
    // "message"} when the request asks for API version 2024-01-01 (as supabase-js does).
    private static FakeResponse AuthError(HttpRequest request, int status, string errorCode, string message) =>
        request.Headers["X-Supabase-Api-Version"].ToString() == "2024-01-01"
            ? FakeResponse.Json(status, new JsonObject { ["code"] = errorCode, ["message"] = message })
            : FakeResponse.Json(status, new JsonObject { ["code"] = status, ["error_code"] = errorCode, ["msg"] = message });

    [GeneratedRegex(@"^armory_[a-z0-9_]+\z")]
    private static partial Regex FunctionNamePattern();

    [GeneratedRegex(@"^p_[a-z0-9_]+\z")]
    private static partial Regex ArgumentNamePattern();

    // POST /rest/v1/rpc/{function} (CLIENT.md section 1).
    private async Task<FakeResponse> RpcAsync(HttpContext context, string function)
    {
        var request = context.Request;
        if (!ApiKeyMatches(request)) return InvalidApiKey();

        string? email = null;
        var bearer = BearerToken(request);
        if (bearer is not null && !FixedTimeEquals(bearer, AnonKey))
        {
            var check = CheckAccessToken(bearer, out email);
            if (check == TokenCheck.Expired) return JwtError(ExpiredTokenCode, "JWT expired");
            if (check != TokenCheck.Live) return JwtError("PGRST301", "JWT invalid");
        }
        var anonymous = email is null;

        if (!HttpMethods.IsPost(request.Method)) return PostgrestError(405, "PGRST101", "This fake accepts only POST for RPC", null, null);
        // PostgREST reads a JSON body only when Content-Type says so (an absent header means JSON).
        if (request.ContentType is { Length: > 0 } contentType
            && !string.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            return PostgrestError(415, "PGRST107", $"The request's Content-Type is not acceptable: {contentType}", null, null);

        var (empty, json) = await ReadJsonAsync(request);
        JsonElement body;
        if (empty) body = JsonDocument.Parse("{}").RootElement.Clone();
        else if (json is { ValueKind: JsonValueKind.Object } parsed) body = parsed;
        else return PostgrestError(400, "PGRST102", "Empty or invalid json", null, null);

        if (RecordRpcArguments) _rpcArguments.Enqueue((function, email, body.Clone()));
        var arguments = body.EnumerateObject().ToList();
        var names = arguments.Select(a => a.Name).ToList();
        if (!FunctionNamePattern().IsMatch(function) || names.Any(n => !ArgumentNamePattern().IsMatch(n)) || IsHidden(function, names.Count))
            return FunctionNotFound(function, names);
        if (_failures.TryGetValue(function, out var failures) && failures.TryDequeue(out var failure))
            return PostgrestError(PostgrestStatusFor(failure.Code, anonymous), failure.Code, failure.Message, null, null);

        try
        {
            var candidates = await LookupFunctionAsync(function);
            var matches = candidates.Where(c => c.Accepts(names)).ToList();
            if (matches.Count == 0) return FunctionNotFound(function, names);
            if (matches.Count > 1)
                return PostgrestError(300, "PGRST203", $"Could not choose the best candidate function between overloads of public.{function}", null,
                    "Try renaming the parameters or the function itself in the database so function overloading can be resolved");

            var target = matches[0];
            var (sql, parameters) = target.BuildCall(arguments);
            var result = await Database.RunAsCallerAsync(email, AdminEmails, async (connection, cancellationToken) =>
            {
                if (email is not null)
                {
                    // PostgREST hands the database the caller's JWT claims (auth.uid() reads sub).
                    await using var claims = new NpgsqlCommand("select set_config('request.jwt.claims', $1, true)", connection);
                    claims.Parameters.Add(new NpgsqlParameter { Value = ClaimsFor(email) });
                    await claims.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddRange(parameters.ToArray());
                var value = await command.ExecuteScalarAsync(cancellationToken);
                return value is string text ? text : null;
            });
            // Realtime delivers what this call wrote to the change feed (FakeSupabase.Realtime.cs).
            await PushRealtimeChangesAsync();
            if (target.ReturnsVoid) return FakeResponse.Empty(204);
            return FakeResponse.RawJson(200, result ?? "null");
        }
        catch (PostgresException e)
        {
            return PostgrestError(PostgrestStatusFor(e.SqlState, anonymous), e.SqlState, e.MessageText, e.Detail, e.Hint);
        }
        catch (NpgsqlException e)
        {
            return PostgrestError(503, "PGRST000", "Database connection error. Retrying the connection.", e.Message, null);
        }
    }

    private static FakeResponse PostgrestError(int status, string code, string message, string? details, string? hint) =>
        FakeResponse.Json(status, new JsonObject { ["code"] = code, ["message"] = message, ["details"] = details, ["hint"] = hint });

    private static FakeResponse JwtError(string code, string message)
    {
        var response = PostgrestError(401, code, message, null, null);
        response.Headers["WWW-Authenticate"] = $"Bearer error=\"invalid_token\", error_description=\"{message}\"";
        return response;
    }

    private static FakeResponse FunctionNotFound(string function, IEnumerable<string> names)
    {
        var list = string.Join(", ", names.Order(StringComparer.Ordinal));
        return PostgrestError(404, "PGRST202",
            $"Could not find the function public.{function}({list}) in the schema cache",
            $"Searched for the function public.{function} with parameters {list} or with a single unnamed json/jsonb parameter, but no matches were found in the schema cache.",
            null);
    }

    private async Task<List<RpcFunction>> LookupFunctionAsync(string function)
    {
        const string sql = """
            select p.oid::int8,
                   p.proretset,
                   p.prorettype = 'pg_catalog.void'::pg_catalog.regtype,
                   (t.typtype = 'c' or p.prorettype = 'pg_catalog.record'::pg_catalog.regtype),
                   p.pronargdefaults::int4,
                   coalesce((select array_agg(a.name order by a.ord)
                             from unnest(coalesce(p.proallargtypes, p.proargtypes::pg_catalog.oid[]), p.proargnames, p.proargmodes)
                                  with ordinality as a(type, name, mode, ord)
                             where coalesce(a.mode, 'i') in ('i', 'b', 'v')), '{}'::text[]),
                   coalesce((select array_agg(pg_catalog.format_type(a.type, null) order by a.ord)
                             from unnest(coalesce(p.proallargtypes, p.proargtypes::pg_catalog.oid[]), p.proargnames, p.proargmodes)
                                  with ordinality as a(type, name, mode, ord)
                             where coalesce(a.mode, 'i') in ('i', 'b', 'v')), '{}'::text[])
            from pg_catalog.pg_proc p
            join pg_catalog.pg_namespace n on n.oid = p.pronamespace
            join pg_catalog.pg_type t on t.oid = p.prorettype
            where n.nspname = 'public' and p.proname = $1 and p.prokind = 'f'
            """;
        await using var connection = await Database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = function });
        await using var reader = await command.ExecuteReaderAsync();
        var functions = new List<RpcFunction>();
        while (await reader.ReadAsync())
        {
            var argumentNames = reader.GetFieldValue<string?[]>(5);
            var argumentTypes = reader.GetFieldValue<string[]>(6);
            functions.Add(new RpcFunction(
                function,
                ReturnsSet: reader.GetBoolean(1),
                ReturnsVoid: reader.GetBoolean(2),
                ReturnsRow: reader.GetBoolean(3),
                DefaultCount: reader.GetInt32(4),
                Inputs: argumentNames.Zip(argumentTypes, (name, type) => new RpcArgument(name, type)).ToArray()));
        }
        return functions;
    }

    private sealed record RpcArgument(string? Name, string Type);

    private sealed record RpcFunction(string Name, bool ReturnsSet, bool ReturnsVoid, bool ReturnsRow, int DefaultCount, RpcArgument[] Inputs)
    {
        public bool Accepts(IReadOnlyCollection<string> names)
        {
            if (names.Any(n => Inputs.All(i => i.Name != n))) return false;
            return Inputs.Take(Inputs.Length - DefaultCount).All(i => i.Name is not null && names.Contains(i.Name));
        }

        // Named arguments, each value a parameter cast to the declared type. The function,
        // argument and type names all come from pg_proc after the name patterns matched.
        public (string Sql, List<NpgsqlParameter> Parameters) BuildCall(IEnumerable<JsonProperty> arguments)
        {
            var parameters = new List<NpgsqlParameter>();
            var parts = new List<string>();
            foreach (var argument in arguments)
            {
                var input = Inputs.First(i => i.Name == argument.Name);
                var index = parameters.Count + 1;
                if (input.Type.EndsWith("[]", StringComparison.Ordinal) && argument.Value.ValueKind == JsonValueKind.Array)
                {
                    parameters.Add(new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                        Value = argument.Value.EnumerateArray().Select(e => ParameterText(e, json: false)).ToArray(),
                    });
                    parts.Add($"{argument.Name} => ${index}::text[]::{input.Type}");
                }
                else
                {
                    var json = input.Type is "json" or "jsonb";
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)ParameterText(argument.Value, json) ?? DBNull.Value });
                    parts.Add($"{argument.Name} => ${index}::{input.Type}");
                }
            }
            var call = $"public.{Name}({string.Join(", ", parts)})";
            var sql = ReturnsSet ? $"select coalesce(json_agg(r), '[]'::json)::text from {call} r"
                : ReturnsVoid ? $"select null::text from {call} r"
                : ReturnsRow ? $"select row_to_json(r)::text from {call} r"
                : $"select to_json({call})::text";
            return (sql, parameters);
        }

        // A JSON null is SQL NULL; a string is its text (or its JSON text for a json or jsonb
        // argument); anything else is its raw JSON text, which the cast parses.
        private static string? ParameterText(JsonElement value, bool json) => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when !json => value.GetString(),
            _ => value.GetRawText(),
        };
    }

    private sealed record AccessTokenState(string Email, DateTimeOffset ExpiresAt, bool Expired);

    private sealed record RefreshTokenState(string Email, Guid Family, bool Used, bool Revoked);
}
