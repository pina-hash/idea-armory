using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Client;
using Armory.Telemetry;

namespace Armory.Agent.Tests;

// The agent's telemetry (docs/agent/TELEMETRY.md): an incident written while a session with
// known tokens is signed in holds none of them, wherever they turned up (an exception, a
// notice, a log line, the snapshot, the person's own words), and Report a problem saves the
// words and says so plainly while the website's half is not live.
public sealed class AgentTelemetryTests
{
    private const string Access = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGV4In0.c2lnbmF0dXJlLW9mLXRoZS10b2tlbg";
    private const string Refresh = "v1.Mr7qZb2nXcK4pLw9Ty3hUe";
    private const string Anon = "anon-key-6f3a1c9e2b7d4085";

    // PostgREST before the website's migration: every submit RPC is "function not found".
    private sealed class NotLiveYet : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"code\":\"PGRST202\",\"message\":\"Could not find the function\",\"details\":null,\"hint\":null}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (AgentTelemetry Telemetry, AgentPaths Paths, NotLiveYet Network) Start(TempFolder temp, bool signedIn = true)
    {
        var paths = new AgentPaths(temp.Root, true);
        Directory.CreateDirectory(paths.LogFolder);
        // Lines as an older version might have left them, before any scrubbing.
        File.WriteAllLines(paths.LogFile, ["2026-10-07T18:00:00.000Z started 0.2.1", "2026-10-07T18:00:01.000Z refresh_token=" + Refresh,
            "2026-10-07T18:00:02.000Z Authorization: Bearer " + Access]);
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        var telemetry = new AgentTelemetry(paths, log);
        var network = new NotLiveYet();
        var secrets = new InMemorySecretStore();
        var http = new HttpClient(network);
        var sessions = new SessionManager(http, secrets);
        if (signedIn)
            sessions.SignIn(new ArmorySession("https://project.supabase.test", Anon, Access, Refresh, DateTimeOffset.UtcNow.AddHours(1),
                "alex.kim@students.test", Guid.NewGuid(), "LAB-PC-07"));
        telemetry.Attach(() => sessions.Current,
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["online"] = true, ["lastError"] = "401 for token " + Access, ["key"] = Anon }),
            () => new JsonObject { ["quick"] = true },
            new ArmoryApi(new PostgrestClient(http, sessions, telemetry.Recorder)), () => false);
        return (telemetry, paths, network);
    }

    // PostgREST after 0233: every armory_submit_app_feedback answers with a new id.
    private sealed class Live : HttpMessageHandler
    {
        public List<(string Path, JsonObject Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            lock (Calls) Calls.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"" + Guid.NewGuid() + "\"", Encoding.UTF8, "application/json") };
        }
    }

    private static string AllIncidentText(AgentPaths paths)
        => string.Join("\n", Directory.EnumerateFiles(paths.IncidentsFolder, "*.json.gz")
            .Select(f => JsonSerializer.Serialize(IncidentDocument.Read(File.ReadAllBytes(f)))));

    [Fact]
    public async Task An_incident_from_a_session_with_known_tokens_holds_none_of_them()
    {
        using var temp = new TempFolder();
        var (telemetry, paths, _) = Start(temp);
        await using (telemetry)
        {
            var recorder = telemetry.Recorder;
            recorder.Rpc("armory_project_files", 30, 401, "JWT " + Access + " expired");
            recorder.Notice("cantSend", "Robot 2027/Plate.SLDPRT", "refused: {\"refresh_token\":\"" + Refresh + "\"}");
            recorder.Note("headers", "apikey: " + Anon);
            recorder.FileFailed("Robot 2027/Plate.SLDPRT", new IOException("upload to https://storage.test/x?X-Amz-Signature=" + Refresh));
            var (ok, message) = await telemetry.ReportProblemAsync("bug", "It froze after I pasted " + Access);
            Assert.True(ok);
            Assert.Equal("Saved. It will be sent when the website is ready.", message);
            telemetry.CrashNow("unhandled exception", new InvalidOperationException("token " + Access + " and " + Refresh));

            var text = AllIncidentText(paths);
            Assert.Equal(2, Directory.EnumerateFiles(paths.IncidentsFolder, "*.json.gz").Count());
            Assert.DoesNotContain(Access, text);
            Assert.DoesNotContain(Refresh, text);
            Assert.DoesNotContain(Anon, text);
            Assert.DoesNotContain("eyJ", text);
            Assert.Contains(Scrubber.Mask, text);
            // What is meant to be there is: who, where, and what happened.
            Assert.Contains("alex.kim@students.test", text);
            Assert.Contains("LAB-PC-07", text);
            Assert.Contains("Robot 2027/Plate.SLDPRT", text);
            Assert.Contains("It froze after I pasted", text);
            Assert.Contains("armory_project_files", text);
            // And the last flight on disk holds none of them either.
            telemetry.LastFlight.Write();
            var flight = JsonSerializer.Serialize(telemetry.LastFlight.TryRead());
            Assert.DoesNotContain(Access, flight);
            Assert.DoesNotContain(Refresh, flight);
        }
        Assert.False(File.Exists(paths.LastFlightFile)); // a clean stop leaves no last flight
    }

    [Fact]
    public async Task Report_a_problem_needs_words_keeps_them_here_and_never_shows_the_site_not_ready_as_an_error()
    {
        using var temp = new TempFolder();
        var (telemetry, paths, network) = Start(temp);
        await using (telemetry)
        {
            Assert.Equal((false, "Write a few words about what happened."), await telemetry.ReportProblemAsync("bug", "   "));
            Assert.False(Directory.Exists(paths.IncidentsFolder) && Directory.EnumerateFiles(paths.IncidentsFolder, "*.json.gz").Any());
            var (ok, message) = await telemetry.ReportProblemAsync("rant", string.Join(" ", Enumerable.Repeat("word", 2000)));
            Assert.True(ok);
            Assert.Equal("Saved. It will be sent when the website is ready.", message);
            var file = Assert.Single(Directory.EnumerateFiles(paths.IncidentsFolder, "*-userReport.json.gz"));
            var incident = IncidentDocument.Read(File.ReadAllBytes(file));
            Assert.Equal("other", incident["feedback"]!["kind"]!.GetValue<string>());
            Assert.Equal(AgentTelemetry.MaximumReportCharacters, incident["feedback"]!["body"]!.GetValue<string>().Length);
            Assert.Null(incident["feedbackId"]);
            // A second report a moment later is saved too, and the site is not asked again.
            var calls = network.Calls;
            Assert.Equal("Saved. It will be sent when the website is ready.", (await telemetry.ReportProblemAsync("idea", "Dark mode for the tray.")).Message);
            Assert.Equal(calls, network.Calls);
            Assert.Equal(2, Directory.EnumerateFiles(paths.IncidentsFolder, "*-userReport.json.gz").Count());
            Assert.Contains(AgentTelemetry.ReportKinds, k => k == "idea");
        }
    }

    // Send feedback (v0.3): the words go as a note on their own, with Armory's version and what it
    // was doing, never an incident after them, and never another person's address.
    [Fact]
    public async Task Send_feedback_sends_a_note_on_its_own_with_no_other_persons_address()
    {
        using var temp = new TempFolder();
        var paths = new AgentPaths(temp.Root, true);
        Directory.CreateDirectory(paths.LogFolder);
        File.WriteAllLines(paths.LogFile, ["2026-10-08T18:00:00.000Z started 0.3.0", "2026-10-08T18:00:01.000Z check out: Plate.SLDPRT is held by maria.lopez@students.test"]);
        var log = new AgentLog(paths.LogFile, paths.CrashFile);
        var network = new Live();
        var http = new HttpClient(network);
        var sessions = new SessionManager(http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession("https://project.supabase.test", Anon, Access, Refresh, DateTimeOffset.UtcNow.AddHours(1), "alex.kim@students.test", Guid.NewGuid(), "LAB-PC-07"));
        await using var telemetry = new AgentTelemetry(paths, log);
        telemetry.Attach(() => sessions.Current, _ => Task.FromResult<JsonNode?>(new JsonObject { ["online"] = true, ["holder"] = "sam.lee@students.test" }),
            () => new JsonObject { ["quick"] = true }, new ArmoryApi(new PostgrestClient(http, sessions, telemetry.Recorder)), () => false);
        Assert.Equal((false, "Write a few words first."), await telemetry.SendFeedbackAsync("idea", "  "));
        Assert.Empty(network.Calls);
        Assert.Equal((true, "Sent. Thank you for the feedback."), await telemetry.SendFeedbackAsync("idea", "Show who is online on the team page."));
        var (path, note) = Assert.Single(network.Calls);
        Assert.Equal("/rest/v1/rpc/armory_submit_app_feedback", path);
        Assert.Equal(("idea", "Show who is online on the team page.", AgentPaths.Version, "LAB-PC-07"),
            (note["p_kind"]!.GetValue<string>(), note["p_body"]!.GetValue<string>(), note["p_app_version"]!.GetValue<string>(), note["p_device_name"]!.GetValue<string>()));
        var context = note["p_context"]!.ToJsonString();
        Assert.Contains("check out: Plate.SLDPRT is held by [address]", context);
        Assert.DoesNotContain("maria.lopez@students.test", context);
        Assert.DoesNotContain("sam.lee@students.test", context);
        Assert.DoesNotContain(Access, context);
        // Kept as sent; no incident follows a note.
        var file = Assert.Single(Directory.EnumerateFiles(paths.IncidentsFolder, "*-note.sent.json.gz"));
        Assert.True(IncidentDocument.Read(File.ReadAllBytes(file))[IncidentDocument.NoteOnlyField]!.GetValue<bool>());
        Assert.DoesNotContain(network.Calls, c => c.Path.EndsWith("armory_submit_app_incident", StringComparison.Ordinal));
    }
}
