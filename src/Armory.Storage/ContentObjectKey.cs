using System.Security.Cryptography;

namespace Armory.Storage;

public static class ContentObjectKey
{
    public static string FromBytes(ReadOnlySpan<byte> bytes) => FromHash(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    public static string FromHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A SHA-256 hash must be 64 hexadecimal characters.", nameof(hash));
        hash = hash.ToLowerInvariant();
        return $"blobs/sha256/{hash[..2]}/{hash.Substring(2, 2)}/{hash}";
    }
    public static string HashFromKey(string key)
    {
        var hash = key.Split('/').LastOrDefault() ?? "";
        _ = FromHash(hash);
        return hash.ToLowerInvariant();
    }
}
