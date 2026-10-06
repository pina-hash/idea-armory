using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.TestSupport.Tests;

// The fake Supabase must answer exactly like docs/agent/CLIENT.md sections 1 and 2.
public sealed class FakeSupabaseTests(TestDatabaseFixture fixture) : IClassFixture<TestDatabaseFixture>
{
    private static async Task AssertPostgrestError(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        Assert.Equal(status, response.StatusCode);
        var body = (await Harness.Json(response))!.AsObject();
        Assert.Equal(["code", "details", "hint", "message"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(code, (string?)body["code"]);
        if (message is not null) Assert.Equal(message, (string?)body["message"]);
    }

    [PostgresFact]
    public async Task RefreshRotatesTheRefreshTokenAndAnswersLikeSupabase()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        var first = h.Supabase.IssueSession(email.ToUpperInvariant(), TimeSpan.FromMinutes(10));
        Assert.Equal(email, first.Email);
        Assert.Equal(h.Clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(), first.ExpiresAt.ToUnixTimeSeconds());

        using var response = await h.RefreshToken(first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await Harness.Json(response))!;
        var access = (string)body["access_token"]!;
        var refresh = (string)body["refresh_token"]!;
        Assert.NotEqual(first.AccessToken, access);
        Assert.NotEqual(first.RefreshToken, refresh);
        Assert.Equal("bearer", (string?)body["token_type"]);
        Assert.Equal(3600L, (long)body["expires_in"]!);
        Assert.Equal(h.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds(), (long)body["expires_at"]!);
        Assert.Equal(email, (string?)body["user"]!["email"]);
        Assert.Equal(h.Supabase.UserIdFor(email).ToString(), (string?)body["user"]!["id"]);

        using var rpc = await h.Rpc("armory_my_projects", "{}", access);
        Assert.Equal(HttpStatusCode.OK, rpc.StatusCode);
        Assert.Equal("[]", await rpc.Content.ReadAsStringAsync());

        using var next = await h.RefreshToken(refresh); // the rotated token continues the chain
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, h.Supabase.TokenRequestCount);
    }

    // GoTrue's body is {"code": 400, "error_code", "msg"}; it has no OAuth "error" field, so
    // a client keyed on "invalid_grant" would never see one from real Supabase either.
    private static async Task AssertGoTrueError(HttpResponseMessage response, string errorCode, string message)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await Harness.Json(response))!.AsObject();
        Assert.Equal(["code", "error_code", "msg"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(400, (int)body["code"]!);
        Assert.Equal(errorCode, (string?)body["error_code"]);
        Assert.Equal(message, (string?)body["msg"]);
    }

    // Guarded name kept: "invalid grant" is CLIENT.md's word for these refusals.
    [PostgresFact]
    public async Task ReusedUnknownOrRevokedRefreshTokensAre400InvalidGrant()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var session = h.Supabase.IssueSession(Harness.Email("student"));
        (await h.RefreshToken(session.RefreshToken)).Dispose();

        await AssertGoTrueError(await h.RefreshToken(session.RefreshToken), "refresh_token_already_used", "Invalid Refresh Token: Already Used");
        await AssertGoTrueError(await h.RefreshToken("refresh-never-issued"), "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found");

        var revoked = h.Supabase.IssueSession(Harness.Email("student"));
        h.Supabase.RevokeRefreshToken(revoked.RefreshToken);
        await AssertGoTrueError(await h.RefreshToken(revoked.RefreshToken), "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found");

        await AssertGoTrueError(await h.Refresh("{}", h.Supabase.AnonKey), "validation_failed", "refresh_token required");
        await AssertGoTrueError(await h.Refresh("{}", h.Supabase.AnonKey, grantType: "password"), "validation_failed", "unsupported_grant_type");

        // supabase-js asks for API version 2024-01-01, which answers {"code": error_code, "message"}.
        using var versioned = new HttpRequestMessage(HttpMethod.Post, new Uri(h.Supabase.BaseUri, "auth/v1/token?grant_type=refresh_token"))
        {
            Content = Harness.JsonBody("""{"refresh_token":"refresh-never-issued"}"""),
        };
        versioned.Headers.Add("apikey", h.Supabase.AnonKey);
        versioned.Headers.Add("X-Supabase-Api-Version", "2024-01-01");
        using var answer = await h.Http.SendAsync(versioned);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("""{"code":"refresh_token_not_found","message":"Invalid Refresh Token: Refresh Token Not Found"}""", await answer.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task ReusingARotatedRefreshTokenRevokesTheWholeFamily()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        var first = h.Supabase.IssueSession(email);
        var other = h.Supabase.IssueSession(email); // another computer: its own family
        using var rotated = await h.RefreshToken(first.RefreshToken);
        var second = (string)(await Harness.Json(rotated))!["refresh_token"]!;

        // A stale copy of the first token comes back (say, a second process that never saw
        // the rotation): GoTrue's reuse detection revokes every token of that session.
        await AssertGoTrueError(await h.RefreshToken(first.RefreshToken), "refresh_token_already_used", "Invalid Refresh Token: Already Used");
        await AssertGoTrueError(await h.RefreshToken(second), "refresh_token_already_used", "Invalid Refresh Token: Already Used");

        using var unaffected = await h.RefreshToken(other.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentRefreshesOfOneTokenSucceedExactlyOnce()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var session = h.Supabase.IssueSession(Harness.Email("student"));
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.RefreshToken(session.RefreshToken)));
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, answers.Count(a => a.StatusCode == HttpStatusCode.BadRequest));
        foreach (var answer in answers) answer.Dispose();
    }

    [PostgresFact]
    public async Task TokenEndpointRequiresTheAnonKey()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var session = h.Supabase.IssueSession(Harness.Email("student"));
        var json = new JsonObject { ["refresh_token"] = session.RefreshToken }.ToJsonString();
        foreach (var key in new[] { "wrong-key", null })
        {
            using var refused = await h.Refresh(json, key);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("""{"message":"Invalid API key"}""", await refused.Content.ReadAsStringAsync());
        }
        using var accepted = await h.RefreshToken(session.RefreshToken); // a refused request never consumed the token
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [PostgresFact]
    public async Task RpcScalarsAreBareJsonValues()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var admin = Harness.Email("admin");
        h.Supabase.AdminEmails.Add(admin);
        var token = h.Supabase.IssueSession(admin).AccessToken;

        var op = Guid.NewGuid();
        using var created = await h.Rpc("armory_create_project", new JsonObject { ["p_name"] = "Robot " + op.ToString("N")[..8], ["p_season"] = 2027, ["p_operation"] = op.ToString() }, token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.StartsWith("application/json", created.Content.Headers.ContentType!.ToString());
        var uuid = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.String, uuid.ValueKind);
        var project = Guid.Parse(uuid.GetString()!);

        using var added = await h.Rpc("armory_add_member", new JsonObject { ["p_project"] = project.ToString(), ["p_email"] = Harness.Email("student"), ["p_role"] = "student", ["p_operation"] = Guid.NewGuid().ToString() }, token);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal("true", await added.Content.ReadAsStringAsync());

        using var member = await h.Rpc("armory_is_member", new JsonObject { ["p_project"] = Guid.NewGuid().ToString() }, token);
        Assert.Equal("false", await member.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task RpcJsonbIsTheValueAndTablesAreArraysOfObjects()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, mentor, _) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(mentor).AccessToken;

        using var mine = await h.Rpc("armory_my_projects", "{}", token);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var projects = (await Harness.Json(mine))!.AsArray();
        Assert.Equal(project.ToString(), (string?)Assert.Single(projects)!["id"]);
        Assert.Equal("mentor", (string?)projects[0]!["role"]);

        using var allocated = await h.Rpc("armory_allocate_part_number", new JsonObject { ["p_project"] = project.ToString(), ["p_subsystem"] = 1, ["p_season"] = 2027, ["p_operation"] = Guid.NewGuid().ToString() }, token);
        Assert.Equal(HttpStatusCode.OK, allocated.StatusCode);
        Assert.Equal("""[{"part_number":"5669-27-0100","subsystem_full":false}]""", await allocated.Content.ReadAsStringAsync());

        using var changes = await h.Rpc("armory_list_changes", new JsonObject { ["p_project"] = project.ToString() }, token); // p_after has a default
        Assert.Equal(HttpStatusCode.OK, changes.StatusCode);
        var rows = (await Harness.Json(changes))!.AsArray();
        Assert.Equal(["project_created", "member_added"], rows.Select(r => (string?)r!["kind"]));
        Assert.Equal(["cursor", "project_id", "kind", "entity_id", "payload", "created_at"], rows[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(JsonValueKind.Number, rows[0]!["cursor"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Object, rows[0]!["payload"]!.GetValueKind());

        using var after = await h.Rpc("armory_list_changes", new JsonObject { ["p_project"] = project.ToString(), ["p_after"] = 999999999 }, token);
        Assert.Equal("[]", await after.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task RpcErrorsUsePostgrestShapeAndStatus()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, mentor, student) = await h.SeedProjectAsync();
        var studentToken = h.Supabase.IssueSession(student).AccessToken;
        var mentorToken = h.Supabase.IssueSession(mentor).AccessToken;

        using (var refused = await h.Rpc("armory_create_project", new JsonObject { ["p_name"] = "Nope", ["p_season"] = 2027, ["p_operation"] = Guid.NewGuid().ToString() }, studentToken))
        {
            await AssertPostgrestError(refused, HttpStatusCode.Forbidden, "42501", "only a site admin may create an Armory project");
            var body = (await Harness.Json(refused))!;
            Assert.Null(body["details"]);
            Assert.Null(body["hint"]);
        }

        using (var anonymous = await h.Rpc("armory_my_projects", "{}", accessToken: null))
            await AssertPostgrestError(anonymous, HttpStatusCode.Unauthorized, "42501");
        using (var anonKeyAsBearer = await h.Rpc("armory_my_projects", "{}", accessToken: h.Supabase.AnonKey))
            await AssertPostgrestError(anonKeyAsBearer, HttpStatusCode.Unauthorized, "42501");
        // An internal helper no client role holds (0231's grants, mirrored by server/sql/005_v2.sql section 9).
        using (var revoked = await h.Rpc("armory_add_change", new JsonObject { ["p_project"] = project.ToString(), ["p_kind"] = "forged", ["p_entity"] = project.ToString(), ["p_payload"] = new JsonObject() }, studentToken))
            await AssertPostgrestError(revoked, HttpStatusCode.Forbidden, "42501");
        // RLS policies name armory_current_email, so authenticated holds it, as in production, and it answers only the caller.
        using (var email = await h.Rpc("armory_current_email", "{}", studentToken))
        {
            Assert.Equal(HttpStatusCode.OK, email.StatusCode);
            Assert.Equal(student, (string?)await Harness.Json(email));
        }

        using var device = await h.Rpc("armory_register_device", new JsonObject { ["p_name"] = "Lab PC", ["p_operation"] = Guid.NewGuid().ToString() }, studentToken);
        var deviceId = (string)(await Harness.Json(device))!;
        JsonObject CreateFile(string folder) => new() { ["p_project"] = project.ToString(), ["p_folder"] = folder, ["p_name"] = "Arm.SLDPRT", ["p_device"] = deviceId, ["p_operation"] = Guid.NewGuid().ToString() };
        using (var first = await h.Rpc("armory_create_file", CreateFile("Parts"), studentToken)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var taken = await h.Rpc("armory_create_file", CreateFile("Other"), studentToken))
        {
            await AssertPostgrestError(taken, HttpStatusCode.Conflict, "23505", "A file named \"Arm.SLDPRT\" already exists in this project.");
            var body = (await Harness.Json(taken))!;
            Assert.Equal("Parts", (string?)JsonNode.Parse((string)body["details"]!)!["existing_folder"]);
            Assert.Equal("Choose another name, or open the existing file.", (string?)body["hint"]);
        }

        h.Supabase.AdminEmails.Add(mentor);
        using (var badName = await h.Rpc("armory_create_project", new JsonObject { ["p_name"] = "CON", ["p_season"] = 2027, ["p_operation"] = Guid.NewGuid().ToString() }, mentorToken))
            await AssertPostgrestError(badName, HttpStatusCode.BadRequest, "22023");
        using (var lastMentor = await h.Rpc("armory_remove_member", new JsonObject { ["p_project"] = project.ToString(), ["p_email"] = mentor, ["p_operation"] = Guid.NewGuid().ToString() }, mentorToken))
            await AssertPostgrestError(lastMentor, HttpStatusCode.BadRequest, "P0001", "A project always keeps at least one mentor.");
        using (var notUuid = await h.Rpc("armory_project_files", new JsonObject { ["p_project"] = "not-a-uuid" }, mentorToken))
            await AssertPostgrestError(notUuid, HttpStatusCode.BadRequest, "22P02");
        using (var badJson = await h.Rpc("armory_my_projects", "[1,2]", mentorToken))
            await AssertPostgrestError(badJson, HttpStatusCode.BadRequest, "PGRST102");
    }

    // Guarded name kept: unknown tokens are PGRST301; expired ones are PGRST303 by default
    // and PGRST301 when ExpiredTokenCode says so.
    [PostgresFact]
    public async Task ExpiredOrUnknownTokensAre401Pgrst301()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var expired = h.Supabase.IssueSession(Harness.Email("student"));
        h.Supabase.ExpireAccessToken(expired.AccessToken);
        using (var response = await h.Rpc("armory_my_projects", "{}", expired.AccessToken))
        {
            await AssertPostgrestError(response, HttpStatusCode.Unauthorized, "PGRST303", "JWT expired");
            Assert.Equal("Bearer error=\"invalid_token\", error_description=\"JWT expired\"", Assert.Single(response.Headers.GetValues("WWW-Authenticate")));
        }

        var timedOut = h.Supabase.IssueSession(Harness.Email("student"), TimeSpan.FromMinutes(5));
        using (var live = await h.Rpc("armory_my_projects", "{}", timedOut.AccessToken)) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(h.Supabase.EmailForAccessToken(timedOut.AccessToken));
        using (var response = await h.Rpc("armory_my_projects", "{}", timedOut.AccessToken))
            await AssertPostgrestError(response, HttpStatusCode.Unauthorized, "PGRST303", "JWT expired");

        // Older PostgREST answered PGRST301 for an expired JWT; CLIENT.md allows both.
        h.Supabase.ExpiredTokenCode = "PGRST301";
        using (var response = await h.Rpc("armory_my_projects", "{}", timedOut.AccessToken))
            await AssertPostgrestError(response, HttpStatusCode.Unauthorized, "PGRST301", "JWT expired");
        Assert.Throws<ArgumentException>(() => h.Supabase.ExpiredTokenCode = "PGRST302");

        using (var unknown = await h.Rpc("armory_my_projects", "{}", "access-never-issued"))
            await AssertPostgrestError(unknown, HttpStatusCode.Unauthorized, "PGRST301", "JWT invalid");

        var live2 = h.Supabase.IssueSession(Harness.Email("student"));
        using var badKey = await h.Rpc("armory_my_projects", "{}", live2.AccessToken, apiKey: "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, badKey.StatusCode);
        Assert.Equal("""{"message":"Invalid API key"}""", await badKey.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task UnknownFunctionsAndArgumentsAre404Pgrst202()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var token = h.Supabase.IssueSession(Harness.Email("student")).AccessToken;
        using (var r = await h.Rpc("armory_nope", "{}", token))
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202", "Could not find the function public.armory_nope() in the schema cache");
        using (var r = await h.Rpc("armory_my_projects", """{"p_bogus":1}""", token))
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202");
        using (var r = await h.Rpc("pg_sleep", """{"p_seconds":1}""", token))
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202");
        using (var r = await h.Rpc("armory_create_project", """{"p_name":"Robot","p_season":2027}""", token)) // p_operation is required
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202");
        using (var r = await h.Rpc("armory_project_files", """{"P_PROJECT":"x"}""", token))
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202");
        using (var r = await h.Rpc("armory_project_files", """{"p_project\n":"x"}""", token))
            await AssertPostgrestError(r, HttpStatusCode.NotFound, "PGRST202");
        using (var r = await h.Rpc("armory_project_files", """{"p_project":"x","p_project":"y"}""", token))
            await AssertPostgrestError(r, HttpStatusCode.BadRequest, "PGRST102");
    }

    // Every code CLIENT.md names, plus the rest of PostgREST's table where CLIENT.md says
    // only "anything else".
    [Fact]
    public void PostgrestStatusMappingFollowsClientMd()
    {
        // Every code CLIENT.md names.
        Assert.Equal(403, FakeSupabase.PostgrestStatusFor("42501", anonymous: false));
        Assert.Equal(401, FakeSupabase.PostgrestStatusFor("42501", anonymous: true));
        Assert.Equal(409, FakeSupabase.PostgrestStatusFor("23505", false));
        Assert.Equal(409, FakeSupabase.PostgrestStatusFor("23503", false));
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("P0001", false));
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("22023", false));
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("22P02", false));
        Assert.Equal(404, FakeSupabase.PostgrestStatusFor("42883", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("P0002", false));
        Assert.Equal(503, FakeSupabase.PostgrestStatusFor("08006", false));
        Assert.Equal(503, FakeSupabase.PostgrestStatusFor("53300", false));
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("23514", false)); // anything else
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("42P10", false));
        // The rest of PostgREST's table, which CLIENT.md's "anything else" does not cover.
        Assert.Equal(404, FakeSupabase.PostgrestStatusFor("42P01", true));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("55000", false)); // the immutable-version trigger
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("40001", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("40P01", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("57014", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("XX000", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("54000", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("53400", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("42P17", false));
        Assert.Equal(405, FakeSupabase.PostgrestStatusFor("25006", false));
        Assert.Equal(500, FakeSupabase.PostgrestStatusFor("25P02", false));
        Assert.Equal(403, FakeSupabase.PostgrestStatusFor("28000", false));
        Assert.Equal(403, FakeSupabase.PostgrestStatusFor("0P000", false));
    }

    [PostgresFact]
    public async Task RpcBodiesMustBeJson415()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var token = h.Supabase.IssueSession(Harness.Email("student")).AccessToken;
        async Task<HttpResponseMessage> Send(HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(h.Supabase.BaseUri, "rest/v1/rpc/armory_my_projects")) { Content = content };
            request.Headers.Add("apikey", h.Supabase.AnonKey);
            request.Headers.Add("Authorization", "Bearer " + token);
            return await h.Http.SendAsync(request);
        }

        // new StringContent(json) without a media type is text/plain: PostgREST refuses it.
        using (var plain = await Send(new StringContent("{}")))
            await AssertPostgrestError(plain, HttpStatusCode.UnsupportedMediaType, "PGRST107");
        using (var form = await Send(new StringContent("{}", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")))
            await AssertPostgrestError(form, HttpStatusCode.UnsupportedMediaType, "PGRST107");
        using (var json = await Send(System.Net.Http.Json.JsonContent.Create(new Dictionary<string, object>())))
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        using (var upper = await Send(new StringContent("{}", System.Text.Encoding.UTF8, "Application/JSON")))
            Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        var untyped = new ByteArrayContent("{}"u8.ToArray()); // no Content-Type header: PostgREST assumes JSON
        using (var none = await Send(untyped)) Assert.Equal(HttpStatusCode.OK, none.StatusCode);

        using var get = new HttpRequestMessage(HttpMethod.Put, new Uri(h.Supabase.BaseUri, "rest/v1/rpc/armory_my_projects")) { Content = Harness.JsonBody("{}") };
        get.Headers.Add("apikey", h.Supabase.AnonKey);
        get.Headers.Add("Authorization", "Bearer " + token);
        using var refused = await h.Http.SendAsync(get);
        await AssertPostgrestError(refused, HttpStatusCode.MethodNotAllowed, "PGRST101");
    }

    [PostgresFact]
    public async Task ConcurrentCallersNeverSeeEachOthersIdentity()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, mentor, _) = await h.SeedProjectAsync();
        var mentorToken = h.Supabase.IssueSession(mentor).AccessToken;
        var outsider = Harness.Email("outsider");
        var outsiderToken = h.Supabase.IssueSession(outsider).AccessToken;

        // Two agents and an anonymous caller hammer the same pool at once. Identity is
        // transaction-local, so every answer belongs to its own caller.
        var calls = Enumerable.Range(0, 60).Select(async i =>
        {
            var who = (i % 3) switch { 0 => mentorToken, 1 => outsiderToken, _ => null };
            using var response = await h.Rpc("armory_my_projects", "{}", who);
            return (i % 3, response.StatusCode, await response.Content.ReadAsStringAsync());
        });
        foreach (var (kind, status, body) in await Task.WhenAll(calls))
        {
            switch (kind)
            {
                case 0:
                    Assert.Equal(HttpStatusCode.OK, status);
                    Assert.Contains(project.ToString(), body);
                    break;
                case 1:
                    Assert.Equal((HttpStatusCode.OK, "[]"), (status, body));
                    break;
                default:
                    Assert.Equal(HttpStatusCode.Unauthorized, status);
                    break;
            }
        }

        // And nothing is left on a pooled connection afterwards.
        for (var i = 0; i < 5; i++)
        {
            await using var connection = await h.Database.OpenAsync();
            Assert.Equal("", await Harness.Command(connection, "select coalesce(current_setting('armory.test_email', true), '')").ExecuteScalarAsync());
            Assert.Equal("", await Harness.Command(connection, "select coalesce(current_setting('armory.test_admins', true), '')").ExecuteScalarAsync());
            Assert.NotEqual("authenticated", await Harness.Command(connection, "select current_user::text").ExecuteScalarAsync());
        }
    }

    [PostgresFact]
    public async Task FunctionAndArgumentNamesCannotInjectSql()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var token = h.Supabase.IssueSession(Harness.Email("student")).AccessToken;
        await using (var connection = await h.Database.OpenAsync())
        {
            await Harness.Command(connection, """
                create or replace function public.armory_v2_echo(p_text2 text) returns text language sql security definer set search_path='' as $$ select p_text2 $$;
                grant execute on function public.armory_v2_echo(text) to authenticated;
                """).ExecuteNonQueryAsync();
        }
        using (var digits = await h.Rpc("armory_v2_echo", """{"p_text2":"'); drop table public.armory_projects; --"}""", token))
        {
            Assert.Equal(HttpStatusCode.OK, digits.StatusCode); // names with digits resolve; values are parameters
            Assert.Equal("'); drop table public.armory_projects; --", (string?)await Harness.Json(digits));
        }

        foreach (var function in new[] { "armory_my_projects();drop table public.armory_projects;--", "armory_my_projects%28%29", "ARMORY_MY_PROJECTS", "public.armory_my_projects" })
        {
            using var response = await h.Rpc(function, "{}", token);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{function} answered {(int)response.StatusCode}");
        }
        foreach (var name in new[] { "p_text2 => null); drop table public.armory_projects; --", "p_text2\"", "p_text2--", "\"p_text2\"" })
        {
            using var response = await h.Rpc("armory_v2_echo", new JsonObject { [name] = "x" }.ToJsonString(), token);
            await AssertPostgrestError(response, HttpStatusCode.NotFound, "PGRST202");
        }
        await using var check = await h.Database.OpenAsync();
        Assert.Equal(1L, await Harness.Command(check, "select count(*) from pg_class where oid = 'public.armory_projects'::regclass").ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task OfflineAbortsEveryRequestAtTheConnection()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var session = h.Supabase.IssueSession(Harness.Email("student"));
        h.Supabase.Offline = true;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => h.Rpc("armory_my_projects", "{}", session.AccessToken));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => h.RefreshToken(session.RefreshToken));
        h.Supabase.Offline = false;
        using var back = await h.RefreshToken(session.RefreshToken); // the aborted refresh never consumed the token
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(1, h.Supabase.RpcCount("armory_my_projects"));
        Assert.Equal(2, h.Supabase.TokenRequestCount);
        Assert.Equal(2, h.Supabase.RequestCounts[FakeSupabase.TokenPath]);
    }

    [PostgresFact]
    public async Task DroppedAcknowledgementCommitsButLosesTheAnswer()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var admin = Harness.Email("admin");
        h.Supabase.AdminEmails.Add(admin);
        var token = h.Supabase.IssueSession(admin).AccessToken;
        var name = "Robot " + Guid.NewGuid().ToString("N")[..8];
        var call = new JsonObject { ["p_name"] = name, ["p_season"] = 2027, ["p_operation"] = Guid.NewGuid().ToString() };

        h.Supabase.DropRpcAcknowledgement("armory_create_project", 1);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => h.Rpc("armory_create_project", call, token));
        await using (var connection = await h.Database.OpenAsync())
        {
            var stored = Assert.IsType<Guid>(await Harness.Command(connection, "select id from armory_projects where name=$1", name).ExecuteScalarAsync()); // committed before the drop

            using var replay = await h.Rpc("armory_create_project", call, token); // the same operation is a receipt hit
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(stored.ToString(), (string?)await Harness.Json(replay));
        }
        Assert.Equal(2, h.Supabase.RpcCount("armory_create_project"));
    }

    [PostgresFact]
    public async Task FaultInjectorAndScheduledFaultsFailChosenCalls()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var admin = Harness.Email("admin");
        h.Supabase.AdminEmails.Add(admin);
        var token = h.Supabase.IssueSession(admin).AccessToken;

        h.Supabase.FaultInjector = info => info.Path.EndsWith("/armory_my_projects", StringComparison.Ordinal) && info.CallNumber == 1
            ? FakeFault.Respond(503) : FakeFault.None;
        using (var failed = await h.Rpc("armory_my_projects", "{}", token)) Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using (var next = await h.Rpc("armory_my_projects", "{}", token)) Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        h.Supabase.FaultInjector = null;

        var name = "Robot " + Guid.NewGuid().ToString("N")[..8];
        h.Supabase.ScheduleFault(FakeSupabase.RpcPathPrefix + "armory_create_project", 1, FakeFault.DropBeforeHandling);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => h.Rpc("armory_create_project", new JsonObject { ["p_name"] = name, ["p_season"] = 2027, ["p_operation"] = Guid.NewGuid().ToString() }, token));
        await using var connection = await h.Database.OpenAsync();
        Assert.Equal(0L, (long)(await Harness.Command(connection, "select count(*) from armory_projects where name=$1", name).ExecuteScalarAsync())!);
    }
}
