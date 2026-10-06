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
    public int Status { get; } = status;
    public string? SqlState { get; } = sqlState;
    public string? Details { get; } = details;
    public string? Hint { get; } = hint;
    public bool IsNameTaken => SqlState == "23505";
    public bool IsForbidden => SqlState == "42501" || Status == 403;
    public bool IsInvalidInput => SqlState is not null && SqlState.StartsWith("22", StringComparison.Ordinal);
    // 55006 (object in use): a folder rename or delete refused because someone else has a file in it
    // checked out, or because the target folder already holds files. FolderRefusal.TryParse(Details).
    // PostgREST answers it with HTTP 500, so branch on this, never on Status.
    public bool IsInUse => SqlState == "55006";
    // 40P01 (deadlock) and 40001 (serialization failure): the server rolled the call back and
    // nothing was written. PostgrestClient resends such a call itself before raising this.
    public bool IsTransient => SqlState is "40P01" or "40001";
}

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
