using Armory.Core;

namespace Armory.Agent.Engine;

// Durability and the engine's own indexes (docs/agent/ENGINE.md, "Saving state"). Every server
// write has its in-flight record on disk before it is sent: FlushAsync is a group commit, so the
// units waiting at one moment share one save (the document is serialized once, on the engine
// thread, and written off it, in order). An id is saved (by its block) before anything durable
// carries it, and a few rare steps (folder moves, folder operations) save at once as before.
// Everything else marks the document dirty and is saved at the end of the pass phase, or of the
// action, that changed it. A crash replays from the last save: in-flight records are sent again
// with the same operation id, and what was not saved yet is found again (AttachEntries,
// AdoptIdenticalBases).
public sealed partial class SyncEngine
{
    private bool dirty;
    private Task? nextFlush;
    private Task lastWrite = Task.CompletedTask;

    // A change that needs no save of its own: the next save carries it.
    private void MarkDirty() => dirty = true;

    // Saved before the next step, which depends on it (a folder about to move, a folder operation
    // about to be sent, a block of ids). Blocks the engine thread for the write; rare.
    private void SaveNow() => Write().GetAwaiter().GetResult();

    // Durable before the caller goes on: every caller waiting at this moment shares one save.
    private Task FlushAsync() => nextFlush ??= FlushSoonAsync();

    private async Task FlushSoonAsync()
    {
        // The units that are ready now (answers that just arrived) record their own writes
        // first and join this save, and so does every unit that arrives while the save before
        // it is still being written: a slow disk means fewer, larger groups, never a queue of
        // writes.
        await Task.Yield();
        await lastWrite.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); // its failure is its own waiters'
        nextFlush = null;
        await Write();
    }

    // Serializes now (on the engine thread) and writes after every earlier write (off it). A
    // document that did not change since the last save is not written again.
    private Task Write()
    {
        var parts = state.SerializeParts(whole: false);
        dirty = false;
        if (parts is null)
        {
            if (!lastWrite.IsFaulted) return lastWrite; // the last save holds this document already
            parts = state.SerializeParts(whole: true)!; // that save failed: this one writes it all
        }
        var store = deps.State;
        return lastWrite = lastWrite.ContinueWith(_ => store.Save(parts), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    // Saves what is dirty and waits until every write so far is on disk (the end of a pass, an
    // action or the engine). A failed write is reported to its own waiters, not here.
    private async Task SettleAsync()
    {
        try
        {
            if (nextFlush is { } pending) await pending;
            if (dirty) await Write();
            await lastWrite;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            deps.Log?.Invoke("state: " + error.Message);
        }
    }

    // After a failure (a crash, in tests): the saves already asked for finish, and nothing else is
    // saved, as in a real crash; a new engine over the same stores never sees a write after its start.
    private async Task DrainAsync()
    {
        try { if (nextFlush is { } pending) await pending; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { await lastWrite; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // One id, never handed out before (EngineState.NextId): a new block is saved before its first id.
    private string NextId(string kind)
    {
        if (state.IdsRunOut)
        {
            state.ReserveIds();
            SaveNow();
        }
        return state.NextId(kind);
    }

    // ---- Snapshots by id and by (path, hash), with their sizes --------------------------------

    private sealed record SnapshotInfo(SavedSnapshot Snapshot, int Order);
    private Dictionary<string, SnapshotInfo>? snapshotsById;
    private Dictionary<(string Path, string Hash), SnapshotInfo>? snapshotsByPath;
    private readonly Dictionary<string, long> snapshotSizes = new(StringComparer.Ordinal);

    // Read from the store once per start; every capture after that is added as it is made.
    private void IndexSnapshots()
    {
        if (snapshotsById is not null) return;
        snapshotsById = new(StringComparer.Ordinal);
        snapshotsByPath = new(PathAndHash.Instance);
        foreach (var snapshot in deps.Snapshots.Enumerate()) AddSnapshot(snapshot);
    }

    private void AddSnapshot(SavedSnapshot snapshot)
    {
        var info = new SnapshotInfo(snapshot, snapshotsById!.Count);
        if (!snapshotsById.TryAdd(snapshot.Id, info)) return;
        snapshotsByPath![(snapshot.Path, snapshot.Hash)] = info; // the newest capture of those bytes at that path
    }

    // One save of a file kept here: the snapshot and its journal entry (SaveRecorder), indexed
    // with the size of the bytes it took.
    private SavedSnapshot Record(string id, VaultPath recordPath, string author, Stream source)
    {
        IndexSnapshots();
        var counted = new CountingStream(source);
        var snapshot = recorder.Record(id, recordPath, author, counted);
        AddSnapshot(snapshot);
        snapshotSizes[snapshot.Id] = counted.Count;
        return snapshot;
    }

    private SavedSnapshot? SnapshotById(string id)
    {
        IndexSnapshots();
        return snapshotsById!.TryGetValue(id, out var info) ? info.Snapshot : null;
    }

    // The bytes behind a hash for this file: its own newest capture of them, else the newest
    // capture of them at its path.
    private SavedSnapshot? SnapshotFor(FileState st, string hash)
    {
        IndexSnapshots();
        SnapshotInfo? best = null;
        foreach (var id in st.Entries)
            if (snapshotsById!.TryGetValue(id, out var info) && info.Snapshot.Hash == hash && (best is null || info.Order > best.Order)) best = info;
        if (best is not null) return best.Snapshot;
        return snapshotsByPath!.TryGetValue((st.Path, hash), out var byPath) ? byPath.Snapshot : null;
    }

    // Known from the capture; a snapshot made before this start is measured once.
    private long SizeOf(SavedSnapshot snapshot)
    {
        if (snapshotSizes.TryGetValue(snapshot.Id, out var size)) return size;
        using (var stream = deps.Snapshots.OpenRead(snapshot.Id)) size = stream.Length;
        snapshotSizes[snapshot.Id] = size;
        return size;
    }

    private sealed class PathAndHash : IEqualityComparer<(string Path, string Hash)>
    {
        internal static readonly PathAndHash Instance = new();
        public bool Equals((string Path, string Hash) x, (string Path, string Hash) y)
            => string.Equals(x.Hash, y.Hash, StringComparison.Ordinal) && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, string Hash) key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path), StringComparer.Ordinal.GetHashCode(key.Hash));
    }

    // Counts the bytes a capture reads, so the snapshot's size is known without reading it again.
    private sealed class CountingStream(Stream inner) : Stream
    {
        internal long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Counted(await inner.ReadAsync(buffer, cancellationToken));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Counted(int read)
        {
            Count += read;
            return read;
        }
    }
}
