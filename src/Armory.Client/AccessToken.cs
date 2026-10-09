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
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumLength) return null;
        var first = token.IndexOf('.', StringComparison.Ordinal);
        var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
        if (first <= 0 || second < 0 || token.IndexOf('.', second + 1) >= 0) return null;
        var payload = token.AsSpan(first + 1, second - first - 1);
        if (!Base64Url.IsValid(payload)) return null;
        var buffer = new byte[Base64Url.GetMaxDecodedLength(payload.Length)];
        try
        {
            if (!Base64Url.TryDecodeFromChars(payload, buffer, out var written)) return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, written));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is JsonException or FormatException) { return null; }
    }
}
