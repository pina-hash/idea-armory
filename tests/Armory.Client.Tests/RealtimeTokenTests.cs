using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Armory.Client.Tests;

// Feedback b32e6726 (0.3.3, IDEA-00): every hour the live updates logged "Token has expired" and
// "join refused ... InvalidJWTToken: Token has expired N seconds ago" every few seconds, and the
// window's status flickered with them. The server's clock and this computer's disagree, so a
// token this computer believes is good can already be refused. These hold the feed to: renew
// ahead of time, renew at once when the server says expired, and never storm the server.
public sealed class RealtimeTokenTests
{
    private static readonly Guid ProjectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // A JWT-shaped token (unsigned: nothing here checks signatures) with iat and exp.
    private static string Jwt(string name, long iat, long exp)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"none\"}") + "." + B64($"{{\"sub\":\"{Guid.Empty}\",\"name\":\"{name}\",\"iat\":{iat},\"exp\":{exp}}}") + ".sig";
    }

    // The token endpoint: each refresh hands out the next token.
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public int Refreshes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Refreshes);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var body = new JsonObject { ["access_token"] = Jwt("fresh" + n, now, now + 3600), ["refresh_token"] = "refresh" + n, ["expires_in"] = 3600, ["expires_at"] = now + 3600 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    // A Realtime server in memory: refuses joins whose token is in Stale, answers heartbeats.
    private sealed class Server
    {
        public readonly HashSet<string> Stale = [];
        public readonly List<(string Topic, string Token)> Joins = [];
        public readonly List<string> TokensHanded = [];
        public Socket? Last;

        public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
        {
            Last = new Socket(this);
            return Task.FromResult<WebSocket>(Last);
        }

        public void Handle(Socket socket, JsonObject frame)
        {
            var topic = frame["topic"]!.GetValue<string>();
            var ev = frame["event"]!.GetValue<string>();
            var reference = frame["ref"]?.GetValue<string>();
            switch (ev)
            {
                case "phx_join":
                    var token = frame["payload"]!["access_token"]!.GetValue<string>();
                    lock (Joins) Joins.Add((topic, token));
                    var refused = Stale.Contains(token);
                    socket.Push(new JsonObject
                    {
                        ["topic"] = topic, ["event"] = "phx_reply", ["ref"] = reference,
                        ["payload"] = refused
                            ? new JsonObject { ["status"] = "error", ["response"] = new JsonObject { ["reason"] = "InvalidJWTToken: Token has expired 12 seconds ago" } }
                            : new JsonObject { ["status"] = "ok", ["response"] = new JsonObject() },
                    });
                    break;
                case "heartbeat":
                    socket.Push(new JsonObject { ["topic"] = "phoenix", ["event"] = "phx_reply", ["ref"] = reference, ["payload"] = new JsonObject { ["status"] = "ok" } });
                    break;
                case "access_token":
                    lock (TokensHanded) TokensHanded.Add(frame["payload"]!["access_token"]!.GetValue<string>());
                    break;
            }
        }

        // What Supabase sends a channel whose token ran out.
        public void Expire(string topic)
        {
            Last!.Push(new JsonObject { ["topic"] = topic, ["event"] = "system", ["payload"] = new JsonObject { ["status"] = "error", ["message"] = "Token has expired 0 seconds ago" } });
            Last!.Push(new JsonObject { ["topic"] = topic, ["event"] = "phx_close", ["payload"] = new JsonObject() });
        }

        public int JoinsWith(Func<string, bool> token) { lock (Joins) return Joins.Count(j => token(j.Token)); }
    }

    private sealed class Socket(Server server) : WebSocket
    {
        private readonly Channel<byte[]> inbox = Channel.CreateUnbounded<byte[]>();
        private WebSocketState state = WebSocketState.Open;
        public void Push(JsonObject frame) => inbox.Writer.TryWrite(Encoding.UTF8.GetBytes(frame.ToJsonString()));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { state = WebSocketState.Closed; inbox.Writer.TryComplete(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override void Dispose() => inbox.Writer.TryComplete();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await inbox.Reader.WaitToReadAsync(cancellationToken)) return new(0, WebSocketMessageType.Close, true);
            var bytes = await inbox.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            server.Handle(this, (JsonObject)JsonNode.Parse(buffer.AsSpan())!);
            return Task.CompletedTask;
        }
    }

    private static (RealtimeFeed Feed, SessionManager Sessions, TokenEndpoint Tokens, List<string> Log) Feed(Server server, string token, DateTimeOffset expiresAt)
    {
        var tokens = new TokenEndpoint();
        var sessions = new SessionManager(new HttpClient(tokens), new InMemorySecretStore());
        sessions.SignIn(new ArmorySession("https://example.supabase.co", "anon", token, "refresh0", expiresAt, "alex@school.test", Guid.NewGuid(), "LAB-1"));
        var log = new List<string>();
        var feed = new RealtimeFeed(sessions, server.ConnectAsync, line => { lock (log) log.Add(line); });
        return (feed, sessions, tokens, log);
    }

    private static async Task Until(Func<bool> done, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(done());
    }

    // A slow clock: the computer believes its token is good for 30 more minutes; the server
    // already refuses it. One renewal, then every channel joins with the new token.
    [Fact]
    public async Task A_join_refused_as_expired_renews_the_sign_in_once_and_joins_with_it()
    {
        var server = new Server();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stale = Jwt("stale", now - 3600, now + 1800);
        server.Stale.Add(stale);
        var (feed, sessions, tokens, log) = Feed(server, stale, DateTimeOffset.UtcNow.AddMinutes(30));
        feed.SetProjects([ProjectA, ProjectB]);
        using var stop = new CancellationTokenSource();
        var run = feed.RunAsync(stop.Token);
        await Until(() => feed.Joined.Count == 2, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await stop.CancelAsync();
        await run;
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal(1, feed.ForcedRenewals);
        Assert.Equal(2, server.JoinsWith(t => t == stale)); // one each, refused
        Assert.Equal(2, server.JoinsWith(t => t != stale)); // one each, with the new token
        Assert.NotEqual(stale, sessions.Current!.AccessToken);
        // The refusal is logged once, not once per channel or per second.
        Assert.Single(log, l => l.Contains("join refused", StringComparison.Ordinal));
    }

    // The hourly expiry while connected: the server closes every channel. The sign-in renews at
    // once, the open channels get it, the closed ones join again once each, and nothing storms.
    [Fact]
    public async Task Channels_closed_for_an_expired_token_rejoin_once_with_a_renewed_one()
    {
        var server = new Server();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = Jwt("first", now, now + 3600);
        var (feed, _, tokens, log) = Feed(server, first, DateTimeOffset.UtcNow.AddMinutes(50));
        feed.SetProjects([ProjectA, ProjectB]);
        using var stop = new CancellationTokenSource();
        var run = feed.RunAsync(stop.Token);
        await Until(() => feed.Joined.Count == 2, TimeSpan.FromSeconds(5));
        var changes = 0;
        feed.Changed += _ => Interlocked.Increment(ref changes);
        server.Stale.Add(first);
        server.Expire(RealtimeFeed.TopicFor(ProjectA));
        server.Expire(RealtimeFeed.TopicFor(ProjectB));
        await Until(() => feed.Joined.Count == 2 && server.JoinsWith(t => t != first) == 2, TimeSpan.FromSeconds(5));
        await Task.Delay(1500);
        await stop.CancelAsync();
        await run;
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal(2, server.JoinsWith(t => t != first));
        Assert.Equal(2, server.JoinsWith(t => t == first)); // only the first joins
        Assert.Equal(2, changes); // one read of each project after it joined again, no more
        Assert.True(log.Count(l => l.Contains("expired", StringComparison.OrdinalIgnoreCase)) <= 1, string.Join("\n", log));
    }

    // Renewed ahead: a token with four minutes left (less than RenewAhead) is renewed before the
    // first join, so the server never sees one about to run out.
    [Fact]
    public async Task A_token_close_to_running_out_is_renewed_before_it_is_used()
    {
        var server = new Server();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var old = Jwt("old", now - 3360, now + 240);
        var (feed, _, tokens, _) = Feed(server, old, DateTimeOffset.UtcNow.AddMinutes(4));
        feed.SetProjects([ProjectA]);
        using var stop = new CancellationTokenSource();
        var run = feed.RunAsync(stop.Token);
        await Until(() => feed.Joined.Count == 1, TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await run;
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal(0, server.JoinsWith(t => t == old));
    }

    // A token's life counts from now on this computer, whatever its clock says about the server's.
    [Fact]
    public void A_tokens_local_expiry_is_its_life_from_now_whatever_the_clocks_say()
    {
        var serverNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var token = Jwt("t", serverNow.ToUnixTimeSeconds(), serverNow.ToUnixTimeSeconds() + 3600);
        // This computer runs 3 minutes slow: the server's expires_at would be 63 minutes away here.
        var slowNow = serverNow.AddMinutes(-3);
        Assert.Equal(slowNow.AddHours(1), AccessToken.LocalExpiry(slowNow, token, serverNow.AddHours(1), null));
        // Fast by 3 minutes: the server's expires_at is the earlier one.
        var fastNow = serverNow.AddMinutes(3);
        Assert.Equal(serverNow.AddHours(1), AccessToken.LocalExpiry(fastNow, token, serverNow.AddHours(1), 3600));
        // No claims and no expires_at: expires_in from now.
        Assert.Equal(slowNow.AddSeconds(900), AccessToken.LocalExpiry(slowNow, "not-a-jwt", null, 900));
    }
}
