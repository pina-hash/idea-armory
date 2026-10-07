using System.Text.Json.Nodes;
using Armory.Telemetry;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The incident uploader against the fake Supabase (docs/agent/TELEMETRY.md, "Upload"). The
// site's armory_submit_app_feedback and armory_submit_app_incident are not live yet
// (docs/agent/website-requests-v0.3.md, 4 and 4b): PostgREST answers 404 PGRST202 for them, so
// the incidents wait here. The stand-ins below are created in the test database to play the
// site's migration arriving, with the argument names and types the request names.
public sealed partial class ClientTests
{
    private const string WebsiteStandIn = """
        create table public.test_app_feedback (id uuid primary key default gen_random_uuid(), email text not null, device_name text,
            app_version text not null, kind text check (kind in ('bug','idea','other')), body text not null, context jsonb not null);
        create table public.test_app_incidents (id uuid primary key default gen_random_uuid(), email text not null, device_name text,
            app_version text not null, kind text not null, summary text not null check (length(summary) <= 500), project_id uuid,
            report jsonb not null, feedback_id uuid references public.test_app_feedback);
        create function public.armory_submit_app_feedback(p_kind text, p_body text, p_app_version text, p_device_name text, p_context jsonb)
        returns uuid language sql security definer as $$
            insert into public.test_app_feedback (email, device_name, app_version, kind, body, context)
            values (current_setting('armory.test_email', true), p_device_name, p_app_version, p_kind, p_body, p_context) returning id $$;
        create function public.armory_submit_app_incident(p_kind text, p_summary text, p_app_version text, p_device_name text, p_project uuid,
            p_report jsonb, p_feedback uuid)
        returns uuid language sql security definer as $$
            insert into public.test_app_incidents (email, device_name, app_version, kind, summary, project_id, report, feedback_id)
            values (current_setting('armory.test_email', true), p_device_name, p_app_version, p_kind, p_summary, p_project, p_report, p_feedback) returning id $$;
        grant execute on function public.armory_submit_app_feedback(text, text, text, text, jsonb) to authenticated;
        grant execute on function public.armory_submit_app_incident(text, text, text, text, uuid, jsonb, uuid) to authenticated;
        """;

    private static async Task ShipWebsiteAsync(Env env)
    {
        await using var connection = await env.Db.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(WebsiteStandIn, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<object?[]>> RowsAsync(Env env, string sql)
    {
        await using var connection = await env.Db.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static string SaveIncident(IncidentStore store, DateTimeOffset at, string kind, IncidentFeedback? feedback = null)
    {
        var glitch = feedback is null ? new Glitch(kind, $"A {kind} on the test PC.", null) : GlitchRules.UserReport(feedback.Kind, feedback.Body);
        var events = new JsonArray(new JsonObject { ["seq"] = 1, ["kind"] = "passEnd", ["ms"] = 75_000 });
        var incident = IncidentDocument.Build(glitch, new("0.3.0", "Windows 11", "LAB-PC-07", "alex.kim@students.test"), at, null, events, 1, 4000,
            new JsonObject { ["online"] = true }, ["2026-10-07T18:00:00.000Z pass: ended after 75000 ms (loop)"], feedback);
        return store.Save(at, glitch.Kind, IncidentDocument.Render(incident, Scrubber.None));
    }

    [PostgresFact]
    public async Task Incidents_wait_while_the_site_lacks_the_rpc_and_go_once_it_has_it()
    {
        await using var env = await Env.StartAsync();
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        var folder = Directory.CreateTempSubdirectory("armory-incidents-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var log = new List<string>();
            var file = SaveIncident(store, clock.GetUtcNow(), GlitchKinds.SlowPass);
            var uploader = new IncidentUploader(api, store, () => false, clock, log.Add);

            // Not live: kept here, said only in the log, and not asked again for 6 hours.
            Assert.Equal(UploadOutcome.NotLive, await uploader.StepAsync());
            Assert.Equal(1, env.Supabase.RpcCount(ArmoryApi.SubmitIncidentRpc));
            Assert.Equal([file], store.Pending());
            Assert.Contains(log, l => l.Contains("the site has no armory_submit_app_incident yet", StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(UploadOutcome.Waiting, await uploader.StepAsync());
            // A restart remembers the wait.
            var restarted = new IncidentUploader(api, store, () => false, clock);
            Assert.True(restarted.IsWaiting(ArmoryApi.SubmitIncidentRpc));
            Assert.Equal(UploadOutcome.Waiting, await restarted.StepAsync());
            clock.Advance(TimeSpan.FromHours(5));
            Assert.Equal(UploadOutcome.Waiting, await restarted.StepAsync());
            Assert.Equal(1, env.Supabase.RpcCount(ArmoryApi.SubmitIncidentRpc));

            // The site ships its migration; six hours after the first try, the incident goes.
            await ShipWebsiteAsync(env);
            clock.Advance(TimeSpan.FromMinutes(56));
            Assert.Equal(UploadOutcome.Sent, await restarted.StepAsync());
            Assert.Empty(store.Pending());
            Assert.True(IncidentStore.IsMarked(Assert.Single(store.All()), IncidentStore.SentMark));
            var row = Assert.Single(await RowsAsync(env, "select email, kind, summary, device_name, app_version, report->>'kind', feedback_id from public.test_app_incidents"));
            Assert.Equal(new object?[] { "alex.kim@students.test", "slowPass", "A slowPass on the test PC.", "LAB-PC-07", "0.3.0", "slowPass", null }, row);
            Assert.Equal(UploadOutcome.Nothing, await restarted.StepAsync());
        }
        finally { folder.Delete(recursive: true); }
    }

    [PostgresFact]
    public async Task Incidents_go_one_a_minute_and_never_while_a_transfer_runs()
    {
        await using var env = await Env.StartAsync();
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        await ShipWebsiteAsync(env);
        var folder = Directory.CreateTempSubdirectory("armory-incidents-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            SaveIncident(store, clock.GetUtcNow(), GlitchKinds.Crash);
            SaveIncident(store, clock.GetUtcNow().AddSeconds(1), GlitchKinds.ReadOnlyBroken);
            var transferring = true;
            var uploader = new IncidentUploader(api, store, () => transferring, clock);
            Assert.Equal(UploadOutcome.Busy, await uploader.StepAsync());
            Assert.Equal(0, env.Supabase.RpcCount(ArmoryApi.SubmitIncidentRpc));
            transferring = false;
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            Assert.Equal(UploadOutcome.TooSoon, await uploader.StepAsync());
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(UploadOutcome.TooSoon, await uploader.StepAsync());
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            // Oldest first.
            Assert.Equal(["crash", "readOnlyBroken"], (await RowsAsync(env, "select kind from public.test_app_incidents order by report->>'createdAt'")).Select(r => (string)r[0]!));
        }
        finally { folder.Delete(recursive: true); }
    }

    [PostgresFact]
    public async Task A_report_sends_its_words_first_and_its_incident_is_linked_to_them()
    {
        await using var env = await Env.StartAsync();
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        var folder = Directory.CreateTempSubdirectory("armory-incidents-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var file = SaveIncident(store, clock.GetUtcNow(), GlitchKinds.UserReport, new IncidentFeedback("bug", "Check in spun for a minute on Gearbox.SLDASM."));
            var uploader = new IncidentUploader(api, store, () => false, clock);
            // The site has neither RPC: the words are saved here, and nothing more is asked.
            Assert.Equal(UploadOutcome.NotLive, await uploader.SendFeedbackNowAsync(file));
            Assert.Equal(UploadOutcome.NotLive, await uploader.SendFeedbackNowAsync(file));
            Assert.Equal(UploadOutcome.Waiting, await uploader.StepAsync());
            Assert.Equal(1, env.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc));
            Assert.Equal(0, env.Supabase.RpcCount(ArmoryApi.SubmitIncidentRpc));

            await ShipWebsiteAsync(env);
            clock.Advance(IncidentUploader.NotLiveRetry);
            Assert.Equal(UploadOutcome.Sent, await uploader.SendFeedbackNowAsync(file));
            var feedback = Assert.Single(await RowsAsync(env, "select id, kind, body, context->'log'->>0 from public.test_app_feedback"));
            Assert.Equal("bug", feedback[1]);
            Assert.Equal("Check in spun for a minute on Gearbox.SLDASM.", feedback[2]);
            Assert.Contains("pass: ended", (string)feedback[3]!);
            // The incident follows on the uploader's own round, linked to those words, once.
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            var incident = Assert.Single(await RowsAsync(env, "select kind, feedback_id from public.test_app_incidents"));
            Assert.Equal(new object?[] { "userReport", feedback[0] }, incident);
            Assert.Equal(2, env.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc));
        }
        finally { folder.Delete(recursive: true); }
    }

    [PostgresFact]
    public async Task Every_call_and_transfer_goes_into_the_flight_recorder_without_a_token()
    {
        await using var env = await Env.StartAsync();
        var issued = env.Supabase.IssueSession("alex.kim@students.test");
        var sessions = new SessionManager(env.Http, new InMemorySecretStore());
        sessions.SignIn(new ArmorySession(env.Supabase.SupabaseUrl, env.Supabase.AnonKey, issued.AccessToken, issued.RefreshToken, issued.ExpiresAt,
            "alex.kim@students.test", Guid.Empty, "test PC"));
        var recorder = new FlightRecorder();
        var api = new ArmoryApi(new PostgrestClient(env.Http, sessions, recorder));
        Assert.Empty(await api.MyProjectsAsync());
        await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppIncidentAsync("crash", "x", "0.3.0", null, null, new JsonObject(), null));
        var events = recorder.Snapshot();
        Assert.Equal([("armory_my_projects", 200, true, (string?)null), ("armory_submit_app_incident", 404, false, "PGRST202")],
            events.Select(e => (e.Name!, e.Status, e.Ok, e.Detail)));
        foreach (var e in events)
            Assert.DoesNotContain(issued.AccessToken, string.Join("|", e.Name, e.Target, e.Detail, e.Stack));
    }
}
