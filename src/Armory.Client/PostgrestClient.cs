using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Telemetry;

namespace Armory.Client;

// POST {supabase_url}/rest/v1/rpc/{function} with the signed-in user's token and the
// public anon key (docs/agent/CLIENT.md section 1). Every call goes into the flight recorder
// with its name, how long it took and its answer (never a token or a body).
public sealed class PostgrestClient(HttpClient http, SessionManager sessions, FlightRecorder? recorder = null)
{
    public const int MaximumResends = 3;

    public async Task<JsonNode?> CallAsync(string function, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        if (!function.StartsWith("armory_", StringComparison.Ordinal)) throw new ArgumentException("Only armory_ RPCs are called.", nameof(function));
        if (recorder is null) return await CallUnrecordedAsync(function, arguments, cancellationToken);
        var started = recorder.Now();
        try
        {
            var answer = await CallUnrecordedAsync(function, arguments, cancellationToken);
            recorder.Rpc(function, recorder.MillisecondsSince(started), 200, null);
            return answer;
        }
        catch (ArmoryRpcException error)
        {
            recorder.Rpc(function, recorder.MillisecondsSince(started), error.Status, error.SqlState ?? "refused");
            throw;
        }
        catch (Exception error)
        {
            recorder.Rpc(function, recorder.MillisecondsSince(started), 0, error switch
            {
                ArmoryOfflineException => "offline",
                ArmorySignedOutException => "signedOut",
                OperationCanceledException => "canceled",
                _ => error.GetType().Name,
            });
            throw;
        }
    }

    private async Task<JsonNode?> CallUnrecordedAsync(string function, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var session = await sessions.GetFreshAsync(cancellationToken: cancellationToken);
        // One token refresh per call, whatever came before it: a token can expire during a
        // deadlock resend's wait, and that 401 is still refreshed once and sent again.
        var refreshed = false;
        var resent = 0;
        while (true)
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
                if (response.StatusCode == HttpStatusCode.Unauthorized && error.Code?.StartsWith("PGRST3", StringComparison.Ordinal) == true && !refreshed)
                {
                    refreshed = true;
                    session = await sessions.GetFreshAsync(forceRefresh: true, cancellationToken);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests)
                    throw new ArmoryOfflineException($"Armory is busy or unavailable ({function}, {(int)response.StatusCode}).");
                // A deadlock or serialization failure rolled the whole call back, receipt included, so
                // the same body (the same operation id) is sent again. 0232's folder rename and delete
                // can deadlock with a check out or check in in the same folder (docs/server/contract.md).
                if (error.Code is "40P01" or "40001" && resent < MaximumResends)
                {
                    resent++;
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * resent), cancellationToken);
                    continue;
                }
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
