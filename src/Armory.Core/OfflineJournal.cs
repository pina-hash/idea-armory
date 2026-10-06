using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace Armory.Core;

// Implementations serialize writers and make Flush durable. Only an incomplete tail may
// be truncated; committed entries are immutable. No filesystem implementation lives here.
public interface IJournalStore
{
    byte[] ReadAll();
    void Append(ReadOnlySpan<byte> bytes);
    void Flush();
    void TruncateIncompleteTail(int validLength);
    // A value that changes whenever the bytes ReadAll returns change, by any route, and that
    // fails exactly as ReadAll would. OfflineJournal keeps its decoded entries while it is
    // unchanged. Null (the default) means unknown: the journal reads the store on every call.
    long? Generation => null;
}
public sealed record JournalEntry(string Id, IntentKind Kind, string Path, string? Hash, string? SnapshotId, string Author);
public sealed record JournalRead(IReadOnlyList<JournalEntry> Entries, int ValidLength, bool TornTail);
public interface IIntentSink
{
    // Atomically commit both the effect and Id. Never use a separate local "done" flag
    // as proof of remote completion. Repeated Id with different content must be rejected.
    void ApplyOnce(JournalEntry entry);
}

public sealed class OfflineJournal(IJournalStore store)
{
    private const int HeaderSize = 8;
    private const int DigestSize = 32;
    private const uint CommitMarker = 0x41524D59;
    private const int MaxPayload = 1024 * 1024;

    public static byte[] Encode(JournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Author) ||
            !VaultPath.TryCreate(entry.Path, out _, out _) || !Enum.IsDefined(entry.Kind) ||
            (entry.Kind == IntentKind.Upload && (string.IsNullOrEmpty(entry.Hash) || string.IsNullOrEmpty(entry.SnapshotId))))
            throw new ArgumentException("An intent needs an id, author, valid path, and an immutable snapshot for an upload.", nameof(entry));
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > MaxPayload) throw new ArgumentException("Journal intent exceeds one MiB; store bytes in the snapshot store.", nameof(entry));
        var frame = new byte[HeaderSize + payload.Length + DigestSize + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), ~payload.Length);
        payload.CopyTo(frame, HeaderSize);
        SHA256.HashData(payload).CopyTo(frame, HeaderSize + payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4), CommitMarker);
        return frame;
    }

    private readonly object gate = new();
    private Decoded? cache;

    // Decoded entries for one store generation. Entries only grow; ById keeps the first
    // entry with each id, which is the one a duplicate check compares against.
    private sealed class Decoded
    {
        internal required List<JournalEntry> Entries { get; init; }
        internal Dictionary<string, JournalEntry> ById { get; } = new(StringComparer.Ordinal);
        internal int ValidLength { get; set; }
        internal bool TornTail { get; set; }
        internal long? Generation { get; set; }
        internal JournalEntry[]? Snapshot { get; set; }
        internal void Add(JournalEntry entry, int frameLength)
        {
            Entries.Add(entry);
            ById.TryAdd(entry.Id, entry);
            ValidLength += frameLength;
            Snapshot = null;
        }
    }

    private sealed record Frames(List<JournalEntry> Entries, int ValidLength, bool TornTail);

    private static Frames Decode(byte[] bytes)
    {
        List<JournalEntry> entries = [];
        var position = 0;
        while (position < bytes.Length)
        {
            var remaining = bytes.Length - position;
            if (remaining < HeaderSize) break;
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position));
            var inverse = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
            if (length is < 1 or > MaxPayload || inverse != ~length)
                throw new InvalidDataException("Corrupt journal header; stop and retain all bytes for recovery.");
            var frameLength = HeaderSize + length + DigestSize + 4;
            if (remaining < frameLength) break; // MUTATION: torn entry dropped
            var payload = bytes.AsSpan(position + HeaderSize, length);
            var digest = bytes.AsSpan(position + HeaderSize + length, DigestSize);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), digest) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + frameLength - 4)) != CommitMarker)
                throw new InvalidDataException("Corrupt committed journal frame; stop and retain all bytes for recovery.");
            var entry = JsonSerializer.Deserialize<JournalEntry>(payload) ?? throw new InvalidDataException("Missing journal intent.");
            _ = Encode(entry); // Validate even if the store was populated by a different producer.
            entries.Add(entry);
            position += frameLength;
        }
        return new(entries, position, position != bytes.Length);
    }

    // The store is read and decoded only when its generation changed (or is unknown). The
    // generation is taken before the bytes, so a concurrent change can only cause a re-read.
    private Decoded Load()
    {
        var generation = store.Generation;
        if (cache is not null && generation is not null && cache.Generation == generation) return cache;
        cache = null;
        var read = Decode(store.ReadAll());
        var decoded = new Decoded { Entries = read.Entries, ValidLength = read.ValidLength, TornTail = read.TornTail, Generation = generation };
        foreach (var entry in decoded.Entries) decoded.ById.TryAdd(entry.Id, entry);
        if (generation is not null) cache = decoded;
        return decoded;
    }

    public JournalRead Read()
    {
        lock (gate)
        {
            var decoded = Load();
            if (decoded.Generation is null) return new(decoded.Entries, decoded.ValidLength, decoded.TornTail);
            return new(decoded.Snapshot ??= [.. decoded.Entries], decoded.ValidLength, decoded.TornTail);
        }
    }

    // The first committed entry with this id, without reading the store while it is unchanged.
    public bool TryGet(string id, [NotNullWhen(true)] out JournalEntry? entry)
    {
        lock (gate) return Load().ById.TryGetValue(id, out entry);
    }

    public void Append(JournalEntry entry)
    {
        var frame = Encode(entry);
        lock (gate)
        {
            try
            {
                var existing = Load();
                if (existing.TornTail)
                {
                    store.TruncateIncompleteTail(existing.ValidLength);
                    existing.TornTail = false;
                    existing.Generation = store.Generation;
                }
                if (existing.ById.TryGetValue(entry.Id, out var duplicate))
                {
                    if (duplicate != entry) throw new InvalidDataException("An intent id cannot be reused for different content.");
                    store.Flush();
                    return;
                }
                store.Append(frame);
                store.Flush();
                if (existing != cache) return;
                existing.Generation = store.Generation;
                // Keep exactly what a fresh read decodes from these bytes. If it would not
                // decode, keep nothing: the next read decodes the store and fails as before.
                JournalEntry? appended = null;
                try { appended = Decode(frame).Entries.SingleOrDefault(); }
                catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { }
                if (appended is null || existing.Generation is null) cache = null;
                else existing.Add(appended, frame.Length);
            }
            catch
            {
                cache = null; // A failed write leaves the store unknown; read it again next time.
                throw;
            }
        }
    }

    public void Replay(IIntentSink sink)
    {
        foreach (var entry in Read().Entries) sink.ApplyOnce(entry);
    }
}
