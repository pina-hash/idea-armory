using System.Security.Cryptography;

namespace Armory.Core;

public static class ContentAddress
{
    // SHA256's stream overload consumes a bounded buffer and leaves ownership with the caller.
    public static async ValueTask<string> ComputeAsync(Stream source, CancellationToken cancellationToken = default)
        => Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
}
