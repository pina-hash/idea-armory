using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Armory.TestSupport;

/// <summary>
/// What the browser saw. When the site redirected, <see cref="Status"/> and <see cref="Body"/>
/// are the agent's loopback callback answer and <see cref="CallbackUri"/> is where the site
/// sent the browser. Otherwise they are the failing page or start answer, and
/// <see cref="CallbackUri"/> is null.
/// </summary>
public sealed record FakeBrowserResult(HttpStatusCode Status, string Body, HttpStatusCode StartStatus, Uri? CallbackUri);

/// <summary>
/// A student's browser for the connect flow (CONTRACT.md section 3b and 3c). It is signed in to
/// the fake site through the test-only <c>X-Test-Site-User</c> header.
/// </summary>
public sealed class FakeBrowser : IDisposable
{
    private readonly HttpClient _site;

    public FakeBrowser() : this(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { }

    /// <param name="handler">Must not follow redirects; the browser follows the 303 itself.</param>
    public FakeBrowser(HttpMessageHandler handler) => _site = new HttpClient(handler);

    /// <summary>
    /// Opens <paramref name="connectUrl"/> signed in as <paramref name="email"/>, confirms (POSTs
    /// /api/armory/connect/start with the page's port, state, challenge and device), and then
    /// follows the 303 to the agent's loopback callback with a plain HttpClient.
    /// </summary>
    public async Task<FakeBrowserResult> SignInAndApproveAsync(Uri connectUrl, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        using (var page = new HttpRequestMessage(HttpMethod.Get, connectUrl))
        {
            page.Headers.Add(FakeIdeaBosco.SiteUserHeader, email);
            using var pageResponse = await _site.SendAsync(page, cancellationToken);
            if (pageResponse.StatusCode != HttpStatusCode.OK)
            {
                var pageBody = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
                return new FakeBrowserResult(pageResponse.StatusCode, pageBody, pageResponse.StatusCode, null);
            }
        }

        var query = QueryHelpers.ParseQuery(connectUrl.Query);
        var body = new JsonObject();
        if (query.TryGetValue("port", out var port))
            body["port"] = int.TryParse(port.ToString(), out var number) ? number : port.ToString();
        foreach (var name in new[] { "state", "challenge", "device" })
        {
            if (query.TryGetValue(name, out var value)) body[name] = value.ToString();
        }

        using var start = new HttpRequestMessage(HttpMethod.Post, new Uri(connectUrl, FakeIdeaBosco.ConnectStartPath))
        {
            Content = JsonContent.Create(body),
        };
        start.Headers.Add(FakeIdeaBosco.SiteUserHeader, email);
        using var startResponse = await _site.SendAsync(start, cancellationToken);
        if (startResponse.StatusCode != HttpStatusCode.SeeOther || startResponse.Headers.Location is null)
        {
            var startBody = await startResponse.Content.ReadAsStringAsync(cancellationToken);
            return new FakeBrowserResult(startResponse.StatusCode, startBody, startResponse.StatusCode, null);
        }

        var callback = startResponse.Headers.Location.IsAbsoluteUri
            ? startResponse.Headers.Location
            : new Uri(connectUrl, startResponse.Headers.Location);
        using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        using var callbackResponse = await plain.GetAsync(callback, cancellationToken);
        var callbackBody = await callbackResponse.Content.ReadAsStringAsync(cancellationToken);
        return new FakeBrowserResult(callbackResponse.StatusCode, callbackBody, startResponse.StatusCode, callback);
    }

    public void Dispose() => _site.Dispose();
}
