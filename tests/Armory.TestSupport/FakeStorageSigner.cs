using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Armory.Storage.Tests;

namespace Armory.TestSupport;

/// <summary>
/// Presigned URLs for the fake S3, shaped like SigV4 query authentication. A
/// <see cref="FakeIdeaBosco"/> signs each blob URL for one method, one object key, its signed
/// headers (for a PUT, the exact <c>content-length</c>) and a 15 minute life on the site's
/// clock. A <see cref="FakeNetworkHandler"/> over the same <see cref="FakeS3"/> refuses any
/// request that does not carry a valid, unexpired signature, the way a private bucket does.
/// The pairing goes through the FakeS3 instance, so neither needs a reference to the other.
/// </summary>
internal sealed class FakeStorageSigner
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private static readonly ConditionalWeakTable<FakeS3, ConcurrentDictionary<string, FakeStorageSigner>> Registry = new();

    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly Func<DateTimeOffset> _now;

    private FakeStorageSigner(Func<DateTimeOffset> now) => _now = now;

    public string AccessKeyId { get; } = "FAKEARMORY" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    /// <summary>Makes <paramref name="s3"/> private: from now on only URLs this signer (or another attached one) signed reach it.</summary>
    public static FakeStorageSigner Attach(FakeS3 s3, Func<DateTimeOffset> now)
    {
        var signer = new FakeStorageSigner(now);
        Registry.GetValue(s3, static _ => new ConcurrentDictionary<string, FakeStorageSigner>(StringComparer.Ordinal))[signer.AccessKeyId] = signer;
        return signer;
    }

    public static bool IsPrivate(FakeS3 s3) => Registry.TryGetValue(s3, out var signers) && !signers.IsEmpty;

    /// <summary>A URL for <paramref name="method"/> on <paramref name="key"/>, valid from <paramref name="issuedAt"/> (whole seconds) for <paramref name="lifetime"/>.</summary>
    public Uri Sign(string key, string method, DateTimeOffset issuedAt, TimeSpan lifetime, IReadOnlyDictionary<string, string> headers, long? contentLength)
    {
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = FakeNetworkHandler.S3Host };
        foreach (var (name, value) in headers) signed[name.Trim().ToLowerInvariant()] = value.Trim();
        if (contentLength is { } length) signed["content-length"] = length.ToString(CultureInfo.InvariantCulture);

        var path = "/" + key;
        var date = issuedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var expires = ((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var names = string.Join(';', signed.Keys);
        var signature = Signature(method, path, date, expires, signed);
        var credential = $"{AccessKeyId}/{date[..8]}/auto/s3/aws4_request";
        return new Uri($"https://{FakeNetworkHandler.S3Host}{path}?X-Amz-Algorithm={Algorithm}&X-Amz-Credential={Uri.EscapeDataString(credential)}" +
            $"&X-Amz-Date={date}&X-Amz-Expires={expires}&X-Amz-SignedHeaders={Uri.EscapeDataString(names)}&X-Amz-Signature={signature}");
    }

    private string Signature(string method, string path, string date, string expires, IEnumerable<KeyValuePair<string, string>> signedHeaders)
    {
        var text = new StringBuilder().Append(method.ToUpperInvariant()).Append('\n').Append(path).Append('\n').Append(date).Append('\n').Append(expires);
        foreach (var (name, value) in signedHeaders) text.Append('\n').Append(name).Append(':').Append(value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Null when the request may reach the FakeS3; otherwise the S3 error answer.</summary>
    public static async Task<HttpResponseMessage?> CheckAsync(FakeS3 s3, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Registry.TryGetValue(s3, out var signers) || signers.IsEmpty) return null;
        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query);
        string? Param(string name) => query[name];
        if (Param("X-Amz-Algorithm") != Algorithm || Param("X-Amz-Credential") is not { } credential || Param("X-Amz-Date") is not { } date
            || Param("X-Amz-Expires") is not { } expires || Param("X-Amz-SignedHeaders") is not { } names || Param("X-Amz-Signature") is not { } signature)
            return S3Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied");
        if (!signers.TryGetValue(credential.Split('/')[0], out var signer))
            return S3Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", "The AWS Access Key Id you provided does not exist in our records.");
        if (request.Method == HttpMethod.Put && request.Content?.Headers.ContentLength is null)
            return S3Error(HttpStatusCode.LengthRequired, "MissingContentLength", "You must provide the Content-Length HTTP header.");

        var signed = new List<KeyValuePair<string, string>>();
        foreach (var name in names.Split(';'))
        {
            var value = name switch
            {
                "host" => uri.Authority.ToLowerInvariant(),
                "content-length" => request.Content?.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture),
                _ => HeaderValue(request, name),
            };
            signed.Add(new(name, value ?? ""));
        }
        var expected = signer.Signature(request.Method.Method, uri.AbsolutePath, date, expires, signed);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided.");

        if (!DateTimeOffset.TryParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issued)
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || signer._now() >= issued.AddSeconds(seconds))
            return S3Error(HttpStatusCode.Forbidden, "AccessDenied", "Request has expired");

        if (request.Method == HttpMethod.Put && request.Content is { } content)
        {
            // The body must be exactly the signed length, as a real HTTP stack enforces.
            await content.LoadIntoBufferAsync(cancellationToken);
            var actual = (await content.ReadAsByteArrayAsync(cancellationToken)).LongLength;
            if (actual != content.Headers.ContentLength)
                return S3Error(HttpStatusCode.BadRequest, "IncompleteBody", "You did not provide the number of bytes specified by the Content-Length HTTP header.");
        }
        return null;
    }

    private static string? HeaderValue(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values)) return string.Join(',', values.Select(v => v.Trim()));
        if (request.Content is not null && request.Content.Headers.TryGetValues(name, out var contentValues)) return string.Join(',', contentValues.Select(v => v.Trim()));
        return null;
    }

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code, string message) => new(status)
    {
        Content = new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{WebUtility.HtmlEncode(message)}</Message></Error>", Encoding.UTF8, "application/xml"),
    };
}
