using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Telemetry;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The client's part of a computer shared by several students (docs/agent/PROFILES.md): removing
// a student ends their sign-in on the server as far as it can and always forgets it here, and a
// saved note goes only while the student who wrote it is the one in use (F13).
public sealed class SharedComputerClientTests
{
    // Supabase's auth endpoints and PostgREST, answering from a list; every request kept.
    private sealed class Site : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Bearer, string? ApiKey, string Body)> Requests { get; } = [];
        public bool Offline { get; set; }
        public int LogoutStatus { get; set; } = 204;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("offline (test)");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null, body));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/logout") return new HttpResponseMessage((HttpStatusCode)LogoutStatus);
            if (path == "/auth/v1/token")
                return Json(200, """{"access_token":"renewed-access","refresh_token":"renewed-refresh","expires_in":3600}""");
            if (path == "/rest/v1/rpc/" + ArmoryApi.SubmitFeedbackRpc) return Json(200, "\"0f000000-0000-0000-0000-0000000000aa\"");
            return Json(404, """{"code":"PGRST202","message":"Could not find the function","details":null,"hint":null}""");
        }

        private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static ArmorySession Session(string email, TimeSpan lifetime) => new("https://project.supabase.test", "anon-key", "access-" + email, "refresh-" + email,
        DateTimeOffset.UtcNow + lifetime, email, Guid.NewGuid(), "LAB-PC-14");

    [Fact]
    public async Task Removing_a_sign_in_ends_it_on_the_server_when_it_can_and_always_forgets_it_here()
    {
        var site = new Site();
        var secrets = new InMemorySecretStore();
        var sessions = new SessionManager(new HttpClient(site), secrets);
        sessions.SignIn(Session("alex.kim@students.test", TimeSpan.FromHours(1)));
        var signedOut = 0;
        sessions.SignedOut += () => signedOut++;
        Assert.True(await sessions.SignOutSessionAsync());
        var logout = Assert.Single(site.Requests);
        Assert.Equal((HttpMethod.Post, "/auth/v1/logout?scope=local", "access-alex.kim@students.test", "anon-key"), (logout.Method, logout.Path, logout.Bearer, logout.ApiKey));
        Assert.Null(sessions.Current);
        Assert.Null(secrets.Read("armory-session"));
        Assert.Equal(1, signedOut);
        // Nothing signed in: nothing sent.
        Assert.False(await sessions.SignOutSessionAsync());
        Assert.Single(site.Requests);

        // An old access token is renewed first (the sign-out needs a live one).
        sessions.SignIn(Session("alex.kim@students.test", TimeSpan.FromSeconds(10)));
        Assert.True(await sessions.SignOutSessionAsync());
        Assert.Equal(["/auth/v1/token?grant_type=refresh_token", "/auth/v1/logout?scope=local"], site.Requests.Skip(1).Select(r => r.Path));
        Assert.Equal("renewed-access", site.Requests[^1].Bearer);

        // Offline or refused: forgotten here all the same.
        sessions.SignIn(Session("alex.kim@students.test", TimeSpan.FromHours(1)));
        site.Offline = true;
        Assert.False(await sessions.SignOutSessionAsync());
        Assert.Null(secrets.Read("armory-session"));
        site.Offline = false;
        site.LogoutStatus = 401;
        sessions.SignIn(Session("alex.kim@students.test", TimeSpan.FromHours(1)));
        Assert.False(await sessions.SignOutSessionAsync());
        Assert.Null(sessions.Current);
    }

    private static string SaveNote(IncidentStore store, DateTimeOffset at, string? email, string words)
    {
        var glitch = GlitchRules.UserReport("idea", words);
        var incident = IncidentDocument.Build(glitch with { Kind = IncidentDocument.NoteKind }, new("0.3.3", "Windows 11", "LAB-PC-14", email), at, null, new JsonArray(), 0, 4000,
            new JsonObject { ["online"] = true }, [], new IncidentFeedback("idea", words));
        incident[IncidentDocument.NoteOnlyField] = true;
        return store.Save(at, IncidentDocument.NoteKind, IncidentDocument.Render(incident, Scrubber.None, 4 * 1024 * 1024));
    }

    private static string? Words(string body) => JsonNode.Parse(body)!["p_body"]!.GetValue<string>();

    [Fact]
    public async Task A_note_is_sent_only_while_its_writer_is_in_use()
    {
        var folder = Directory.CreateTempSubdirectory("armory-profiles-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var alexNote = SaveNote(store, clock.GetUtcNow(), "alex.kim@students.test", "Alex's idea");
            clock.Advance(TimeSpan.FromSeconds(1));
            var nobodysNote = SaveNote(store, clock.GetUtcNow(), null, "Written while nobody was signed in");
            var site = new Site();
            var sessions = new SessionManager(new HttpClient(site), new InMemorySecretStore());
            sessions.SignIn(Session("jordan.reyes@students.test", TimeSpan.FromHours(1)));
            var api = new ArmoryApi(new PostgrestClient(new HttpClient(site), sessions));
            var inUse = "jordan.reyes@students.test";
            var uploader = new IncidentUploader(api, store, () => false, clock) { WriterInUse = () => inUse };

            // Jordan is in use: Alex's words wait (asked directly, too), the one with no writer goes.
            Assert.Equal(UploadOutcome.Waiting, await uploader.SendFeedbackNowAsync(alexNote));
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            var sent = Assert.Single(site.Requests, r => r.Path.Contains(ArmoryApi.SubmitFeedbackRpc, StringComparison.Ordinal));
            Assert.Equal("Written while nobody was signed in", Words(sent.Body));
            clock.Advance(IncidentUploader.Every);
            Assert.Equal(UploadOutcome.Waiting, await uploader.StepAsync());
            Assert.Equal([alexNote], store.Pending());

            // Alex is in use again: his note goes, under his account.
            inUse = "alex.kim@students.test";
            Assert.Equal(UploadOutcome.Sent, await uploader.SendFeedbackNowAsync(alexNote));
            Assert.Equal("Alex's idea", Words(site.Requests[^1].Body));
            Assert.Empty(store.Pending());

            // One student per computer (no WriterInUse): everything goes, as before.
            var maria = SaveNote(store, clock.GetUtcNow(), "maria.lopez@students.test", "Maria's idea");
            Assert.Equal(UploadOutcome.Sent, await new IncidentUploader(api, store, () => false, clock).SendFeedbackNowAsync(maria));
        }
        finally { folder.Delete(recursive: true); }
    }
}
