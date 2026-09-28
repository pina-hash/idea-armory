using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Armory.Storage;

public sealed class SigV4Presigner(string accessKey,string secretKey,string region,string service="s3")
{
    public Uri Presign(Uri uri,HttpMethod method,DateTimeOffset now,TimeSpan lifetime,IReadOnlyDictionary<string,string>? signedHeaders=null)
    {
        if(lifetime<=TimeSpan.Zero || lifetime>TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var headers=new SortedDictionary<string,string>(StringComparer.Ordinal){{"host",uri.IsDefaultPort?uri.Host:$"{uri.Host}:{uri.Port}"}};
        if(signedHeaders is not null) foreach(var h in signedHeaders) headers[h.Key.ToLowerInvariant()]=h.Value.Trim();
        var headerNames=string.Join(';',headers.Keys); var date=now.UtcDateTime.ToString("yyyyMMdd",CultureInfo.InvariantCulture); var stamp=now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
        var scope=$"{date}/{region}/{service}/aws4_request";
        var parameters=ParseQuery(uri.Query); parameters["X-Amz-Algorithm"]="AWS4-HMAC-SHA256"; parameters["X-Amz-Credential"]=$"{accessKey}/{scope}"; parameters["X-Amz-Date"]=stamp; parameters["X-Amz-Expires"]=((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture); parameters["X-Amz-SignedHeaders"]=headerNames;
        var query=string.Join('&',parameters.OrderBy(x=>x.Key,StringComparer.Ordinal).ThenBy(x=>x.Value,StringComparer.Ordinal).Select(x=>$"{Encode(x.Key)}={Encode(x.Value)}"));
        var canonicalHeaders=string.Concat(headers.Select(x=>$"{x.Key}:{string.Join(' ',x.Value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))}\n"));
        var canonical=$"{method.Method}\n{CanonicalPath(uri.AbsolutePath)}\n{query}\n{canonicalHeaders}\n{headerNames}\nUNSIGNED-PAYLOAD";
        var toSign=$"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key=Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4"+secretKey),date),region),service),"aws4_request");
        var signature=Hex(Hmac(key,toSign)); var builder=new UriBuilder(uri){Query=query+"&X-Amz-Signature="+signature}; return builder.Uri;
    }
    private static SortedDictionary<string,string> ParseQuery(string query){var d=new SortedDictionary<string,string>(StringComparer.Ordinal);foreach(var p in query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)){var a=p.Split('=',2);d[Uri.UnescapeDataString(a[0])]=a.Length==2?Uri.UnescapeDataString(a[1]):"";}return d;}
    private static string CanonicalPath(string p)=>string.Join('/',p.Split('/').Select(Encode)).Replace("%2F","/",StringComparison.OrdinalIgnoreCase);
    private static string Encode(string s)=>Uri.EscapeDataString(s).Replace("%7E","~",StringComparison.OrdinalIgnoreCase);
    private static byte[] Hmac(byte[] key,string value){using var h=new HMACSHA256(key);return h.ComputeHash(Encoding.UTF8.GetBytes(value));}
    private static string Hex(byte[] bytes)=>Convert.ToHexStringLower(bytes);
}
