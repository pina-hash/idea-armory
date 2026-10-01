using Armory.Storage.Tests;

namespace Armory.TestSupport;

/// <summary>
/// The agent's network in tests: requests to <see cref="S3Host"/> go to a <see cref="FakeS3"/>,
/// and everything else (the loopback fakes) goes to a real <see cref="SocketsHttpHandler"/>
/// with no proxy. Build the agent's HttpClient on it.
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
        IsS3(request) ? _s3.SendAsync(request, cancellationToken) : base.SendAsync(request, cancellationToken);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        IsS3(request) ? _s3.Send(request, cancellationToken) : base.Send(request, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _s3.Dispose();
        base.Dispose(disposing);
    }
}
