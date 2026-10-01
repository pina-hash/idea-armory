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

    [PostgresFact]
    public async Task ReusedUnknownOrRevokedRefreshTokensAre400InvalidGrant()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var session = h.Supabase.IssueSession(Harness.Email("student"));
        (await h.RefreshToken(session.RefreshToken)).Dispose();

        async Task AssertGrant(HttpResponseMessage response, string description)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = (await Harness.Json(response))!;
            Assert.Equal("invalid_grant", (string?)body["error"]);
            Assert.Equal(description, (string?)body["error_description"]);
        }
        await AssertGrant(await h.RefreshToken(session.RefreshToken), "Invalid Refresh Token: Already Used");
        await AssertGrant(await h.RefreshToken("refresh-never-issued"), "Invalid Refresh Token: Not Found");

        var revoked = h.Supabase.IssueSession(Harness.Email("student"));
        h.Supabase.RevokeRefreshToken(revoked.RefreshToken);
        await AssertGrant(await h.RefreshToken(revoked.RefreshToken), "Invalid Refresh Token: Not Found");

        using var missing = await h.Refresh("{}", h.Supabase.AnonKey);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_request", (string?)(await Harness.Json(missing))!["error"]);
        using var password = await h.Refresh("{}", h.Supabase.AnonKey, grantType: "password");
        Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
        Assert.Equal("unsupported_grant_type", (string?)(await Harness.Json(password))!["error"]);
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
        using (var revoked = await h.Rpc("armory_current_email", "{}", studentToken))
            await AssertPostgrestError(revoked, HttpStatusCode.Forbidden, "42501");

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

    [PostgresFact]
    public async Task ExpiredOrUnknownTokensAre401Pgrst301()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var expired = h.Supabase.IssueSession(Harness.Email("student"));
        h.Supabase.ExpireAccessToken(expired.AccessToken);
        using (var response = await h.Rpc("armory_my_projects", "{}", expired.AccessToken))
            await AssertPostgrestError(response, HttpStatusCode.Unauthorized, "PGRST301", "JWT expired");

        var timedOut = h.Supabase.IssueSession(Harness.Email("student"), TimeSpan.FromMinutes(5));
        using (var live = await h.Rpc("armory_my_projects", "{}", timedOut.AccessToken)) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(h.Supabase.EmailForAccessToken(timedOut.AccessToken));
        using (var response = await h.Rpc("armory_my_projects", "{}", timedOut.AccessToken))
            await AssertPostgrestError(response, HttpStatusCode.Unauthorized, "PGRST301", "JWT expired");

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

    [Fact]
    public void PostgrestStatusMappingFollowsClientMd()
    {
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
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("55000", false));
        Assert.Equal(400, FakeSupabase.PostgrestStatusFor("42P01", true));
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
