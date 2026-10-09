using System.Buffers.Text;
using System.Security.Cryptography;

namespace Armory.Agent;

// What one token of a notification stands for: its action (ProtocolLink.CheckOut, Show, SaveIn or KeepLocal), the
// vault-relative paths it acts on, and the notification it belongs to (its tag), if any.
internal sealed record ToastTicket(string Action, IReadOnlyList<string> Paths, string? Tag, DateTimeOffset Issued);

// The tokens Armory's notifications carry in their links (ProtocolLink), in memory only: each is
// 128 random bits, answers once, and only for 30 minutes; at most 200 live at once (the oldest
// goes first). A new Armory knows none of the last one's, so an old notification's button only
// opens the window. Thread safe.
internal sealed class ToastTokens(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal const int MostLive = 200;

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, ToastTicket> live = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    internal int Count
    {
        get { lock (gate) return live.Count; }
    }

    // A new token for action on paths; the link to put on a button is ProtocolLink.Format(token, action).
    internal string Issue(string action, IReadOnlyList<string> paths, string? tag = null)
    {
        if (!ProtocolLink.IsAction(action)) throw new ArgumentException("Unknown notification action " + action + ".", nameof(action));
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        lock (gate)
        {
            live[token] = new ToastTicket(action, paths.ToArray(), tag, time.GetUtcNow());
            order.Enqueue(token);
            while (live.Count > MostLive && order.TryDequeue(out var oldest)) live.Remove(oldest);
            // Tokens already used or expired leave the queue as they reach its head.
            while (order.TryPeek(out var head) && !live.ContainsKey(head)) order.Dequeue();
        }
        return token;
    }

    // The ticket behind a link, once: a token used before, expired, unknown, or named with
    // another action than it was made for answers nothing (and is gone either way).
    internal bool TryTake(ProtocolLink link, out ToastTicket? ticket)
    {
        ticket = null;
        lock (gate)
        {
            if (!live.Remove(link.Token, out var found)) return false;
            if (time.GetUtcNow() - found.Issued > Lifetime || found.Action != link.Action) return false;
            ticket = found;
            return true;
        }
    }

    // Armory is quitting: no token answers again.
    internal void Clear()
    {
        lock (gate)
        {
            live.Clear();
            order.Clear();
        }
    }
}
