using System.Security.Cryptography;
using Armory.Core;

namespace Armory.Core.Tests;

internal sealed class MemorySnapshots : ISaveSnapshotStore
{
    internal Dictionary<string, (SavedSnapshot Metadata, byte[] Bytes)> Items { get; } = new(StringComparer.Ordinal);
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source)
    {
        using var stream = new MemoryStream();
        source.CopyTo(stream);
        var bytes = stream.ToArray();
        var snapshot = new SavedSnapshot(id, path.Value, Convert.ToHexStringLower(SHA256.HashData(bytes)), author);
        if (Items.TryGetValue(id, out var old)) { Assert.Equal(old.Metadata, snapshot); Assert.Equal(old.Bytes, bytes); }
        else Items.Add(id, (snapshot, bytes));
        return snapshot;
    }
    public IReadOnlyList<SavedSnapshot> Enumerate() => Items.Values.Select(v => v.Metadata).ToArray();
}

public sealed class JournalTests
{
    private static JournalEntry Entry(string id) => new(id, IntentKind.Upload, "robot/file.txt", "hash", "snapshot-" + id, "Alex");

    [Fact]
    public void Crash_at_every_byte_drops_only_torn_final_entry_and_accepts_future_appends()
    {
        var frame = OfflineJournal.Encode(Entry("second"));
        for (var cut = 0; cut <= frame.Length; cut++)
        {
            var store = new MemoryJournalStore();
            var journal = new OfflineJournal(store);
            journal.Append(Entry("first"));
            store.CrashAfter = cut;
            Assert.Throws<SimulatedCrash>(() => journal.Append(Entry("second")));
            var read = new OfflineJournal(store).Read();
            Assert.Equal(cut == frame.Length ? 2 : 1, read.Entries.Count);
            Assert.Equal(cut > 0 && cut < frame.Length, read.TornTail);
            journal.Append(Entry("third"));
            var sink = new RecordingSink();
            journal.Replay(sink);
            journal.Replay(sink);
            Assert.Equal(cut == frame.Length ? new[] { "first", "second", "third" } : ["first", "third"], sink.Applied.Select(e => e.Id));
        }
    }

    [Fact]
    public void Replay_after_effect_committed_but_ack_lost_is_idempotent()
    {
        var journal = new OfflineJournal(new MemoryJournalStore());
        journal.Append(Entry("1"));
        journal.Append(Entry("2") with { Kind = IntentKind.Tombstone, Hash = null, SnapshotId = null });
        var sink = new RecordingSink { CrashAfterCommit = true };
        Assert.Throws<SimulatedCrash>(() => journal.Replay(sink));
        journal.Replay(sink);
        journal.Replay(sink);
        Assert.Equal(["1", "2"], sink.Applied.Select(e => e.Id));
    }

    [Fact]
    public void Flush_failure_retries_same_id_without_duplicate()
    {
        var store = new MemoryJournalStore { CrashOnFlush = true };
        var journal = new OfflineJournal(store);
        Assert.Throws<SimulatedCrash>(() => journal.Append(Entry("1")));
        journal.Append(Entry("1"));
        Assert.Single(journal.Read().Entries);
        Assert.Throws<InvalidDataException>(() => journal.Append(Entry("1") with { Hash = "different" }));
    }

    [Theory]
    [InlineData(0)][InlineData(8)][InlineData(-1)]
    public void Corruption_fails_closed_without_erasing_evidence(int offset)
    {
        var store = new MemoryJournalStore();
        var journal = new OfflineJournal(store);
        journal.Append(Entry("1"));
        var index = offset < 0 ? store.Bytes.Count - 1 : offset;
        store.Bytes[index] ^= 0xff;
        var before = store.ReadAll();
        Assert.Throws<InvalidDataException>(() => journal.Read());
        Assert.Equal(before, store.ReadAll());
    }

    [Fact]
    public void Intent_validation_requires_identity_valid_path_and_snapshot()
    {
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("")));
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("1") with { Author = "" }));
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("1") with { Path = "../outside" }));
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("1") with { SnapshotId = null }));
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("1") with { Kind = (IntentKind)99 }));
        Assert.Throws<ArgumentException>(() => OfflineJournal.Encode(Entry("1") with { Author = new string('a', 1024 * 1024) }));
    }

    [Fact]
    public void Orphaned_snapshot_is_rejournaled_after_torn_save_and_each_save_survives()
    {
        var store = new MemoryJournalStore();
        var snapshots = new MemorySnapshots();
        var journal = new OfflineJournal(store);
        var recorder = new SaveRecorder(snapshots, journal);
        recorder.Record("device:1", Fixtures.Path(), "Alex", new MemoryStream([1]));
        store.CrashAfter = 10;
        Assert.Throws<SimulatedCrash>(() => recorder.Record("device:2", Fixtures.Path(), "Alex", new MemoryStream([2])));
        new SaveRecorder(snapshots, new OfflineJournal(store)).Recover();
        recorder.Record("device:3", Fixtures.Path(), "Alex", new MemoryStream([3]));
        recorder.Recover();
        Assert.Equal(3, journal.Read().Entries.Count);
        Assert.Equal(3, snapshots.Items.Count);
        var sink = new RecordingSink();
        journal.Replay(sink);
        Assert.Equal(["device:1", "device:2", "device:3"], sink.Applied.Select(e => e.Id));
    }
}
