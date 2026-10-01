using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Armory.Client;

public interface IBrowserLauncher
{
    void Open(Uri uri);
}

public sealed class ConnectException(string studentMessage, int? status = null) : ArmoryClientException(studentMessage)
{
    public int? Status { get; } = status;
}

// Contract section 3: a native-app loopback sign-in with state and PKCE. The browser does
// the site's normal Google sign-in; this computer only ever receives a one-time code, which
// is useless without the verifier that never leaves this process.
public sealed class ConnectFlow(HttpClient siteHttp, Uri site, IBrowserLauncher browser, SessionManager sessions)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public async Task<ArmorySession> ConnectAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("A device name is required.", nameof(deviceName));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Challenge(verifier);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connect = new Uri(site, "/armory/connect?port=" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "&state=" + Uri.EscapeDataString(state) + "&challenge=" + Uri.EscapeDataString(challenge) + "&device=" + Uri.EscapeDataString(deviceName));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var code = await WaitForCodeAsync(listener, state, () => browser.Open(connect), timeout.Token);
            var session = await ExchangeAsync(code, verifier, deviceName, cancellationToken);
            sessions.SignIn(session);
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectException("The browser did not finish connecting in time. Try again.");
        }
        finally { listener.Stop(); }
    }

    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, Action openBrowser, CancellationToken ct)
    {
        openBrowser();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            // A connection that never sends a request must not stall the sign-in.
            using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slow.CancelAfter(TimeSpan.FromSeconds(10));
            string? target;
            try { target = await ReadRequestTargetAsync(stream, slow.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (IOException) { continue; }
            if (target is null) { await RespondAsync(stream, 400, "Bad request.", ct); continue; }
            var question = target.IndexOf('?', StringComparison.Ordinal);
            var path = question < 0 ? target : target[..question];
            if (path != "/callback") { await RespondAsync(stream, 404, "Not found.", ct); continue; }
            var query = ParseQuery(question < 0 ? "" : target[(question + 1)..]);
            query.TryGetValue("state", out var returnedState);
            query.TryGetValue("code", out var code);
            if (returnedState is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returnedState), Encoding.ASCII.GetBytes(state)))
            {
                // A stray or forged request cannot cancel a real sign-in: keep waiting.
                await RespondAsync(stream, 400, "This sign-in link does not match. Go back to Armory and click Connect again.", ct);
                continue;
            }
            if (string.IsNullOrEmpty(code)) { await RespondAsync(stream, 400, "The sign-in did not finish. Go back to Armory and click Connect again.", ct); continue; }
            await RespondAsync(stream, 200, "This computer is connected to Armory. You can close this tab.", ct);
            return code;
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var line = head.Split("\r\n", 2)[0].Split(' ');
        return line.Length == 3 && line[0] == "GET" && line[1].StartsWith('/') ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>IDEA Armory</title></head>" +
            "<body style=\"font-family:Segoe UI,system-ui,sans-serif;margin:3rem;font-size:1.25rem\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return values;
    }

    private async Task<ArmorySession> ExchangeAsync(string code, string verifier, string deviceName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(site, "/api/armory/connect/exchange"))
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["code"] = code, ["verifier"] = verifier }),
        };
        HttpResponseMessage response;
        try { response = await siteHttp.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new ConnectException("ideabosco.com could not be reached. Check the internet connection and try again."); }
        using (response)
        {
            switch ((int)response.StatusCode)
            {
                case 400: throw new ConnectException("Armory could not read the sign-in. Click Connect again.", 400);
                case 401: throw new ConnectException("That sign-in was already used or did not match. Click Connect again.", 401);
                case 410: throw new ConnectException("The sign-in took too long and expired. Click Connect again.", 410);
                case 429: throw new ConnectException("Too many tries. Wait a minute, then click Connect again.", 429);
            }
            if (!response.IsSuccessStatusCode) throw new ConnectException($"ideabosco.com had a problem ({(int)response.StatusCode}). Try again in a minute.", (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                ? s : throw new ConnectException("ideabosco.com sent an incomplete sign-in. Click Connect again.");
            var expires = root.TryGetProperty("expires_at", out var e) ? e.ValueKind switch
            {
                JsonValueKind.Number => DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()),
                JsonValueKind.String when long.TryParse(e.GetString(), out var seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds),
                JsonValueKind.String => DateTimeOffset.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new ConnectException("ideabosco.com sent an incomplete sign-in. Click Connect again."),
            } : throw new ConnectException("ideabosco.com sent an incomplete sign-in. Click Connect again.");
            if (!Guid.TryParse(Text("device_id"), out var device)) throw new ConnectException("ideabosco.com sent an incomplete sign-in. Click Connect again.");
            return new ArmorySession(Text("supabase_url").TrimEnd('/'), Text("anon_key"), Text("access_token"), Text("refresh_token"),
                expires, Text("email").Trim().ToLowerInvariant(), device, deviceName);
        }
    }
}
