using System.Diagnostics;
using Armory.TestSupport;

namespace Armory.EndToEnd.Tests;

// A network profile for throughput measurements: a fixed round trip for every request to file
// storage, ideabosco.com and Supabase, plus bandwidth for storage bodies (a per-connection rate
// and one shared link rate, so concurrency does not scale for free). None (the default) adds
// nothing, so every other test runs at loopback speed.
internal sealed record LatencyProfile(TimeSpan StorageRoundTrip, double StorageBytesPerSecond, double LinkBytesPerSecond,
    TimeSpan SiteRoundTrip, TimeSpan RpcRoundTrip)
{
    public static LatencyProfile None { get; } = new(TimeSpan.Zero, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

    // A school network to Cloudflare R2 and Supabase: 60 ms to storage at 4 MB/s per connection
    // and 25 MB/s for the whole link, 250 ms for a blob URL (the site checks the token, the
    // membership and the object before signing), 60 ms per database call.
    public static LatencyProfile School { get; } = new(TimeSpan.FromMilliseconds(60), 4_000_000, 25_000_000,
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(60));

    public bool IsNone => this == None;
}

// The link storage bodies share: each body reserves its share of the link's rate, in order. A
// computer has its own unless the world gives every computer one (a classroom behind one school
// connection).
internal sealed class NetworkLink
{
    private readonly object gate = new();
    private long free;

    // Seconds until a body of this many bytes, sent now, is through the link at this rate.
    public double Reserve(long bytes, double bytesPerSecond)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            var start = Math.Max(now, free);
            free = start + (long)(bytes / bytesPerSecond * Stopwatch.Frequency);
            return (double)(free - now) / Stopwatch.Frequency;
        }
    }
}

// Sits between the agent's HttpClient and FakeNetworkHandler. Requests are classified by where
// they go: the fake S3 host is storage, the fake site's port is ideabosco.com, the fake
// Supabase port is the database. A storage body pays its bytes over the per-connection rate
// and reserves its share of the link.
internal sealed class LatencyHandler(LatencyProfile profile, int sitePort, int rpcPort, HttpMessageHandler inner, NetworkLink? link = null) : DelegatingHandler(inner)
{
    private readonly NetworkLink link = link ?? new NetworkLink();
    // The profile in force now: a test builds a large vault at loopback speed, then measures its
    // clicks on the school network (LargeVaultResponsivenessTests).
    public LatencyProfile Profile { get; set; } = profile;
    private long storageRequests, storageGets, siteRequests, rpcRequests, storageBytes;

    public long StorageRequests => Interlocked.Read(ref storageRequests);
    // Downloads from file storage (GET), the measure of "nothing was downloaded again".
    public long StorageGets => Interlocked.Read(ref storageGets);
    public long SiteRequests => Interlocked.Read(ref siteRequests);
    public long RpcRequests => Interlocked.Read(ref rpcRequests);
    public long StorageBytes => Interlocked.Read(ref storageBytes);
    // A test's file storage trouble: an answer for a request to storage instead of storage's own
    // (a refusal), or null to let it through.
    public Func<HttpRequestMessage, HttpResponseMessage?>? StorageFault { get; set; }
    // A test's slow server call: extra time for a database call, by its path ("/rest/v1/rpc/...").
    public Func<string, TimeSpan>? RpcDelay { get; set; }
    // A test's slow file storage on this computer only: extra time for one storage request.
    public Func<HttpRequestMessage, TimeSpan>? StorageDelay { get; set; }
    // A test watching file storage: told when each storage request starts (true) and when it
    // ends, answered or not (false), so it can see when no transfer was running at all.
    public Action<HttpRequestMessage, bool>? StorageWatch { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (string.Equals(uri.Host, FakeNetworkHandler.S3Host, StringComparison.OrdinalIgnoreCase))
        {
            if (StorageFault?.Invoke(request) is { } refused) return refused;
            Interlocked.Increment(ref storageRequests);
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref storageGets);
            var watch = StorageWatch;
            watch?.Invoke(request, true);
            try
            {
                var sent = request.Content?.Headers.ContentLength ?? 0;
                await Task.Delay(Profile.StorageRoundTrip + Body(sent) + (StorageDelay?.Invoke(request) ?? TimeSpan.Zero), cancellationToken);
                var response = await base.SendAsync(request, cancellationToken);
                var received = request.Method == HttpMethod.Get && response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
                if (received > 0) await Task.Delay(Body(received), cancellationToken);
                Interlocked.Add(ref storageBytes, sent + received);
                return response;
            }
            finally { watch?.Invoke(request, false); }
        }
        if (uri.Port == sitePort)
        {
            Interlocked.Increment(ref siteRequests);
            await Task.Delay(Profile.SiteRoundTrip, cancellationToken);
        }
        else if (uri.Port == rpcPort)
        {
            Interlocked.Increment(ref rpcRequests);
            await Task.Delay(Profile.RpcRoundTrip + (RpcDelay?.Invoke(uri.AbsolutePath) ?? TimeSpan.Zero), cancellationToken);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    // Time for one body: the slower of its own connection and its place on the shared link.
    private TimeSpan Body(long bytes)
    {
        var profile = Profile;
        if (bytes <= 0 || profile.StorageBytesPerSecond <= 0) return TimeSpan.Zero;
        var own = bytes / profile.StorageBytesPerSecond;
        var shared = profile.LinkBytesPerSecond > 0 ? link.Reserve(bytes, profile.LinkBytesPerSecond) : 0;
        return TimeSpan.FromSeconds(Math.Max(own, shared));
    }
}
