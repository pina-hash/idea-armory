using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Client;

namespace Armory.Agent;

// One picture of the Armory window for Send feedback: its id (32 lowercase hex), the PNG bytes
// exactly as they would be sent, and their size in pixels.
internal sealed record WindowShot(string Id, byte[] Png, int Width, int Height, bool Scaled);

// Send feedback's pictures of the window (0.3.3): the last one only, in memory only. Never
// written to disk, and served to the page at https://armory.local/shot/<id>.png with
// Cache-Control: no-store, so not even WebView2's cache keeps it. The person sees exactly the
// bytes that would go. A new picture replaces the last; a sent one is forgotten.
internal sealed class WindowShots
{
    internal const string Prefix = "/shot/";
    // The largest side the page may ask for, in CSS pixels.
    internal const int MaximumSide = 16384;
    internal const string Gone = "That picture isn't here any more. Add it again, or send your note without it.";
    internal const string NotTaken = "Armory couldn't take a picture of its window. You can send your note without one.";
    internal const string TooLarge = "The picture of this window is over 2 MB even made smaller. You can send your note without it.";
    private readonly object gate = new();
    private WindowShot? last;

    // A picture's id as the page sends it back: 32 lowercase hex digits.
    internal static bool IsId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string UrlOf(string id) => "https://" + PageAssets.HostName + Prefix + id + ".png";

    // "/shot/<id>.png": the id it names, or null.
    internal static string? IdOfPath(string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.Ordinal)) return null;
        var id = path[Prefix.Length..^4];
        return IsId(id) ? id : null;
    }

    internal WindowShot Keep(byte[] png, int width, int height, bool scaled)
    {
        var shot = new WindowShot(Guid.NewGuid().ToString("N"), png, width, height, scaled);
        lock (gate) last = shot;
        return shot;
    }

    internal WindowShot? Get(string? id)
    {
        lock (gate) return last is { } shot && shot.Id == id ? shot : null;
    }

    internal void Forget(string id)
    {
        lock (gate) if (last?.Id == id) last = null;
    }

    // What the window took, as the page's answer: kept and served when it is a PNG within 2 MB.
    internal WindowShotView Answer(WindowCapture? capture)
    {
        if (capture is null || FeedbackScreenshots.Dimensions(capture.Png) is not { } size) return Refused(NotTaken);
        if (capture.Png.LongLength > FeedbackScreenshots.MaximumBytes) return Refused(TooLarge);
        var shot = Keep(capture.Png, size.Width, size.Height, capture.Scaled);
        return new WindowShotView(true, shot.Id, UrlOf(shot.Id), shot.Width, shot.Height, shot.Png.LongLength, shot.Scaled, null);
    }

    internal static WindowShotView Refused(string why) => new(false, null, null, 0, 0, 0, false, why);

    // The DevTools protocol's Page.captureScreenshot parameters MainWindow sends to take the
    // picture again smaller: PNG, the page's own viewport only (never beyond it), at scale.
    // tools/agent-ui/check-ui.mjs sends this very JSON to Chromium and checks the size it gets.
    internal static string CaptureParameters(int cssWidth, int cssHeight, double scale)
        => JsonSerializer.Serialize(new
        {
            format = "png",
            captureBeyondViewport = false,
            clip = new { x = 0, y = 0, width = cssWidth, height = cssHeight, scale },
        });
}

// The window's Send feedback and "Your feedback" (0.3.3, docs/agent/CLIENT.md section 7).
//
// A note WITHOUT a picture takes the durable path: saved first in the incidents folder, sent at
// once, and sent later by the uploader when it can't go now (AgentTelemetry.SendFeedbackAsync).
// A note WITH a picture goes now through FeedbackSender.SendAsync and is never written to disk
// with its picture: its words are scrubbed exactly as a saved note's (ComposeNoteAsync) and its
// context is built the same way. When it can't go (offline, the hour's limit, a refused picture)
// the answer offers it without the picture, which then takes the durable path. So nothing a
// person typed is lost, and a picture of the window is never kept on disk.
internal sealed class FeedbackDesk(FeedbackSender sender, AgentTelemetry telemetry, ArmoryApi api, SessionManager sessions, WindowShots shots,
    TimeProvider clock, Action<string> log)
{
    // A site without armory_my_app_feedback (404 PGRST202) is not asked again for an hour.
    internal static readonly TimeSpan MissingRetry = TimeSpan.FromHours(1);
    internal const int ListLimit = 50;
    internal const string OfflineWithPicture = "You're offline, so your note and its picture weren't sent. Send it without the picture, and Armory sends it once this computer is back online.";
    internal const string ListOffline = "You're offline. Your feedback shows here once this computer is back online.";
    internal const string ListFailed = "Armory couldn't read your feedback. Try again in a moment.";
    private readonly object gate = new();
    private DateTimeOffset missingUntil = DateTimeOffset.MinValue;

    // shot: the id of a picture captureWindow answered with, or null.
    internal async Task<ActionResult> SendAsync(string? kind, string? body, string? tried, string? area, string? shot, CancellationToken ct = default)
    {
        if (shot is null)
        {
            var (ok, message) = await telemetry.SendFeedbackAsync(kind, body, tried, area).ConfigureAwait(false);
            return new ActionResult(ok, message);
        }
        if (shots.Get(shot) is not { } picture) return new ActionResult(false, WindowShots.Gone, ActionResult.WithoutPicture);
        var words = (body ?? "").Trim();
        if (words.Length == 0) return new ActionResult(false, "Write a few words first.");
        var normalized = FeedbackSender.KindOf(kind);
        // The same scrubbing as a saved note: other people's addresses masked, this computer's
        // tokens and keys gone, from the words, what was tried and the area alike.
        var composed = await telemetry.Reporter.ComposeNoteAsync(normalized, words, FeedbackSender.Optional(tried, FeedbackSender.MaximumTriedCharacters),
            FeedbackSender.Optional(area, FeedbackSender.MaximumAreaCharacters)).ConfigureAwait(false);
        var said = composed["feedback"] as JsonObject;
        var note = new FeedbackNote(normalized, Text(said, "body") ?? words, Text(said, "tried"), Text(said, "area"), picture.Png,
            IncidentUploader.FeedbackContext(composed));
        var result = await sender.SendAsync(note, ct).ConfigureAwait(false);
        log($"send feedback: {normalized} with a picture, {result.GetType().Name}");
        if (result.Ok)
        {
            shots.Forget(picture.Id);
            return new ActionResult(true, result.Message);
        }
        return result switch
        {
            FeedbackResult.Offline => new ActionResult(false, OfflineWithPicture, ActionResult.WithoutPicture),
            FeedbackResult.RateLimited => new ActionResult(false, result.Message + " Or send it without the picture, and Armory sends it then.", ActionResult.WithoutPicture),
            _ when result.CanSendWithoutPicture => new ActionResult(false, result.Message, ActionResult.WithoutPicture),
            _ => new ActionResult(false, result.Message),
        };
    }

    // "Your feedback": this account's notes, newest first, with where each one is. Never throws
    // but for cancellation.
    internal async Task<FeedbackListView> ReadAsync(CancellationToken ct = default)
    {
        if (sessions.Current is null) return List(FeedbackListView.SignedOut, null);
        lock (gate) if (clock.GetUtcNow() < missingUntil) return List(FeedbackListView.Missing, null);
        IReadOnlyList<AppFeedbackNote>? notes;
        try { notes = await api.MyAppFeedbackAsync(ListLimit, ct).ConfigureAwait(false); }
        catch (ArmoryOfflineException) { return List(FeedbackListView.Offline, ListOffline); }
        catch (ArmorySignedOutException) { return List(FeedbackListView.SignedOut, null); }
        catch (ArmoryRpcException error) when (error.SqlState == "42501") { return List(FeedbackListView.SignedOut, null); }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            log($"your feedback: could not be read ({error.GetType().Name}: {error.Message})");
            return List(FeedbackListView.Failed, ListFailed);
        }
        if (notes is null)
        {
            lock (gate) missingUntil = clock.GetUtcNow() + MissingRetry;
            log($"your feedback: the site has no {ArmoryApi.MyFeedbackRpc} yet; asked again in {MissingRetry.TotalMinutes:0} minutes");
            return List(FeedbackListView.Missing, null);
        }
        // 0235 brought the list and the picture bucket together, so one answer says both.
        return new FeedbackListView(FeedbackListView.Shown, !sender.NewFieldsMissing, null, [.. notes.Select(ViewOf)]);
    }

    private static FeedbackListView List(string state, string? message) => new(state, false, message, []);

    private static FeedbackNoteView ViewOf(AppFeedbackNote n)
        => new(n.Id.ToString(), Iso(n.CreatedAt), n.Kind, n.Body, n.Tried, n.Area, n.HasScreenshot, n.AppVersion, n.DeviceName, n.Status,
            AppFeedbackNote.StatusWords(n.Status), n.ReviewedAt is { } reviewed ? Iso(reviewed) : null);

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
