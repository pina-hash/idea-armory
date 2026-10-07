using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Armory.Storage;
using Armory.Telemetry;

namespace Armory.Client;

public sealed record BlobUrl(Uri Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt, bool Exists);
public sealed class BlobRefusedException(int status, string message) : ArmoryClientException(message)
{
    public int Status { get; } = status;
}
// One file's transfer did not go through this time (file storage refused it, it took too long,
// or the bytes were cut off), while the connection itself works. Only that file waits for the
// next try; a real connection failure is ArmoryOfflineException.
public sealed class StorageTransferException(string message, Exception? inner = null) : ArmoryClientException(message, inner);

// Contract section 2: ideabosco.com mints a 15-minute URL for one content-addressed object;
// the bytes go straight between this computer and storage. Each URL request and each transfer
// goes into the flight recorder (its size, how long, how it ended; never the URL, which is a
// credential while it lasts).
public sealed class BlobClient(HttpClient siteHttp, HttpClient storageHttp, Uri site, SessionManager sessions, FlightRecorder? recorder = null)
{
    public const long MaximumPutBytes = 2L * 1024 * 1024 * 1024;
    public const string UrlCall = "blob-url";
    private int active;

    // Uploads and downloads under way right now (the incident uploader waits for none).
    public int ActiveTransfers => Volatile.Read(ref active);

    public async Task<BlobUrl> GetUrlAsync(Guid project, string hash, long bytes, HttpMethod method, CancellationToken ct = default)
    {
        if (recorder is null) return await GetUrlUnrecordedAsync(project, hash, bytes, method, ct);
        var started = recorder.Now();
        try
        {
            var url = await GetUrlUnrecordedAsync(project, hash, bytes, method, ct);
            recorder.Rpc(UrlCall, recorder.MillisecondsSince(started), 200, null);
            return url;
        }
        catch (Exception error)
        {
            recorder.Rpc(UrlCall, recorder.MillisecondsSince(started), (error as BlobRefusedException)?.Status ?? 0, Outcome(error));
            throw;
        }
    }

    private static string Outcome(Exception error) => error switch
    {
        ArmoryOfflineException => "offline",
        ArmorySignedOutException => "signedOut",
        OperationCanceledException => "canceled",
        _ => error.GetType().Name,
    };

    // One transfer, counted while it runs and recorded when it ends.
    private async Task<T> TransferAsync<T>(string direction, long bytes, Func<Task<T>> transfer, Func<T, bool> sent)
    {
        Interlocked.Increment(ref active);
        var started = recorder?.Now() ?? 0;
        try
        {
            var result = await transfer();
            if (recorder is not null && sent(result)) recorder.Transfer(direction, bytes, recorder.MillisecondsSince(started), true, 200, null);
            return result;
        }
        catch (Exception error)
        {
            recorder?.Transfer(direction, bytes, recorder.MillisecondsSince(started), false, (error as BlobRefusedException)?.Status ?? 0, Outcome(error));
            throw;
        }
        finally { Interlocked.Decrement(ref active); }
    }

    private async Task<BlobUrl> GetUrlUnrecordedAsync(Guid project, string hash, long bytes, HttpMethod method, CancellationToken ct)
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
            catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new StorageTransferException("ideabosco.com took too long to answer.", error); }
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

    // Returns false when storage already held the object, so nothing was sent (and nothing is
    // reported). progress receives the bytes sent so far: 0 when sending starts, then the running
    // total, and 0 again if the request body is sent again from the start.
    public Task<bool> UploadAsync(Guid project, string hash, long bytes, Func<Stream> open, CancellationToken ct = default, IProgress<long>? progress = null)
        => TransferAsync("upload", bytes, () => UploadUnrecordedAsync(project, hash, bytes, open, ct, progress), sent => sent);

    private async Task<bool> UploadUnrecordedAsync(Guid project, string hash, long bytes, Func<Stream> open, CancellationToken ct, IProgress<long>? progress)
    {
        if (bytes > MaximumPutBytes) throw new BlobRefusedException(400, "Files larger than 2 GiB cannot be stored in Armory yet.");
        var url = await GetUrlAsync(project, hash, bytes, HttpMethod.Put, ct);
        if (url.Exists) return false;
        await using var source = open();
        progress?.Report(0);
        using var request = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new StreamContent(progress is null ? source : new ProgressStream(source, progress)) };
        request.Content.Headers.ContentLength = bytes;
        foreach (var header in url.Headers)
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await SendStorageAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new StorageTransferException($"File storage refused the upload ({(int)response.StatusCode}).");
        return true;
    }

    // Writes verified bytes to destination. Mismatched bytes throw HashMismatchException
    // after writing; the caller owns the destination and must discard it. progress receives the
    // bytes received so far: 0 when the body starts, then the running total. Every call starts
    // again from 0, so a retried download reports from 0.
    public Task DownloadAsync(Guid project, string hash, long bytes, Stream destination, CancellationToken ct = default, IProgress<long>? progress = null)
        => TransferAsync("download", bytes, async () => { await DownloadUnrecordedAsync(project, hash, bytes, destination, ct, progress); return true; }, _ => true);

    private async Task DownloadUnrecordedAsync(Guid project, string hash, long bytes, Stream destination, CancellationToken ct, IProgress<long>? progress)
    {
        var url = await GetUrlAsync(project, hash, bytes, HttpMethod.Get, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url.Url);
        foreach (var header in url.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await SendStorageAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new StorageTransferException($"File storage refused the download ({(int)response.StatusCode}).");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var received = await response.Content.ReadAsStreamAsync(ct);
            progress?.Report(0);
            await using var body = progress is null ? received : new ProgressStream(received, progress);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (HttpRequestException error) { throw new StorageTransferException("The download was cut off.", error); }
        catch (HttpIOException error) { throw new StorageTransferException("The download was cut off.", error); }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(hash.ToLowerInvariant())))
            throw new HashMismatchException("Downloaded bytes do not match their content-addressed key.");
    }

    private async Task<HttpResponseMessage> SendStorageAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        try { return await storageHttp.SendAsync(request, option, ct); }
        catch (HttpRequestException error) { throw new ArmoryOfflineException("File storage could not be reached.", error); }
        catch (TaskCanceledException error) when (!ct.IsCancellationRequested) { throw new StorageTransferException("File storage took too long to answer.", error); }
    }

    private sealed record Answer(string? Url, Dictionary<string, string>? Headers, string? ExpiresAt, bool Exists);
}
