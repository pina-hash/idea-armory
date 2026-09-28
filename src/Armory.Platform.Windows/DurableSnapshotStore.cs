using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed class DurableSnapshotStore : ISaveSnapshotStore, IDisposable
{
    private readonly string folder;
    private readonly FileStream ownership;
    private readonly List<SnapshotManifest> captured = [];
    private long sequence;
    private sealed record SnapshotManifest(long Sequence, SavedSnapshot Snapshot, string Blob);
    public DurableSnapshotStore(WindowsPaths paths)
    {
        folder = Path.Combine(paths.PrivateDirectory(), "snapshots");
        Directory.CreateDirectory(folder);
        ownership = new(Path.Combine(folder, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid snapshot metadata.");
            if (!File.Exists(Path.Combine(folder, manifest.Blob))) throw new InvalidDataException("Snapshot metadata has no durable bytes.");
            captured.Add(manifest);
            sequence = Math.Max(sequence, manifest.Sequence);
        }
        captured.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        // Incomplete blob writes are unacknowledged. Retain complete orphan blobs for diagnosis.
        foreach (var pending in Directory.EnumerateFiles(folder, "*.pending")) File.Delete(pending);
    }
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source)
    {
        if (!path.IsValid || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(author)) throw new ArgumentException("Snapshot identity, path, and author are required.");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        var pending = Path.Combine(folder, key + ".pending");
        string hash;
        try
        {
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                source.CopyTo(output);
                output.Flush(true);
                output.Position = 0;
                hash = Convert.ToHexStringLower(SHA256.HashData(output));
            }
            var snapshot = new SavedSnapshot(id, path.Value, hash, author);
            var old = captured.FirstOrDefault(m => m.Snapshot.Id == id);
            if (old is not null)
            {
                if (old.Snapshot != snapshot) throw new InvalidDataException("A capture id cannot be reused for different bytes or metadata.");
                return old.Snapshot;
            }
            var blob = key + ".blob";
            NativeMethods.Move(pending, Path.Combine(folder, blob), replace: true);
            var manifest = new SnapshotManifest(++sequence, snapshot, blob);
            var metadataPending = Path.Combine(folder, key + ".json.pending");
            using (var output = new FileStream(metadataPending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, manifest); output.Flush(true); }
            NativeMethods.Move(metadataPending, Path.Combine(folder, key + ".json"), replace: false);
            captured.Add(manifest);
            return snapshot;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public IReadOnlyList<SavedSnapshot> Enumerate() => captured.Select(m => m.Snapshot).ToArray();
    public Stream OpenRead(string id)
    {
        var manifest = captured.Single(m => m.Snapshot.Id == id);
        var stream = new FileStream(Path.Combine(folder, manifest.Blob), FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (actual != manifest.Snapshot.Hash) { stream.Dispose(); throw new InvalidDataException("Captured bytes failed their content hash."); }
        stream.Position = 0;
        return stream;
    }
    public void Dispose() => ownership.Dispose();
}
