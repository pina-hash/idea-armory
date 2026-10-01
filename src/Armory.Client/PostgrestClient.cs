using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Client;

// POST {supabase_url}/rest/v1/rpc/{function} with the signed-in user's token and the
// public anon key (docs/agent/CLIENT.md section 1).
public sealed class PostgrestClient(HttpClient http, SessionManager sessions)
{
    public async Task<JsonNode?> CallAsync(string function, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        if (!function.StartsWith("armory_", StringComparison.Ordinal)) throw new ArgumentException("Only armory_ RPCs are called.", nameof(function));
        var session = await sessions.GetFreshAsync(cancellationToken: cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, session.SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/" + function)
            {
                Content = JsonContent.Create(arguments, options: Json.Options),
            };
            request.Headers.TryAddWithoutValidation("apikey", session.AnonKey);
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            request.Headers.Accept.Add(new("application/json"));
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, cancellationToken); }
            catch (HttpRequestException error) { throw new ArmoryOfflineException($"Armory could not be reached ({function}).", error); }
            catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new ArmoryOfflineException($"Armory took too long to answer ({function}).", error); }
            using (response)
            {
                string body;
                try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
                catch (HttpRequestException cut) { throw new ArmoryOfflineException($"Armory's answer was cut off ({function}).", cut); }
                if (response.IsSuccessStatusCode) return body.Length == 0 ? null : JsonNode.Parse(body);
                var error = ParseError(body);
                if (response.StatusCode == HttpStatusCode.Unauthorized && error.Code?.StartsWith("PGRST3", StringComparison.Ordinal) == true && attempt == 0)
                {
                    session = await sessions.GetFreshAsync(forceRefresh: true, cancellationToken);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests)
                    throw new ArmoryOfflineException($"Armory is busy or unavailable ({function}, {(int)response.StatusCode}).");
                throw new ArmoryRpcException((int)response.StatusCode, error.Code, error.Message ?? $"{function} failed with {(int)response.StatusCode}.", error.Details, error.Hint);
            }
        }
    }

    private static (string? Code, string? Message, string? Details, string? Hint) ParseError(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
                return (Text(o, "code"), Text(o, "message"), Text(o, "details"), Text(o, "hint"));
        }
        catch (JsonException) { }
        return (null, null, null, null);
        static string? Text(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;
    }
}
