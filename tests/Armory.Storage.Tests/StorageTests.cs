using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Armory.Storage;

namespace Armory.Storage.Tests;

public sealed class StorageTests
{
    [Fact] public void ObjectKeyUsesSpecifiedFanout()
    {
        var hash=new string('a',64);
        Assert.Equal($"blobs/sha256/aa/aa/{hash}",ContentObjectKey.FromHash(hash));
    }
    [Fact] public async Task HeadSkipsBytesAlreadyStored()
    {
        var s=new FakeS3(); var bytes=Encoding.UTF8.GetBytes("saved"); s.Objects[ContentObjectKey.FromBytes(bytes)]=bytes;
        var client=Client(s,out _); await client.UploadAsync(bytes); Assert.Equal(1,s.Heads); Assert.Equal(0,s.Puts);
    }
    [Fact] public async Task PutThenGetRoundTripsAndVerifiesHash()
    {
        var s=new FakeS3(); var client=Client(s,out _); var bytes=Encoding.UTF8.GetBytes("armory"); var key=await client.UploadAsync(bytes);
        Assert.Equal(bytes,await client.DownloadAsync(key));
    }
    [Fact] public async Task CorruptedGetIsRejectedBeforeBytesAreReturned()
    {
        var s=new FakeS3(); var client=Client(s,out _); var bytes=Encoding.UTF8.GetBytes("good"); var key=await client.UploadAsync(bytes); s.CorruptGets=true;
        await Assert.ThrowsAsync<HashMismatchException>(()=>client.DownloadAsync(key));
    }
    [Fact] public async Task MultipartRetriesDroppedPartAndCompletes()
    {
        var s=new FakeS3{DropFirstPart=true}; var client=Client(s,out _,threshold:5*1024*1024); var bytes=RandomNumberGenerator.GetBytes(6*1024*1024);
        var key=await client.UploadAsync(bytes); Assert.True(s.PartAttempts>=3); Assert.Equal(bytes,s.Objects[key]); Assert.Equal(0,s.Aborts);
    }
    [Fact] public async Task ExhaustedMultipartAborts()
    {
        var s=new FakeS3{AlwaysDropParts=true}; var client=Client(s,out _,threshold:5*1024*1024);
        await Assert.ThrowsAsync<HttpRequestException>(()=>client.UploadAsync(RandomNumberGenerator.GetBytes(6*1024*1024))); Assert.Equal(1,s.Aborts);
    }
    [Fact] public void AwsPublishedPresignExampleHasExpectedSignature()
    {
        var signer=new SigV4Presigner("AKIAIOSFODNN7EXAMPLE","wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY","us-east-1");
        var url=signer.Presign(new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),HttpMethod.Get,new DateTimeOffset(2013,5,24,0,0,0,TimeSpan.Zero),TimeSpan.FromDays(1));
        Assert.Equal("aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",url.Query.Split("X-Amz-Signature=")[1]); // AWS S3 SigV4 query-auth example
    }
    private static S3StorageClient Client(FakeS3 s,out HttpClient http,long threshold=64L*1024*1024){http=new HttpClient(s){BaseAddress=new Uri("https://fake/")};return new(http,new FakeUrls(),threshold);}
}
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
internal sealed class FakeS3:HttpMessageHandler
{
    public ConcurrentDictionary<string,byte[]> Objects{get;}=new(); public bool CorruptGets,DropFirstPart,AlwaysDropParts; public int Heads,Puts,PartAttempts,Aborts;
    private readonly ConcurrentDictionary<int,byte[]> parts=new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)
    {
        var key=r.RequestUri!.AbsolutePath.TrimStart('/'); var q=r.RequestUri.Query;
        if(r.Method==HttpMethod.Head){Heads++;return new(Objects.ContainsKey(key)?HttpStatusCode.OK:HttpStatusCode.NotFound);}
        if(r.Method==HttpMethod.Get){if(!Objects.TryGetValue(key,out var b))return new(HttpStatusCode.NotFound);return new(HttpStatusCode.OK){Content=new ByteArrayContent(CorruptGets?[..b,0]:b)};}
        if(r.Method==HttpMethod.Post&&q=="?uploads")return new(HttpStatusCode.OK){Content=new StringContent("<InitiateMultipartUploadResult><UploadId>u1</UploadId></InitiateMultipartUploadResult>")};
        if(r.Method==HttpMethod.Put&&q.Contains("partNumber=")){PartAttempts++;var n=int.Parse(q.Split("partNumber=")[1]);if(AlwaysDropParts||(DropFirstPart&&PartAttempts==1))throw new HttpRequestException("injected dropped connection");parts[n]=await r.Content!.ReadAsByteArrayAsync(c);var x=new HttpResponseMessage(HttpStatusCode.OK);x.Headers.ETag=new($"\"p{n}\"");return x;}
        if(r.Method==HttpMethod.Post&&q.Contains("complete=1")){Objects[key]=parts.OrderBy(x=>x.Key).SelectMany(x=>x.Value).ToArray();return new(HttpStatusCode.OK);}
        if(r.Method==HttpMethod.Delete){Aborts++;return new(HttpStatusCode.NoContent);}
        if(r.Method==HttpMethod.Put){Puts++;Objects[key]=await r.Content!.ReadAsByteArrayAsync(c);return new(HttpStatusCode.OK);}
        return new(HttpStatusCode.BadRequest);
    }
}
