using Armory.Storage.Tests;

namespace Armory.TestSupport;

/// <summary>
/// The agent's network in tests: requests to <see cref="S3Host"/> go to a <see cref="FakeS3"/>,
/// and everything else (the loopback fakes) goes to a real <see cref="SocketsHttpHandler"/>
/// with no proxy. Build the agent's HttpClient on it.
/// <para>
/// Once a <see cref="FakeIdeaBosco"/> is built over the same FakeS3, the bucket is private:
/// only a URL that site signed reaches the FakeS3, and only with its signed method, object,
/// headers and (for a PUT) exact length, before it expires 15 minutes after issue on the
/// site's clock. Anything else gets S3's own 403 (or 411 for a PUT without a length).
/// </para>
/// </summary>
public sealed class FakeNetworkHandler : DelegatingHandler
{
    public const string S3Host = "fake-s3.armory.test";

    private readonly HttpMessageInvoker _s3;

    public FakeNetworkHandler(FakeS3 s3) : this(s3, new SocketsHttpHandler { UseProxy = false }) { }

    public FakeNetworkHandler(FakeS3 s3, HttpMessageHandler inner) : base(inner)
    {
        S3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _s3 = new HttpMessageInvoker(s3, disposeHandler: false);
    }

    public FakeS3 S3 { get; }

    private static bool IsS3(HttpRequestMessage request) =>
        string.Equals(request.RequestUri?.Host, S3Host, StringComparison.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        IsS3(request) ? SendToS3Async(request, cancellationToken) : base.SendAsync(request, cancellationToken);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        IsS3(request) ? SendToS3Async(request, cancellationToken).GetAwaiter().GetResult() : base.Send(request, cancellationToken);

    private async Task<HttpResponseMessage> SendToS3Async(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var refusal = await FakeStorageSigner.CheckAsync(S3, request, cancellationToken);
        var response = refusal ?? await _s3.SendAsync(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _s3.Dispose();
        base.Dispose(disposing);
    }
}
