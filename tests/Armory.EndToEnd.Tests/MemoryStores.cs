using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.EndToEnd.Tests;

// Durable stores that outlive an engine instance: a "crash" drops the engine and builds a
// new one over the same objects, exactly as a restarted process reopens its files.
internal sealed class MemoryJournalStore : IJournalStore
{
    private readonly List<byte> bytes = [];
    private long generation;
    public byte[] ReadAll() { lock (bytes) return bytes.ToArray(); }
    public void Append(ReadOnlySpan<byte> value) { lock (bytes) { bytes.AddRange(value.ToArray()); generation++; } }
    public void Flush() { }
    public void TruncateIncompleteTail(int validLength) { lock (bytes) { bytes.RemoveRange(validLength, bytes.Count - validLength); generation++; } }
    public long? Generation { get { lock (bytes) return generation; } }
}

internal sealed class MemorySnapshotStore : ISnapshotStore
{
    private readonly List<(SavedSnapshot Snapshot, byte[] Bytes)> items = [];
    private readonly Dictionary<string, int> byId = new(StringComparer.Ordinal);
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source)
    {
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        var bytes = copy.ToArray();
        var snapshot = new SavedSnapshot(id, path.Value, Convert.ToHexStringLower(SHA256.HashData(bytes)), author);
        lock (items)
        {
            if (byId.TryGetValue(id, out var at))
            {
                var old = items[at].Snapshot;
                if (old != snapshot) throw new InvalidDataException("A capture id cannot be reused for different bytes.");
                return old;
            }
            byId[id] = items.Count;
            items.Add((snapshot, bytes));
        }
        return snapshot;
    }
    public IReadOnlyList<SavedSnapshot> Enumerate() { lock (items) return items.Select(i => i.Snapshot).ToArray(); }
    public Stream OpenRead(string id)
    {
        lock (items) return byId.TryGetValue(id, out var at) ? new MemoryStream(items[at].Bytes, writable: false) : throw new InvalidOperationException($"No snapshot {id}.");
    }
    public IReadOnlyList<(SavedSnapshot Snapshot, byte[] Bytes)> All() { lock (items) return items.ToArray(); }
}

// Keeps the document in a buffer of its own, grown as needed and never shrunk, so 5,000 files
// saved thousands of times do not allocate each time.
internal sealed class MemoryStateStore : IEngineStateStore
{
    private readonly object gate = new();
    private byte[] buffer = [];
    private int length = -1;
    public int Saves { get; private set; }
    // A slow disk: every save takes this long (it runs off the engine thread).
    public TimeSpan Delay { get; set; }
    public byte[]? Load() { lock (gate) return length < 0 ? null : buffer.AsSpan(0, length).ToArray(); }
    public void Save(byte[] value) => Save([value]);
    public void Save(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
        lock (gate)
        {
            var total = 0;
            foreach (var part in parts) total += part.Length;
            if (buffer.Length < total) buffer = new byte[Math.Max(total, buffer.Length * 2)];
            var at = 0;
            foreach (var part in parts)
            {
                part.Span.CopyTo(buffer.AsSpan(at));
                at += part.Length;
            }
            length = total;
            Saves++;
        }
    }
}
