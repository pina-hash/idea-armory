using System.Buffers.Binary;
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

    public JournalRead Read()
    {
        var bytes = store.ReadAll();
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

    public void Append(JournalEntry entry)
    {
        var frame = Encode(entry);
        var existing = Read();
        if (existing.TornTail) store.TruncateIncompleteTail(existing.ValidLength);
        var duplicate = existing.Entries.FirstOrDefault(e => e.Id == entry.Id);
        if (duplicate is not null)
        {
            if (duplicate != entry) throw new InvalidDataException("An intent id cannot be reused for different content.");
            store.Flush();
            return;
        }
        store.Append(frame);
        store.Flush();
    }

    public void Replay(IIntentSink sink)
    {
        foreach (var entry in Read().Entries) sink.ApplyOnce(entry);
    }
}
