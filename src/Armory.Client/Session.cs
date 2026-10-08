using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armory.Client;

// One signed-in computer. Both tokens are secrets: ToString redacts them, and only
// ISecretStore ever persists them.
public sealed record ArmorySession(
    string SupabaseUrl, string AnonKey, string AccessToken, string RefreshToken,
    DateTimeOffset ExpiresAt, string Email, Guid DeviceId, string DeviceName)
{
    public override string ToString() => $"ArmorySession {{ Email = {Email}, DeviceId = {DeviceId}, DeviceName = {DeviceName}, ExpiresAt = {ExpiresAt:O}, tokens redacted }}";
}

// Secrets at rest. Windows: Armory.Platform.Windows.DpapiSecretStore (DPAPI CurrentUser).
public interface ISecretStore
{
    byte[]? Read(string name);
    void Write(string name, byte[] value);
    void Delete(string name);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public byte[]? Read(string name) { lock (gate) return values.TryGetValue(name, out var v) ? v.ToArray() : null; }
    public void Write(string name, byte[] value) { lock (gate) values[name] = value.ToArray(); }
    public void Delete(string name) { lock (gate) values.Remove(name); }
}

internal static class SessionStorage
{
    internal const string Name = "armory-session";
    private sealed record Stored(string SupabaseUrl, string AnonKey, string AccessToken, string RefreshToken,
        DateTimeOffset ExpiresAt, string Email, Guid DeviceId, string DeviceName);
    internal static void Save(ISecretStore store, ArmorySession s)
        => store.Write(Name, JsonSerializer.SerializeToUtf8Bytes(new Stored(s.SupabaseUrl, s.AnonKey, s.AccessToken, s.RefreshToken, s.ExpiresAt, s.Email, s.DeviceId, s.DeviceName)));
    internal static ArmorySession? Load(ISecretStore store)
    {
        var bytes = store.Read(Name);
        if (bytes is null) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(bytes);
            return s is null ? null : new(s.SupabaseUrl, s.AnonKey, s.AccessToken, s.RefreshToken, s.ExpiresAt, s.Email, s.DeviceId, s.DeviceName);
        }
        catch (JsonException) { return null; }
    }
}

public class ArmoryClientException(string message, Exception? inner = null) : Exception(message, inner);
// The network or the server is unreachable. The agent queues work and retries later.
public sealed class ArmoryOfflineException(string message, Exception? inner = null) : ArmoryClientException(message, inner);
// The refresh token was refused. This computer must be connected again.
public sealed class ArmorySignedOutException(string message) : ArmoryClientException(message);
public sealed class ArmoryRpcException(int status, string? sqlState, string message, string? details, string? hint)
    : ArmoryClientException(message)
{
    public const string NotMemberMessage = "not a project member";
    public const string RateLimitedState = "PT429", InvalidValueState = "22023";

    public int Status { get; } = status;
    public string? SqlState { get; } = sqlState;
    public string? Details { get; } = details;
    public string? Hint { get; } = hint;
    // The JSON DETAIL the Armory convention adds where there is more to say (reason, field,
    // limit, size, retry_after_seconds, names, total), or null when DETAIL is not a JSON object.
    public RefusalDetail? Detail { get; } = RefusalDetail.TryParse(details);
    // DETAIL.reason, or null. Branch on SqlState and this, never on Status: PostgREST answers
    // 23505 and 23503 both with 409, and 55xxx and P0002 both with 500.
    public string? Reason => Detail?.Reason;
    public bool IsNameTaken => SqlState == "23505";
    // 42501 by SQLSTATE only: a bare 403 with no code (a proxy, a gateway) is not Armory's refusal.
    public bool IsForbidden => SqlState == "42501";
    public bool IsInvalidInput => SqlState is not null && SqlState.StartsWith("22", StringComparison.Ordinal);
    // "No longer a member of this project" (ARMORY.md v0.3, "What the Windows app must do"): P0001
    // from armory_list_changes, armory_acquire_lock and armory_save_side_version, and 42501 from
    // armory_project_files, armory_file_history, armory_create_file and armory_move_file, both
    // with the text "not a project member". Treated alike.
    public bool IsNotMember => SqlState is "P0001" or "42501" && string.Equals(Message, NotMemberMessage, StringComparison.Ordinal);
    // PT429 (HTTP 429): a per-account limit; DETAIL.retry_after_seconds says when to send again.
    public bool IsRateLimited => SqlState == RateLimitedState;
    public TimeSpan? RetryAfter => Detail?.RetryAfterSeconds is { } seconds and >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    // 22023 with reason too_large or too_long: the payload must be shortened, then sent once.
    public bool IsTooLarge => SqlState == InvalidValueState && Reason is "too_large" or "too_long";
    // 55006 (object in use): a folder rename or delete refused because someone else has a file in it
    // checked out, or because the target folder already holds files. FolderRefusal.TryParse(Details).
    // PostgREST answers it with HTTP 500, so branch on this, never on Status.
    public bool IsInUse => SqlState == "55006";
    // 40P01 (deadlock) and 40001 (serialization failure): the server rolled the call back and
    // nothing was written. PostgrestClient resends such a call itself before raising this.
    public bool IsTransient => SqlState is "40P01" or "40001";
    // PostgREST's 404 PGRST202: the site has no such function (yet). An RPC the website has not
    // shipped answers this until its migration is live (docs/agent/TELEMETRY.md, "Upload").
    public bool IsFunctionMissing => Status == 404 && SqlState == "PGRST202";
}

// The JSON DETAIL of an Armory refusal (ARMORY.md, "Refusals keep the Armory convention"):
// reason, plus names and total, or field, limit and size, or retry_after_seconds for PT429.
// Every field is optional; TryParse returns null for anything that is not a JSON object.
public sealed record RefusalDetail(string? Reason, string? Field, long? Limit, long? Size, double? RetryAfterSeconds, long? Total, IReadOnlyList<string> Names)
{
    public static RefusalDetail? TryParse(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return null;
        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new List<string>();
            if (root.TryGetProperty("names", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) names.Add(item.GetString()!);
            return new(Text(root, "reason"), Text(root, "field"), Whole(root, "limit"), Whole(root, "size"), Number(root, "retry_after_seconds"), Whole(root, "total"), names);
        }
        catch (JsonException) { return null; }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
        static long? Whole(JsonElement e, string name) => Number(e, name) is { } n ? (long)n : null;
    }
}

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
