using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Armory.TestSupport;

/// <summary>
/// Supabase Realtime on the fake: <c>/realtime/v1/websocket?apikey=&amp;vsn=1.0.0</c>, the Phoenix
/// protocol's JSON frames (phx_join, heartbeat, access_token, phx_leave), and postgres_changes
/// INSERTs on <c>armory_change_feed</c>. Each join is recorded with its filter
/// (<see cref="RealtimeJoins"/>), so a test can hold every subscription to <c>project_id=eq.&lt;id&gt;</c>.
/// After each RPC, the change feed's new rows go to every channel whose filter matches the row
/// (an unfiltered channel gets every row its caller may read, which is the flood ARMORY.md warns
/// about) and whose caller is a member of the row's project or a site admin, as RLS would allow.
/// </summary>
public sealed partial class FakeSupabase
{
    public const string RealtimePath = "/realtime/v1/websocket";

    /// <summary>One postgres_changes subscription a client asked for in a phx_join.</summary>
    public sealed record RealtimeJoin(string Topic, string? Event, string? Schema, string? Table, string? Filter, string? Email, bool Accepted);

    private sealed class RealtimeChannel(WebSocket socket, SemaphoreSlim send, string topic, string? filter, string email)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Send { get; } = send;
        public string Topic { get; } = topic;
        public string? Filter { get; } = filter;
        public string Email { get; set; } = email;
    }

    private readonly ConcurrentQueue<RealtimeJoin> _realtimeJoins = new();
    private readonly List<RealtimeChannel> _realtimeChannels = [];
    private readonly SemaphoreSlim _realtimePush = new(1, 1);
    private long _realtimeCursor = -1;
    private int _realtimeDelivered;
    private volatile bool _realtimeEnabled = true;

    /// <summary>When false, the websocket answers 404, like a project without Realtime.</summary>
    public bool RealtimeEnabled { get => _realtimeEnabled; set => _realtimeEnabled = value; }

    /// <summary>Every subscription asked for so far, in order.</summary>
    public IReadOnlyList<RealtimeJoin> RealtimeJoins => _realtimeJoins.ToArray();

    /// <summary>The channels joined now (topic and filter).</summary>
    public IReadOnlyList<(string Topic, string? Filter, string Email)> RealtimeChannels
    {
        get { lock (_realtimeChannels) return _realtimeChannels.Select(c => (c.Topic, c.Filter, c.Email)).ToArray(); }
    }

    /// <summary>postgres_changes frames delivered so far.</summary>
    public int RealtimeDelivered => Volatile.Read(ref _realtimeDelivered);

    private protected override async Task<bool> HandleWebSocketAsync(HttpContext context)
    {
        if (context.Request.Path.Value != RealtimePath) return false;
        if (!RealtimeEnabled) { context.Response.StatusCode = 404; return true; }
        if (!FixedTimeEquals(context.Request.Query["apikey"].ToString(), AnonKey)) { context.Response.StatusCode = 401; return true; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var send = new SemaphoreSlim(1, 1);
        await EnsureRealtimeCursorAsync();
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var frame = JsonNode.Parse(message.ToArray()) as JsonObject;
                message.SetLength(0);
                if (frame is not null) await HandleRealtimeFrameAsync(socket, send, frame);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            lock (_realtimeChannels) _realtimeChannels.RemoveAll(c => ReferenceEquals(c.Socket, socket));
        }
        return true;
    }

    private async Task HandleRealtimeFrameAsync(WebSocket socket, SemaphoreSlim send, JsonObject frame)
    {
        var topic = frame["topic"]?.GetValue<string>() ?? "";
        var name = frame["event"]?.GetValue<string>() ?? "";
        var reference = frame["ref"]?.GetValue<string>();
        var payload = frame["payload"] as JsonObject ?? [];
        switch (name)
        {
            case "heartbeat":
                await SendRealtimeAsync(socket, send, Reply(topic, reference, "ok", new JsonObject()));
                return;
            case "phx_join":
            {
                var token = payload["access_token"]?.GetValue<string>();
                var live = CheckAccessToken(token, out var email) == TokenCheck.Live;
                var changes = (payload["config"] as JsonObject)?["postgres_changes"] as JsonArray ?? [];
                var accepted = new JsonArray();
                var id = 1;
                foreach (var change in changes.OfType<JsonObject>())
                {
                    var join = new RealtimeJoin(topic, change["event"]?.GetValue<string>(), change["schema"]?.GetValue<string>(), change["table"]?.GetValue<string>(),
                        change["filter"]?.GetValue<string>(), email, live);
                    _realtimeJoins.Enqueue(join);
                    if (!live) continue;
                    lock (_realtimeChannels) _realtimeChannels.Add(new RealtimeChannel(socket, send, topic, join.Filter, email!));
                    accepted.Add(new JsonObject { ["id"] = id++, ["event"] = join.Event, ["schema"] = join.Schema, ["table"] = join.Table, ["filter"] = join.Filter });
                }
                await SendRealtimeAsync(socket, send, live
                    ? Reply(topic, reference, "ok", new JsonObject { ["postgres_changes"] = accepted })
                    : Reply(topic, reference, "error", new JsonObject { ["reason"] = "Invalid JWT" }));
                return;
            }
            case "access_token":
            {
                if (CheckAccessToken(payload["access_token"]?.GetValue<string>(), out var email) == TokenCheck.Live)
                    lock (_realtimeChannels) foreach (var c in _realtimeChannels.Where(c => ReferenceEquals(c.Socket, socket) && c.Topic == topic)) c.Email = email!;
                return;
            }
            case "phx_leave":
                lock (_realtimeChannels) _realtimeChannels.RemoveAll(c => ReferenceEquals(c.Socket, socket) && c.Topic == topic);
                await SendRealtimeAsync(socket, send, Reply(topic, reference, "ok", new JsonObject()));
                return;
        }
    }

    private static JsonObject Reply(string topic, string? reference, string status, JsonObject response) => new()
    {
        ["topic"] = topic,
        ["event"] = "phx_reply",
        ["payload"] = new JsonObject { ["status"] = status, ["response"] = response },
        ["ref"] = reference,
    };

    private static async Task SendRealtimeAsync(WebSocket socket, SemaphoreSlim send, JsonObject frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await send.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException) { }
        finally { send.Release(); }
    }

    private async Task EnsureRealtimeCursorAsync()
    {
        if (Interlocked.Read(ref _realtimeCursor) >= 0) return;
        await _realtimePush.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _realtimeCursor) >= 0) return;
            await using var connection = await Database.OpenAsync();
            await using var command = new NpgsqlCommand("select coalesce(max(cursor), 0) from public.armory_change_feed", connection);
            Interlocked.Exchange(ref _realtimeCursor, (long)(await command.ExecuteScalarAsync())!);
        }
        finally { _realtimePush.Release(); }
    }

    // The change feed's rows written since the last push, to every channel allowed to see them.
    private async Task PushRealtimeChangesAsync()
    {
        RealtimeChannel[] channels;
        lock (_realtimeChannels) channels = [.. _realtimeChannels];
        if (channels.Length == 0 || Interlocked.Read(ref _realtimeCursor) < 0) return;
        await _realtimePush.WaitAsync();
        try
        {
            await using var connection = await Database.OpenAsync();
            var rows = new List<(long Cursor, Guid Project, string Kind, Guid Entity, string Payload, DateTime At)>();
            await using (var command = new NpgsqlCommand("select cursor, project_id, kind, entity_id, payload::text, created_at from public.armory_change_feed where cursor > $1 order by cursor", connection))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = Interlocked.Read(ref _realtimeCursor) });
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetDateTime(5)));
            }
            foreach (var row in rows)
            {
                Interlocked.Exchange(ref _realtimeCursor, row.Cursor);
                foreach (var channel in channels)
                {
                    if (channel.Filter is { } filter && filter != "project_id=eq." + row.Project.ToString("D")) continue;
                    if (!AdminEmails.Contains(channel.Email) && !await IsMemberAsync(connection, row.Project, channel.Email)) continue;
                    var record = new JsonObject
                    {
                        ["cursor"] = row.Cursor, ["project_id"] = row.Project.ToString("D"), ["kind"] = row.Kind, ["entity_id"] = row.Entity.ToString("D"),
                        ["payload"] = JsonNode.Parse(row.Payload), ["created_at"] = row.At.ToString("O"),
                    };
                    await SendRealtimeAsync(channel.Socket, channel.Send, new JsonObject
                    {
                        ["topic"] = channel.Topic,
                        ["event"] = "postgres_changes",
                        ["payload"] = new JsonObject
                        {
                            ["data"] = new JsonObject
                            {
                                ["schema"] = "public", ["table"] = "armory_change_feed", ["commit_timestamp"] = row.At.ToString("O"), ["type"] = "INSERT",
                                ["record"] = record, ["columns"] = new JsonArray(), ["errors"] = null,
                            },
                            ["ids"] = new JsonArray(1),
                        },
                        ["ref"] = null,
                    });
                    Interlocked.Increment(ref _realtimeDelivered);
                }
            }
        }
        finally { _realtimePush.Release(); }
    }

    private static async Task<bool> IsMemberAsync(NpgsqlConnection connection, Guid project, string email)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from public.armory_members where project_id = $1 and email = $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = project });
        command.Parameters.Add(new NpgsqlParameter { Value = email });
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
