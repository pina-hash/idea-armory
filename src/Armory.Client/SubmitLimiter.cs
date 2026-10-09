using Armory.Telemetry;

namespace Armory.Client;

// When each submit RPC may be asked again, shared by everything that sends feedback and
// incidents (the background IncidentUploader and the window's FeedbackSender), so one PT429
// makes both wait, and kept in the incidents folder (upload-wait.json) across restarts. Two kinds
// of wait: the site does not have the RPC yet (404 PGRST202), and this account reached its hourly
// limit (PT429, until DETAIL retry_after_seconds has passed). Safe from any thread.
public sealed class SubmitLimiter
{
    // A rate-limit wait is kept under its own key, so it never reads as "not on the site".
    internal const string RateLimitedKey = "#rate-limited";
    private readonly IncidentStore? store;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, DateTimeOffset> waits;
    private readonly object gate = new();

    public SubmitLimiter(IncidentStore? store = null, TimeProvider? clock = null)
    {
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        waits = store?.ReadWaits() ?? new(StringComparer.Ordinal);
    }

    // True while this RPC is not asked: the site lacks it, or this account reached its limit.
    public bool IsWaiting(string rpc) => Remaining(rpc) is not null || IsRateLimited(rpc);

    // True while a PT429 for this RPC has not run out.
    public bool IsRateLimited(string rpc) => RateLimitedFor(rpc) is not null;

    // How long until a PT429 for this RPC runs out, or null when there is none.
    public TimeSpan? RateLimitedFor(string rpc) => Remaining(rpc + RateLimitedKey);

    // The site has no such RPC (404 PGRST202): not asked again for this long.
    public void NotLive(string rpc, TimeSpan wait) => Wait(rpc, wait);

    // PT429: not sent again for this long (the DETAIL's retry_after_seconds).
    public void RateLimited(string rpc, TimeSpan wait) => Wait(rpc + RateLimitedKey, wait);

    private TimeSpan? Remaining(string key)
    {
        lock (gate)
        {
            if (!waits.TryGetValue(key, out var until)) return null;
            var left = until - clock.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    private void Wait(string key, TimeSpan wait)
    {
        Dictionary<string, DateTimeOffset> copy;
        lock (gate)
        {
            waits[key] = clock.GetUtcNow() + wait;
            copy = new(waits, StringComparer.Ordinal);
        }
        if (store is null) return;
        try { store.WriteWaits(copy); }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
    }
}
