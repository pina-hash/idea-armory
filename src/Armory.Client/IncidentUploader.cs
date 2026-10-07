using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Telemetry;

namespace Armory.Client;

public enum UploadOutcome
{
    // Nothing waits to be sent.
    Nothing,
    // A transfer is running: incidents never compete with a student's files.
    Busy,
    // One went less than a minute ago.
    TooSoon,
    // What waits needs an RPC the site does not have yet; its next try is hours away.
    Waiting,
    Sent,
    // The site answered 404 PGRST202: its migration is not live. Kept here; tried again in 6 hours.
    NotLive,
    // Offline, signed out, or the site was busy: tried again on the next round.
    Offline,
    // The site refused this one for good (a bad field, too large): kept here as ".held" for a
    // person to hand over, never sent again.
    Held,
    // Anything else: tried again on the next round.
    Failed,
}

// Sends the incidents folder to the site in the background (docs/agent/TELEMETRY.md, "Upload"):
// only while no transfer is running, at most one incident a minute, oldest first, through
// armory_submit_app_incident (and armory_submit_app_feedback first, for a report a person
// wrote). An RPC the site does not have yet (404 PGRST202) is not asked for again for 6 hours,
// across restarts, and is never shown to anyone: the files simply wait here.
public sealed class IncidentUploader
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan NotLiveRetry = TimeSpan.FromHours(6);
    // The site refuses a report over 1 MB; the uploader trims the oldest events to fit under this.
    public const int MaximumReportBytes = 900 * 1024;
    public const int FeedbackLogLines = 200;
    private readonly ArmoryApi api;
    private readonly IncidentStore store;
    private readonly Func<bool> transferring;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private readonly Dictionary<string, DateTimeOffset> waits;
    private DateTimeOffset nextAttempt = DateTimeOffset.MinValue;

    public IncidentUploader(ArmoryApi api, IncidentStore store, Func<bool> transferring, TimeProvider? clock = null, Action<string>? log = null)
    {
        this.api = api;
        this.store = store;
        this.transferring = transferring;
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
        waits = store.ReadWaits();
    }

    // True while the site is known not to have this RPC yet (until its 6-hour wait is over).
    public bool IsWaiting(string rpc) => waits.TryGetValue(rpc, out var until) && clock.GetUtcNow() < until;

    // One round: sends at most one incident (with its feedback first, when it has some).
    public async Task<UploadOutcome> StepAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (transferring()) return UploadOutcome.Busy;
            var pending = store.Pending();
            if (pending.Count == 0) return UploadOutcome.Nothing;
            var now = clock.GetUtcNow();
            if (now < nextAttempt) return UploadOutcome.TooSoon;
            foreach (var file in pending)
            {
                if (!TryRead(file, out var incident)) continue;
                if (IsWaiting(NeedsFeedback(incident) ? ArmoryApi.SubmitFeedbackRpc : ArmoryApi.SubmitIncidentRpc)) continue;
                nextAttempt = now + Every;
                return await SendAsync(file, incident, feedbackOnly: false, ct);
            }
            return UploadOutcome.Waiting;
        }
        finally { gate.Release(); }
    }

    // "Report a problem": the person's words go at once (the incident follows in the
    // background). NotLive and Offline mean they are saved here and go later.
    public async Task<UploadOutcome> SendFeedbackNowAsync(string file, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!TryRead(file, out var incident)) return UploadOutcome.Held;
            if (!NeedsFeedback(incident)) return UploadOutcome.Sent;
            if (IsWaiting(ArmoryApi.SubmitFeedbackRpc)) return UploadOutcome.NotLive;
            return await SendAsync(file, incident, feedbackOnly: true, ct);
        }
        finally { gate.Release(); }
    }

    // Rounds every minute (sooner after Wake) until canceled.
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await StepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { log?.Invoke("incident upload: " + error.GetType().Name + ": " + error.Message); }
            try
            {
                await wake.WaitAsync(Every, ct);
                while (wake.CurrentCount > 0) await wake.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    // A new incident was saved: the next round comes now (still at most one a minute).
    public void Wake() => wake.Release();

    private static bool NeedsFeedback(JsonObject incident) => incident["feedback"] is JsonObject && incident["feedbackId"] is null;

    private bool TryRead(string file, out JsonObject incident)
    {
        try
        {
            incident = store.Read(file);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            incident = null!;
            log?.Invoke($"incident upload: {Path.GetFileName(file)} could not be read ({error.GetType().Name}); it is kept as held");
            try { store.Mark(file, IncidentStore.HeldMark); }
            catch (Exception mark) when (mark is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private async Task<UploadOutcome> SendAsync(string file, JsonObject incident, bool feedbackOnly, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var rpc = ArmoryApi.SubmitFeedbackRpc;
        try
        {
            var appVersion = Text(incident, "appVersion") ?? "unknown";
            var device = Text(incident, "deviceName");
            if (NeedsFeedback(incident))
            {
                var feedback = (JsonObject)incident["feedback"]!;
                var id = await api.SubmitAppFeedbackAsync(Text(feedback, "kind") ?? "other", Text(feedback, "body") ?? "", appVersion, device, FeedbackContext(incident), ct);
                incident["feedbackId"] = id.ToString();
                store.Rewrite(file, incident);
                log?.Invoke($"incident upload: the report in {name} was sent ({id})");
            }
            if (feedbackOnly) return UploadOutcome.Sent;
            rpc = ArmoryApi.SubmitIncidentRpc;
            if (IsWaiting(rpc)) return UploadOutcome.NotLive;
            var report = IncidentDocument.FitJson(incident, MaximumReportBytes);
            var sent = await api.SubmitAppIncidentAsync(Text(incident, "kind") ?? GlitchKinds.UserReport, GlitchRules.Clip(Text(incident, "summary") ?? ""),
                appVersion, device, Id(incident, "projectId"), report, Id(incident, "feedbackId"), ct);
            store.Mark(file, IncidentStore.SentMark);
            log?.Invoke($"incident upload: {name} was sent ({sent})");
            return UploadOutcome.Sent;
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        {
            waits[rpc] = clock.GetUtcNow() + NotLiveRetry;
            try { store.WriteWaits(waits); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
            log?.Invoke($"incident upload: the site has no {rpc} yet; {name} waits here, tried again in {NotLiveRetry.TotalHours:0} hours");
            return UploadOutcome.NotLive;
        }
        catch (Exception error) when (error is ArmoryOfflineException or ArmorySignedOutException)
        {
            return UploadOutcome.Offline;
        }
        catch (ArmoryRpcException error) when (error.IsInvalidInput || error.Status is 400 or 413)
        {
            log?.Invoke($"incident upload: the site refused {name} ({error.Status} {error.SqlState}); it is kept as held");
            try { store.Mark(file, IncidentStore.HeldMark); }
            catch (Exception mark) when (mark is IOException or UnauthorizedAccessException) { }
            return UploadOutcome.Held;
        }
        catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"incident upload: {name} did not go this time ({error.GetType().Name})");
            return UploadOutcome.Failed;
        }
    }

    // What a person's report carries besides their words: what the app was doing and its last
    // log lines (paths, never file contents), from the incident saved with it.
    private static JsonObject FeedbackContext(JsonObject incident)
    {
        var lines = new JsonArray();
        if (incident["log"] is JsonArray log)
            foreach (var line in log.Skip(Math.Max(0, log.Count - FeedbackLogLines))) lines.Add(line?.DeepClone());
        return new JsonObject
        {
            ["incidentId"] = incident["id"]?.DeepClone(),
            ["osVersion"] = incident["osVersion"]?.DeepClone(),
            ["snapshot"] = incident["snapshot"]?.DeepClone(),
            ["log"] = lines,
        };
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static Guid? Id(JsonObject o, string name) => Guid.TryParse(Text(o, name), out var id) ? id : null;
}
