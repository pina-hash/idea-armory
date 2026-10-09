using System.Text.RegularExpressions;

namespace Armory.Agent;

// Every line the agent writes to disk passes through Scrub. It removes anything that looks
// like a credential: JWTs (eyJ...), Bearer values, the values of access_token,
// refresh_token, Authorization, apikey, anon_key, code, verifier and pin in JSON, query strings,
// headers and plain "key: value" text, and long random base64url runs. It errs toward
// removing too much. An ArmorySession is only ever logged through its redacting ToString.
public static partial class Redactor
{
    public const string Mask = "[redacted]";

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = Jwt().Replace(text, Mask);
        result = AuthorizationHeader().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
        result = BearerValue().Replace(result, m => m.Groups["scheme"].Value + " " + Mask);
        result = QuotedKeyValue().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + "\"" + Mask + "\"");
        result = BareKeyValue().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
        result = LongRun().Replace(result, m => IsHexOrGuid(m.Value) ? m.Value : Mask);
        return result;
    }

    // Content hashes and GUIDs (hex digits and hyphens only) stay readable in the log. Every
    // other run of 32 or more base64url characters is removed, even one that happens to lack
    // a digit or an upper-case letter: a random 43-character token sometimes does.
    private static bool IsHexOrGuid(string run)
        => run.All(c => char.IsAsciiHexDigit(c) || c == '-');

    private const string Keys = "access_token|refresh_token|provider_token|provider_refresh_token|id_token|apikey|api_key|anon_key|anonKey|" +
        "accessToken|refreshToken|code|code_verifier|verifier|password|secret|client_secret|service_role_key|token_hash|" +
        // A shared computer's PIN (docs/agent/PROFILES.md): never logged, and masked if it ever were.
        "pin";

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{4,}(?:\.[A-Za-z0-9_\-]*){0,2}")]
    private static partial Regex Jwt();

    // "Authorization: Bearer x", "authorization=Basic x" and "\"Authorization\":\"Bearer x\"":
    // everything up to the end of the line or the closing quote.
    [GeneratedRegex("(?<key>\"?\\b(?:proxy-)?authorization\\b\"?)(?<sep>\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|[^\\r\\n,;}]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?<scheme>\bBearer)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerValue();

    // JSON members: "refresh_token": "..." (also with single quotes).
    [GeneratedRegex("(?<key>[\"'](?:" + Keys + ")[\"'])(?<sep>\\s*:\\s*)(?:\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*'|[^\\s,}\\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedKeyValue();

    // Query strings, form bodies, headers and log text: code=..., apikey: ..., verifier = ...
    [GeneratedRegex("(?<key>\\b(?:" + Keys + "))(?<sep>\\s*[:=]\\s*)(?!\\[redacted\\])(?:\"[^\"]*\"|'[^']*'|[^\\s&,;\"'}\\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BareKeyValue();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{32,}={0,2}(?![A-Za-z0-9_\-])")]
    private static partial Regex LongRun();
}
