using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Armory.Storage;

public sealed record PresignedRequest(Uri Url, IReadOnlyDictionary<string,string>? Headers = null);
public interface IStorageUrlProvider
{
    ValueTask<PresignedRequest> HeadAsync(string key, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> PutAsync(string key, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> GetAsync(string key, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> CreateMultipartAsync(string key, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> UploadPartAsync(string key, string uploadId, int partNumber, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> CompleteMultipartAsync(string key, string uploadId, CancellationToken cancellationToken);
    ValueTask<PresignedRequest> AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken);
}
public sealed class HashMismatchException(string message) : IOException(message);
public sealed class S3StorageClient(HttpClient http, IStorageUrlProvider urls, long multipartThreshold = 64L*1024*1024, int maxAttempts = 4)
{
    public async Task<string> UploadAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var key=ContentObjectKey.FromBytes(bytes.Span);
        using(var head=await SendAsync(HttpMethod.Head,await urls.HeadAsync(key,cancellationToken),null,cancellationToken))
            if(head.StatusCode==HttpStatusCode.OK) return key;
        if(bytes.Length<=multipartThreshold)
        {
            using var response=await SendAsync(HttpMethod.Put,await urls.PutAsync(key,cancellationToken),bytes,cancellationToken);
            response.EnsureSuccessStatusCode(); return key;
        }
        await UploadMultipartAsync(key,bytes,cancellationToken); return key;
    }
    private async Task UploadMultipartAsync(string key,ReadOnlyMemory<byte> bytes,CancellationToken ct)
    {
        using var created=await SendAsync(HttpMethod.Post,await urls.CreateMultipartAsync(key,ct),null,ct);
        created.EnsureSuccessStatusCode();
        var xml=XDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        var uploadId=xml.Descendants().First(x=>x.Name.LocalName=="UploadId").Value;
        var parts=new List<(int Number,string ETag)>();
        try
        {
            var size=(int)Math.Min(Math.Max(5L*1024*1024,multipartThreshold),int.MaxValue);
            for(int offset=0,number=1;offset<bytes.Length;offset+=size,number++)
            {
                var part=bytes.Slice(offset,Math.Min(size,bytes.Length-offset)); HttpResponseMessage? response=null;
                for(var attempt=1;attempt<=maxAttempts;attempt++) try
                {
                    response=await SendAsync(HttpMethod.Put,await urls.UploadPartAsync(key,uploadId,number,ct),part,ct);
                    response.EnsureSuccessStatusCode(); break;
                }
                catch(HttpRequestException) when(attempt<maxAttempts) { response?.Dispose(); }
                if(response is null || !response.IsSuccessStatusCode) throw new HttpRequestException($"Part {number} failed after {maxAttempts} attempts.");
                using(response) parts.Add((number,response.Headers.ETag?.Tag ?? throw new InvalidDataException("Part response omitted ETag.")));
            }
            var body=Encoding.UTF8.GetBytes("<CompleteMultipartUpload>"+string.Concat(parts.Select(p=>$"<Part><PartNumber>{p.Number}</PartNumber><ETag>{p.ETag}</ETag></Part>"))+"</CompleteMultipartUpload>");
            using var complete=await SendAsync(HttpMethod.Post,await urls.CompleteMultipartAsync(key,uploadId,ct),body,ct); complete.EnsureSuccessStatusCode();
        }
        catch
        {
            try { using var abort=await SendAsync(HttpMethod.Delete,await urls.AbortMultipartAsync(key,uploadId,ct),null,ct); } catch(HttpRequestException) { }
            throw;
        }
    }
    public async Task<byte[]> DownloadAsync(string key,CancellationToken cancellationToken=default)
    {
        using var response=await SendAsync(HttpMethod.Get,await urls.GetAsync(key,cancellationToken),null,cancellationToken); response.EnsureSuccessStatusCode();
        var bytes=await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var actual=Convert.ToHexStringLower(SHA256.HashData(bytes)); var expected=ContentObjectKey.HashFromKey(key);
        if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual),Encoding.ASCII.GetBytes(expected))) throw new HashMismatchException("Downloaded bytes do not match their content-addressed key.");
        return bytes;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method,PresignedRequest request,ReadOnlyMemory<byte>? body,CancellationToken ct)
    {
        using var message=new HttpRequestMessage(method,request.Url);
        if(body is { } b) message.Content=new ByteArrayContent(b.ToArray());
        if(request.Headers is not null) foreach(var pair in request.Headers) if(!message.Headers.TryAddWithoutValidation(pair.Key,pair.Value)) message.Content?.Headers.TryAddWithoutValidation(pair.Key,pair.Value);
        return await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,ct);
    }
}
