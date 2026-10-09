using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Telemetry;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The 0.3.3 client calls against a recorded-response layer (no database): Force check in of many
// files in one call (idea-app 0234, armory_break_locks), Send feedback the same as the website's
// (0235: the eight-argument armory_submit_app_feedback, the screenshot's upload to Storage, and
// FeedbackSender's results), "Your feedback" (armory_my_app_feedback), and the version limits.
// Each answer is what PostgREST or Storage answers for those bodies, status, SQLSTATE, message
// and JSON DETAIL included.
public sealed class ServerCallsClientTests
{
    private static readonly Guid User = Guid.Parse("5e0c9a3b-1f2d-4c6e-8a7b-9d0e1f2a3b4c");
    private const string Supabase = "https://project.supabase.test";

    // A GoTrue-shaped access token (header.payload.signature) whose sub is the auth uid.
    private static string Jwt(Guid? sub, string? raw = null)
    {
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = raw ?? JsonSerializer.Serialize(new Dictionary<string, object?> { ["aud"] = "authenticated", ["sub"] = sub?.ToString(), ["email"] = "alex.kim@students.test", ["role"] = "authenticated" });
        return B64("""{"alg":"HS256","typ":"JWT"}""") + "." + B64(payload) + ".c2lnbmF0dXJl";
    }

    // PostgREST and Storage in a box: answers queued per RPC or per Storage bucket (the last one
    // repeats), every request kept with its headers.
    private sealed class Recorded : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(int Status, string Body)>> answers = new(StringComparer.Ordinal);
        public List<(string Path, string Function, JsonObject? Body, byte[] Bytes, Dictionary<string, string> Headers)> Calls { get; } = [];
        public Func<string, Exception?>? Fault { get; set; }

        public Recorded Answer(string function, int status, string body)
        {
            if (!answers.TryGetValue(function, out var queue)) answers[function] = queue = new();
            queue.Enqueue((status, body));
            return this;
        }

        public Recorded Refuse(string function, int status, string code, string message, object? detail = null)
            => Answer(function, status, new JsonObject
            {
                ["code"] = code, ["message"] = message,
                ["details"] = detail is null ? null : detail as string ?? JsonSerializer.Serialize(detail), ["hint"] = null,
            }.ToJsonString());

        public Recorded Missing(string function)
            => Refuse(function, 404, "PGRST202", $"Could not find the function public.{function} in the schema cache");

        // Storage's refusal body: {"statusCode": "413", "error", "message"}.
        public Recorded Storage(int status, string? code = null, string error = "", string message = "")
            => Answer("storage", status, code is null ? """{"Key":"armory-feedback-shots/x","Id":"1"}""" : new JsonObject { ["statusCode"] = code, ["error"] = error, ["message"] = message }.ToJsonString());

        public IEnumerable<JsonObject> BodiesOf(string function) => Calls.Where(c => c.Function == function).Select(c => c.Body!);
        public IReadOnlyList<(string Path, byte[] Bytes, Dictionary<string, string> Headers)> Uploads
            => Calls.Where(c => c.Function == "storage").Select(c => (c.Path, c.Bytes, c.Headers)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var storage = path.StartsWith("/storage/v1/object/", StringComparison.Ordinal);
            var function = storage ? "storage" : path["/rest/v1/rpc/".Length..];
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (Calls) Calls.Add((path, function, storage ? null : (JsonObject)JsonNode.Parse(bytes)!, bytes, headers));
            if (Fault?.Invoke(function) is { } fault) throw fault;
            if (!answers.TryGetValue(function, out var queue) || queue.Count == 0)
                return Json(404, $$"""{"code":"PGRST202","message":"Could not find the function public.{{function}}","details":null,"hint":null}""");
            var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Json(status, text);
        }

        private static HttpResponseMessage Json(int status, string body)
            => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static (ArmoryApi Api, SessionManager Sessions, HttpClient Http) Client(Recorded recorded, string? token = null)
    {
        var http = new HttpClient(recorded);
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession(Supabase, "anon-key", token ?? Jwt(User), "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1), "alex.kim@students.test", Guid.Parse("11111111-2222-3333-4444-555555555555"), "LAB-PC-07"));
        return (new ArmoryApi(new PostgrestClient(http, sessions)), sessions, http);
    }

    private static FeedbackSender Sender(Recorded recorded, TestClock? clock = null, List<string>? log = null, SubmitLimiter? limiter = null, string version = "0.3.3")
    {
        var (api, sessions, http) = Client(recorded);
        return new FeedbackSender(api, new FeedbackScreenshots(http, sessions), sessions, version, limiter ?? new SubmitLimiter(clock: clock), clock, log is null ? null : log.Add);
    }

    // A PNG of the given size: the signature, then filler.
    private static byte[] Png(int bytes)
    {
        var png = new byte[bytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        return png;
    }

    private const string NoteId = "0f000000-0000-0000-0000-0000000000aa";
    private static string Id(string id = NoteId) => "\"" + id + "\"";

    // ---- armory_break_locks (0234) -----------------------------------------------------------

    [Fact]
    public async Task Break_locks_sends_500_distinct_files_in_id_order_and_reads_broken_and_each_refusal()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var recorded = new Recorded().Answer(ArmoryApi.BreakLocksRpc, 200, $$"""
            {"total":3,"succeeded":2,"refused":1,"results":[
              {"file_id":"{{a}}","ok":true,"broken":true},
              {"file_id":"{{b}}","ok":true,"broken":false},
              {"file_id":"{{c}}","ok":false,"code":"P0001","message":"only a mentor or cad_lead may break a lock"}]}
            """);
        var (api, _, _) = Client(recorded);
        var device = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var answer = await api.BreakLocksAsync([c, a, b], device, operation);
        Assert.Equal((3, 2, 1), (answer.Total, answer.Succeeded, answer.Refused));
        // Done is broken: false means nobody had it checked out any more.
        Assert.Equal([(a, true, true), (b, true, false), (c, false, false)], answer.Results.Select(r => (r.FileId, r.Ok, r.Done)));
        Assert.Equal(("P0001", "only a mentor or cad_lead may break a lock"), (answer.Results[2].Code, answer.Results[2].Message));
        var sent = recorded.BodiesOf(ArmoryApi.BreakLocksRpc).Single();
        Assert.Equal([a.ToString(), b.ToString(), c.ToString()], sent["p_files"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal((device.ToString(), operation.ToString()), (sent["p_device"]!.GetValue<string>(), sent["p_operation"]!.GetValue<string>()));
        Assert.Equal(["p_device", "p_files", "p_operation"], sent.Select(p => p.Key).Order(StringComparer.Ordinal));

        // 1 to 500 distinct files a call, never sent otherwise; Chunk makes the calls in id order,
        // the order PostgreSQL sorts uuids in (their text).
        await Assert.ThrowsAsync<ArgumentException>(() => api.BreakLocksAsync([], device, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => api.BreakLocksAsync([a, a], device, Guid.NewGuid()));
        var many = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => api.BreakLocksAsync(many, device, Guid.NewGuid()));
        var chunks = ArmoryApi.Chunk(many);
        Assert.Equal([500, 500, 200], chunks.Select(x => x.Length));
        Assert.Equal(many.Select(g => g.ToString()).Order(StringComparer.Ordinal), chunks.SelectMany(x => x).Select(g => g.ToString()));
        Assert.Single(recorded.BodiesOf(ArmoryApi.BreakLocksRpc));

        // The whole call's refusals are ArmoryRpcException: 22023 count, and 404 PGRST202 on a site before 0234.
        var refusing = new Recorded()
            .Refuse(ArmoryApi.BreakLocksRpc, 400, "22023", "A batch is 1 to 500 files.", new { reason = "count", total = 501, limit = 500 })
            .Missing(ArmoryApi.BreakLocksRpc);
        var (api2, _, _) = Client(refusing);
        var count = await Assert.ThrowsAsync<ArmoryRpcException>(() => api2.BreakLocksAsync([a], device, Guid.NewGuid()));
        Assert.Equal(("22023", "count", 501L, 500L), (count.SqlState, count.Reason, count.Detail!.Total!.Value, count.Detail.Limit!.Value));
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api2.BreakLocksAsync([a], device, Guid.NewGuid()))).IsFunctionMissing);
    }

    [Fact]
    public async Task A_deadlock_or_serialization_failure_is_sent_again_with_the_same_body()
    {
        var a = Guid.NewGuid();
        var recorded = new Recorded()
            .Refuse(ArmoryApi.BreakLocksRpc, 500, "40P01", "deadlock detected")
            .Refuse(ArmoryApi.BreakLocksRpc, 500, "40001", "could not serialize access")
            .Answer(ArmoryApi.BreakLocksRpc, 200, $$"""{"total":1,"succeeded":1,"refused":0,"results":[{"file_id":"{{a}}","ok":true,"broken":true}]}""");
        var (api, _, _) = Client(recorded);
        var answer = await api.BreakLocksAsync([a], Guid.NewGuid(), Guid.NewGuid());
        Assert.True(answer.Results.Single().Done);
        var bodies = recorded.BodiesOf(ArmoryApi.BreakLocksRpc).Select(b => b.ToJsonString()).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.Single(bodies.Distinct());
        // Bounded: after MaximumResends it is raised as transient.
        var stuck = new Recorded().Refuse(ArmoryApi.BreakLocksRpc, 500, "40P01", "deadlock detected");
        var (api2, _, _) = Client(stuck);
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api2.BreakLocksAsync([a], Guid.NewGuid(), Guid.NewGuid()))).IsTransient);
        Assert.Equal(1 + PostgrestClient.MaximumResends, stuck.BodiesOf(ArmoryApi.BreakLocksRpc).Count());
    }

    // ---- The access token's claims ------------------------------------------------------------

    [Fact]
    public void The_auth_uid_is_the_tokens_sub_and_anything_else_reads_as_none()
    {
        Assert.Equal(User, AccessToken.Subject(Jwt(User)));
        Assert.Equal("alex.kim@students.test", AccessToken.Claim(Jwt(User), "email"));
        Assert.Null(AccessToken.Subject(Jwt(null)));
        Assert.Null(AccessToken.Subject(Jwt(null, """{"sub":"alex"}""")));
        Assert.Null(AccessToken.Subject(Jwt(null, """["not","an","object"]""")));
        Assert.Null(AccessToken.Subject(Jwt(null, """{"sub":42}""")));
        foreach (var bad in new[] { null, "", "access-token", "a.b", "a.b.c.d", "a.%%%.c", "a." + new string('x', 20000) + ".c", "eyJhbGciOiJIUzI1NiJ9.bm90IGpzb24.c2ln" })
            Assert.Null(AccessToken.Subject(bad));
    }

    // ---- The screenshot's upload ----------------------------------------------------------------

    [Fact]
    public async Task A_screenshot_goes_to_the_callers_own_folder_as_a_new_png_and_never_overwrites()
    {
        var recorded = new Recorded().Storage(200);
        var (_, sessions, http) = Client(recorded);
        var recorder = new FlightRecorder();
        var shots = new FeedbackScreenshots(http, sessions, recorder);
        var png = Png(1000);
        var key = await shots.UploadAsync(png);
        Assert.Matches($"^{User}/[0-9a-f]{{8}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{12}}\\.png$", key);
        var (path, bytes, headers) = Assert.Single(recorded.Uploads);
        Assert.Equal("/storage/v1/object/armory-feedback-shots/" + key, path);
        Assert.Equal(png, bytes);
        Assert.Equal("anon-key", headers["apikey"]);
        Assert.Equal("Bearer " + Jwt(User), headers["Authorization"]);
        Assert.Equal("image/png", headers["Content-Type"]);
        Assert.Equal("false", headers["x-upsert"]);
        Assert.Equal("1000", headers["Content-Length"]);
        // A second upload is a new key.
        Assert.NotEqual(key, await shots.UploadAsync(png));
        // The flight recorder has its size and how it ended, never the token or the key.
        var events = recorder.Snapshot().Where(e => e.Kind == FlightKind.Transfer).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(("screenshot", 1000L, true), (e.Name, e.Bytes, e.Ok)));
        Assert.DoesNotContain(recorder.Snapshot(), e => (e.Name + e.Target + e.Detail + e.Stack).Contains("eyJ", StringComparison.Ordinal) ||
            (e.Name + e.Target + e.Detail).Contains(User.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_screenshot_over_2_MiB_or_not_a_png_is_refused_here_before_anything_is_sent()
    {
        var recorded = new Recorded().Storage(200);
        var (_, sessions, http) = Client(recorded);
        var shots = new FeedbackScreenshots(http, sessions);
        var large = await Assert.ThrowsAsync<ScreenshotRefusedException>(() => shots.UploadAsync(Png(2097153)));
        Assert.Equal(("too_large", 0), (large.Reason, large.Status));
        Assert.Equal("The screenshot is 2.0 MB and the limit is 2 MB.", large.Message);
        Assert.Equal("not_png", (await Assert.ThrowsAsync<ScreenshotRefusedException>(() => shots.UploadAsync(new byte[100]))).Reason);
        Assert.Empty(recorded.Uploads);
        // Exactly 2 MiB goes.
        await shots.UploadAsync(Png(2097152));
        Assert.Single(recorded.Uploads);
        // A token without an auth uid: refused here, nothing sent.
        var (_, noSub, http2) = Client(new Recorded().Storage(200), Jwt(null));
        Assert.Equal("no_account", (await Assert.ThrowsAsync<ScreenshotRefusedException>(() => new FeedbackScreenshots(http2, noSub).UploadAsync(Png(10)))).Reason);
    }

    [Fact]
    public async Task Storage_refusals_are_plain_reasons_whether_storage_answers_their_status_or_400()
    {
        foreach (var legacy in new[] { false, true })
        {
            int Status(int code) => legacy ? 400 : code;
            var recorded = new Recorded()
                .Storage(Status(413), "413", "Payload too large", "The object exceeded the maximum allowed size")
                .Storage(Status(403), "403", "Unauthorized", "new row violates row-level security policy")
                .Storage(Status(409), "409", "Duplicate", "The resource already exists")
                .Storage(Status(404), "404", "Bucket not found", "Bucket not found")
                .Storage(400, "415", "invalid_mime_type", "mime type image/jpeg is not supported")
                .Storage(503, "503", "Service Unavailable", "busy");
            var (_, sessions, http) = Client(recorded);
            var shots = new FeedbackScreenshots(http, sessions);
            foreach (var (reason, status) in new[] { ("too_large", 413), ("not_allowed", 403), ("exists", 409), ("not_available", 404), ("refused", 400) })
            {
                var refused = await Assert.ThrowsAsync<ScreenshotRefusedException>(() => shots.UploadAsync(Png(10)));
                Assert.Equal((reason, legacy ? 400 : status), (refused.Reason, refused.Status));
            }
            await Assert.ThrowsAsync<ArmoryOfflineException>(() => shots.UploadAsync(Png(10)));
        }
        // No connection at all: offline.
        var down = new Recorded { Fault = _ => new HttpRequestException("no route") };
        var (_, s, h) = Client(down);
        await Assert.ThrowsAsync<ArmoryOfflineException>(() => new FeedbackScreenshots(h, s).UploadAsync(Png(10)));
    }

    // ---- The eight-argument feedback (0235) and Your feedback ------------------------------------

    [Fact]
    public async Task The_wide_submit_sends_all_eight_named_arguments_with_blank_ones_as_null()
    {
        var recorded = new Recorded().Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
        var (api, _, _) = Client(recorded);
        Assert.Equal(Guid.Parse(NoteId), await api.SubmitAppFeedbackAsync("praise", "Love the new check in.", "0.3.3", "LAB-PC-07", new JsonObject { ["view"] = "files" },
            "  ", "Files", null));
        var body = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Single();
        Assert.Equal(["p_app_version", "p_area", "p_body", "p_context", "p_device_name", "p_kind", "p_screenshot", "p_tried"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("praise", "Files"), (body["p_kind"]!.GetValue<string>(), body["p_area"]!.GetValue<string>()));
        Assert.Null(body["p_tried"]);
        Assert.Null(body["p_screenshot"]);
        Assert.True(body.ContainsKey("p_tried") && body.ContainsKey("p_screenshot"));
        // The five-argument call is unchanged: five names, never the new ones.
        await api.SubmitAppFeedbackAsync("idea", "words", "0.3.3", null, []);
        Assert.Equal(5, recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Last().Count);
    }

    [Fact]
    public async Task Your_feedback_reads_each_note_and_is_null_on_a_site_without_it()
    {
        var recorded = new Recorded().Answer(ArmoryApi.MyFeedbackRpc, 200, """
            [{"id":"0f000000-0000-0000-0000-000000000001","created_at":"2026-10-08T18:00:00+00:00","kind":"praise","body":"Love it.","tried":null,"area":"Files",
              "has_screenshot":true,"app_version":"0.3.3","device_name":"LAB-PC-07","status":"resolved","reviewed_at":"2026-10-09T08:00:00+00:00"},
             {"id":"0f000000-0000-0000-0000-000000000002","created_at":"2026-10-08T17:00:00+00:00","kind":"bug","body":"Spins.","tried":"Restarted.","area":null,
              "has_screenshot":false,"app_version":"0.3.2","device_name":null,"status":"spam","reviewed_at":null},
             {"id":"0f000000-0000-0000-0000-000000000003","created_at":"2026-10-08T16:00:00+00:00","kind":"idea","body":"Dark mode.","tried":null,"area":null,
              "has_screenshot":false,"app_version":"0.3.2","device_name":null,"status":"new","reviewed_at":null}]
            """);
        var (api, _, _) = Client(recorded);
        var notes = await api.MyAppFeedbackAsync(20);
        Assert.NotNull(notes);
        Assert.Equal(20, recorded.BodiesOf(ArmoryApi.MyFeedbackRpc).Single()["p_limit"]!.GetValue<int>());
        var first = notes[0];
        Assert.Equal((Guid.Parse("0f000000-0000-0000-0000-000000000001"), "praise", "Love it.", (string?)null, "Files", true, "0.3.3", "LAB-PC-07", "resolved"),
            (first.Id, first.Kind, first.Body, first.Tried, first.Area, first.HasScreenshot, first.AppVersion, first.DeviceName, first.Status));
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), first.ReviewedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero), first.CreatedAt);
        // A note marked spam never reads as spam here.
        Assert.Equal(["resolved", AppFeedbackNote.Closed, AppFeedbackNote.New], notes.Select(n => n.Status));
        Assert.Equal("Restarted.", notes[1].Tried);
        // No replies exist: a note is only its own words and its status.
        Assert.DoesNotContain(typeof(AppFeedbackNote).GetProperties(), p => p.Name.Contains("Reply", StringComparison.OrdinalIgnoreCase));

        var (api2, _, _) = Client(new Recorded().Missing(ArmoryApi.MyFeedbackRpc));
        Assert.Null(await api2.MyAppFeedbackAsync());
        var (api3, _, _) = Client(new Recorded().Refuse(ArmoryApi.MyFeedbackRpc, 403, "42501", "Sign in to see your feedback."));
        Assert.True((await Assert.ThrowsAsync<ArmoryRpcException>(() => api3.MyAppFeedbackAsync())).IsForbidden);
    }

    // ---- FeedbackSender: the window's one entry point -------------------------------------------

    [Fact]
    public async Task A_note_with_a_picture_uploads_it_first_then_sends_the_note_naming_it()
    {
        var recorded = new Recorded().Storage(200).Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
        var sender = Sender(recorded);
        var result = await sender.SendAsync(new FeedbackNote("Bug", "  Check in spun.  ", "  Restarted Armory.  ", " Files ", Png(5000),
            new JsonObject { ["view"] = "files", ["online"] = true }));
        var sent = Assert.IsType<FeedbackResult.Sent>(result);
        Assert.Equal(Guid.Parse(NoteId), sent.Id);
        Assert.True(result.Ok);
        Assert.Equal("Sent. Thank you for the feedback.", result.Message);
        Assert.Equal(["storage", ArmoryApi.SubmitFeedbackRpc], recorded.Calls.Select(c => c.Function));
        var key = recorded.Uploads.Single().Path["/storage/v1/object/armory-feedback-shots/".Length..];
        var note = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Single();
        Assert.Equal(("bug", "Check in spun.", "Restarted Armory.", "Files", key, "0.3.3", "LAB-PC-07"),
            (note["p_kind"]!.GetValue<string>(), note["p_body"]!.GetValue<string>(), note["p_tried"]!.GetValue<string>(), note["p_area"]!.GetValue<string>(),
             note["p_screenshot"]!.GetValue<string>(), note["p_app_version"]!.GetValue<string>(), note["p_device_name"]!.GetValue<string>()));
        Assert.Equal("""{"view":"files","online":true}""", note["p_context"]!.ToJsonString());
    }

    [Fact]
    public async Task Text_is_trimmed_and_cut_to_the_sites_limits_here_and_never_refused_for_length()
    {
        var recorded = new Recorded().Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
        var sender = Sender(recorded, version: "0.3.3-" + new string('b', 80));
        var tried = new string('t', 1500);
        var area = "  " + new string('a', 119) + "\U0001F600" + "zz";
        var context = new JsonObject { ["log"] = new string('x', 200 * 1024), ["view"] = "files" };
        Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("other", new string('w', 9000), tried, area, null, context)));
        var note = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Single();
        Assert.Equal(8000, note["p_body"]!.GetValue<string>().Length);
        Assert.Equal(1000, note["p_tried"]!.GetValue<string>().Length);
        // 120 characters as the site counts them: the emoji is one, never cut in half.
        var sentArea = note["p_area"]!.GetValue<string>();
        Assert.Equal(120, sentArea.EnumerateRunes().Count());
        Assert.EndsWith("\U0001F600", sentArea);
        Assert.Equal(64, note["p_app_version"]!.GetValue<string>().Length);
        Assert.True(Encoding.UTF8.GetByteCount(note["p_context"]!.ToJsonString()) <= FeedbackSender.MaximumContextBytes);
        Assert.Equal("files", note["p_context"]!["view"]!.GetValue<string>());
        Assert.True(note["p_context"]!["trimmed"]!.GetValue<bool>());

        // Nothing to say, or not connected: refused here, nothing sent.
        Assert.Equal("Write a few words first.", (await sender.SendAsync(new FeedbackNote("bug", "   "))).Message);
        Assert.Single(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc));
        // An unknown kind is "other".
        await sender.SendAsync(new FeedbackNote("Complaint", "words"));
        Assert.Equal("other", recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Last()["p_kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_site_before_0235_gets_the_five_arguments_without_the_new_fields_and_praise_goes_as_other()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
        var recorded = new Recorded()
            .Missing(ArmoryApi.SubmitFeedbackRpc) // the wide form: no such function
            .Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id())
            .Storage(200);
        var log = new List<string>();
        var sender = Sender(recorded, clock, log);
        var result = await sender.SendAsync(new FeedbackNote("praise", "Love the new check in.", "Nothing", "Files"));
        var narrow = Assert.IsType<FeedbackResult.SentWithoutNewFields>(result);
        Assert.Equal(("other", true, true), (narrow.Kind, narrow.KindChanged, narrow.LeftOutDetails));
        Assert.Equal("Sent. Thank you for the feedback. The website can't take the picture, what you tried or the area yet, so they were left out." +
            " The website doesn't take praise yet, so it went as other feedback.", result.Message);
        var bodies = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).ToList();
        Assert.Equal([8, 5], bodies.Select(b => b.Count));
        Assert.Equal("other", bodies[1]["p_kind"]!.GetValue<string>());
        Assert.True(sender.NewFieldsMissing);
        Assert.Contains(log, l => l.Contains("no eight-argument armory_submit_app_feedback", StringComparison.Ordinal));

        // Within the hour the wide form is not asked again, and a picture is not uploaded at all
        // (no note could name it).
        var again = await sender.SendAsync(new FeedbackNote("bug", "Spins.", null, null, Png(100)));
        Assert.Equal((false, true), (((FeedbackResult.SentWithoutNewFields)again).KindChanged, ((FeedbackResult.SentWithoutNewFields)again).LeftOutDetails));
        Assert.Equal(5, recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Last().Count);
        Assert.Empty(recorded.Uploads);
        // A plain note with nothing new says nothing was left out.
        Assert.Equal("Sent. Thank you for the feedback.", (await sender.SendAsync(new FeedbackNote("idea", "Dark mode."))).Message);
        // An hour later the wide form is asked again.
        clock.Advance(FeedbackSender.WideMissingRetry);
        Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("idea", "Again.")));
        Assert.Equal(8, recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Last().Count);
        Assert.False(sender.NewFieldsMissing);

        // Neither form (a site before 0233): plain words, nothing thrown.
        var none = Sender(new Recorded());
        var failed = Assert.IsType<FeedbackResult.Failed>(await none.SendAsync(new FeedbackNote("bug", "words")));
        Assert.Equal("The website can't take feedback from the app yet. Try again later.", failed.Message);
    }

    [Fact]
    public async Task Each_screenshot_refusal_keeps_the_note_and_offers_it_without_the_picture()
    {
        foreach (var reason in new[] { "bad_path", "not_found", "in_use" })
        {
            var recorded = new Recorded().Storage(200)
                .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "The screenshot ...", new { reason, field = "screenshot" })
                .Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
            var sender = Sender(recorded);
            var note = new FeedbackNote("bug", "Spins.", null, "Files", Png(100));
            var refused = Assert.IsType<FeedbackResult.ScreenshotRefused>(await sender.SendAsync(note));
            Assert.Equal(reason, refused.Reason);
            Assert.True(refused.CanSendWithoutPicture);
            Assert.False(refused.Ok);
            Assert.StartsWith("Your note wasn't sent: ", refused.Message);
            Assert.EndsWith(" You can send it without the picture.", refused.Message);
            // Without the picture: the same note goes, with no key.
            Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(note with { Screenshot = null }));
            Assert.Null(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Last()["p_screenshot"]);
            Assert.Single(recorded.Uploads);
        }
        // Storage's refusals (400, 403, 409) and 413: the note was not sent.
        foreach (var (status, code, expected) in new[] { (403, "403", "not_allowed"), (409, "409", "exists"), (400, "400", "refused") })
        {
            var recorded = new Recorded().Storage(status, code, "x", "y").Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
            var refused = Assert.IsType<FeedbackResult.ScreenshotRefused>(await Sender(recorded).SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: Png(100))));
            Assert.Equal(expected, refused.Reason);
            Assert.Empty(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc));
        }
        var tooLargeThere = new Recorded().Storage(413, "413", "Payload too large", "The object exceeded the maximum allowed size");
        var large = Assert.IsType<FeedbackResult.TooLarge>(await Sender(tooLargeThere).SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: Png(100))));
        Assert.Equal("screenshot", large.Field);
        Assert.True(large.CanSendWithoutPicture);
        // Over 2 MiB: refused here before anything goes, with plain sizes.
        var nothing = new Recorded();
        var local = Assert.IsType<FeedbackResult.TooLarge>(await Sender(nothing).SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: Png(2500000))));
        Assert.Equal(("screenshot", 2500000L, 2097152L), (local.Field, local.Size, local.Limit));
        Assert.Equal("Your note wasn't sent: the screenshot is 2.4 MB and the limit is 2 MB. You can send it without the picture.", local.Message);
        Assert.Equal("not_png", Assert.IsType<FeedbackResult.ScreenshotRefused>(await Sender(nothing).SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: new byte[64]))).Reason);
        Assert.Empty(nothing.Calls);
    }

    [Fact]
    public async Task A_picture_sent_again_after_a_failure_is_named_again_not_uploaded_twice()
    {
        var recorded = new Recorded().Storage(200);
        var (api, sessions, http) = Client(recorded);
        var limiter = new SubmitLimiter();
        var sender = new FeedbackSender(api, new FeedbackScreenshots(http, sessions), sessions, "0.3.3", limiter);
        var offline = true;
        recorded.Fault = f => f == ArmoryApi.SubmitFeedbackRpc && offline ? new HttpRequestException("cut") : null;
        recorded.Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
        var png = Png(300);
        Assert.IsType<FeedbackResult.Offline>(await sender.SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: png)));
        offline = false;
        Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("bug", "Spins.", Screenshot: png)));
        Assert.Single(recorded.Uploads);
        var keys = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Select(b => b["p_screenshot"]!.GetValue<string>()).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Single(keys.Distinct());
        // Once a note names it, the same picture again is a new upload (a key is on one note only).
        Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("bug", "Again.", Screenshot: png)));
        Assert.Equal(2, recorded.Uploads.Count);
    }

    [Fact]
    public async Task PT429_waits_retry_after_for_the_window_and_the_uploader_alike()
    {
        var folder = Directory.CreateTempSubdirectory("armory-feedback-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var recorded = new Recorded()
                .Refuse(ArmoryApi.SubmitFeedbackRpc, 429, "PT429", "Too many feedback notes from this account in the last hour.",
                    new { reason = "rate_limited", limit = 20, window_seconds = 3600, retry_after_seconds = 1500 })
                .Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id())
                .Storage(200);
            var (api, sessions, http) = Client(recorded);
            var limiter = new SubmitLimiter(store, clock);
            var sender = new FeedbackSender(api, new FeedbackScreenshots(http, sessions), sessions, "0.3.3", limiter, clock);
            var limited = Assert.IsType<FeedbackResult.RateLimited>(await sender.SendAsync(new FeedbackNote("idea", "One more.", Screenshot: Png(50))));
            Assert.Equal(TimeSpan.FromSeconds(1500), limited.RetryAfter);
            Assert.Equal("You've sent a lot of feedback this hour. Try again in 25 minutes.", limited.Message);
            var calls = recorded.Calls.Count;
            // Nothing goes before the wait is over: not the window's note, not its picture, and
            // not the uploader's saved notes (one limiter, kept across a restart).
            clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(TimeSpan.FromMinutes(5), Assert.IsType<FeedbackResult.RateLimited>(await sender.SendAsync(new FeedbackNote("idea", "Still?", Screenshot: Png(50)))).RetryAfter);
            Assert.Equal("You've sent a lot of feedback this hour. Try again in 5 minutes.", (await sender.SendAsync(new FeedbackNote("idea", "Still?"))).Message);
            var uploader = new IncidentUploader(api, store, () => false, clock, feedback: sender);
            Assert.True(uploader.IsWaiting(ArmoryApi.SubmitFeedbackRpc));
            Assert.True(new SubmitLimiter(store, clock).IsRateLimited(ArmoryApi.SubmitFeedbackRpc));
            Assert.Equal(calls, recorded.Calls.Count);
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.False(uploader.IsWaiting(ArmoryApi.SubmitFeedbackRpc));
            Assert.IsType<FeedbackResult.Sent>(await sender.SendAsync(new FeedbackNote("idea", "Now.")));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Offline_too_large_and_other_refusals_are_plain_results_never_exceptions()
    {
        var down = new Recorded { Fault = _ => new HttpRequestException("no route") };
        var offline = await Sender(down).SendAsync(new FeedbackNote("bug", "words", Screenshot: Png(10)));
        Assert.IsType<FeedbackResult.Offline>(offline);
        Assert.Equal("You're offline, so your note wasn't sent. Try again once this computer is back online.", offline.Message);
        Assert.IsType<FeedbackResult.Offline>(await Sender(down).SendAsync(new FeedbackNote("bug", "words")));

        var tooLong = new Recorded().Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "The area is 130 characters; the limit is 120.", new { reason = "too_long", field = "area", limit = 120, size = 130 });
        var area = Assert.IsType<FeedbackResult.TooLarge>(await Sender(tooLong).SendAsync(new FeedbackNote("bug", "words", Area: "Files")));
        Assert.Equal(("area", 130L, 120L, false), (area.Field, area.Size, area.Limit, area.CanSendWithoutPicture));
        Assert.Equal("Your note wasn't sent: the area is too long for the website.", area.Message);

        // A context the site measures too large is shortened and sent once more, never the same twice.
        var context = new Recorded()
            .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "The context is 140000 bytes; the limit is 131072.", new { reason = "too_large", field = "context", limit = 131072, size = 140000 })
            .Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
        var big = new JsonObject { ["log"] = new string('x', 60 * 1024), ["view"] = "files" };
        Assert.IsType<FeedbackResult.Sent>(await Sender(context).SendAsync(new FeedbackNote("bug", "words", Context: big)));
        var sizes = context.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Select(b => Encoding.UTF8.GetByteCount(b["p_context"]!.ToJsonString())).ToList();
        Assert.Equal(2, sizes.Count);
        Assert.True(sizes[1] <= FeedbackSender.ShortenedContextBytes && sizes[1] < sizes[0], string.Join(",", sizes));

        var kind = new Recorded().Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "The kind of note is bug, idea, praise or other.", new { reason = "kind", field = "kind" });
        Assert.Equal("The website refused the note: The kind of note is bug, idea, praise or other.",
            Assert.IsType<FeedbackResult.Failed>(await Sender(kind).SendAsync(new FeedbackNote("bug", "words"))).Message);

        // Signed out: plain words, nothing sent.
        var signedOut = new Recorded();
        var (api, sessions, http) = Client(signedOut);
        sessions.SignOut();
        var result = await new FeedbackSender(api, new FeedbackScreenshots(http, sessions), sessions, "0.3.3").SendAsync(new FeedbackNote("bug", "words"));
        Assert.Equal("Connect this computer to Armory first, then send it.", result.Message);
        Assert.Empty(signedOut.Calls);
    }

    [Fact]
    public async Task The_uploaders_saved_notes_go_through_the_sender_with_no_new_fields()
    {
        var folder = Directory.CreateTempSubdirectory("armory-feedback-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var glitch = new Glitch(IncidentDocument.NoteKind, "A note from the window (idea): Dark mode.", null);
            var incident = IncidentDocument.Build(glitch, new("0.3.3", "Windows 11", "LAB-PC-07", "alex.kim@students.test"), clock.GetUtcNow(), null, [], 0, 4000,
                new JsonObject { ["online"] = true }, ["2026-10-09T09:00:00.000Z sync: idle"], new IncidentFeedback("idea", "Dark mode."));
            incident[IncidentDocument.NoteOnlyField] = true;
            var file = store.Save(clock.GetUtcNow(), IncidentDocument.NoteKind, IncidentDocument.Render(incident, Scrubber.None));
            var recorded = new Recorded().Answer(ArmoryApi.SubmitFeedbackRpc, 200, Id());
            var (api, sessions, http) = Client(recorded);
            var sender = new FeedbackSender(api, new FeedbackScreenshots(http, sessions), sessions, "0.3.3", new SubmitLimiter(store, clock), clock);
            var uploader = new IncidentUploader(api, store, () => false, clock, feedback: sender);
            Assert.Same(sender.Limiter, uploader.Limiter);
            Assert.Equal(UploadOutcome.Sent, await uploader.SendFeedbackNowAsync(file));
            var note = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Single();
            Assert.Equal(8, note.Count);
            Assert.Equal(("idea", "Dark mode."), (note["p_kind"]!.GetValue<string>(), note["p_body"]!.GetValue<string>()));
            Assert.Null(note["p_tried"]);
            Assert.Null(note["p_area"]);
            Assert.Null(note["p_screenshot"]);
            Assert.Empty(store.Pending());

            // Neither form on the site: the note waits here (NotLive), as before.
            var second = store.Save(clock.GetUtcNow().AddMinutes(1), IncidentDocument.NoteKind, IncidentDocument.Render(incident, Scrubber.None));
            var none = new Recorded();
            var (api2, sessions2, http2) = Client(none);
            var waiting = new IncidentUploader(api2, store, () => false, clock,
                feedback: new FeedbackSender(api2, new FeedbackScreenshots(http2, sessions2), sessions2, "0.3.3", new SubmitLimiter(store, clock), clock));
            Assert.Equal(UploadOutcome.NotLive, await waiting.SendFeedbackNowAsync(second));
            Assert.Equal([8, 5], none.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Select(b => b.Count));
            Assert.Equal([second], store.Pending());
        }
        finally { folder.Delete(recursive: true); }
    }

    // ---- The version limits --------------------------------------------------------------------

    [Fact]
    public async Task A_heartbeats_version_is_cut_to_40_and_a_refused_version_is_never_sent_again()
    {
        var recorded = new Recorded().Answer("armory_heartbeat", 204, "");
        var (api, sessions, _) = Client(recorded);
        var longVersion = "0.3.3-preview.20261009+" + new string('f', 40);
        var heartbeat = new TeamHeartbeat(api, sessions, longVersion);
        Assert.Equal(40, heartbeat.AppVersion.Length);
        Assert.Equal(longVersion[..40], heartbeat.AppVersion);
        Assert.True(await heartbeat.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.Equal(longVersion[..40], recorded.BodiesOf("armory_heartbeat").Single()["p_app_version"]!.GetValue<string>());
        Assert.Equal("0.3.3", TeamHeartbeat.VersionFor(" 0.3.3 "));

        // If the server refused the version anyway (22023, field app_version), the beat goes again
        // at once without one, and so does every later beat: never the same refusal in a loop.
        var refusing = new Recorded()
            .Refuse("armory_heartbeat", 400, "22023", "app version is too long", new { reason = "too_long", field = "app_version", limit = 40, size = 41 })
            .Answer("armory_heartbeat", 204, "");
        var (api2, sessions2, _) = Client(refusing);
        var log = new List<string>();
        var guarded = new TeamHeartbeat(api2, sessions2, "0.3.3", log: log.Add);
        Assert.True(await guarded.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.True(await guarded.BeatAsync(TeamHeartbeat.Syncing, default));
        var beats = refusing.BodiesOf("armory_heartbeat").ToList();
        Assert.Equal(3, beats.Count);
        Assert.Equal("0.3.3", beats[0]["p_app_version"]!.GetValue<string>());
        Assert.Null(beats[1]["p_app_version"]);
        Assert.Null(beats[2]["p_app_version"]);
        Assert.Single(log, l => l.Contains("refused the version", StringComparison.Ordinal));
    }

    [Fact]
    public void Feedback_takes_64_characters_of_version_and_never_a_blank_one()
    {
        Assert.Equal(64, FeedbackSender.FitVersion(new string('9', 70)).Length);
        Assert.Equal("unknown", FeedbackSender.FitVersion("  "));
        Assert.Equal("0.3.3", FeedbackSender.FitVersion("0.3.3"));
        Assert.Equal(["bug", "idea", "praise", "other"], FeedbackSender.Kinds);
        Assert.Equal("praise", FeedbackSender.KindOf(" PRAISE "));
        Assert.Equal("other", FeedbackSender.KindOf(null));
    }
}
