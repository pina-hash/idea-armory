using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armory.Client;

// Live updates (ARMORY.md v0.3, item 1): Supabase Realtime pushes each new armory_change_feed row
// the moment it is written. One websocket to {supabase_url}/realtime/v1/websocket (Phoenix
// protocol 1.0.0, JSON), one channel per synced project, each with a postgres_changes INSERT
// subscription FILTERED by project_id=eq.<id>: since 0233 a site admin's session reads every
// project's rows, so an unfiltered subscription would bring every project's changes. There is no
// other kind of subscription here (FilterFor is the only filter, and JoinMessage always sets it).
//
// An event is only a reason to read the server again: Changed names the project and carries
// nothing else, and nothing here writes local state. The engine's poll stays the floor, so a
// socket that drops, a join the server refuses, or a site without Realtime costs nothing but
// latency. Failures are logged (once per kind of failure until a connection succeeds) and
// retried with a growing wait, never thrown to anyone.
public sealed class RealtimeFeed
{
    public const string Schema = "public", Table = "armory_change_feed", Event = "INSERT";
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private readonly SessionManager sessions;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> connect;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private HashSet<Guid> wanted = [];
    private string? lastProblem;

    // connect: opens the websocket (tests pass their own); the default is a ClientWebSocket.
    public RealtimeFeed(SessionManager sessions, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null, Action<string>? log = null)
    {
        this.sessions = sessions;
        this.connect = connect ?? ConnectAsync;
        this.log = log;
    }

    // A project's change feed has a new row: read it again. Raised on a pool thread.
    public event Action<Guid>? Changed;

    // How many times a connection opened, and the topics joined now (tests and the incident snapshot).
    public int Connections { get; private set; }
    public IReadOnlyCollection<Guid> Joined { get { lock (gate) return joinedNow.ToArray(); } }
    private HashSet<Guid> joinedNow = [];

    // The projects this computer syncs now. Channels follow at once (joined, or left).
    public void SetProjects(IEnumerable<Guid> projects)
    {
        var next = projects.ToHashSet();
        lock (gate)
        {
            if (next.SetEquals(wanted)) return;
            wanted = next;
        }
        wake.Release();
    }

    // The only filter there is: one project's rows.
    public static string FilterFor(Guid project) => "project_id=eq." + project.ToString("D");
    public static string TopicFor(Guid project) => "realtime:armory-feed-" + project.ToString("D");

    // {supabase_url}/realtime/v1/websocket with the public anon key, as Supabase's own clients
    // connect (the anon key is public; the access token goes in each join, never in the URL).
    public static Uri SocketUri(string supabaseUrl, string anonKey)
    {
        var builder = new UriBuilder(supabaseUrl.TrimEnd('/') + "/realtime/v1/websocket");
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        builder.Query = "apikey=" + Uri.EscapeDataString(anonKey) + "&vsn=1.0.0";
        return builder.Uri;
    }

    // The phx_join for one project's channel: INSERTs on armory_change_feed where project_id is it.
    public static JsonObject JoinMessage(Guid project, string accessToken, string reference) => new()
    {
        ["topic"] = TopicFor(project),
        ["event"] = "phx_join",
        ["payload"] = new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["broadcast"] = new JsonObject { ["ack"] = false, ["self"] = false },
                ["presence"] = new JsonObject { ["key"] = "" },
                ["postgres_changes"] = new JsonArray(new JsonObject
                {
                    ["event"] = Event, ["schema"] = Schema, ["table"] = Table, ["filter"] = FilterFor(project),
                }),
                ["private"] = false,
            },
            ["access_token"] = accessToken,
        },
        ["ref"] = reference,
        ["join_ref"] = reference,
    };

    // Connects, keeps the channels in step with SetProjects, and reconnects after any failure,
    // until canceled. Never throws (but OperationCanceledException when canceled).
    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var session = sessions.Current;
            bool any;
            lock (gate) any = wanted.Count > 0;
            if (session is null || !any)
            {
                // Nothing to listen to: wait for projects or a sign-in (looked at again every few seconds).
                try { await wake.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            var healthy = false;
            try
            {
                // A token good for a while goes in the joins (a refresh, when it is close to expiring).
                session = await sessions.GetFreshAsync(cancellationToken: ct);
                healthy = await RunConnectionAsync(session, ct);
            }
            catch (ArmorySignedOutException) { continue; } // signed out: waits above for a sign-in
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { Problem(error.GetType().Name + ": " + error.Message); }
            lock (gate) joinedNow = [];
            failures = healthy ? 0 : failures + 1;
            if (failures == 0) continue;
            try { await Task.Delay(Backoff[Math.Min(failures, Backoff.Length) - 1], ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One connection. True when it was useful (a join answered ok) before it ended.
    private async Task<bool> RunConnectionAsync(ArmorySession session, CancellationToken ct)
    {
        using var socket = await connect(SocketUri(session.SupabaseUrl, session.AnonKey), ct);
        Connections++;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = new SemaphoreSlim(1, 1);
        var reference = 0;
        var pendingJoins = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var joined = new Dictionary<Guid, string>(); // project -> topic
        string? heartbeatRef = null;
        var useful = false;
        var token = session.AccessToken;

        async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await send.WaitAsync(stop.Token);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token); }
            finally { send.Release(); }
        }

        var receiving = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (!stop.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                JsonObject? frame = null;
                try { frame = JsonNode.Parse(message.ToArray()) as JsonObject; }
                catch (JsonException) { }
                message.SetLength(0);
                if (frame is not null) Handle(frame);
            }
        }, stop.Token);

        void Handle(JsonObject frame)
        {
            var topic = Text(frame["topic"]);
            var name = Text(frame["event"]);
            var payload = frame["payload"] as JsonObject;
            var reply = Text(frame["ref"]);
            switch (name)
            {
                case "phx_reply" when reply is not null && reply == Volatile.Read(ref heartbeatRef):
                    Volatile.Write(ref heartbeatRef, null);
                    return;
                case "phx_reply" when reply is not null:
                    Guid project;
                    lock (pendingJoins)
                    {
                        if (!pendingJoins.Remove(reply, out project)) return;
                    }
                    if (Text(payload?["status"]) == "ok")
                    {
                        useful = true;
                        lastProblem = null;
                        lock (gate) joinedNow.Add(project);
                        // Joined late: whatever was written meanwhile is read now.
                        Changed?.Invoke(project);
                    }
                    else
                    {
                        Problem("join refused for " + topic + ": " + payload?["response"]?.ToJsonString());
                        lock (joined) joined.Remove(project);
                    }
                    return;
                case "postgres_changes":
                    if (ProjectOf(topic) is { } changed) Changed?.Invoke(changed);
                    return;
                case "system" when Text(payload?["status"]) == "error":
                case "phx_error":
                case "phx_close":
                    Problem(name + " on " + topic + (payload is null ? "" : ": " + Text(payload["message"])));
                    if (ProjectOf(topic) is { } dropped)
                    {
                        lock (joined) joined.Remove(dropped);
                        lock (gate) joinedNow.Remove(dropped);
                    }
                    return;
            }
        }

        try
        {
            var lastBeat = Environment.TickCount64 - (long)HeartbeatEvery.TotalMilliseconds - 1;
            while (!stop.IsCancellationRequested)
            {
                if (receiving.IsCompleted)
                {
                    await receiving; // a failure ends this connection
                    return useful;
                }
                // Channels follow the projects this computer syncs.
                HashSet<Guid> want;
                lock (gate) want = [.. wanted];
                if (want.Count == 0) { await CloseAsync(socket); return useful; }
                List<(Guid Project, string Topic)> leave, join;
                lock (joined)
                {
                    leave = joined.Where(j => !want.Contains(j.Key)).Select(j => (j.Key, j.Value)).ToList();
                    join = want.Where(p => !joined.ContainsKey(p)).Select(p => (p, TopicFor(p))).ToList();
                    foreach (var (project, _) in leave) joined.Remove(project);
                    foreach (var (project, topic) in join) joined[project] = topic;
                }
                foreach (var (project, topic) in leave)
                {
                    lock (gate) joinedNow.Remove(project);
                    await SendAsync(new JsonObject { ["topic"] = topic, ["event"] = "phx_leave", ["payload"] = new JsonObject(), ["ref"] = Next() });
                }
                foreach (var (project, _) in join)
                {
                    var r = Next();
                    lock (pendingJoins) pendingJoins[r] = project;
                    await SendAsync(JoinMessage(project, token, r));
                }
                if (Environment.TickCount64 - lastBeat >= (long)HeartbeatEvery.TotalMilliseconds)
                {
                    // A heartbeat unanswered for a whole interval means the connection is dead.
                    if (Volatile.Read(ref heartbeatRef) is not null)
                    {
                        Problem("heartbeat unanswered");
                        await CloseAsync(socket);
                        return useful;
                    }
                    lastBeat = Environment.TickCount64;
                    var beat = Next();
                    Volatile.Write(ref heartbeatRef, beat);
                    await SendAsync(new JsonObject { ["topic"] = "phoenix", ["event"] = "heartbeat", ["payload"] = new JsonObject(), ["ref"] = beat });
                    // A renewed sign-in goes to every channel, so the server keeps delivering.
                    try
                    {
                        var fresh = await sessions.GetFreshAsync(cancellationToken: stop.Token);
                        if (fresh.AccessToken != token)
                        {
                            token = fresh.AccessToken;
                            List<string> topics;
                            lock (joined) topics = [.. joined.Values];
                            foreach (var topic in topics)
                                await SendAsync(new JsonObject { ["topic"] = topic, ["event"] = "access_token", ["payload"] = new JsonObject { ["access_token"] = token }, ["ref"] = Next() });
                        }
                    }
                    catch (ArmorySignedOutException) { await CloseAsync(socket); return useful; }
                    catch (ArmoryOfflineException) { }
                }
                // The next look: when the heartbeat is due, sooner when the projects change.
                var due = TimeSpan.FromMilliseconds(Math.Max(1, (long)HeartbeatEvery.TotalMilliseconds - (Environment.TickCount64 - lastBeat)));
                await Task.WhenAny(wake.WaitAsync(due, stop.Token), receiving);
            }
            return useful;
        }
        finally
        {
            await stop.CancelAsync();
            try { await receiving; }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) { }
        }

        string Next() => (++reference).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Guid? ProjectOf(string? topic)
        => topic is not null && topic.StartsWith("realtime:armory-feed-", StringComparison.Ordinal) && Guid.TryParse(topic["realtime:armory-feed-".Length..], out var id) ? id : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void Problem(string what)
    {
        // The same problem again (a site without Realtime, a refused join) is logged once.
        if (what == lastProblem) return;
        lastProblem = what;
        log?.Invoke("live updates: " + what + " (the regular check for changes carries on)");
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
