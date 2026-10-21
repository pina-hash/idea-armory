using System.Buffers.Text;
using System.Text.Json;

namespace Armory.Client;

// Reads the claims of a Supabase access token (a JWT) on this computer. Nothing here checks the
// signature: the server does that on every call, and the claims are only used to name things
// the server checks again (the screenshot folder is the auth uid, armory_submit_app_feedback
// refuses any other folder as bad_path). A token is a secret: nothing here logs it, puts it in
// an exception or keeps it.
public static class AccessToken
{
    // The longest token read at all; a Supabase access token is well under 2 KiB.
    private const int MaximumLength = 16 * 1024;

    // The auth uid (the "sub" claim) as a uuid, or null when the token is not a JWT, its payload
    // is not JSON, or sub is missing or not a uuid.
    public static Guid? Subject(string? token)
        => Claim(token, "sub") is { } sub && Guid.TryParseExact(sub, "D", out var id) ? id : null;

    // One string claim of the token's payload, or null.
    public static string? Claim(string? token, string name)
        => Read(token, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // One whole-number claim (exp, iat), or null.
    public static long? NumberClaim(string? token, string name)
        => Read(token, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    // When a token received just now stops working, by THIS computer's clock (feedback b32e6726:
    // the live updates said "Token has expired" every second). Supabase's expires_at and the
    // token's exp are the server's clock; a lab computer whose clock runs a few minutes slow then
    // believes a token is good minutes after the server stopped taking it, and renews it too late.
    // A token's life (exp minus iat) and expires_in are lengths, which no clock skews, so the
    // earliest of the three, counted from now, is the answer.
    public static DateTimeOffset LocalExpiry(DateTimeOffset now, string? token, DateTimeOffset? serverExpiresAt, long? expiresIn)
    {
        var earliest = serverExpiresAt ?? now.AddSeconds(expiresIn ?? 3600);
        if (expiresIn is { } seconds && seconds > 0) earliest = Min(earliest, now.AddSeconds(seconds));
        if (NumberClaim(token, "exp") is { } exp && NumberClaim(token, "iat") is { } iat && exp > iat)
            earliest = Min(earliest, now.AddSeconds(exp - iat));
        return earliest;

        static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }

    private static bool Read(string? token, string name, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrEmpty(token) || token.Length > MaximumLength) return false;
        var first = token.IndexOf('.', StringComparison.Ordinal);
        var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
        if (first <= 0 || second < 0 || token.IndexOf('.', second + 1) >= 0) return false;
        var payload = token.AsSpan(first + 1, second - first - 1);
        if (!Base64Url.IsValid(payload)) return false;
        var buffer = new byte[Base64Url.GetMaxDecodedLength(payload.Length)];
        try
        {
            if (!Base64Url.TryDecodeFromChars(payload, buffer, out var written)) return false;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, written));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(name, out var found)) return false;
            value = found.Clone();
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException) { return false; }
    }
}
