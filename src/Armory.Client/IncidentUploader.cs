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
    // The site refused this one for good (a bad field, too large even after shortening): kept here
    // as ".held" for a person to hand over, never sent again.
    Held,
    // Anything else: tried again on the next round.
    Failed,
    // PT429: this account sent its limit for the hour. Kept here and sent again once the site's
    // retry_after_seconds have passed (remembered across restarts).
    RateLimited,
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
    // A PT429 without a readable retry_after_seconds waits this long.
    public static readonly TimeSpan RateLimitedRetry = TimeSpan.FromMinutes(5);
    // The site refuses a report over 1 MiB and a note's context over 128 KiB, measured as sent
    // (pg_column_size of the jsonb, which can be larger than its JSON text). The uploader trims
    // to these JSON sizes, well under, and to the shortened ones after a too_large answer.
    public const int MaximumReportBytes = 640 * 1024, ShortenedReportBytes = 128 * 1024;
    public const int MaximumContextBytes = 96 * 1024, ShortenedContextBytes = 24 * 1024;
    public const int MaximumBodyCharacters = 8000, MaximumVersionCharacters = 64;
    public const int FeedbackLogLines = 200;
    // The calls already answered too_large or too_long for this file: from then on only the
    // shortened payload is sent, once, never the same payload again.
    private const string ShortenedField = "shortened";
    private readonly ArmoryApi api;
    private readonly IncidentStore store;
    private readonly Func<bool> transferring;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    // When each RPC may be asked again, shared with the window's FeedbackSender (one PT429 makes
    // both wait) and kept in the incidents folder across restarts.
    private readonly SubmitLimiter limiter;
    // The window's sender: a note's words go through it (the eight-argument form while the site
    // has it, the five-argument one otherwise), or straight through ArmoryApi without one.
    private readonly FeedbackSender? feedback;
    // The files whose person's words went in this run (under the gate).
    private readonly HashSet<string> feedbackSent = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset nextAttempt = DateTimeOffset.MinValue;

    // feedback: the window's FeedbackSender, which a note's words go through; its limiter is then
    // this uploader's too (give it one over the same store). limiter: the waits to share without
    // a sender. Without either, this uploader keeps its own waits over the store.
    public IncidentUploader(ArmoryApi api, IncidentStore store, Func<bool> transferring, TimeProvider? clock = null, Action<string>? log = null,
        FeedbackSender? feedback = null, SubmitLimiter? limiter = null)
    {
        this.api = api;
        this.store = store;
        this.transferring = transferring;
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
        this.feedback = feedback;
        this.limiter = feedback?.Limiter ?? limiter ?? new SubmitLimiter(store, this.clock);
    }

    // The waits this uploader keeps (give the same one to a FeedbackSender).
    public SubmitLimiter Limiter => limiter;

    // A computer shared by several students (docs/agent/PROFILES.md, F13): the address of the
    // student in use now. A saved note or incident another student wrote waits here until its
    // writer is the one in use, so their words only ever go under their own account; one written
    // while nobody was signed in carries no one's words and goes with whoever is in use. Null:
    // everything goes (one student per computer, as before).
    public Func<string?>? WriterInUse { get; init; }

    private bool WrittenByTheOneInUse(JsonObject incident)
    {
        if (WriterInUse is null || Text(incident, "email") is not { Length: > 0 } writer) return true;
        return WriterInUse() is { } inUse && string.Equals(writer.Trim(), inUse.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // True while this RPC is not asked: the site lacks it (for 6 hours after a 404 PGRST202), or
    // this account reached its limit (until the PT429's retry_after_seconds have passed).
    public bool IsWaiting(string rpc) => limiter.IsWaiting(rpc);

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
                if (!WrittenByTheOneInUse(incident)) continue;
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
            // Saving the note woke the background round, which may have sent it already (and
            // marked a note on its own sent, so it is not where it was): that is Sent, never
            // Held (0.3.1 said "couldn't send" for feedback that had gone).
            if (feedbackSent.Contains(file)) return UploadOutcome.Sent;
            if (!TryRead(file, out var incident)) return UploadOutcome.Held;
            if (!WrittenByTheOneInUse(incident)) return UploadOutcome.Waiting;
            if (!NeedsFeedback(incident)) return UploadOutcome.Sent;
            if (IsWaiting(ArmoryApi.SubmitFeedbackRpc)) return IsRateLimitWait(ArmoryApi.SubmitFeedbackRpc) ? UploadOutcome.RateLimited : UploadOutcome.NotLive;
            return await SendAsync(file, incident, feedbackOnly: true, ct);
        }
        finally { gate.Release(); }
    }

    // Rounds every minute (sooner after Wake) until canceled. The first comes a minute after
    // the start (or at the first Wake), so a start's first pass has the network to itself.
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(Every, ct);
                while (wake.CurrentCount > 0) await wake.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { return; }
            try { await StepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { log?.Invoke("incident upload: " + error.GetType().Name + ": " + error.Message); }
        }
    }

    // A new incident was saved: the next round comes now (still at most one a minute).
    public void Wake() => wake.Release();

    // A note's words, what was tried and the area: through the window's FeedbackSender when there
    // is one (the eight-argument form while the site has it, never a picture: a saved note has
    // none). Without one, the five-argument form, praise going as other and tried, area and the
    // kind asked for in the context (FeedbackSender.WithAsked), as the sender's fallback does.
    private async Task<Guid> SubmitNoteAsync(string kind, string body, string version, string? device, JsonObject context, string? tried, string? area,
        CancellationToken ct)
    {
        if (feedback is not null) return (await feedback.SubmitOnceAsync(kind, body, FeedbackSender.FitVersion(version), device, context, tried, area, null, ct)).Id;
        var narrow = FeedbackSender.NarrowKind(kind);
        return await api.SubmitAppFeedbackAsync(narrow, body, version, device, FeedbackSender.WithAsked(context, kind != narrow ? kind : null, tried, area), ct);
    }

    private static bool NeedsFeedback(JsonObject incident) => incident["feedback"] is JsonObject && incident["feedbackId"] is null;
    private static bool NoteOnly(JsonObject incident) => incident[IncidentDocument.NoteOnlyField] is JsonValue v && v.TryGetValue<bool>(out var only) && only;
    // A wait a PT429 set (remembered under its own key), not a site without the RPC.
    private bool IsRateLimitWait(string rpc) => limiter.IsRateLimited(rpc);

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
            var device = Text(incident, "deviceName");
            if (NeedsFeedback(incident))
            {
                var note = (JsonObject)incident["feedback"]!;
                var id = await SendShortenedOnceAsync(file, incident, rpc, shortened => SubmitNoteAsync(FeedbackSender.KindOf(Text(note, "kind")),
                    Body(Text(note, "body") ?? "", shortened), Version(incident, shortened), device, FeedbackContext(incident, shortened ? ShortenedContextBytes : MaximumContextBytes),
                    FeedbackSender.Optional(Text(note, "tried"), FeedbackSender.MaximumTriedCharacters), FeedbackSender.Optional(Text(note, "area"), FeedbackSender.MaximumAreaCharacters), ct));
                incident["feedbackId"] = id.ToString();
                feedbackSent.Add(file);
                store.Rewrite(file, incident);
                log?.Invoke($"incident upload: the report in {name} was sent ({id})");
            }
            if (NoteOnly(incident))
            {
                // A note sent on its own: nothing follows it.
                store.Mark(file, IncidentStore.SentMark);
                return UploadOutcome.Sent;
            }
            if (feedbackOnly) return UploadOutcome.Sent;
            rpc = ArmoryApi.SubmitIncidentRpc;
            if (IsWaiting(rpc)) return IsRateLimitWait(rpc) ? UploadOutcome.RateLimited : UploadOutcome.NotLive;
            var sent = await SendShortenedOnceAsync(file, incident, rpc, shortened => api.SubmitAppIncidentAsync(Text(incident, "kind") ?? GlitchKinds.UserReport,
                Summary(Text(incident, "summary") ?? "", shortened), Version(incident, shortened), device, Id(incident, "projectId"),
                IncidentDocument.FitJson(incident, shortened ? ShortenedReportBytes : MaximumReportBytes), Id(incident, "feedbackId"), ct));
            store.Mark(file, IncidentStore.SentMark);
            log?.Invoke($"incident upload: {name} was sent ({sent})");
            return UploadOutcome.Sent;
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        {
            limiter.NotLive(rpc, NotLiveRetry);
            log?.Invoke($"incident upload: the site has no {rpc} yet; {name} waits here, tried again in {NotLiveRetry.TotalHours:0} hours");
            return UploadOutcome.NotLive;
        }
        catch (ArmoryRpcException error) when (error.IsRateLimited)
        {
            // PT429: the account's hourly limit. The DETAIL says when to send again.
            var wait = error.RetryAfter ?? RateLimitedRetry;
            limiter.RateLimited(rpc, wait);
            log?.Invoke($"incident upload: the site's limit for {rpc} was reached; {name} waits here, sent again in {Math.Ceiling(wait.TotalSeconds):0} s");
            return UploadOutcome.RateLimited;
        }
        catch (Exception error) when (error is ArmoryOfflineException or ArmorySignedOutException)
        {
            return UploadOutcome.Offline;
        }
        catch (ArmoryRpcException error) when (error.SqlState is { Length: 5 })
        {
            // 22023 other than a first too_large or too_long (kind, empty, not_object,
            // feedback_not_found, or too large even shortened) is a bug in this app; any other
            // SQLSTATE is a refusal for good. Logged here and never sent again. (PostgREST's own
            // codes, PGRSTxxx, are not the database's answer: they go again on a later round.)
            var why = error.SqlState == ArmoryRpcException.InvalidValueState ? "a bug in this app" : "a refusal";
            log?.Invoke($"incident upload: the site refused {name} ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}" +
                $"{(error.Detail?.Field is { } field ? " " + field : "")}: {error.Message}), {why}; it is kept as held");
            Hold(file);
            return UploadOutcome.Held;
        }
        catch (Exception error) when (error is ArmoryClientException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"incident upload: {name} did not go this time ({error.GetType().Name})");
            return UploadOutcome.Failed;
        }
    }

    // Sends one call. An answer of 22023 too_large or too_long is shortened (trimmed log lines and
    // events, a capped context) and sent once more; the file remembers it, so a later round sends
    // only the shortened payload, and a second such answer is thrown (the file is then held).
    private async Task<T> SendShortenedOnceAsync<T>(string file, JsonObject incident, string rpc, Func<bool, Task<T>> send)
    {
        var shortened = incident[ShortenedField] is JsonObject done && done[rpc] is not null;
        if (shortened) return await send(true);
        try { return await send(false); }
        catch (ArmoryRpcException error) when (error.IsTooLarge)
        {
            log?.Invoke($"incident upload: {rpc} answered {error.Reason} for {error.Detail?.Field ?? "a field"} " +
                $"({error.Detail?.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} of {error.Detail?.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}); it is shortened and sent once more");
            if (incident[ShortenedField] is not JsonObject marks) incident[ShortenedField] = marks = new JsonObject();
            marks[rpc] = true;
            store.Rewrite(file, incident);
            return await send(true);
        }
    }

    private void Hold(string file)
    {
        try { store.Mark(file, IncidentStore.HeldMark); }
        catch (Exception mark) when (mark is IOException or UnauthorizedAccessException) { }
    }

    private static string Body(string body, bool shortened)
    {
        var words = body.Trim();
        var limit = shortened ? MaximumBodyCharacters / 2 : MaximumBodyCharacters;
        return words.Length <= limit ? words : words[..limit];
    }

    private static string Summary(string summary, bool shortened)
    {
        var clipped = GlitchRules.Clip(summary);
        return shortened && clipped.Length > 250 ? clipped[..247] + "..." : clipped;
    }

    private static string Version(JsonObject incident, bool shortened)
    {
        var version = Text(incident, "appVersion") is { Length: > 0 } v ? v : "unknown";
        var limit = shortened ? 32 : MaximumVersionCharacters;
        return version.Length <= limit ? version : version[..limit];
    }

    // What a person's report carries besides their words: what the app was doing and its last
    // log lines (paths, never file contents), from the incident saved with it, within maximumBytes
    // as JSON (the site's limit is 128 KiB as sent): the oldest log lines go first, then the
    // snapshot. The window's Send feedback with a picture builds its context the same way.
    public static JsonObject FeedbackContext(JsonObject incident, int maximumBytes = MaximumContextBytes)
    {
        var lines = new JsonArray();
        if (incident["log"] is JsonArray log)
            foreach (var line in log.Skip(Math.Max(0, log.Count - FeedbackLogLines))) lines.Add(line?.DeepClone());
        var context = new JsonObject
        {
            ["incidentId"] = incident["id"]?.DeepClone(),
            ["osVersion"] = incident["osVersion"]?.DeepClone(),
            ["snapshot"] = incident["snapshot"]?.DeepClone(),
            ["log"] = lines,
        };
        while (JsonSerializer.SerializeToUtf8Bytes(context).Length > maximumBytes)
        {
            if (lines.Count > 20) { for (var drop = lines.Count / 2; drop > 0; drop--) lines.RemoveAt(0); continue; }
            if (context["snapshot"] is JsonObject snapshot && snapshot.Count > 1) { context["snapshot"] = new JsonObject { ["trimmed"] = true }; continue; }
            if (lines.Count > 0) { lines.RemoveAt(0); continue; }
            break;
        }
        return context;
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static Guid? Id(JsonObject o, string name) => Guid.TryParse(Text(o, name), out var id) ? id : null;
}
