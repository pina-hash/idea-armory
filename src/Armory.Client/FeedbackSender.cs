using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Client;

// What the window's Send feedback gives (v0.3.2, idea-app 0235, "Send feedback, the same as the
// website's"): the kind (bug, idea, praise or other), the words, what was tried (at most 1000
// characters), the area of the app it is about (a window or view name, at most 120), one PNG of
// the app window or null (at most 2 MiB), and what the app was doing (never file contents).
public sealed record FeedbackNote(string Kind, string Body, string? Tried = null, string? Area = null, byte[]? Screenshot = null, JsonObject? Context = null);

// The answer to one Send feedback, with one plain sentence for the window (Message).
public abstract record FeedbackResult
{
    private FeedbackResult() { }

    // One plain sentence the window can show as it is.
    public abstract string Message { get; }
    // True when the note went.
    public virtual bool Ok => false;
    // True when the note did not go because of its picture: the window offers "Send without the
    // picture" (the same note again, Screenshot null).
    public virtual bool CanSendWithoutPicture => false;

    // The note went with every field it was given.
    public sealed record Sent(Guid Id) : FeedbackResult
    {
        public override bool Ok => true;
        public override string Message => "Sent. Thank you for the feedback.";
    }

    // The site has only the five-argument form (before idea-app 0235): what was tried, the area and
    // the kind asked for went in the note's context (the site keeps it whole), so only a picture is
    // lost (LeftOutPicture says whether one was given). Kind is what it went as: praise goes as
    // "other", because that form refuses praise.
    public sealed record SentWithoutNewFields(Guid Id, string Kind, bool KindChanged, bool LeftOutPicture) : FeedbackResult
    {
        public override bool Ok => true;
        public override string Message => "Sent. Thank you for the feedback." +
            (LeftOutPicture ? " The website can't take pictures yet, so it went without the picture." : "") +
            (KindChanged ? " The website doesn't take praise yet, so it went as other feedback." : "");
    }

    // The picture was refused (by this computer, by file storage, or by the site's check of its
    // key: 22023 bad_path, not_found or in_use with field screenshot). The note was NOT sent.
    public sealed record ScreenshotRefused(string Reason) : FeedbackResult
    {
        public override bool CanSendWithoutPicture => true;
        public override string Message => "Your note wasn't sent: " + Reason switch
        {
            "not_png" => "the screenshot isn't a PNG picture",
            "bad_path" => "the website wouldn't take the screenshot's name",
            "not_found" => "the screenshot didn't reach the website",
            "in_use" => "that screenshot is already on another note",
            "not_available" => "the website isn't ready for screenshots yet",
            _ => "Armory's file storage wouldn't take the screenshot",
        } + ". You can send it without the picture.";
    }

    // Too large for the site: Field is screenshot (over 2 MiB, refused here before anything went,
    // or by file storage), or a text the site measured larger than this computer did.
    public sealed record TooLarge(string Field, long Size, long Limit) : FeedbackResult
    {
        public override bool CanSendWithoutPicture => Field == "screenshot";
        public override string Message => Field == "screenshot"
            ? $"Your note wasn't sent: the screenshot is {Megabytes(Size)} and the limit is 2 MB. You can send it without the picture."
            : $"Your note wasn't sent: {FieldWords(Field)} is too long for the website.";
    }

    // PT429: this account sent 20 notes in the last hour. Nothing is sent before RetryAfter.
    public sealed record RateLimited(TimeSpan RetryAfter) : FeedbackResult
    {
        public override string Message
        {
            get
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalMinutes));
                return $"You've sent a lot of feedback this hour. Try again in {minutes.ToString(CultureInfo.InvariantCulture)} {(minutes == 1 ? "minute" : "minutes")}.";
            }
        }
    }

    // The site or file storage could not be reached. Nothing was sent.
    public sealed record Offline : FeedbackResult
    {
        public override string Message => "You're offline, so your note wasn't sent. Try again once this computer is back online.";
    }

    // Anything else, in plain words (Message is Reason).
    public sealed record Failed(string Reason) : FeedbackResult
    {
        public override string Message => Reason;
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    private static string FieldWords(string field) => field switch
    {
        "body" => "the note",
        "tried" => "what you tried",
        "area" => "the area",
        "context" => "what Armory was doing",
        "app_version" => "Armory's version",
        _ => "part of it",
    };
}

// One note as sent: its id, whether the eight-argument form took it, and the kind it went as.
public sealed record FeedbackSubmission(Guid Id, bool NewFields, string Kind);

// Send feedback, the same as the website's (v0.3.2, idea-app 0235). SendAsync is the window's one
// entry point: it fits the note to the site's limits here (trimmed and cut, never refused for
// length), refuses a picture over 2 MiB before anything goes, uploads the picture
// (FeedbackScreenshots) and then sends the note that names it with the eight-argument
// armory_submit_app_feedback. A site without that form (404 PGRST202) gets the five-argument form
// without the new fields, and is not asked for the wide one again for WideMissingRetry. PT429 is
// recorded in the SubmitLimiter shared with the IncidentUploader, so neither sends again before
// retry_after_seconds have passed. SubmitAsync is the same call without the picture's upload, for
// notes the uploader sends later. Safe from any thread.
public sealed class FeedbackSender
{
    public static readonly IReadOnlyList<string> Kinds = ["bug", "idea", "praise", "other"];
    public const int MaximumBodyCharacters = 8000, MaximumTriedCharacters = 1000, MaximumAreaCharacters = 120, MaximumVersionCharacters = 64;
    public const long MaximumScreenshotBytes = FeedbackScreenshots.MaximumBytes;
    // The site takes a context of 128 KiB measured as stored; JSON is kept well under it, and
    // after a too_large answer shortened once more and sent once.
    public const int MaximumContextBytes = IncidentUploader.MaximumContextBytes, ShortenedContextBytes = IncidentUploader.ShortenedContextBytes;
    public static readonly TimeSpan WideMissingRetry = TimeSpan.FromHours(1);
    private const string Rpc = ArmoryApi.SubmitFeedbackRpc;
    private readonly ArmoryApi api;
    private readonly FeedbackScreenshots? screenshots;
    private readonly SessionManager sessions;
    private readonly string appVersion;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private DateTimeOffset wideMissingUntil = DateTimeOffset.MinValue;
    // The last picture uploaded and not yet on a sent note: sending the same picture again (after
    // Offline, RateLimited or Failed) names it again instead of uploading a second copy.
    private (byte[] Hash, string Key)? uploaded;

    public FeedbackSender(ArmoryApi api, FeedbackScreenshots? screenshots, SessionManager sessions, string appVersion, SubmitLimiter? limiter = null,
        TimeProvider? clock = null, Action<string>? log = null)
    {
        this.api = api;
        this.screenshots = screenshots;
        this.sessions = sessions;
        this.appVersion = FitVersion(appVersion);
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
        Limiter = limiter ?? new SubmitLimiter(clock: this.clock);
    }

    // The waits this sender keeps (shared with the IncidentUploader).
    public SubmitLimiter Limiter { get; }

    // True while the site is known not to have the eight-argument form (a PGRST202 less than
    // WideMissingRetry ago): notes go without the new fields, and a picture is not uploaded.
    public bool NewFieldsMissing { get { lock (gate) return clock.GetUtcNow() < wideMissingUntil; } }

    // The window's Send feedback. Never throws but for cancellation.
    public async Task<FeedbackResult> SendAsync(FeedbackNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        var body = Cut(Clean(note.Body), MaximumBodyCharacters);
        if (body.Length == 0) return new FeedbackResult.Failed("Write a few words first.");
        if (sessions.Current is not { } session) return new FeedbackResult.Failed("Connect this computer to Armory first, then send it.");
        var kind = KindOf(note.Kind);
        var tried = Optional(note.Tried, MaximumTriedCharacters);
        var area = Optional(note.Area, MaximumAreaCharacters);
        var context = FitContext(note.Context, MaximumContextBytes);
        var png = note.Screenshot is { Length: > 0 } picture ? picture : null;
        if (png is not null)
        {
            if (png.LongLength > MaximumScreenshotBytes) return new FeedbackResult.TooLarge("screenshot", png.LongLength, MaximumScreenshotBytes);
            if (!FeedbackScreenshots.IsPng(png)) return new FeedbackResult.ScreenshotRefused("not_png");
        }
        if (Limiter.RateLimitedFor(Rpc) is { } wait) return new FeedbackResult.RateLimited(wait);
        try
        {
            string? key = null;
            // A site known to lack the wide form could not name the picture: it is not uploaded.
            if (png is not null && !NewFieldsMissing)
            {
                if (screenshots is null) return new FeedbackResult.ScreenshotRefused("not_available");
                try { key = await UploadOnceAsync(png, session, ct); }
                catch (ScreenshotRefusedException refused) when (refused.Reason == "too_large")
                { return new FeedbackResult.TooLarge("screenshot", png.LongLength, MaximumScreenshotBytes); }
                catch (ScreenshotRefusedException refused) { return new FeedbackResult.ScreenshotRefused(refused.Reason); }
            }
            FeedbackSubmission sent;
            try { sent = await SubmitAsync(kind, body, appVersion, session.DeviceName, context, tried, area, key, ct); }
            catch (ArmoryRpcException error) when (error.SqlState == ArmoryRpcException.InvalidValueState && error.Detail?.Field == "screenshot")
            {
                // bad_path, not_found or in_use: that key will never do; the note can go without it.
                Forget(key);
                log?.Invoke($"send feedback: the site refused the screenshot ({error.Reason})");
                return new FeedbackResult.ScreenshotRefused(error.Reason ?? "refused");
            }
            Forget(key);
            log?.Invoke($"send feedback: sent {sent.Id} as {sent.Kind}{(sent.NewFields ? "" : " without the new fields")}");
            return sent.NewFields
                ? new FeedbackResult.Sent(sent.Id)
                : new FeedbackResult.SentWithoutNewFields(sent.Id, sent.Kind, sent.Kind != kind, png is not null);
        }
        catch (ArmoryRpcException error) when (error.IsRateLimited)
        {
            return new FeedbackResult.RateLimited(Limiter.RateLimitedFor(Rpc) ?? error.RetryAfter ?? IncidentUploader.RateLimitedRetry);
        }
        catch (ArmoryRpcException error) when (error.IsTooLarge)
        {
            log?.Invoke($"send feedback: the site answered {error.Reason} for {error.Detail?.Field}");
            return new FeedbackResult.TooLarge(error.Detail?.Field ?? "body", error.Detail?.Size ?? 0, error.Detail?.Limit ?? 0);
        }
        catch (ArmoryRpcException error) when (error.IsFunctionMissing)
        {
            return new FeedbackResult.Failed("The website can't take feedback from the app yet. Try again later.");
        }
        catch (ArmoryRpcException error)
        {
            log?.Invoke($"send feedback: refused ({error.SqlState}{(error.Reason is { } reason ? " " + reason : "")}): {error.Message}");
            return new FeedbackResult.Failed($"The website refused the note: {error.Message.TrimEnd('.')}.");
        }
        catch (ArmoryOfflineException) { return new FeedbackResult.Offline(); }
        catch (ArmorySignedOutException) { return new FeedbackResult.Failed("This computer's Armory sign-in ended. Connect it again, then send it."); }
        catch (Exception error) when (error is ArmoryClientException or InvalidDataException or JsonException)
        {
            log?.Invoke($"send feedback: {error.GetType().Name}: {error.Message}");
            return new FeedbackResult.Failed("Armory couldn't send the note. Try again in a moment.");
        }
    }

    // One note, the eight-argument form while the site has it, else the five-argument form with
    // tried, area and the kind asked for in its context (WithAsked) and no screenshot. Praise goes
    // as "other" there: the five-argument form refuses praise ("The kind of note is bug, idea or
    // other."), and the answer's Kind says what it went as. A
    // context the site measures too large is shortened and sent once more (never the same payload
    // twice). Throws as ArmoryApi does (a PGRST202 from the five-argument form too: the site has
    // neither); a PT429 is recorded in the shared limiter first.
    public async Task<FeedbackSubmission> SubmitAsync(string kind, string body, string version, string? deviceName, JsonObject context,
        string? tried = null, string? area = null, string? screenshot = null, CancellationToken ct = default)
    {
        version = FitVersion(version);
        try { return await SubmitOnceAsync(kind, body, version, deviceName, context, tried, area, screenshot, ct); }
        catch (ArmoryRpcException error) when (error.IsTooLarge && error.Detail?.Field == "context")
        {
            log?.Invoke("send feedback: the context was too large for the site; it is shortened and sent once more");
            return await SubmitOnceAsync(kind, body, version, deviceName, FitContext(context, ShortenedContextBytes), tried, area, screenshot, ct);
        }
    }

    // The same, without shortening: the IncidentUploader shortens its own payload once (its file
    // remembers it), so it calls this.
    internal async Task<FeedbackSubmission> SubmitOnceAsync(string kind, string body, string version, string? deviceName, JsonObject context,
        string? tried, string? area, string? screenshot, CancellationToken ct)
    {
        try
        {
            if (!NewFieldsMissing)
            {
                try { return new(await api.SubmitAppFeedbackAsync(kind, body, version, deviceName, context, tried, area, screenshot, ct), true, kind); }
                catch (ArmoryRpcException error) when (error.IsFunctionMissing)
                {
                    lock (gate) wideMissingUntil = clock.GetUtcNow() + WideMissingRetry;
                    log?.Invoke($"send feedback: the site has no eight-argument {Rpc}; notes go without the new fields, and it is asked again in {WideMissingRetry.TotalMinutes:0} minutes");
                }
            }
            // The five-argument form refuses praise: such a note goes as "other". What it has no
            // argument for goes in the context, so only a picture is lost.
            var narrow = NarrowKind(kind);
            return new(await api.SubmitAppFeedbackAsync(narrow, body, version, deviceName, WithAsked(context, kind != narrow ? kind : null, tried, area), ct),
                false, narrow);
        }
        catch (ArmoryRpcException error) when (error.IsRateLimited)
        {
            Limiter.RateLimited(Rpc, error.RetryAfter ?? IncidentUploader.RateLimitedRetry);
            throw;
        }
    }

    // The picture's key: the one uploaded for these same bytes when no sent note names it yet (and
    // it is in this account's folder), else a new upload.
    private async Task<string> UploadOnceAsync(byte[] png, ArmorySession session, CancellationToken ct)
    {
        var hash = SHA256.HashData(png);
        var folder = AccessToken.Subject(session.AccessToken)?.ToString("D") + "/";
        lock (gate)
            if (uploaded is { } done && done.Hash.AsSpan().SequenceEqual(hash) && done.Key.StartsWith(folder, StringComparison.Ordinal)) return done.Key;
        var key = await screenshots!.UploadAsync(png, ct);
        lock (gate) uploaded = (hash, key);
        log?.Invoke($"send feedback: the screenshot was uploaded ({png.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        return key;
    }

    private void Forget(string? key)
    {
        if (key is null) return;
        lock (gate) if (uploaded is { } done && done.Key == key) uploaded = null;
    }

    // The kind the five-argument form takes: bug, idea or other (praise goes as other).
    public static string NarrowKind(string kind) => kind == "praise" ? "other" : kind;

    // The context the five-argument form carries for what it has no argument for: askedKind
    // (praise, when it went as other), tried and area, each only when there is one. They are kept
    // whole: the rest of the context gives way first (FitContext), within MaximumContextBytes.
    public static JsonObject WithAsked(JsonObject? context, string? askedKind, string? tried, string? area)
    {
        var asked = new JsonObject();
        if (askedKind is not null) asked["askedKind"] = askedKind;
        if (tried is not null) asked["tried"] = tried;
        if (area is not null) asked["area"] = area;
        if (asked.Count == 0) return context?.DeepClone() as JsonObject ?? new JsonObject();
        var room = MaximumContextBytes - JsonSerializer.SerializeToUtf8Bytes(asked).Length - 16;
        var carried = FitContext(context, Math.Max(256, room));
        foreach (var (name, value) in asked) carried[name] = value?.DeepClone();
        return carried;
    }

    // bug, idea, praise or other (any case); anything else is "other".
    public static string KindOf(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        return Kinds.Contains(k) ? k : "other";
    }

    // Trimmed and cut to the site's limit; blank is null (the site stores blank as nothing).
    public static string? Optional(string? text, int limit)
    {
        var clean = Cut(Clean(text), limit);
        return clean.Length == 0 ? null : clean;
    }

    // At most 64 characters (the site refuses a longer version), never blank.
    public static string FitVersion(string? version)
    {
        var v = Clean(version);
        return v.Length == 0 ? "unknown" : Cut(v, MaximumVersionCharacters);
    }

    private static string Clean(string? text) => (text ?? "").Trim();

    // Cut at a limit in characters as the site counts them (code points), never inside a pair.
    private static string Cut(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var (count, at) = (0, 0);
        while (at < text.Length && count < limit)
        {
            at += char.IsSurrogatePair(text, at) ? 2 : 1;
            count++;
        }
        return text[..at].TrimEnd();
    }

    // The context within maximumBytes as JSON: the largest entries give way first (replaced by a
    // note that they were trimmed), then everything.
    public static JsonObject FitContext(JsonObject? context, int maximumBytes)
    {
        var fitted = context?.DeepClone() as JsonObject ?? new JsonObject();
        while (JsonSerializer.SerializeToUtf8Bytes(fitted).Length > maximumBytes)
        {
            var largest = fitted.Where(p => p.Value is not JsonValue { } v || v.ToJsonString().Length > 64)
                .OrderByDescending(p => p.Value?.ToJsonString().Length ?? 0).Select(p => p.Key).FirstOrDefault();
            if (largest is null) return new JsonObject { ["trimmed"] = true };
            fitted[largest] = "[trimmed]";
            fitted["trimmed"] = true;
        }
        return fitted;
    }
}
