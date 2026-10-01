using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Armory.Storage;

namespace Armory.Client;

public sealed record BlobUrl(Uri Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt, bool Exists);
public sealed class BlobRefusedException(int status, string message) : ArmoryClientException(message)
{
    public int Status { get; } = status;
}

// Contract section 2: ideabosco.com mints a 15-minute URL for one content-addressed object;
// the bytes go straight between this computer and storage.
public sealed class BlobClient(HttpClient siteHttp, HttpClient storageHttp, Uri site, SessionManager sessions)
{
    public const long MaximumPutBytes = 2L * 1024 * 1024 * 1024;

    public async Task<BlobUrl> GetUrlAsync(Guid project, string hash, long bytes, HttpMethod method, CancellationToken ct = default)
    {
        _ = ContentObjectKey.FromHash(hash);
        if (method != HttpMethod.Put && method != HttpMethod.Get) throw new ArgumentException("Only PUT and GET URLs exist.", nameof(method));
        var session = await sessions.GetFreshAsync(cancellationToken: ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(site, "/api/armory/blob-url"))
            {
                Content = JsonContent.Create(new Dictionary<string, object> { ["projectId"] = project, ["hash"] = hash.ToLowerInvariant(), ["bytes"] = bytes, ["method"] = method.Method }),
            };
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            HttpResponseMessage response;
            try { response = await siteHttp.SendAsync(request, ct); }
            catch (HttpRequestException error) { throw new ArmoryOfflineException("ideabosco.com could not be reached.", error); }
            catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new ArmoryOfflineException("ideabosco.com took too long to answer.", error); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    session = await sessions.GetFreshAsync(forceRefresh: true, ct);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests)
                    throw new ArmoryOfflineException($"Armory file storage is not available right now ({(int)response.StatusCode}).");
                if (!response.IsSuccessStatusCode)
                    throw new BlobRefusedException((int)response.StatusCode, $"ideabosco.com refused the file transfer ({(int)response.StatusCode}).");
                Answer? answer;
                try { answer = await response.Content.ReadFromJsonAsync<Answer>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); }
                catch (JsonException error) { throw new ArmoryOfflineException("ideabosco.com sent an unreadable answer.", error); }
                if (answer?.Url is null) throw new ArmoryOfflineException("ideabosco.com sent an answer without a URL.");
                return new(new Uri(answer.Url), answer.Headers ?? new Dictionary<string, string>(),
                    DateTimeOffset.Parse(answer.ExpiresAt ?? DateTimeOffset.UtcNow.ToString("O"), CultureInfo.InvariantCulture), answer.Exists);
            }
        }
    }

    // Returns false when storage already held the object, so nothing was sent.
    public async Task<bool> UploadAsync(Guid project, string hash, long bytes, Func<Stream> open, CancellationToken ct = default)
    {
        if (bytes > MaximumPutBytes) throw new BlobRefusedException(400, "Files larger than 2 GiB cannot be stored in Armory yet.");
        var url = await GetUrlAsync(project, hash, bytes, HttpMethod.Put, ct);
        if (url.Exists) return false;
        await using var source = open();
        using var request = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new StreamContent(source) };
        request.Content.Headers.ContentLength = bytes;
        foreach (var header in url.Headers)
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await SendStorageAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ArmoryOfflineException($"File storage refused the upload ({(int)response.StatusCode}).");
        return true;
    }

    // Writes verified bytes to destination. Mismatched bytes throw HashMismatchException
    // after writing; the caller owns the destination and must discard it.
    public async Task DownloadAsync(Guid project, string hash, long bytes, Stream destination, CancellationToken ct = default)
    {
        var url = await GetUrlAsync(project, hash, bytes, HttpMethod.Get, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url.Url);
        foreach (var header in url.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await SendStorageAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new ArmoryOfflineException($"File storage refused the download ({(int)response.StatusCode}).");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (HttpRequestException error) { throw new ArmoryOfflineException("The download was cut off.", error); }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(hash.ToLowerInvariant())))
            throw new HashMismatchException("Downloaded bytes do not match their content-addressed key.");
    }

    private async Task<HttpResponseMessage> SendStorageAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        try { return await storageHttp.SendAsync(request, option, ct); }
        catch (HttpRequestException error) { throw new ArmoryOfflineException("File storage could not be reached.", error); }
        catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new ArmoryOfflineException("File storage took too long to answer.", error); }
    }

    private sealed record Answer(string? Url, Dictionary<string, string>? Headers, string? ExpiresAt, bool Exists);
}
