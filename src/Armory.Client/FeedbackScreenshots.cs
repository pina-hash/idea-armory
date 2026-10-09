using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Armory.Telemetry;

namespace Armory.Client;

// Storage refused a feedback screenshot, or this computer did before sending it. The note can
// still go without the picture. Reason: too_large (over MaximumBytes, or Storage's 413),
// not_png, refused (400), not_allowed (403), exists (409), not_available (no bucket, 404) or
// no_account (the access token carries no auth uid).
public sealed class ScreenshotRefusedException(string reason, int status, string message) : ArmoryClientException(message)
{
    public string Reason { get; } = reason;
    // Storage's HTTP status, or 0 when this computer refused before sending.
    public int Status { get; } = status;
}

// The window's screenshot for a feedback note (v0.3.2, idea-app 0235): one PNG of the app
// window, at most 2 MiB, uploaded to the private Storage bucket armory-feedback-shots under the
// signed-in person's own folder as <auth uid>/<new lowercase uuid>.png, before the note that names
// it (armory_submit_app_feedback checks the key's shape, that the folder is the caller's and that
// the object is there). Nothing can update or delete an object, so every upload is a new key and
// x-upsert is false.
//
//   POST {supabase_url}/storage/v1/object/armory-feedback-shots/<uid>/<uuid>.png
//   apikey: {anon_key}
//   Authorization: Bearer {access_token}
//   Content-Type: image/png
//   x-upsert: false
//
// Each upload goes into the flight recorder (its size, how long, how it ended; never the token).
public sealed class FeedbackScreenshots(HttpClient http, SessionManager sessions, FlightRecorder? recorder = null)
{
    public const string Bucket = "armory-feedback-shots";
    public const long MaximumBytes = 2097152;
    public const string ContentType = "image/png";
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The object key for a picture of this person: <auth uid>/<new lowercase uuid>.png.
    public static string KeyFor(Guid user) => user.ToString("D") + "/" + Guid.NewGuid().ToString("D") + ".png";

    // True when the bytes start with the PNG signature.
    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngSignature);

    // Uploads the picture and returns its key. Throws ScreenshotRefusedException when this
    // computer or Storage refuses it (the note can go without it), ArmoryOfflineException when
    // Storage can't be reached or is busy, ArmorySignedOutException when signed out.
    public async Task<string> UploadAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        if (png.Length > MaximumBytes)
            throw new ScreenshotRefusedException("too_large", 0, $"The screenshot is {Megabytes(png.Length)} and the limit is 2 MB.");
        if (!IsPng(png.Span)) throw new ScreenshotRefusedException("not_png", 0, "The screenshot isn't a PNG picture.");
        var started = recorder?.Now() ?? 0;
        try
        {
            var key = await UploadUnrecordedAsync(png, ct);
            recorder?.Transfer("screenshot", png.Length, recorder.MillisecondsSince(started), true, 200, null);
            return key;
        }
        catch (Exception error)
        {
            recorder?.Transfer("screenshot", png.Length, recorder.MillisecondsSince(started), false, (error as ScreenshotRefusedException)?.Status ?? 0, error switch
            {
                ScreenshotRefusedException refused => refused.Reason,
                ArmoryOfflineException => "offline",
                ArmorySignedOutException => "signedOut",
                OperationCanceledException => "canceled",
                _ => error.GetType().Name,
            });
            throw;
        }
    }

    private async Task<string> UploadUnrecordedAsync(ReadOnlyMemory<byte> png, CancellationToken ct)
    {
        var session = await sessions.GetFreshAsync(cancellationToken: ct);
        var refreshed = false;
        while (true)
        {
            var user = AccessToken.Subject(session.AccessToken)
                ?? throw new ScreenshotRefusedException("no_account", 0, "Armory couldn't tell which account to keep the screenshot under.");
            var key = KeyFor(user);
            using var request = new HttpRequestMessage(HttpMethod.Post, session.SupabaseUrl.TrimEnd('/') + "/storage/v1/object/" + Bucket + "/" + key)
            {
                Content = new ReadOnlyMemoryContent(png),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            request.Content.Headers.ContentLength = png.Length;
            request.Headers.TryAddWithoutValidation("apikey", session.AnonKey);
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            request.Headers.TryAddWithoutValidation("x-upsert", "false");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException error) { throw new ArmoryOfflineException("Armory's file storage could not be reached for the screenshot.", error); }
            catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new ArmoryOfflineException("Armory's file storage took too long to take the screenshot.", error); }
            using (response)
            {
                if (response.IsSuccessStatusCode) return key;
                var (code, error) = await StorageErrorAsync(response, ct);
                // An expired token: Storage answers it as InvalidJWT (400, 401 or 403 by version).
                // Renewed once and sent again under a new key.
                if (!refreshed && (response.StatusCode == HttpStatusCode.Unauthorized || string.Equals(error, "InvalidJWT", StringComparison.OrdinalIgnoreCase)))
                {
                    refreshed = true;
                    session = await sessions.GetFreshAsync(forceRefresh: true, ct);
                    continue;
                }
                var status = (int)response.StatusCode;
                if (status is 429 or 502 or 503 or 504) throw new ArmoryOfflineException($"Armory's file storage is busy ({status}).");
                // Storage answers HTTP 400 for every refusal but a 500 and names the real one in the
                // body's statusCode, so the body is read first. 544 DatabaseTimeout, 503
                // DatabaseReadOnly or LockTimeout, 423 ResourceLocked, 429 and 408 are a busy
                // Storage, never a refusal of the picture: trying again works.
                var effective = code is >= 400 and < 600 ? code.Value : status;
                if (IsBusy(effective) || status >= 500) throw new ArmoryOfflineException($"Armory's file storage is busy ({effective}).");
                throw effective switch
                {
                    413 => new ScreenshotRefusedException("too_large", status, "Armory's file storage says the screenshot is too large."),
                    403 => new ScreenshotRefusedException("not_allowed", status, "Armory's file storage wouldn't take the screenshot from this account."),
                    409 => new ScreenshotRefusedException("exists", status, "Armory's file storage already has a screenshot with that name."),
                    404 => new ScreenshotRefusedException("not_available", status, "Armory's file storage isn't ready for screenshots yet."),
                    _ => new ScreenshotRefusedException("refused", status, $"Armory's file storage refused the screenshot ({effective})."),
                };
            }
        }
    }

    // A Storage code that means "busy, try again": a timeout, a lock, a read-only or slow database.
    internal static bool IsBusy(int code) => code is 408 or 423 or 429 || code >= 500;

    // The width and height in pixels a PNG's header (IHDR) gives, or null when it isn't a PNG.
    public static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> png)
    {
        // The signature (8), the IHDR chunk's length (4) and type (4), then width and height.
        if (png.Length < 24 || !IsPng(png) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    // Storage's error body: {"statusCode": "413", "code": "EntityTooLarge", "error": "Payload too large", "message": "..."}.
    private static async Task<(int? Code, string? Error)> StorageErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            int? code = root.TryGetProperty("statusCode", out var value) ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            } : null;
            // InvalidJWT is in "error", and in "code" too on current Storage.
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (root.TryGetProperty("code", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "InvalidJWT") error = "InvalidJWT";
            return (code, error);
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or InvalidOperationException) { return (null, null); }
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

// How much smaller a picture of the window is taken again when it is over the limit. The host
// measures the bytes of each try, so this never depends on the display's scaling. A picture's
// area, and roughly its PNG's bytes, shrink with the square of the scale.
public static class ScreenshotFit
{
    // Never smaller than a quarter of the window's own size: smaller would not be readable.
    public const double Smallest = 0.25;
    private const double Aim = 0.85, Step = 0.05, AtLeast = 0.1;

    // The next scale for a picture of `bytes` taken at `scale`: aimed at 85% of the limit, in
    // steps of 0.05, at least 0.1 smaller than `scale` and never below Smallest. Null when the
    // picture already fits, or `scale` is already the smallest.
    public static double? NextScale(long bytes, double scale, long limit = FeedbackScreenshots.MaximumBytes)
    {
        if (bytes <= limit || scale <= Smallest + 1e-9) return null;
        var aimed = scale * Math.Sqrt(Aim * limit / bytes);
        var stepped = Math.Floor(aimed / Step + 1e-9) * Step;
        return Math.Round(Math.Max(Smallest, Math.Min(stepped, scale - AtLeast)), 2);
    }
}
