using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Armory.TestSupport;

/// <summary>
/// Supabase Storage's upload on the fake (<c>POST /storage/v1/object/{bucket}/{name}</c>), for the
/// feedback screenshots of idea-app 0235. The bucket's rules come from <c>storage.buckets</c>
/// (made by <see cref="ArmoryV3StandIn.ApplyFeedbackV2Async"/>: private, image/png only, 2097152
/// bytes), and each upload is inserted into <c>storage.objects</c> as the caller (role
/// authenticated, the request's JWT claims set), so 0235's insert policy (the caller's own folder)
/// is PostgreSQL's own check, and the unique (bucket, name) refuses an overwrite. Stricter than
/// Storage on one point: a name in armory-feedback-shots must be <c>&lt;uuid&gt;/&lt;uuid&gt;.png</c>,
/// lowercase (Storage takes any name in the caller's folder, and the submit refuses the rest as
/// bad_path), so a client proven here always uploads the key the submit takes.
/// <para>
/// Refusals carry Storage's body, <c>{"statusCode": "413", "code": "EntityTooLarge", "error",
/// "message"}</c>, with HTTP 400, as Storage answers every refusal but a 500 (its error handler
/// sends <c>userStatusCode</c>, which is 400 unless the code is 500). While
/// <see cref="StorageRealStatus"/> is on, the body's code is the HTTP status instead. An expired or
/// bad token is <c>{"statusCode": "400", "code": "InvalidJWT", "error": "InvalidJWT"}</c> either way.
/// </para>
/// </summary>
public sealed partial class FakeSupabase
{
    public const string StoragePathPrefix = "/storage/v1/object/";
    public const string FeedbackShotsBucket = "armory-feedback-shots";

    /// <summary>An object Storage holds: its bytes and the content type it was sent with.</summary>
    public sealed record StoredObject(string Bucket, string Name, byte[] Bytes, string ContentType, string? Email);

    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string StatusCode, string Code, string Error)> _storageFailures = new();
    private int _storageUploads;

    /// <summary>
    /// False (the default): every Storage refusal is HTTP 400 with its own code only in the body, as
    /// Storage answers. True: the body's code is the HTTP status too.
    /// </summary>
    public bool StorageRealStatus { get; set; }

    /// <summary>
    /// The next <paramref name="times"/> uploads are answered with this body without storing
    /// anything: a busy Storage (544 DatabaseTimeout, 503 DatabaseReadOnly or LockTimeout, 423
    /// ResourceLocked, 429), with HTTP 400, or the code when <see cref="StorageRealStatus"/>.
    /// </summary>
    public void FailStorage(string statusCode, string code, string error, int times = 1)
    {
        for (var i = 0; i < times; i++) _storageFailures.Enqueue((statusCode, code, error));
    }

    /// <summary>Uploads that reached Storage, refused or not.</summary>
    public int StorageUploads => Volatile.Read(ref _storageUploads);

    /// <summary>Every object stored so far, by "bucket/name".</summary>
    public IReadOnlyDictionary<string, StoredObject> StoredObjects => new Dictionary<string, StoredObject>(_objects, StringComparer.Ordinal);

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.png\z")]
    private static partial Regex FeedbackShotKey();

    private async Task<FakeResponse> StorageAsync(HttpContext context, string rest)
    {
        var request = context.Request;
        Interlocked.Increment(ref _storageUploads);
        if (!ApiKeyMatches(request)) return InvalidApiKey();
        if (!HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method))
            return StorageError(405, "MethodNotAllowed", "Method not allowed", "This fake takes only uploads.");
        var bearer = BearerToken(request);
        string? email = null;
        if (bearer is not null && !FixedTimeEquals(bearer, AnonKey))
        {
            var check = CheckAccessToken(bearer, out email);
            if (check == TokenCheck.Expired) return StorageError(400, "InvalidJWT", "InvalidJWT", "\"exp\" claim timestamp check failed");
            if (check != TokenCheck.Live) return StorageError(400, "InvalidJWT", "InvalidJWT", "invalid signature");
        }
        if (_storageFailures.TryDequeue(out var failure))
        {
            var code = int.Parse(failure.StatusCode, System.Globalization.CultureInfo.InvariantCulture);
            return FakeResponse.Json(StorageRealStatus ? code : 400, new JsonObject
            {
                ["statusCode"] = failure.StatusCode, ["code"] = failure.Code, ["error"] = failure.Error, ["message"] = failure.Error,
            });
        }
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return StorageError(400, "InvalidKey", "InvalidKey", "The object key is missing.");
        var bucket = rest[..slash];
        var name = Uri.UnescapeDataString(rest[(slash + 1)..]);

        var rules = await BucketAsync(bucket);
        if (rules is null) return StorageError(404, "NoSuchBucket", "Bucket not found", "Bucket not found");
        var contentType = request.ContentType?.Split(';')[0].Trim() ?? "";
        if (rules.Value.Mime is { Length: > 0 } allowed && !allowed.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return StorageError(415, "InvalidMimeType", "invalid_mime_type", $"mime type {contentType} is not supported");
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (rules.Value.Limit is { } limit && bytes.LongLength > limit)
            return StorageError(413, "EntityTooLarge", "Payload too large", "The object exceeded the maximum allowed size");
        if (bucket == FeedbackShotsBucket && !FeedbackShotKey().IsMatch(name))
            return StorageError(400, "InvalidKey", "InvalidKey", $"Invalid key: {name}");
        if (email is null) return StorageError(403, "AccessDenied", "Unauthorized", "new row violates row-level security policy");

        try
        {
            await Database.RunAsCallerAsync(email, AdminEmails, async (connection, cancellationToken) =>
            {
                await using (var claims = new NpgsqlCommand("select set_config('request.jwt.claims', $1, true)", connection))
                {
                    claims.Parameters.Add(new NpgsqlParameter { Value = ClaimsFor(email) });
                    await claims.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var insert = new NpgsqlCommand(
                    "insert into storage.objects (bucket_id, name, owner, metadata) values ($1, $2, auth.uid(), jsonb_build_object('mimetype', $3::text, 'size', $4::bigint))", connection);
                insert.Parameters.Add(new NpgsqlParameter { Value = bucket });
                insert.Parameters.Add(new NpgsqlParameter { Value = name });
                insert.Parameters.Add(new NpgsqlParameter { Value = contentType });
                insert.Parameters.Add(new NpgsqlParameter { Value = bytes.LongLength });
                return await insert.ExecuteNonQueryAsync(cancellationToken);
            });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return StorageError(409, "KeyAlreadyExists", "Duplicate", "The resource already exists");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return StorageError(403, "AccessDenied", "Unauthorized", "new row violates row-level security policy");
        }
        _objects[bucket + "/" + name] = new StoredObject(bucket, name, bytes, contentType, email);
        return FakeResponse.Json(200, new JsonObject { ["Key"] = bucket + "/" + name, ["Id"] = Guid.NewGuid().ToString() });
    }

    private async Task<(long? Limit, string[]? Mime)?> BucketAsync(string bucket)
    {
        await using var connection = await Database.OpenAsync();
        await using var exists = new NpgsqlCommand("select to_regclass('storage.buckets') is not null", connection);
        if (!(bool)(await exists.ExecuteScalarAsync())!) return null;
        await using var command = new NpgsqlCommand("select file_size_limit, allowed_mime_types from storage.buckets where id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = bucket });
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<string[]>(1));
    }

    // Storage's StorageBackendError rendered: HTTP 400 (a 500 stays 500), the real code as text in
    // the body; the real code as the HTTP status too while StorageRealStatus.
    private FakeResponse StorageError(int status, string code, string error, string message)
        => FakeResponse.Json(StorageRealStatus || status == 500 ? status : 400, new JsonObject
        {
            ["statusCode"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture), ["code"] = code, ["error"] = error, ["message"] = message,
        });
}
