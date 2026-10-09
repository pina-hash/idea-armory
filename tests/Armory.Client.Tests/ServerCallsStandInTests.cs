using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The 0.3.3 client calls through the fake Supabase over PostgreSQL, against the stand-ins that
// copy idea-app 0234 (armory_break_locks) and 0235 (the eight-argument feedback, Your feedback,
// and the armory-feedback-shots bucket with its policies) as they are: the answers travel
// through PostgREST's status mapping and Storage's refusals, as on the site.
public sealed partial class ClientTests
{
    private static byte[] TestPng(int bytes)
    {
        var png = new byte[bytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        return png;
    }

    private static async Task<long> CountAsync(Env env, string sql) => Convert.ToInt64((await RowsAsync(env, sql))[0][0], System.Globalization.CultureInfo.InvariantCulture);

    [PostgresFact]
    public async Task Break_locks_breaks_each_file_on_its_own_refuses_a_count_and_replays_without_writing()
    {
        await using var env = await Env.StartAsync();
        await ArmoryV3StandIn.ApplyBreakLocksAsync(env.Db);
        var (mentorSessions, mentor, _, _) = env.SignedIn("pina@ideabosco.test", admin: true);
        var (_, alex, _, _) = env.SignedIn("alex.kim@students.test");
        var project = await mentor.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        await mentor.AddMemberAsync(project, "alex.kim@students.test", MemberRole.Student, Guid.NewGuid());
        var mentorPc = await mentor.RegisterDeviceAsync("mentor laptop", Guid.NewGuid());
        var alexPc = await alex.RegisterDeviceAsync("LAB-PC-07", Guid.NewGuid());
        var files = new List<Guid>();
        for (var i = 0; i < 4; i++) files.Add(await alex.CreateFileAsync(project, "Drive", $"Part{i}.SLDPRT", alexPc, Guid.NewGuid()));
        foreach (var file in files.Take(3)) Assert.True(await alex.AcquireLockAsync(file, alexPc, Guid.NewGuid()));

        // A student may not: each file refused on its own with armory_break_lock's words.
        var refused = await alex.BreakLocksAsync(files, alexPc, Guid.NewGuid());
        Assert.Equal((4, 0, 4), (refused.Total, refused.Succeeded, refused.Refused));
        Assert.All(refused.Results, r => Assert.Equal((false, "P0001", "only a mentor or cad_lead may break a lock"), (r.Ok, r.Code, r.Message)));

        var operation = Guid.NewGuid();
        var answer = await mentor.BreakLocksAsync(files, mentorPc, operation);
        Assert.Equal((4, 4, 0), (answer.Total, answer.Succeeded, answer.Refused));
        Assert.Equal(files.Order().Select(f => (f, files.IndexOf(f) < 3)), answer.Results.Select(r => (r.FileId, r.Done)));
        Assert.Equal(0, await CountAsync(env, "select count(*) from public.armory_locks where broken_at is null"));
        var changes = await CountAsync(env, "select count(*) from public.armory_change_feed where kind = 'lock_broken'");
        Assert.Equal(3, changes);
        // The broken change names the former holder and their computer, as armory_break_lock writes it.
        var payload = (await RowsAsync(env, "select payload::text from public.armory_change_feed where kind = 'lock_broken' limit 1"))[0][0] as string;
        Assert.Contains("\"former_holder\": \"alex.kim@students.test\"", payload);
        Assert.Contains($"\"former_device_id\": \"{alexPc}\"", payload);

        // Alex checks one out again; the same operation answers the first time and writes nothing.
        Assert.True(await alex.AcquireLockAsync(files[0], alexPc, Guid.NewGuid()));
        var replay = await mentor.BreakLocksAsync(files, mentorPc, operation);
        Assert.Equal(answer.Results.Select(r => (r.FileId, r.Ok, r.Done)), replay.Results.Select(r => (r.FileId, r.Ok, r.Done)));
        Assert.Equal(1, await CountAsync(env, "select count(*) from public.armory_locks where broken_at is null"));
        Assert.Equal(changes, await CountAsync(env, "select count(*) from public.armory_change_feed where kind = 'lock_broken'"));
        // The same id for another RPC, or from another caller, is refused.
        var reused = await Assert.ThrowsAsync<ArmoryRpcException>(() => alex.BreakLocksAsync(files, alexPc, operation));
        Assert.Equal(("P0001", "operation id was already used by another caller or RPC"), (reused.SqlState, reused.Message));

        // Refusals of the whole call: a device that is not the caller's, and the count (raw, since
        // the client never sends one: 0 files, or more than 500 distinct ones).
        var device = await Assert.ThrowsAsync<ArmoryRpcException>(() => mentor.BreakLocksAsync(files, alexPc, Guid.NewGuid()));
        Assert.Equal(("P0001", "device is not registered to caller"), (device.SqlState, device.Message));
        var rest = new PostgrestClient(env.Http, mentorSessions);
        foreach (var count in new[] { 0, 501 })
        {
            var ids = new JsonArray([.. Enumerable.Range(0, count).Select(_ => (JsonNode?)Guid.NewGuid().ToString())]);
            var tooMany = await Assert.ThrowsAsync<ArmoryRpcException>(() => rest.CallAsync(ArmoryApi.BreakLocksRpc,
                new Dictionary<string, object?> { ["p_files"] = ids, ["p_device"] = mentorPc, ["p_operation"] = Guid.NewGuid() }));
            Assert.Equal((400, "22023", "count", (long)count, 500L), (tooMany.Status, tooMany.SqlState, tooMany.Reason, tooMany.Detail!.Total!.Value, tooMany.Detail.Limit!.Value));
        }

        // The switch: a site without 0234 answers 404 PGRST202.
        env.Supabase.HideFunction(ArmoryApi.BreakLocksRpc);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => mentor.BreakLocksAsync(files, mentorPc, Guid.NewGuid()))).IsFunctionMissing);
        env.Supabase.ShowFunction(ArmoryApi.BreakLocksRpc);
        // A deadlock the database rolled back is sent again by the client with the same body.
        env.Supabase.FailRpc(ArmoryApi.BreakLocksRpc, "40P01", "deadlock detected");
        var calls = env.Supabase.RpcCount(ArmoryApi.BreakLocksRpc);
        Assert.True((await mentor.BreakLocksAsync([files[0]], mentorPc, Guid.NewGuid())).Results.Single().Done);
        Assert.Equal(calls + 2, env.Supabase.RpcCount(ArmoryApi.BreakLocksRpc));
    }

    [PostgresFact]
    public async Task The_wide_feedback_takes_a_picture_from_storage_and_refuses_bad_path_not_found_and_in_use()
    {
        await using var env = await Env.StartAsync();
        await ArmoryV3StandIn.ApplyFeedbackV2Async(env.Db);
        var (sessions, api, _, _) = env.SignedIn("alex.kim@students.test");
        var (otherSessions, _, _, _) = env.SignedIn("maria.lopez@students.test");
        var shots = new FeedbackScreenshots(env.Http, sessions);
        // The access token's sub is the auth uid the fake's database sees.
        Assert.Equal(env.Supabase.UserIdFor("alex.kim@students.test"), AccessToken.Subject(sessions.Current!.AccessToken));

        var key = await shots.UploadAsync(TestPng(4096));
        Assert.StartsWith(env.Supabase.UserIdFor("alex.kim@students.test") + "/", key);
        var stored = env.Supabase.StoredObjects["armory-feedback-shots/" + key];
        Assert.Equal(("image/png", 4096), (stored.ContentType, stored.Bytes.Length));
        var id = await api.SubmitAppFeedbackAsync("praise", "Love it.", "0.3.3", "LAB-PC-07", new JsonObject { ["view"] = "files" }, "Checked in.", "Files", key);
        Assert.Equal(new object?[] { "praise", "Checked in.", "Files", key }, (await RowsAsync(env, $"select kind, tried, area, screenshot_path from public.armory_app_feedback where id = '{id}'"))[0]);

        async Task<ArmoryRpcException> Refused(string? shot, string? tried = null, string? area = null, string kind = "bug")
            => await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync(kind, "words", "0.3.3", null, [], tried, area, shot));
        // Another person's folder, upper case, or no uuid shape: bad_path. Never uploaded: not_found.
        // Already on a note: in_use.
        var otherKey = await new FeedbackScreenshots(env.Http, otherSessions).UploadAsync(TestPng(100));
        foreach (var bad in new[] { otherKey, key.ToUpperInvariant(), "picture.png", env.Supabase.UserIdFor("alex.kim@students.test") + "/x.png" })
        {
            var badPath = await Refused(bad);
            Assert.Equal(("22023", "bad_path", "screenshot"), (badPath.SqlState, badPath.Reason, badPath.Detail!.Field));
        }
        var missing = await Refused(env.Supabase.UserIdFor("alex.kim@students.test") + "/" + Guid.NewGuid() + ".png");
        Assert.Equal(("not_found", "screenshot"), (missing.Reason, missing.Detail!.Field));
        var inUse = await Refused(key);
        Assert.Equal(("in_use", "screenshot", 400), (inUse.Reason, inUse.Detail!.Field, inUse.Status));
        // What was tried and the area, over their limits after trimming.
        var tried = await Refused(null, tried: " " + new string('t', 1001) + " ");
        Assert.Equal(("too_long", "tried", 1000L, 1001L), (tried.Reason, tried.Detail!.Field, tried.Detail.Limit!.Value, tried.Detail.Size!.Value));
        var area = await Refused(null, area: new string('a', 121));
        Assert.Equal(("too_long", "area", 120L, 121L), (area.Reason, area.Detail!.Field, area.Detail.Limit!.Value, area.Detail.Size!.Value));
        Assert.Equal("The kind of note is bug, idea, praise or other.", (await Refused(null, kind: "complaint")).Message);
        // The five-argument form still refuses praise with its 0233 words.
        var praise = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("praise", "words", "0.3.3", null, []));
        Assert.Equal(("22023", "kind", "The kind of note is bug, idea or other."), (praise.SqlState, praise.Reason, praise.Message));
        // Blank new fields are stored as nothing.
        var plain = await api.SubmitAppFeedbackAsync("idea", "Dark mode.", "0.3.3", null, [], "  ", "", null);
        Assert.Equal(new object?[] { null, null, null }, (await RowsAsync(env, $"select tried, area, screenshot_path from public.armory_app_feedback where id = '{plain}'"))[0]);
    }

    [PostgresFact]
    public async Task Storage_takes_only_a_png_of_2_MiB_in_the_callers_own_folder_and_never_overwrites()
    {
        await using var env = await Env.StartAsync();
        var (sessions, _, _, _) = env.SignedIn("alex.kim@students.test");
        var shots = new FeedbackScreenshots(env.Http, sessions);
        // Before 0235 there is no bucket.
        Assert.Equal("not_available", (await Assert.ThrowsAsync<ScreenshotRefusedException>(() => shots.UploadAsync(TestPng(10)))).Reason);
        await ArmoryV3StandIn.ApplyFeedbackV2Async(env.Db);
        var uid = env.Supabase.UserIdFor("alex.kim@students.test");
        async Task<HttpResponseMessage> Put(string name, byte[] bytes, string type = "image/png", string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, env.Supabase.SupabaseUrl + "/storage/v1/object/armory-feedback-shots/" + name) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            request.Headers.TryAddWithoutValidation("apikey", env.Supabase.AnonKey);
            request.Headers.Authorization = new("Bearer", token ?? sessions.Current!.AccessToken);
            request.Headers.TryAddWithoutValidation("x-upsert", "false");
            return await env.Http.SendAsync(request);
        }
        // The real code of each refusal, as the body names it (and the HTTP status too while
        // StorageRealStatus is on).
        env.Supabase.StorageRealStatus = true;
        var name = $"{uid}/{Guid.NewGuid()}.png";
        Assert.Equal(HttpStatusCode.OK, (await Put(name, TestPng(10))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(name, TestPng(10))).StatusCode); // never overwritten
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put($"{uid}/{Guid.NewGuid()}.png", TestPng(2097153))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put($"{uid}/{Guid.NewGuid()}.png", TestPng(2097152))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Put($"{uid}/{Guid.NewGuid()}.png", TestPng(10), "image/jpeg")).StatusCode);
        // Another person's folder: the insert policy (own folder) refuses it.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put($"{Guid.NewGuid()}/{Guid.NewGuid()}.png", TestPng(10))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put($"{uid}/picture.png", TestPng(10))).StatusCode);
        // A bad token is InvalidJWT, 400 on any Storage.
        var badToken = await Put($"{uid}/{Guid.NewGuid()}.png", TestPng(10), token: "not-a-token");
        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal("InvalidJWT", JsonNode.Parse(await badToken.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Equal(2, env.Supabase.StoredObjects.Count); // the two that went; the duplicate never replaced the first
        // Storage as it answers: every refusal but a 500 is HTTP 400 with its own code in the body.
        env.Supabase.StorageRealStatus = false;
        var refused = await Put($"{Guid.NewGuid()}/{Guid.NewGuid()}.png", TestPng(10));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        Assert.Equal(("403", "AccessDenied"), (body["statusCode"]!.GetValue<string>(), body["code"]!.GetValue<string>()));
        // A busy Storage (544 in a 400 body) is offline for the client, never a refused picture.
        env.Supabase.FailStorage("544", "DatabaseTimeout", "Database timeout");
        await Assert.ThrowsAsync<ArmoryOfflineException>(() => shots.UploadAsync(TestPng(10)));
        Assert.Equal(2, env.Supabase.StoredObjects.Count);
        // An expired token is renewed once and the picture goes.
        env.Supabase.ExpireAccessToken(sessions.Current!.AccessToken);
        var renewed = await shots.UploadAsync(TestPng(10));
        Assert.StartsWith(uid + "/", renewed);
        Assert.Equal(1, env.Supabase.TokenRequestCount);
    }

    [PostgresFact]
    public async Task Your_feedback_lists_only_your_own_notes_newest_first_and_never_says_spam()
    {
        await using var env = await Env.StartAsync();
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        var (_, maria, _, _) = env.SignedIn("maria.lopez@students.test");
        await ArmoryV3StandIn.ApplyReportsAsync(env.Db);
        Assert.Null(await api.MyAppFeedbackAsync()); // 0233 only: not available, the window hides the list
        await ArmoryV3StandIn.ApplyFeedbackV2Async(env.Db);
        Assert.Empty((await api.MyAppFeedbackAsync())!);
        var first = await api.SubmitAppFeedbackAsync("bug", "Spins.", "0.3.2", "LAB-PC-07", []);
        await Task.Delay(20);
        var second = await api.SubmitAppFeedbackAsync("praise", "Love it.", "0.3.3", "LAB-PC-07", [], "Checked in.", "Files", null);
        await maria.SubmitAppFeedbackAsync("idea", "Not Alex's.", "0.3.3", null, []);
        await ArmoryV3StandIn.SetFeedbackStatusAsync(env.Db, first, "spam");
        var notes = (await api.MyAppFeedbackAsync())!;
        Assert.Equal([second, first], notes.Select(n => n.Id));
        Assert.Equal((AppFeedbackNote.New, "Checked in.", "Files", false), (notes[0].Status, notes[0].Tried, notes[0].Area, notes[0].HasScreenshot));
        Assert.Equal(AppFeedbackNote.Closed, notes[1].Status);
        Assert.NotNull(notes[1].ReviewedAt);
        Assert.Single((await api.MyAppFeedbackAsync(1))!);
        // The switch: hidden, it is "not available" again.
        env.Supabase.HideFunction(ArmoryApi.MyFeedbackRpc);
        Assert.Null(await api.MyAppFeedbackAsync());
    }

    [PostgresFact]
    public async Task Both_forms_share_twenty_notes_an_hour_and_the_wide_one_can_be_hidden_alone()
    {
        await using var env = await Env.StartAsync();
        await ArmoryV3StandIn.ApplyFeedbackV2Async(env.Db);
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        for (var i = 0; i < 10; i++) await api.SubmitAppFeedbackAsync("idea", "wide " + i, "0.3.3", null, [], null, null, null);
        for (var i = 0; i < 10; i++) await api.SubmitAppFeedbackAsync("idea", "narrow " + i, "0.3.3", null, []);
        foreach (var call in new Func<Task<Guid>>[] { () => api.SubmitAppFeedbackAsync("idea", "one too many", "0.3.3", null, []),
            () => api.SubmitAppFeedbackAsync("idea", "one too many", "0.3.3", null, [], null, null, null) })
        {
            var limited = await Assert.ThrowsAsync<ArmoryRpcException>(call);
            Assert.Equal((429, "rate_limited", 20L), (limited.Status, limited.Reason, limited.Detail!.Limit!.Value));
            Assert.InRange(limited.RetryAfter!.Value.TotalSeconds, 3000, 3600);
        }
        // The eight-argument form hidden alone: it answers PGRST202 while the five-argument one is there.
        env.Supabase.HideFunction(ArmoryApi.SubmitFeedbackRpc, 8);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "x", "0.3.3", null, [], null, null, null))).IsFunctionMissing);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "x", "0.3.3", null, []))).IsRateLimited);
    }
}
