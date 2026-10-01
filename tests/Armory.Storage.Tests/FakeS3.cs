using System.Collections.Concurrent;
using System.Net;
using System.Web;
using Armory.Storage;

namespace Armory.Storage.Tests;

// One fake S3 shared by Armory.Storage.Tests and Armory.TestSupport (which compiles this
// same file through a link). Counters use Interlocked so concurrent requests count exactly.
internal sealed class FakeUrls:IStorageUrlProvider
{
    private static PresignedRequest U(string key,string q)=>new(new Uri("https://fake/"+key+q));
    public ValueTask<PresignedRequest> HeadAsync(string k,CancellationToken c)=>ValueTask.FromResult(U(k,"?op=head"));
    public ValueTask<PresignedRequest> PutAsync(string k,CancellationToken c)=>ValueTask.FromResult(U(k,"?op=put"));
    public ValueTask<PresignedRequest> GetAsync(string k,CancellationToken c)=>ValueTask.FromResult(U(k,"?op=get"));
    public ValueTask<PresignedRequest> CreateMultipartAsync(string k,CancellationToken c)=>ValueTask.FromResult(U(k,"?uploads"));
    public ValueTask<PresignedRequest> UploadPartAsync(string k,string u,int n,CancellationToken c)=>ValueTask.FromResult(U(k,$"?uploadId={u}&partNumber={n}"));
    public ValueTask<PresignedRequest> CompleteMultipartAsync(string k,string u,CancellationToken c)=>ValueTask.FromResult(U(k,$"?uploadId={u}&complete=1"));
    public ValueTask<PresignedRequest> AbortMultipartAsync(string k,string u,CancellationToken c)=>ValueTask.FromResult(U(k,$"?uploadId={u}"));
}

// Each multipart upload has its own id and its own parts, so two agents uploading at once
// never mix parts, and a finished or aborted upload leaves nothing behind for the next one.
public sealed class FakeS3:HttpMessageHandler
{
    public ConcurrentDictionary<string,byte[]> Objects{get;}=new(); public bool CorruptGets,DropFirstPart,AlwaysDropParts; public int Heads,Puts,PartAttempts,Aborts;
    private readonly ConcurrentDictionary<string,(string Key,ConcurrentDictionary<int,byte[]> Parts)> uploads=new(StringComparer.Ordinal);
    private int nextUpload;
    /// <summary>Multipart uploads started and neither completed nor aborted.</summary>
    public int OpenUploads=>uploads.Count;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)
    {
        var key=r.RequestUri!.AbsolutePath.TrimStart('/'); var q=r.RequestUri.Query; var query=HttpUtility.ParseQueryString(q); var uploadId=query["uploadId"];
        if(r.Method==HttpMethod.Head){Interlocked.Increment(ref Heads);return new(Objects.ContainsKey(key)?HttpStatusCode.OK:HttpStatusCode.NotFound);}
        if(r.Method==HttpMethod.Get){if(!Objects.TryGetValue(key,out var b))return new(HttpStatusCode.NotFound);return new(HttpStatusCode.OK){Content=new ByteArrayContent(CorruptGets?[..b,0]:b)};}
        if(r.Method==HttpMethod.Post&&q=="?uploads"){var id="u"+Interlocked.Increment(ref nextUpload);uploads[id]=(key,new());return new(HttpStatusCode.OK){Content=new StringContent($"<InitiateMultipartUploadResult><UploadId>{id}</UploadId></InitiateMultipartUploadResult>")};}
        if(r.Method==HttpMethod.Put&&query["partNumber"] is {} number)
        {
            var attempt=Interlocked.Increment(ref PartAttempts);var n=int.Parse(number,System.Globalization.CultureInfo.InvariantCulture);
            if(AlwaysDropParts||(DropFirstPart&&attempt==1))throw new HttpRequestException("injected dropped connection");
            if(uploadId is null||!uploads.TryGetValue(uploadId,out var upload)||upload.Key!=key)return new(HttpStatusCode.NotFound);
            upload.Parts[n]=await r.Content!.ReadAsByteArrayAsync(c);var x=new HttpResponseMessage(HttpStatusCode.OK);x.Headers.ETag=new($"\"p{n}\"");return x;
        }
        if(r.Method==HttpMethod.Post&&query["complete"]=="1")
        {
            if(uploadId is null||!uploads.TryRemove(uploadId,out var upload)||upload.Key!=key)return new(HttpStatusCode.NotFound);
            Objects[key]=upload.Parts.OrderBy(x=>x.Key).SelectMany(x=>x.Value).ToArray();return new(HttpStatusCode.OK);
        }
        if(r.Method==HttpMethod.Delete){Interlocked.Increment(ref Aborts);if(uploadId is not null)uploads.TryRemove(uploadId,out _);return new(HttpStatusCode.NoContent);}
        if(r.Method==HttpMethod.Put){Interlocked.Increment(ref Puts);Objects[key]=await r.Content!.ReadAsByteArrayAsync(c);return new(HttpStatusCode.OK);}
        return new(HttpStatusCode.BadRequest);
    }
}
