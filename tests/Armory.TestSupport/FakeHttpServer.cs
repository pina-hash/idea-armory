using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Armory.TestSupport;

public enum FakeFaultKind
{
    None,
    /// <summary>The connection is aborted before the request is handled, so nothing happens server side.</summary>
    DropBeforeHandling,
    /// <summary>The request is handled (its database transaction commits), then the connection is aborted, so the answer is lost.</summary>
    DropAfterHandling,
    /// <summary>The request is not handled; the fake answers <see cref="FakeFault.StatusCode"/> instead.</summary>
    Status,
}

/// <summary>A failure a fake injects into one request.</summary>
public readonly record struct FakeFault(FakeFaultKind Kind, int StatusCode = 0)
{
    public static FakeFault None => default;
    public static FakeFault DropBeforeHandling => new(FakeFaultKind.DropBeforeHandling);
    public static FakeFault DropAfterHandling => new(FakeFaultKind.DropAfterHandling);
    /// <summary>Answers <paramref name="statusCode"/> with <c>{"message":"injected failure"}</c> without handling the request.</summary>
    public static FakeFault Respond(int statusCode) => new(FakeFaultKind.Status, statusCode);
}

/// <summary>What a fault hook sees: <see cref="CallNumber"/> is the 1-based count of requests to this path so far, this one included.</summary>
public sealed record FakeRequestInfo(string Method, string Path, string QueryString, int CallNumber);

/// <summary>
/// An in-process Kestrel server on <c>http://127.0.0.1:&lt;free port&gt;</c> with the test
/// switches every fake shares: per-path request counters, an <see cref="Offline"/> switch, a
/// settable <see cref="Clock"/>, and fault injection.
/// </summary>
public abstract class FakeHttpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Path, int CallNumber), FakeFault> _scheduled = new();
    private WebApplication? _app;
    private Uri? _baseUri;
    private volatile bool _offline;
    private TimeProvider _clock = TimeProvider.System;
    private Func<FakeRequestInfo, FakeFault>? _faultInjector;

    /// <summary>The real address after <see cref="StartAsync"/>, for example <c>http://127.0.0.1:41234/</c>.</summary>
    public Uri BaseUri => _baseUri ?? throw new InvalidOperationException("Call StartAsync first.");

    /// <summary>When true, every request is aborted at the connection level, so a client sees an HttpRequestException.</summary>
    public bool Offline { get => _offline; set => _offline = value; }

    /// <summary>The clock for every expiry this fake decides. Assign a <see cref="TestClock"/> to move time.</summary>
    public TimeProvider Clock
    {
        get => Volatile.Read(ref _clock);
        set => Volatile.Write(ref _clock, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Optional per-request hook. Its answer wins over faults added with <see cref="ScheduleFault"/>.</summary>
    public Func<FakeRequestInfo, FakeFault>? FaultInjector
    {
        get => Volatile.Read(ref _faultInjector);
        set => Volatile.Write(ref _faultInjector, value);
    }

    /// <summary>Requests received for <paramref name="path"/> (for example <c>/auth/v1/token</c>), including aborted ones.</summary>
    public int RequestCount(string path) => _counts.TryGetValue(path, out var count) ? count : 0;

    /// <summary>A snapshot of every per-path counter.</summary>
    public IReadOnlyDictionary<string, int> RequestCounts => new Dictionary<string, int>(_counts, StringComparer.Ordinal);

    /// <summary>Injects <paramref name="fault"/> into the <paramref name="callNumber"/>th request (1-based) to <paramref name="path"/>.</summary>
    public void ScheduleFault(string path, int callNumber, FakeFault fault)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(callNumber, 1);
        _scheduled[(path, callNumber)] = fault;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) throw new InvalidOperationException("This fake is already started.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // No file watchers: many fakes start in one test run.
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
            ApplicationName = typeof(FakeHttpServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(DispatchAsync);
        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report its address.");
        _baseUri = new Uri(address.EndsWith('/') ? address : address + "/");
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { }
        }
        await app.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Handles one request and describes the answer; the base class decides whether it is sent.</summary>
    private protected abstract Task<FakeResponse> HandleAsync(HttpContext context);

    /// <summary>A websocket upgrade the fake serves itself, for as long as the socket lives. False: not one of its sockets.</summary>
    private protected virtual Task<bool> HandleWebSocketAsync(HttpContext context) => Task.FromResult(false);

    private async Task DispatchAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var call = _counts.AddOrUpdate(path, 1, static (_, count) => count + 1);
        if (Offline) { context.Abort(); return; }

        var fault = FaultInjector?.Invoke(new FakeRequestInfo(context.Request.Method, path, context.Request.QueryString.Value ?? "", call))
            ?? FakeFault.None;
        if (_scheduled.TryRemove((path, call), out var scheduled) && fault.Kind == FakeFaultKind.None) fault = scheduled;
        switch (fault.Kind)
        {
            case FakeFaultKind.DropBeforeHandling:
                context.Abort();
                return;
            case FakeFaultKind.Status:
                await FakeResponse.Json(fault.StatusCode, new JsonObject { ["message"] = "injected failure" }).WriteAsync(context);
                return;
        }

        if (context.WebSockets.IsWebSocketRequest && await HandleWebSocketAsync(context)) return;

        FakeResponse response;
        try
        {
            response = await HandleAsync(context);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            response = FakeResponse.Json(500, new JsonObject { ["message"] = "fake server error: " + e.Message });
        }

        if (fault.Kind == FakeFaultKind.DropAfterHandling || Offline) { context.Abort(); return; }
        await response.WriteAsync(context);
    }

    // Shared request helpers.

    /// <summary>The request body as JSON: null when the body is empty, invalid JSON, or has a repeated property name.</summary>
    private protected static async Task<(bool Empty, JsonElement? Json)> ReadJsonAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return (true, null);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (HasDuplicateProperty(document.RootElement)) return (false, null);
            return (false, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private static bool HasDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) return true;
        }
        return false;
    }

    /// <summary>The bearer token: null without an Authorization header, empty when the header is not a bearer token.</summary>
    private protected static string? BearerToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values)) return null;
        var value = values.ToString().Trim();
        const string scheme = "Bearer ";
        return value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? value[scheme.Length..].Trim() : "";
    }

    private protected static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private protected static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private protected static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>An answer a fake has decided on but not yet sent.</summary>
internal sealed class FakeResponse
{
    private FakeResponse(int status, string? body, string? contentType)
    {
        Status = status;
        Body = body;
        ContentType = contentType;
    }

    public int Status { get; }
    public string? Body { get; }
    public string? ContentType { get; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static FakeResponse Json(int status, JsonNode? body) => new(status, body?.ToJsonString() ?? "null", "application/json; charset=utf-8");
    public static FakeResponse RawJson(int status, string json) => new(status, json, "application/json; charset=utf-8");
    public static FakeResponse Html(int status, string html) => new(status, html, "text/html; charset=utf-8");
    public static FakeResponse Empty(int status) => new(status, null, null);

    public static FakeResponse Redirect(string location)
    {
        var response = new FakeResponse(303, null, null);
        response.Headers["Location"] = location;
        return response;
    }

    public async Task WriteAsync(HttpContext context)
    {
        context.Response.StatusCode = Status;
        foreach (var (name, value) in Headers) context.Response.Headers[name] = value;
        if (Body is null) return;
        context.Response.ContentType = ContentType;
        await context.Response.WriteAsync(Body, Encoding.UTF8);
    }
}
