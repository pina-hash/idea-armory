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
}
