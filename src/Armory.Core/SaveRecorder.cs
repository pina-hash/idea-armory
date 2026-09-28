namespace Armory.Core;

public sealed record SavedSnapshot(string Id, string Path, string Hash, string Author);
public interface ISaveSnapshotStore
{
    // Atomically persist immutable bytes AND metadata before returning. Id is device-specific,
    // monotonic, durable, and never reused. Enumerate in capture order, including orphan captures.
    SavedSnapshot Capture(string id, VaultPath path, string author, Stream source);
    IReadOnlyList<SavedSnapshot> Enumerate();
}

// Capture every save, not merely the hash left on disk when connectivity returns.
public sealed class SaveRecorder(ISaveSnapshotStore snapshots, OfflineJournal journal)
{
    public SavedSnapshot Record(string id, VaultPath path, string author, Stream source)
    {
        var snapshot = snapshots.Capture(id, path, author, source);
        Append(snapshot);
        return snapshot;
    }
    public void Recover()
    {
        foreach (var snapshot in snapshots.Enumerate()) Append(snapshot);
    }
    private void Append(SavedSnapshot snapshot) => journal.Append(new JournalEntry(snapshot.Id, IntentKind.Upload,
        snapshot.Path, snapshot.Hash, snapshot.Id, snapshot.Author));
}
