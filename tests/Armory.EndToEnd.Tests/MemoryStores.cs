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
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source)
    {
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        var bytes = copy.ToArray();
        var snapshot = new SavedSnapshot(id, path.Value, Convert.ToHexStringLower(SHA256.HashData(bytes)), author);
        lock (items)
        {
            var old = items.FirstOrDefault(i => i.Snapshot.Id == id);
            if (old.Snapshot is not null)
            {
                if (old.Snapshot != snapshot) throw new InvalidDataException("A capture id cannot be reused for different bytes.");
                return old.Snapshot;
            }
            items.Add((snapshot, bytes));
        }
        return snapshot;
    }
    public IReadOnlyList<SavedSnapshot> Enumerate() { lock (items) return items.Select(i => i.Snapshot).ToArray(); }
    public Stream OpenRead(string id) { lock (items) return new MemoryStream(items.Single(i => i.Snapshot.Id == id).Bytes, writable: false); }
    public IReadOnlyList<(SavedSnapshot Snapshot, byte[] Bytes)> All() { lock (items) return items.ToArray(); }
}

internal sealed class MemoryStateStore : IEngineStateStore
{
    private byte[]? state;
    public byte[]? Load() => state?.ToArray();
    public void Save(byte[] value) => state = value.ToArray();
}
