using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Telemetry;

namespace Armory.Agent.Tests;

// The window's Send feedback and "Your feedback" (0.3.3, docs/agent/CLIENT.md section 7 and
// BRIDGE.md): a note without a picture is saved first and sent (the durable path); a note with a
// picture of the window goes now and is never written to disk; a picture that can't go keeps the
// note and offers it without the picture; Your feedback says where each note is, in plain
// words, and hides itself on a site without it. Against a recorded PostgREST and Storage.
public sealed class FeedbackDeskTests
{
    private const string Supabase = "https://project.supabase.test";
    private static readonly Guid User = Guid.Parse("5e0c9a3b-1f2d-4c6e-8a7b-9d0e1f2a3b4c");

    // A GoTrue-shaped access token whose sub is the auth uid (the Storage folder).
    private static string Jwt()
    {
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("""{"alg":"HS256","typ":"JWT"}""") + "." + B64($$"""{"aud":"authenticated","sub":"{{User}}","role":"authenticated"}""") + ".c2lnbmF0dXJl";
    }

    // PostgREST and Storage in a box: every request kept; an answer per path can be set.
    private sealed class Site : HttpMessageHandler
    {
        public List<(string Path, JsonObject? Body, byte[] Bytes)> Calls { get; } = [];
        public Func<string, int, HttpResponseMessage?>? Answer { get; set; }
        public bool Offline { get; set; }
        public string MyFeedback { get; set; } = "[]";

        public IEnumerable<JsonObject> Submits => Calls.Where(c => c.Path.EndsWith(ArmoryApi.SubmitFeedbackRpc, StringComparison.Ordinal)).Select(c => c.Body!);
        public IEnumerable<byte[]> Uploads => Calls.Where(c => c.Path.StartsWith("/storage/v1/object/", StringComparison.Ordinal)).Select(c => c.Bytes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var storage = path.StartsWith("/storage/v1/object/", StringComparison.Ordinal);
            int count;
            lock (Calls)
            {
                Calls.Add((path, storage ? null : JsonNode.Parse(bytes) as JsonObject, bytes));
                count = Calls.Count(c => c.Path == path);
            }
            if (Offline) throw new HttpRequestException("no route to the site");
            if (Answer?.Invoke(path, count) is { } answer) return answer;
            if (storage) return Json(200, """{"Key":"armory-feedback-shots/x","Id":"1"}""");
            if (path.EndsWith(ArmoryApi.SubmitFeedbackRpc, StringComparison.Ordinal)) return Json(200, "\"" + Guid.NewGuid() + "\"");
            if (path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal)) return Json(200, MyFeedback);
            return Missing(path);
        }
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Missing(string path)
        => Json(404, $$"""{"code":"PGRST202","message":"Could not find the function {{path}} in the schema cache","details":null,"hint":null}""");
    private static HttpResponseMessage Refusal(int status, string code, string message, object? detail = null)
        => Json(status, new JsonObject { ["code"] = code, ["message"] = message, ["details"] = detail is null ? null : JsonSerializer.Serialize(detail), ["hint"] = null }.ToJsonString());

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // The desk as AgentHost wires it: one telemetry (incidents folder, uploader), one sender over
    // its limiter, the window's picture store.
    private sealed class Desk : IAsyncDisposable
    {
        public required TempFolder Temp { get; init; }
        public required AgentPaths Paths { get; init; }
        public required AgentTelemetry Telemetry { get; init; }
        public required SessionManager Sessions { get; init; }
        public required FeedbackSender Sender { get; init; }
        public required WindowShots Shots { get; init; }
        public required FeedbackDesk Front { get; init; }
        public required Site Site { get; init; }
        public required Clock Clock { get; init; }

        // The notes and incidents saved (never the last flight, which keeps only events).
        public string[] Incidents() => Directory.Exists(Paths.IncidentsFolder)
            ? [.. Directory.GetFiles(Paths.IncidentsFolder, "*.json.gz").Where(f => !f.EndsWith("last-flight.json.gz", StringComparison.Ordinal))]
            : [];

        public async ValueTask DisposeAsync()
        {
            await Telemetry.DisposeAsync();
            Temp.Dispose();
        }
    }

    private static Desk Start()
    {
        var temp = new TempFolder();
        var paths = new AgentPaths(temp.Root, true);
        Directory.CreateDirectory(paths.LogFolder);
        File.WriteAllLines(paths.LogFile, ["2026-10-09T15:00:00.000Z check out: Plate.SLDPRT is held by maria.lopez@students.test"]);
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        var telemetry = new AgentTelemetry(paths, log);
        var site = new Site();
        var http = new HttpClient(site);
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession(Supabase, "anon-key-6f3a1c9e2b7d4085", Jwt(), "v1.refresh-token-abcdef", DateTimeOffset.UtcNow.AddHours(1),
            "alex.kim@students.test", Guid.NewGuid(), "LAB-PC-07"));
        var api = new ArmoryApi(new PostgrestClient(http, sessions, telemetry.Recorder));
        var clock = new Clock(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero));
        var sender = new FeedbackSender(api, new FeedbackScreenshots(http, sessions, telemetry.Recorder), sessions, AgentPaths.Version, telemetry.Limiter, clock);
        telemetry.Attach(() => sessions.Current, _ => Task.FromResult<JsonNode?>(new JsonObject { ["online"] = true, ["holder"] = "sam.lee@students.test" }),
            () => new JsonObject { ["quick"] = true }, api, () => false, sender);
        var shots = new WindowShots();
        return new Desk
        {
            Temp = temp, Paths = paths, Telemetry = telemetry, Sessions = sessions, Sender = sender, Shots = shots, Site = site, Clock = clock,
            Front = new FeedbackDesk(sender, telemetry, api, sessions, shots, clock, log.Info),
        };
    }

    // A PNG of the window: the signature, IHDR with its size, and bytes no other file holds.
    private static byte[] WindowPng(int width = 1120, int height = 760, int bytes = 4096)
    {
        var png = new byte[bytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        Random.Shared.NextBytes(png.AsSpan(33));
        return png;
    }

    // True when any file under the folder holds these bytes, plain or gzip-compressed.
    private static bool AnyFileHolds(string folder, byte[] needle)
    {
        var mark = needle.AsSpan(40, 64).ToArray();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.AsSpan().IndexOf(mark) >= 0) return true;
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                try { gzip.CopyTo(plain); }
                catch (InvalidDataException) { continue; }
                if (plain.ToArray().AsSpan().IndexOf(mark) >= 0) return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task Send_feedback_without_a_picture_is_saved_first_and_with_one_goes_now_and_is_never_saved()
    {
        await using var d = Start();
        // Without a picture: saved in the incidents folder, then sent, with what was tried and the area.
        var plain = await d.Front.SendAsync("praise", "Check in all is fast now.", "Checked in 200 files.", "Settings", null);
        Assert.Equal(new ActionResult(true, "Sent. Thank you for the feedback."), plain);
        var saved = Assert.Single(d.Incidents());
        Assert.EndsWith("-note.sent.json.gz", saved);
        var words = IncidentDocument.Read(File.ReadAllBytes(saved))["feedback"]!;
        Assert.Equal(("praise", "Checked in 200 files.", "Settings"), (words["kind"]!.GetValue<string>(), words["tried"]!.GetValue<string>(), words["area"]!.GetValue<string>()));
        var first = Assert.Single(d.Site.Submits);
        Assert.Equal(("praise", "Checked in 200 files.", "Settings"), (first["p_kind"]!.GetValue<string>(), first["p_tried"]!.GetValue<string>(), first["p_area"]!.GetValue<string>()));
        Assert.Null(first["p_screenshot"]);

        // With a picture: uploaded and named by the note at once; nothing more on disk, ever.
        var png = WindowPng();
        var shot = d.Shots.Answer(new WindowCapture(png, false));
        Assert.True(shot.Ok);
        var sent = await d.Front.SendAsync("bug", "Maria (maria.lopez@students.test) had it open.", "Asked sam.lee@students.test.", "File details: Gear.SLDPRT", shot.Id);
        Assert.Equal(new ActionResult(true, "Sent. Thank you for the feedback."), sent);
        Assert.Equal(png, Assert.Single(d.Site.Uploads));
        var note = d.Site.Submits.Last();
        Assert.Equal(8, note.Count);
        Assert.StartsWith(User + "/", note["p_screenshot"]!.GetValue<string>());
        // Scrubbed exactly as a saved note: other people's addresses masked, here and in the context.
        Assert.Equal(("bug", "Maria ([address]) had it open.", "Asked [address].", "File details: Gear.SLDPRT"),
            (note["p_kind"]!.GetValue<string>(), note["p_body"]!.GetValue<string>(), note["p_tried"]!.GetValue<string>(), note["p_area"]!.GetValue<string>()));
        var context = note["p_context"]!.ToJsonString();
        Assert.Contains("is held by [address]", context);
        Assert.DoesNotContain("maria.lopez@students.test", context);
        Assert.DoesNotContain("sam.lee@students.test", context);
        Assert.Single(d.Incidents());
        Assert.False(AnyFileHolds(d.Temp.Root, png));
        // Sent: the window forgets it.
        Assert.Null(d.Shots.Get(shot.Id));
    }

    [Fact]
    public async Task A_picture_the_window_no_longer_holds_is_offered_without_it()
    {
        await using var d = Start();
        var gone = await d.Front.SendAsync("bug", "Spins.", null, "Home", new string('a', 32));
        Assert.Equal(new ActionResult(false, WindowShots.Gone, ActionResult.WithoutPicture), gone);
        // Only the last picture is held: an older one is gone too.
        var older = d.Shots.Answer(new WindowCapture(WindowPng(), false));
        var newer = d.Shots.Answer(new WindowCapture(WindowPng(), false));
        Assert.Equal(ActionResult.WithoutPicture, (await d.Front.SendAsync("bug", "Spins.", null, "Home", older.Id)).Offer);
        Assert.Empty(d.Site.Calls);
        Assert.Empty(d.Incidents());
        Assert.NotNull(d.Shots.Get(newer.Id));
        // No words: refused here, the picture kept for the next try.
        Assert.Equal(new ActionResult(false, "Write a few words first."), await d.Front.SendAsync("bug", "  ", null, "Home", newer.Id));
        Assert.NotNull(d.Shots.Get(newer.Id));
    }

    [Fact]
    public async Task A_refused_or_offline_picture_offers_the_note_without_it_and_that_goes_once()
    {
        // Storage refuses the picture (already there): the note isn't sent; offered without it.
        await using (var d = Start())
        {
            d.Site.Answer = (path, _) => path.StartsWith("/storage/", StringComparison.Ordinal)
                ? Json(400, """{"statusCode":"409","code":"KeyAlreadyExists","error":"Duplicate","message":"The resource already exists"}""") : null;
            var shot = d.Shots.Answer(new WindowCapture(WindowPng(), false));
            var refused = await d.Front.SendAsync("bug", "Spins.", "Restarted.", "Home", shot.Id);
            Assert.Equal((false, ActionResult.WithoutPicture), (refused.Ok, refused.Offer));
            Assert.Equal("Your note wasn't sent: Armory's file storage wouldn't take the screenshot. You can send it without the picture.", refused.Message);
            Assert.Empty(d.Site.Submits);
            Assert.Empty(d.Incidents());
            // Without the picture: one saved note, one submit, with every other field.
            Assert.Equal(new ActionResult(true, "Sent. Thank you for the feedback."), await d.Front.SendAsync("bug", "Spins.", "Restarted.", "Home", null));
            var note = Assert.Single(d.Site.Submits);
            Assert.Equal(("Restarted.", "Home"), (note["p_tried"]!.GetValue<string>(), note["p_area"]!.GetValue<string>()));
            Assert.Null(note["p_screenshot"]);
            Assert.Single(d.Incidents());
        }

        // Offline: the words stay; without the picture the note is saved and goes later.
        await using (var d = Start())
        {
            d.Site.Offline = true;
            var png = WindowPng();
            var shot = d.Shots.Answer(new WindowCapture(png, false));
            Assert.Equal(new ActionResult(false, FeedbackDesk.OfflineWithPicture, ActionResult.WithoutPicture), await d.Front.SendAsync("idea", "Dark mode.", null, "Home", shot.Id));
            Assert.Empty(d.Incidents());
            Assert.NotNull(d.Shots.Get(shot.Id)); // kept for a try once online
            Assert.Equal(new ActionResult(true, "Saved. It will be sent when this computer is back online."), await d.Front.SendAsync("idea", "Dark mode.", null, "Home", null));
            var waiting = Assert.Single(d.Incidents());
            Assert.DoesNotContain(".sent.", waiting);
            Assert.False(AnyFileHolds(d.Temp.Root, png));
        }

        // The hour's limit: said with when, and the note without the picture waits for it.
        await using (var d = Start())
        {
            d.Site.Answer = (path, _) => path.EndsWith(ArmoryApi.SubmitFeedbackRpc, StringComparison.Ordinal)
                ? Refusal(429, "PT429", "Too many feedback notes from this account in the last hour.", new { reason = "rate_limited", limit = 20, window_seconds = 3600, retry_after_seconds = 1500 })
                : null;
            var shot = d.Shots.Answer(new WindowCapture(WindowPng(), false));
            var limited = await d.Front.SendAsync("idea", "Dark mode.", null, "Home", shot.Id);
            Assert.Equal(new ActionResult(false,
                "You've sent a lot of feedback this hour. Try again in 25 minutes. Or send it without the picture, and Armory sends it then.", ActionResult.WithoutPicture), limited);
            var submits = d.Site.Submits.Count();
            Assert.Equal(new ActionResult(true, "Saved. You've sent a lot today, so it will be sent in a little while."), await d.Front.SendAsync("idea", "Dark mode.", null, "Home", null));
            Assert.Equal(submits, d.Site.Submits.Count()); // nothing more goes before the wait is over
            Assert.Single(d.Incidents());
        }
    }

    [Fact]
    public async Task Your_feedback_reads_shown_missing_offline_or_signed_out_with_plain_status_words()
    {
        await using var d = Start();
        d.Site.MyFeedback = """
            [{"id":"0f000000-0000-0000-0000-000000000004","created_at":"2026-10-09T14:00:00+00:00","kind":"praise","body":"Fast now.","tried":null,"area":"Settings",
              "has_screenshot":false,"app_version":"0.3.3","device_name":"LAB-PC-07","status":"new","reviewed_at":null},
             {"id":"0f000000-0000-0000-0000-000000000003","created_at":"2026-10-09T13:00:00+00:00","kind":"bug","body":"Spins.","tried":"Restarted.","area":"Home",
              "has_screenshot":true,"app_version":"0.3.3","device_name":"LAB-PC-07","status":"seen","reviewed_at":"2026-10-09T13:30:00+00:00"},
             {"id":"0f000000-0000-0000-0000-000000000002","created_at":"2026-10-08T13:00:00+00:00","kind":"idea","body":"Dark mode.","tried":null,"area":null,
              "has_screenshot":false,"app_version":"0.3.2","device_name":null,"status":"resolved","reviewed_at":"2026-10-09T08:00:00+00:00"},
             {"id":"0f000000-0000-0000-0000-000000000001","created_at":"2026-10-07T13:00:00+00:00","kind":"other","body":"Buy now!","tried":null,"area":null,
              "has_screenshot":false,"app_version":"0.3.2","device_name":null,"status":"spam","reviewed_at":"2026-10-08T08:00:00+00:00"}]
            """;
        var shown = await d.Front.ReadAsync();
        Assert.Equal((FeedbackListView.Shown, true, (string?)null), (shown.State, shown.Pictures, shown.Message));
        Assert.Equal(["Not read yet", "Read by the IDEA team", "Done", "Closed"], shown.Notes.Select(n => n.StatusWords));
        Assert.Equal(["new", "seen", "resolved", "closed"], shown.Notes.Select(n => n.Status)); // spam reads closed
        var bug = shown.Notes[1];
        Assert.Equal(("bug", "Restarted.", "Home", true, "LAB-PC-07", "2026-10-09T13:00:00Z", "2026-10-09T13:30:00Z"),
            (bug.Kind, bug.Tried, bug.Area, bug.HasScreenshot, bug.DeviceName, bug.CreatedAt, bug.ReviewedAt));
        Assert.Equal(FeedbackDesk.ListLimit, d.Site.Calls.Single(c => c.Path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal)).Body!["p_limit"]!.GetValue<int>());
        // The page reads exactly these fields from the message.
        using (var message = JsonDocument.Parse(BridgeMessages.MyFeedbackMessage("r4", shown)))
            Assert.Equal(["message", "notes", "pictures", "requestId", "state", "type"], message.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // A site that can't take pictures (no eight-argument form): the list, and no picture
        // offered. Praise went as other there, and the answer says so.
        d.Site.Answer = (path, count) => path.EndsWith(ArmoryApi.SubmitFeedbackRpc, StringComparison.Ordinal) && count == 1 ? Missing(path) : null;
        Assert.Equal(new ActionResult(true, "Sent. Thank you for the feedback. The website doesn't take praise yet, so it went as other feedback."),
            await d.Front.SendAsync("praise", "Fast now.", null, "Home", null));
        Assert.Equal("other", d.Site.Submits.Last()["p_kind"]!.GetValue<string>());
        Assert.True(d.Sender.NewFieldsMissing);
        Assert.False((await d.Front.ReadAsync()).Pictures);

        // Offline, or the site refusing it some other way: said plainly, and offered again later.
        d.Site.Offline = true;
        var offline = await d.Front.ReadAsync();
        Assert.Equal((FeedbackListView.Offline, false, FeedbackDesk.ListOffline), (offline.State, offline.Pictures, offline.Message));
        Assert.Empty(offline.Notes);
        d.Site.Offline = false;
        d.Site.Answer = (path, _) => path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal) ? Refusal(500, "XX000", "internal error") : null;
        Assert.Equal((FeedbackListView.Failed, FeedbackDesk.ListFailed), ((await d.Front.ReadAsync()).State, (await d.Front.ReadAsync()).Message));
        d.Site.Answer = (path, _) => path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal) ? Refusal(403, "42501", "not signed in") : null;
        Assert.Equal(FeedbackListView.SignedOut, (await d.Front.ReadAsync()).State);

        // A site without it: hidden, and not asked again for an hour.
        d.Site.Answer = (path, _) => path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal) ? Missing(path) : null;
        Assert.Equal(FeedbackListView.Missing, (await d.Front.ReadAsync()).State);
        var asked = d.Site.Calls.Count(c => c.Path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal));
        Assert.Equal(FeedbackListView.Missing, (await d.Front.ReadAsync()).State);
        Assert.Equal(asked, d.Site.Calls.Count(c => c.Path.EndsWith(ArmoryApi.MyFeedbackRpc, StringComparison.Ordinal)));
        d.Clock.Now += FeedbackDesk.MissingRetry;
        d.Site.Answer = null;
        Assert.Equal(FeedbackListView.Shown, (await d.Front.ReadAsync()).State);

        // Signed out: nothing is asked.
        d.Sessions.SignOut();
        var calls = d.Site.Calls.Count;
        Assert.Equal(FeedbackListView.SignedOut, (await d.Front.ReadAsync()).State);
        Assert.Equal(calls, d.Site.Calls.Count);
    }

    [Fact]
    public void The_window_keeps_only_its_last_picture_in_memory()
    {
        var shots = new WindowShots();
        Assert.Equal(WindowShots.Refused(WindowShots.NotTaken), shots.Answer(null));
        Assert.False(shots.Answer(new WindowCapture(new byte[4096], false)).Ok); // not a PNG
        var huge = shots.Answer(new WindowCapture(WindowPng(bytes: (int)FeedbackScreenshots.MaximumBytes + 1), true));
        Assert.Equal((false, WindowShots.TooLarge), (huge.Ok, huge.Message));

        var png = WindowPng(1680, 1140, 300_000);
        var first = shots.Answer(new WindowCapture(png, true));
        Assert.True(first.Ok);
        Assert.Matches("^[0-9a-f]{32}$", first.Id!);
        Assert.Equal(("https://armory.local/shot/" + first.Id + ".png", 1680, 1140, 300_000L, true, (string?)null),
            (first.Url, first.Width, first.Height, first.Bytes, first.Scaled, first.Message));
        Assert.Same(png, shots.Get(first.Id)!.Png); // exactly the bytes shown are the bytes sent
        Assert.Equal(first.Id, WindowShots.IdOfPath("/shot/" + first.Id + ".png"));
        foreach (var bad in new[] { "/shot/" + first.Id, "/shot/../x.png", "/thumb/" + first.Id + ".png", "/shot/" + first.Id!.ToUpperInvariant() + ".png", "/shot/.png" })
            Assert.Null(WindowShots.IdOfPath(bad));

        // A new picture replaces the last one; a sent one is forgotten; nothing else is kept.
        var second = shots.Answer(new WindowCapture(WindowPng(), false));
        Assert.Null(shots.Get(first.Id));
        Assert.NotNull(shots.Get(second.Id));
        Assert.Null(shots.Get(null));
        shots.Forget(second.Id!);
        Assert.Null(shots.Get(second.Id));
        Assert.True(WindowShots.IsId(second.Id));
        Assert.False(WindowShots.IsId("9F2C4E0A1B3D4C5E8F7A6B5C4D3E2F10"));
        Assert.False(WindowShots.IsId(new string('a', 31)));

        // The DevTools call that takes it again smaller: the page's own viewport at that scale.
        Assert.Equal("""{"format":"png","captureBeyondViewport":false,"clip":{"x":0,"y":0,"width":1120,"height":760,"scale":0.5}}""",
            WindowShots.CaptureParameters(1120, 760, 0.5));
    }
}
