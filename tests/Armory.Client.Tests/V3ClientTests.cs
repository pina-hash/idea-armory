using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Telemetry;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The v0.3 client paths (ARMORY.md "The v0.3 server contract", items 2 to 6) against a
// recorded-response layer: each RPC answers what PostgREST answers for the 0233 bodies, status,
// SQLSTATE, message and JSON DETAIL included. No database: every case here is one answer.
public sealed class V3ClientTests
{
    // PostgREST in a box: answers queued per RPC (the last one repeats), every request kept.
    private sealed class Recorded : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(int Status, string Body)>> answers = new(StringComparer.Ordinal);
        public List<(string Function, JsonObject Body)> Calls { get; } = [];

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

        public IEnumerable<JsonObject> BodiesOf(string function) => Calls.Where(c => c.Function == function).Select(c => c.Body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.StartsWith("/rest/v1/rpc/", path);
            var function = path["/rest/v1/rpc/".Length..];
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            lock (Calls) Calls.Add((function, body));
            if (!answers.TryGetValue(function, out var queue) || queue.Count == 0)
                return Json(404, $$"""{"code":"PGRST202","message":"Could not find the function public.{{function}}","details":null,"hint":null}""");
            var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Json(status, text);
        }

        private static HttpResponseMessage Json(int status, string body)
            => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static (ArmoryApi Api, SessionManager Sessions) Client(Recorded recorded)
    {
        var sessions = new SessionManager(new HttpClient(recorded), new InMemorySecretStore());
        sessions.SignIn(new ArmorySession("https://project.supabase.test", "anon-key", "access-token", "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1), "alex.kim@students.test", Guid.Parse("11111111-2222-3333-4444-555555555555"), "LAB-PC-07"));
        return (new ArmoryApi(new PostgrestClient(new HttpClient(recorded), sessions)), sessions);
    }

    private static readonly Guid Project = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid File1 = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    [Fact]
    public async Task Not_a_project_member_reads_alike_from_P0001_and_42501_and_nothing_else_does()
    {
        var recorded = new Recorded()
            .Refuse("armory_list_changes", 400, "P0001", "not a project member")
            .Refuse("armory_acquire_lock", 400, "P0001", "not a project member")
            .Refuse("armory_save_side_version", 400, "P0001", "not a project member")
            .Refuse("armory_project_files", 403, "42501", "not a project member")
            .Refuse("armory_file_history", 403, "42501", "not a project member")
            .Refuse("armory_create_file", 403, "42501", "not a project member")
            .Refuse("armory_move_file", 403, "42501", "not a project member")
            .Refuse("armory_break_lock", 400, "P0001", "only a mentor or cad_lead may break a lock")
            .Refuse("armory_add_member", 403, "42501", "only a mentor or CAD lead may add members");
        var (api, _) = Client(recorded);
        var device = Guid.NewGuid();
        var refusals = new List<ArmoryRpcException>
        {
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.ListChangesAsync(Project, 0)),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.AcquireLockAsync(File1, device, Guid.NewGuid())),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SaveSideVersionAsync(File1, null, "k", new string('a', 64), 1, "r", device, Guid.NewGuid())),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.ProjectFilesAsync(Project)),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.FileHistoryAsync(File1)),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.CreateFileAsync(Project, "", "Plate.SLDPRT", device, Guid.NewGuid())),
            await Assert.ThrowsAsync<ArmoryRpcException>(() => api.MoveFileAsync(File1, "", "Plate2.SLDPRT", device, Guid.NewGuid())),
        };
        Assert.All(refusals, r => Assert.True(r.IsNotMember, $"{r.SqlState} {r.Message}"));
        Assert.Equal(["P0001", "P0001", "P0001", "42501", "42501", "42501", "42501"], refusals.Select(r => r.SqlState));
        // The same SQLSTATEs with other words are other refusals.
        var takeBack = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.BreakLockAsync(File1, device, Guid.NewGuid()));
        Assert.False(takeBack.IsNotMember);
        Assert.False(takeBack.IsForbidden); // P0001, though it is a "may not": branch on the code and the words
        var add = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.AddMemberAsync(Project, "sam@students.test", MemberRole.Student, Guid.NewGuid()));
        Assert.False(add.IsNotMember);
        Assert.True(add.IsForbidden);
    }

    [Fact]
    public async Task Refusals_are_told_apart_by_SQLSTATE_and_reason_never_by_the_status()
    {
        // 23505 and 23503 are both 409; 55006 and P0002 are both 500; a bare 403 from a proxy has no code.
        var recorded = new Recorded()
            .Refuse("armory_create_file", 409, "23503", "insert or update violates a foreign key")
            .Refuse("armory_rename_folder", 500, "P0002", "project not found")
            .Refuse("armory_delete_folder", 500, "55006", "files are checked out", new { reason = "checked_out", names = new[] { "Plate.SLDPRT" }, total = 2 })
            .Answer("armory_my_projects", 403, "<html>Forbidden by proxy</html>");
        var (api, _) = Client(recorded);
        var device = Guid.NewGuid();
        var foreignKey = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.CreateFileAsync(Project, "", "Plate.SLDPRT", device, Guid.NewGuid()));
        Assert.Equal(409, foreignKey.Status);
        Assert.False(foreignKey.IsNameTaken);
        var missing = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.RenameFolderAsync(Project, "A", "B", device, Guid.NewGuid()));
        var inUse = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.DeleteFolderAsync(Project, "A", device, Guid.NewGuid()));
        Assert.Equal(missing.Status, inUse.Status);
        Assert.False(missing.IsInUse);
        Assert.True(inUse.IsInUse);
        Assert.Equal("checked_out", inUse.Reason);
        Assert.Equal(2, inUse.Detail!.Total);
        Assert.Equal(["Plate.SLDPRT"], inUse.Detail.Names);
        var proxy = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.MyProjectsAsync());
        Assert.Equal(403, proxy.Status);
        Assert.Null(proxy.SqlState);
        Assert.False(proxy.IsForbidden);
    }

    [Fact]
    public async Task PT429_is_an_answer_with_its_wait_and_a_gateway_429_is_a_busy_site()
    {
        var recorded = new Recorded()
            .Refuse(ArmoryApi.SubmitFeedbackRpc, 429, "PT429", "too many reports", new { reason = "rate_limited", limit = 20, window_seconds = 3600, retry_after_seconds = 1234 })
            .Answer(ArmoryApi.SubmitIncidentRpc, 429, "rate limit exceeded");
        var (api, _) = Client(recorded);
        var limited = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "words", "0.3.0", "LAB-PC-07", []));
        Assert.True(limited.IsRateLimited);
        Assert.Equal("rate_limited", limited.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1234), limited.RetryAfter);
        Assert.Equal(20, limited.Detail!.Limit);
        Assert.Single(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc)); // a refusal is never sent again by the client itself
        await Assert.ThrowsAsync<ArmoryOfflineException>(() => api.SubmitAppIncidentAsync("crash", "s", "0.3.0", null, null, [], null));
    }

    [Fact]
    public async Task Too_large_and_too_long_are_the_only_22023_to_shorten()
    {
        var recorded = new Recorded()
            .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "context too_large", new { reason = "too_large", field = "context", limit = 131072, size = 150000 })
            .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "body too_long", new { reason = "too_long", field = "body", limit = 8000, size = 8100 })
            .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "kind kind", new { reason = "kind", field = "kind" });
        var (api, _) = Client(recorded);
        var large = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "w", "0.3.0", null, []));
        Assert.True(large.IsTooLarge);
        Assert.Equal(("context", 131072L, 150000L), (large.Detail!.Field, large.Detail.Limit!.Value, large.Detail.Size!.Value));
        var longBody = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "w", "0.3.0", null, []));
        Assert.True(longBody.IsTooLarge);
        var kind = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "w", "0.3.0", null, []));
        Assert.False(kind.IsTooLarge);
        Assert.True(kind.IsInvalidInput);
        Assert.Equal("kind", kind.Reason);
    }

    [Fact]
    public async Task A_batch_reports_each_file_and_takes_500_distinct_files_at_most()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var recorded = new Recorded().Answer("armory_lock_files", 200, $$"""
            {"total":3,"succeeded":2,"refused":1,"results":[
              {"file_id":"{{a}}","ok":true,"acquired":true},
              {"file_id":"{{b}}","ok":true,"acquired":false},
              {"file_id":"{{c}}","ok":false,"code":"P0001","message":"not a project member"}]}
            """).Answer("armory_release_locks", 200, $$"""{"total":1,"succeeded":1,"refused":0,"results":[{"file_id":"{{a}}","ok":true,"released":true}]}""");
        var (api, _) = Client(recorded);
        var device = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var locked = await api.LockFilesAsync([c, a, b], device, operation);
        Assert.Equal((3, 2, 1), (locked.Total, locked.Succeeded, locked.Refused));
        Assert.Equal([(a, true, true), (b, true, false), (c, false, false)], locked.Results.Select(r => (r.FileId, r.Ok, r.Done)));
        Assert.Equal(("P0001", "not a project member"), (locked.Results[2].Code, locked.Results[2].Message));
        // The files go in id order, with the device and the caller's operation id.
        var sent = recorded.BodiesOf("armory_lock_files").Single();
        Assert.Equal([a.ToString(), b.ToString(), c.ToString()], sent["p_files"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(device.ToString(), sent["p_device"]!.GetValue<string>());
        Assert.Equal(operation.ToString(), sent["p_operation"]!.GetValue<string>());
        Assert.True((await api.ReleaseLocksAsync([a], device, Guid.NewGuid())).Results.Single().Done);

        // 1 to 500 distinct files a call; Chunk makes the calls.
        await Assert.ThrowsAsync<ArgumentException>(() => api.LockFilesAsync([], device, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => api.LockFilesAsync([a, a], device, Guid.NewGuid()));
        var many = Enumerable.Range(0, 1201).Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => api.LockFilesAsync(many, device, Guid.NewGuid()));
        var chunks = ArmoryApi.Chunk(many.Concat(many.Take(10)));
        Assert.Equal([500, 500, 201], chunks.Select(x => x.Length));
        Assert.Equal(many.Order(), chunks.SelectMany(x => x));
        Assert.Single(recorded.BodiesOf("armory_lock_files")); // nothing invalid was ever sent
    }

    [Fact]
    public async Task Purged_is_a_time_or_null_and_folder_purged_names_its_files()
    {
        var recorded = new Recorded().Answer("armory_project_purged", 200, "\"2026-10-08T17:30:00+00:00\"").Answer("armory_project_purged", 200, "null");
        var (api, _) = Client(recorded);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 17, 30, 0, TimeSpan.Zero), await api.ProjectPurgedAsync(Project));
        Assert.Null(await api.ProjectPurgedAsync(Project));
        Assert.Equal(Project.ToString(), recorded.BodiesOf("armory_project_purged").First()["p_project"]!.GetValue<string>());

        var payload = new JsonObject { ["folder"] = "Old/Tests", ["files"] = 2, ["file_ids"] = new JsonArray(File1.ToString(), Project.ToString()), ["by"] = "pina@ideabosco.test" };
        var purge = FolderPurge.From(new RemoteChange(9, Project, "folder_purged", Project, payload, DateTimeOffset.UtcNow));
        Assert.NotNull(purge);
        Assert.Equal(("Old/Tests", 2, "pina@ideabosco.test"), (purge.Folder, purge.Files, purge.By));
        Assert.Equal([File1, Project], purge.FileIds);
        Assert.Null(FolderPurge.From(new RemoteChange(10, Project, "tombstone", File1, [], DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task My_projects_carries_can_take_back_and_an_older_server_does_not()
    {
        var recorded = new Recorded().Answer("armory_my_projects", 200, $$"""
            [{"id":"{{Project}}","name":"Robot 2027","season":2027,"role":"student","pinned_release":2025,"release_gate":"warn","archived":false,"can_take_back":true},
             {"id":"{{File1}}","name":"Class","season":null,"role":"mentor","pinned_release":2025,"release_gate":"warn","archived":false}]
            """);
        var (api, _) = Client(recorded);
        var projects = await api.MyProjectsAsync();
        Assert.Equal([(MemberRole.Student, (bool?)true), (MemberRole.Mentor, (bool?)null)], projects.Select(p => (p.Role, p.CanTakeBack)));
    }

    [Fact]
    public async Task Heartbeats_carry_the_device_version_and_state_go_at_once_on_a_change_and_never_throw()
    {
        var recorded = new Recorded().Answer("armory_heartbeat", 204, "");
        var (api, sessions) = Client(recorded);
        var log = new List<string>();
        var heartbeat = new TeamHeartbeat(api, sessions, "0.3.0", log: log.Add);
        using var stop = new CancellationTokenSource();
        var running = heartbeat.RunAsync(stop.Token);
        await Until(() => recorded.BodiesOf("armory_heartbeat").Count() == 1);
        heartbeat.SetState(TeamHeartbeat.Syncing);
        await Until(() => recorded.BodiesOf("armory_heartbeat").Count() == 2);
        heartbeat.SetState(TeamHeartbeat.Syncing); // the same state again sends nothing new
        await Task.Delay(200);
        Assert.Equal(2, recorded.BodiesOf("armory_heartbeat").Count());
        await stop.CancelAsync();
        await running;
        Assert.True(await heartbeat.SayGoodbyeAsync(TimeSpan.FromSeconds(5)));
        var beats = recorded.BodiesOf("armory_heartbeat").ToList();
        Assert.All(beats, b => Assert.Equal("11111111-2222-3333-4444-555555555555", b["p_device"]!.GetValue<string>()));
        Assert.All(beats, b => Assert.Equal("0.3.0", b["p_app_version"]!.GetValue<string>()));
        Assert.Equal(["idle", "syncing", "offline-soon"], beats.Select(b => b["p_state"]!.GetValue<string>()));

        // Refused (another computer took this device's place) or a site without it: logged once, never thrown.
        var refused = new Recorded().Refuse("armory_heartbeat", 400, "P0001", "device is not registered to caller");
        var (api2, sessions2) = Client(refused);
        var log2 = new List<string>();
        var second = new TeamHeartbeat(api2, sessions2, "0.3.0", log: log2.Add);
        Assert.False(await second.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.False(await second.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.Single(log2, l => l.Contains("device is not registered to caller", StringComparison.Ordinal));
        var missing = new Recorded(); // 404 PGRST202 for everything
        var (api3, sessions3) = Client(missing);
        var third = new TeamHeartbeat(api3, sessions3, "0.3.0");
        Assert.False(await third.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.False(await third.BeatAsync(TeamHeartbeat.Idle, default));
        Assert.Single(missing.Calls); // not asked again for hours
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    // ---- The uploader (item 4) ----------------------------------------------------------

    private static string SaveReport(IncidentStore store, DateTimeOffset at, string kind = GlitchKinds.UserReport, int logLines = 50, bool noteOnly = false, int lineLength = 60)
    {
        var glitch = GlitchRules.UserReport("bug", "Check in spun on Gearbox.SLDASM.");
        var events = new JsonArray();
        for (var i = 0; i < 3000; i++) events.Add(new JsonObject { ["seq"] = i, ["kind"] = "rpc", ["fn"] = "armory_list_changes", ["ms"] = 12, ["status"] = 200 });
        var log = Enumerable.Range(0, logLines).Select(i => $"2026-10-08T17:00:{i % 60:00}.000Z sync: line {i} " + new string('x', lineLength)).ToList();
        var incident = IncidentDocument.Build(glitch with { Kind = kind }, new("0.3.0", "Windows 11", "LAB-PC-07", "alex.kim@students.test"), at, null, events, 3000, 4000,
            new JsonObject { ["online"] = true, ["notes"] = new string('n', 2000) }, log, new IncidentFeedback("bug", "Check in spun on Gearbox.SLDASM."));
        if (noteOnly) incident[IncidentDocument.NoteOnlyField] = true;
        return store.Save(at, kind, IncidentDocument.Render(incident, Scrubber.None, 4 * 1024 * 1024));
    }

    [Fact]
    public async Task PT429_waits_the_sites_retry_after_then_sends_again_across_a_restart()
    {
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveReport(store, clock.GetUtcNow());
            var recorded = new Recorded()
                .Refuse(ArmoryApi.SubmitFeedbackRpc, 429, "PT429", "too many reports", new { reason = "rate_limited", limit = 20, window_seconds = 3600, retry_after_seconds = 600 })
                .Answer(ArmoryApi.SubmitFeedbackRpc, 200, "\"0f000000-0000-0000-0000-000000000001\"")
                .Answer(ArmoryApi.SubmitIncidentRpc, 200, "\"0e000000-0000-0000-0000-000000000001\"");
            var (api, _) = Client(recorded);
            var log = new List<string>();
            var uploader = new IncidentUploader(api, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.RateLimited, await uploader.SendFeedbackNowAsync(file));
            Assert.Contains(log, l => l.Contains("sent again in 600 s", StringComparison.Ordinal));
            Assert.Equal([file], store.Pending());
            // Nothing is sent before the wait is over, here or after a restart.
            Assert.Equal(UploadOutcome.RateLimited, await uploader.SendFeedbackNowAsync(file));
            clock.Advance(TimeSpan.FromMinutes(9));
            var restarted = new IncidentUploader(api, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.Waiting, await restarted.StepAsync());
            Assert.Single(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(UploadOutcome.Sent, await restarted.StepAsync());
            Assert.Equal(2, recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Count());
            var incident = recorded.BodiesOf(ArmoryApi.SubmitIncidentRpc).Single();
            Assert.Equal("0f000000-0000-0000-0000-000000000001", incident["p_feedback"]!.GetValue<string>());
            Assert.Empty(store.Pending());
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Too_large_is_shortened_and_sent_once_and_never_the_same_payload_again()
    {
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveReport(store, clock.GetUtcNow(), logLines: 300, lineLength: 400);
            var recorded = new Recorded()
                .Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "context too_large", new { reason = "too_large", field = "context", limit = 131072, size = 140000 })
                .Answer(ArmoryApi.SubmitFeedbackRpc, 200, "\"0f000000-0000-0000-0000-000000000002\"")
                .Refuse(ArmoryApi.SubmitIncidentRpc, 400, "22023", "report too_large", new { reason = "too_large", field = "report", limit = 1048576, size = 1100000 })
                .Answer(ArmoryApi.SubmitIncidentRpc, 200, "\"0e000000-0000-0000-0000-000000000002\"");
            var (api, _) = Client(recorded);
            var log = new List<string>();
            var uploader = new IncidentUploader(api, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            var notes = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Select(b => b["p_context"]!.ToJsonString()).ToList();
            Assert.Equal(2, notes.Count);
            // Within the cap as sent from the start, smaller again after the answer, never the same twice.
            Assert.True(Encoding.UTF8.GetByteCount(notes[0]) <= IncidentUploader.MaximumContextBytes, $"{notes[0].Length}");
            Assert.True(Encoding.UTF8.GetByteCount(notes[1]) <= IncidentUploader.ShortenedContextBytes, $"{notes[1].Length}");
            Assert.NotEqual(notes[0], notes[1]);
            var reports = recorded.BodiesOf(ArmoryApi.SubmitIncidentRpc).Select(b => b["p_report"]!.ToJsonString()).ToList();
            Assert.Equal(2, reports.Count);
            Assert.True(Encoding.UTF8.GetByteCount(reports[0]) <= IncidentUploader.MaximumReportBytes);
            Assert.True(Encoding.UTF8.GetByteCount(reports[1]) <= IncidentUploader.ShortenedReportBytes);
            Assert.NotEqual(reports[0], reports[1]);
            Assert.Equal(2, log.Count(l => l.Contains("shortened and sent once more", StringComparison.Ordinal)));
            Assert.Empty(store.Pending());

            // Too large even shortened: held, and the first payload is never sent again.
            var second = SaveReport(store, clock.GetUtcNow().AddMinutes(5), logLines: 300, lineLength: 400);
            var stubborn = new Recorded().Refuse(ArmoryApi.SubmitFeedbackRpc, 400, "22023", "context too_large", new { reason = "too_large", field = "context", limit = 131072, size = 140000 });
            var (api2, _) = Client(stubborn);
            var again = new IncidentUploader(api2, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.Held, await again.SendFeedbackNowAsync(second));
            Assert.Equal(2, stubborn.Calls.Count);
            Assert.Empty(store.Pending());
            Assert.Contains(store.All(), f => IncidentStore.IsMarked(f, IncidentStore.HeldMark));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task Any_other_22023_is_a_bug_logged_here_and_never_retried()
    {
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveReport(store, clock.GetUtcNow(), kind: GlitchKinds.SlowPass);
            var recorded = new Recorded()
                .Answer(ArmoryApi.SubmitFeedbackRpc, 200, "\"0f000000-0000-0000-0000-000000000003\"")
                .Refuse(ArmoryApi.SubmitIncidentRpc, 400, "22023", "feedback feedback_not_found", new { reason = "feedback_not_found", field = "feedback" });
            var (api, _) = Client(recorded);
            var log = new List<string>();
            var uploader = new IncidentUploader(api, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.Held, await uploader.StepAsync());
            Assert.Contains(log, l => l.Contains("22023 feedback_not_found", StringComparison.Ordinal) && l.Contains("a bug in this app", StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(UploadOutcome.Nothing, await uploader.StepAsync());
            Assert.Single(recorded.BodiesOf(ArmoryApi.SubmitIncidentRpc));
            Assert.True(IncidentStore.IsMarked(Assert.Single(store.All()), IncidentStore.HeldMark));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_note_on_its_own_sends_its_words_and_no_incident()
    {
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveReport(store, clock.GetUtcNow(), kind: IncidentDocument.NoteKind, noteOnly: true);
            var recorded = new Recorded().Answer(ArmoryApi.SubmitFeedbackRpc, 200, "\"0f000000-0000-0000-0000-000000000004\"");
            var (api, _) = Client(recorded);
            var uploader = new IncidentUploader(api, store, () => false, clock);
            Assert.Equal(UploadOutcome.Sent, await uploader.SendFeedbackNowAsync(file));
            var note = recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc).Single();
            Assert.Equal(("bug", "Check in spun on Gearbox.SLDASM.", "0.3.0", "LAB-PC-07"),
                (note["p_kind"]!.GetValue<string>(), note["p_body"]!.GetValue<string>(), note["p_app_version"]!.GetValue<string>(), note["p_device_name"]!.GetValue<string>()));
            Assert.Empty(store.Pending());
            Assert.Equal(UploadOutcome.Nothing, await uploader.StepAsync());
            Assert.Empty(recorded.BodiesOf(ArmoryApi.SubmitIncidentRpc));
        }
        finally { folder.Delete(recursive: true); }
    }

    // The race in 0.3.1's field log: saving the note woke the background round, which sent it
    // (and marked it sent) before Send feedback asked; Send feedback said "couldn't send". It
    // went, once, and Send feedback says so.
    [Fact]
    public async Task A_note_the_background_round_already_sent_is_sent_not_held()
    {
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveReport(store, clock.GetUtcNow(), kind: IncidentDocument.NoteKind, noteOnly: true);
            var recorded = new Recorded().Answer(ArmoryApi.SubmitFeedbackRpc, 200, "\"0f000000-0000-0000-0000-000000000005\"");
            var (api, _) = Client(recorded);
            var log = new List<string>();
            var uploader = new IncidentUploader(api, store, () => false, clock, log.Add);
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            Assert.Equal(UploadOutcome.Sent, await uploader.SendFeedbackNowAsync(file));
            Assert.Single(recorded.BodiesOf(ArmoryApi.SubmitFeedbackRpc));
            Assert.DoesNotContain(log, l => l.Contains("held", StringComparison.Ordinal));
            Assert.Empty(store.Pending());
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Other_peoples_addresses_never_reach_a_report()
    {
        var scrubber = new Scrubber(ownEmail: () => "alex.kim@students.test");
        Assert.Equal("Plate is checked out by [address] on LAB-PC-07; me: alex.kim@students.test",
            scrubber.Scrub("Plate is checked out by maria.lopez@students.test on LAB-PC-07; me: alex.kim@students.test"));
        var tree = new JsonObject { ["a"] = new JsonArray("sam.lee@boscotech.edu", "no address here"), ["b"] = "pina@ideabosco.test said hi" };
        scrubber.ScrubTree(tree);
        Assert.Equal("""{"a":["[address]","no address here"],"b":"[address] said hi"}""", tree.ToJsonString());
    }
}
