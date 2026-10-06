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

// Sits between the agent's HttpClient and FakeNetworkHandler. Requests are classified by where
// they go: the fake S3 host is storage, the fake site's port is ideabosco.com, the fake
// Supabase port is the database. A storage body pays its bytes over the per-connection rate
// and reserves its share of the link.
internal sealed class LatencyHandler(LatencyProfile profile, int sitePort, int rpcPort, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly object gate = new();
    private long linkFree;
    private long storageRequests, siteRequests, rpcRequests, storageBytes;

    public long StorageRequests => Interlocked.Read(ref storageRequests);
    public long SiteRequests => Interlocked.Read(ref siteRequests);
    public long RpcRequests => Interlocked.Read(ref rpcRequests);
    public long StorageBytes => Interlocked.Read(ref storageBytes);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (string.Equals(uri.Host, FakeNetworkHandler.S3Host, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref storageRequests);
            var sent = request.Content?.Headers.ContentLength ?? 0;
            await Task.Delay(profile.StorageRoundTrip + Body(sent), cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            var received = request.Method == HttpMethod.Get && response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
            if (received > 0) await Task.Delay(Body(received), cancellationToken);
            Interlocked.Add(ref storageBytes, sent + received);
            return response;
        }
        if (uri.Port == sitePort)
        {
            Interlocked.Increment(ref siteRequests);
            await Task.Delay(profile.SiteRoundTrip, cancellationToken);
        }
        else if (uri.Port == rpcPort)
        {
            Interlocked.Increment(ref rpcRequests);
            await Task.Delay(profile.RpcRoundTrip, cancellationToken);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    // Time for one body: the slower of its own connection and its place on the shared link.
    private TimeSpan Body(long bytes)
    {
        if (bytes <= 0 || profile.StorageBytesPerSecond <= 0) return TimeSpan.Zero;
        var own = bytes / profile.StorageBytesPerSecond;
        double shared = 0;
        if (profile.LinkBytesPerSecond > 0)
        {
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                var start = Math.Max(now, linkFree);
                linkFree = start + (long)(bytes / profile.LinkBytesPerSecond * Stopwatch.Frequency);
                shared = (double)(linkFree - now) / Stopwatch.Frequency;
            }
        }
        return TimeSpan.FromSeconds(Math.Max(own, shared));
    }
}
