using System.Text.Json.Nodes;
using Armory.Telemetry;
using Armory.TestSupport;

namespace Armory.Client.Tests;

// The v0.3 calls through the fake Supabase over PostgreSQL, against the test stand-in for the
// 0233 parts the app calls (ArmoryV3StandIn): the shapes travel through PostgREST's status
// mapping (PT429 as 429) and the limits are PostgreSQL's own measure (pg_column_size).
public sealed partial class ClientTests
{
    private static async Task<(SessionManager Sessions, ArmoryApi Api, Guid Device)> WithDeviceAsync(Env env, string email)
    {
        var (sessions, api, _, _) = env.SignedIn(email);
        var device = await api.RegisterDeviceAsync("LAB-PC-07", Guid.NewGuid());
        sessions.SignIn(sessions.Current! with { DeviceId = device, DeviceName = "LAB-PC-07" });
        return (sessions, api, device);
    }

    [PostgresFact]
    public async Task Heartbeats_stamp_the_computer_and_a_null_keeps_what_was_sent()
    {
        await using var env = await Env.StartAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(env.Db);
        var (sessions, api, device) = await WithDeviceAsync(env, "alex.kim@students.test");
        var heartbeat = new TeamHeartbeat(api, sessions, "0.3.0");
        Assert.True(await heartbeat.BeatAsync(TeamHeartbeat.Idle, default));
        var row = Assert.Single(await RowsAsync(env, "select app_version, state, last_seen is not null, heartbeat_writes from public.armory_devices"));
        Assert.Equal(new object?[] { "0.3.0", "idle", true, 1 }, row);
        // Within 20 seconds and nothing changed: the server writes nothing (the app adds no suppression).
        Assert.True(await heartbeat.BeatAsync(TeamHeartbeat.Idle, default));
        await api.HeartbeatAsync(device, null, null);
        Assert.Equal(1, (int)(await RowsAsync(env, "select heartbeat_writes from public.armory_devices"))[0][0]!);
        Assert.True(await heartbeat.BeatAsync(TeamHeartbeat.Syncing, default));
        Assert.Equal(new object?[] { "0.3.0", "syncing", 2 }, (await RowsAsync(env, "select app_version, state, heartbeat_writes from public.armory_devices"))[0]);
        Assert.True(await heartbeat.SayGoodbyeAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("offline-soon", (await RowsAsync(env, "select state from public.armory_devices"))[0][0]);
        // Another person's computer: P0001, logged, never thrown.
        var log = new List<string>();
        var (other, otherApi, _) = await WithDeviceAsync(env, "maria.lopez@students.test");
        other.SignIn(other.Current! with { DeviceId = device });
        Assert.False(await new TeamHeartbeat(otherApi, other, "0.3.0", log: log.Add).BeatAsync(TeamHeartbeat.Idle, default));
        Assert.Contains(log, l => l.Contains("P0001", StringComparison.Ordinal) && l.Contains("device is not registered to caller", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task The_sites_limits_answer_PT429_and_too_large_as_sent_and_the_uploader_stays_under_them()
    {
        await using var env = await Env.StartAsync();
        await ArmoryV3StandIn.ApplyReportsAsync(env.Db);
        var (_, api, _, _) = env.SignedIn("alex.kim@students.test");
        // too_large is measured as sent: a 200 KiB context is refused, with its limit and size.
        var big = new JsonObject { ["log"] = new string('x', 200 * 1024) };
        var large = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("bug", "words", "0.3.0", "LAB-PC-07", big));
        Assert.True(large.IsTooLarge);
        Assert.Equal(("context", 131072L), (large.Detail!.Field, large.Detail.Limit!.Value));
        Assert.Equal(400, large.Status);
        var kind = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("praise", "words", "0.3.0", null, []));
        Assert.Equal(("22023", "kind"), (kind.SqlState, kind.Reason));
        Assert.False(kind.IsTooLarge);

        // The uploader's own caps keep a big report and its note under the limits the first time.
        var folder = Directory.CreateTempSubdirectory("armory-v3-");
        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero));
            var store = new IncidentStore(folder.FullName);
            var events = new JsonArray();
            for (var i = 0; i < 4000; i++)
                events.Add(new JsonObject { ["seq"] = i, ["at"] = "2026-10-08T17:59:59.123Z", ["kind"] = "rpc", ["name"] = "armory_list_changes", ["ms"] = 12 + i % 50, ["status"] = 200, ["ok"] = true });
            var log = Enumerable.Range(0, 300).Select(i => $"2026-10-08T17:00:00.000Z sync: Robot 2027/Drivetrain/Part{i}.SLDPRT: " + new string('y', 900)).ToList();
            var incident = IncidentDocument.Build(GlitchRules.UserReport("bug", "Slow."), new("0.3.0", "Windows 11", "LAB-PC-07", "alex.kim@students.test"),
                clock.GetUtcNow(), null, events, 4000, 4000, new JsonObject { ["online"] = true }, log, new IncidentFeedback("bug", "Slow."));
            var file = store.Save(clock.GetUtcNow(), GlitchKinds.UserReport, IncidentDocument.Render(incident, Scrubber.None, 8 * 1024 * 1024));
            var uploader = new IncidentUploader(api, store, () => false, clock);
            Assert.Equal(UploadOutcome.Sent, await uploader.StepAsync());
            Assert.Equal(3, env.Supabase.RpcCount(ArmoryApi.SubmitFeedbackRpc)); // the two refused above, and this one, once
            Assert.Equal(1, env.Supabase.RpcCount(ArmoryApi.SubmitIncidentRpc));
            var sizes = Assert.Single(await RowsAsync(env, "select pg_column_size(report), (select pg_column_size(context) from public.armory_app_feedback) from public.armory_app_incidents"));
            Assert.InRange((int)sizes[0]!, 1, 1048576);
            Assert.InRange((int)sizes[1]!, 1, 131072);
            _ = file;
        }
        finally { folder.Delete(recursive: true); }

        // 20 notes an hour, then PT429 (HTTP 429) with when to send again.
        for (var i = 0; i < 19; i++) await api.SubmitAppFeedbackAsync("idea", "note " + i, "0.3.0", null, []);
        var limited = await Assert.ThrowsAsync<ArmoryRpcException>(() => api.SubmitAppFeedbackAsync("idea", "one too many", "0.3.0", null, []));
        Assert.True(limited.IsRateLimited);
        Assert.Equal(429, limited.Status);
        Assert.Equal(("rate_limited", 20L), (limited.Reason, limited.Detail!.Limit!.Value));
        Assert.InRange(limited.RetryAfter!.Value.TotalSeconds, 3000, 3600);
    }
}
