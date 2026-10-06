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

// A store that counts its writes, as DurableJournalStore does, and can let another writer in
// right after its next write, before the writing journal reads the generation again.
internal sealed class InterleavingJournalStore : IJournalStore
{
    private long generation;
    internal List<byte> Bytes { get; } = [];
    internal int Reads { get; private set; }
    internal Action? AfterNextWrite { get; set; }
    public byte[] ReadAll() { Reads++; return Bytes.ToArray(); }
    public long? Generation => generation;
    public void Append(ReadOnlySpan<byte> bytes) { Bytes.AddRange(bytes.ToArray()); Wrote(); }
    public void Flush() { }
    public void TruncateIncompleteTail(int validLength) { Bytes.RemoveRange(validLength, Bytes.Count - validLength); Wrote(); }
    // A torn write behind every journal's back.
    internal void Tear(ReadOnlySpan<byte> bytes) { Bytes.AddRange(bytes.ToArray()); generation++; }
    private void Wrote()
    {
        generation++;
        var next = AfterNextWrite;
        AfterNextWrite = null;
        next?.Invoke();
    }
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
    [Fact]
    public void Appends_and_reads_reuse_decoded_entries_without_rereading_the_store()
    {
        var store = new MemoryJournalStore();
        var journal = new OfflineJournal(store);
        for (var i = 0; i < 300; i++)
        {
            journal.Append(Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            journal.Append(Entry("0")); // a duplicate is found in memory too
            Assert.Equal(i + 1, journal.Read().Entries.Count);
            Assert.True(journal.TryGet("0", out _));
        }
        Assert.Equal(1, store.Reads);
        var fresh = new OfflineJournal(store).Read();
        Assert.Equal(fresh.Entries, journal.Read().Entries);
        Assert.Equal(fresh.ValidLength, journal.Read().ValidLength);
        Assert.Same(journal.Read().Entries, journal.Read().Entries);
    }

    [Fact]
    public void A_store_without_a_generation_is_read_on_every_call()
    {
        var store = new MemoryJournalStore(tracksGeneration: false);
        var journal = new OfflineJournal(store);
        journal.Append(Entry("1"));
        journal.Append(Entry("2"));
        Assert.Equal(2, journal.Read().Entries.Count);
        Assert.True(journal.TryGet("1", out _));
        Assert.Equal(4, store.Reads);
    }

    [Fact]
    public void Changes_made_behind_the_journal_are_seen()
    {
        var store = new MemoryJournalStore();
        var journal = new OfflineJournal(store);
        journal.Append(Entry("1"));
        // Another writer appends a frame; a torn write lands behind it.
        new OfflineJournal(store).Append(Entry("2"));
        store.Bytes.AddRange(OfflineJournal.Encode(Entry("3")).AsSpan(0, 10).ToArray());
        var read = journal.Read();
        Assert.Equal(["1", "2"], read.Entries.Select(e => e.Id));
        Assert.True(read.TornTail);
        Assert.Throws<InvalidDataException>(() => journal.Append(Entry("2") with { Hash = "different" }));
        journal.Append(Entry("4"));
        Assert.Equal(["1", "2", "4"], journal.Read().Entries.Select(e => e.Id));
        Assert.False(journal.Read().TornTail);
        Assert.True(journal.TryGet("2", out var second));
        Assert.Equal(Entry("2"), second);
        Assert.False(journal.TryGet("3", out _));
    }

    // Another journal writes between this journal's append and its read of the generation:
    // the generation moved by two, so the cache is dropped and the other entry is seen.
    [Fact]
    public void A_write_by_another_journal_right_after_an_append_is_seen()
    {
        var store = new InterleavingJournalStore();
        var journal = new OfflineJournal(store);
        journal.Append(Entry("1"));
        Assert.Single(journal.Read().Entries);
        var reads = store.Reads;
        store.AfterNextWrite = () => new OfflineJournal(store).Append(Entry("2"));
        journal.Append(Entry("3"));
        Assert.Equal(["1", "3", "2"], journal.Read().Entries.Select(e => e.Id));
        Assert.True(store.Reads > reads);
        Assert.True(journal.TryGet("2", out var other));
        Assert.Equal(Entry("2"), other);
        Assert.Throws<InvalidDataException>(() => journal.Append(Entry("2") with { Hash = "different" }));
        var length = store.Bytes.Count;
        journal.Append(Entry("2"));
        Assert.Equal(length, store.Bytes.Count);
        Assert.Equal(new OfflineJournal(store).Read().Entries, journal.Read().Entries);
        // With nothing else writing, appends keep the cache again: the store is not read.
        reads = store.Reads;
        journal.Append(Entry("4"));
        Assert.Equal(["1", "3", "2", "4"], journal.Read().Entries.Select(e => e.Id));
        Assert.Equal(reads, store.Reads);
    }

    // The same between the truncation of a torn tail and the read of the generation.
    [Fact]
    public void A_write_by_another_journal_right_after_a_truncation_is_seen()
    {
        var store = new InterleavingJournalStore();
        var journal = new OfflineJournal(store);
        journal.Append(Entry("1"));
        store.Tear(OfflineJournal.Encode(Entry("torn")).AsSpan(0, 10));
        Assert.True(journal.Read().TornTail);
        store.AfterNextWrite = () => new OfflineJournal(store).Append(Entry("2"));
        journal.Append(Entry("3"));
        var read = journal.Read();
        Assert.Equal(["1", "2", "3"], read.Entries.Select(e => e.Id));
        Assert.False(read.TornTail);
        Assert.Equal(store.Bytes.Count, read.ValidLength);
        Assert.Throws<InvalidDataException>(() => journal.Append(Entry("2") with { Hash = "different" }));
        Assert.Equal(new OfflineJournal(store).Read().Entries, journal.Read().Entries);
    }

    [Fact]
    public void TryGet_returns_the_first_entry_a_duplicate_check_compares_against()
    {
        var store = new MemoryJournalStore();
        // A different producer wrote the same id twice; the first one is the committed one.
        store.Bytes.AddRange(OfflineJournal.Encode(Entry("1")));
        store.Bytes.AddRange(OfflineJournal.Encode(Entry("1") with { Hash = "later" }));
        var journal = new OfflineJournal(store);
        Assert.True(journal.TryGet("1", out var first));
        Assert.Equal("hash", first.Hash);
        journal.Append(Entry("1"));
        Assert.Throws<InvalidDataException>(() => journal.Append(Entry("1") with { Hash = "later" }));
        Assert.Equal(2, journal.Read().Entries.Count);
    }

    // The long-lived journal over a store with a generation must behave exactly like one over a
    // store without (which decodes the store on every call, as before the cache), and every read
    // must equal a fresh journal's read, through torn writes, failed flushes, duplicates,
    // conflicting ids, writes by another journal, torn bytes and corruption made behind its back.
    [Fact]
    public void A_cached_journal_behaves_exactly_like_one_that_rereads_the_store()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var random = new ScheduleRandom(seed);
            MemoryJournalStore[] stores = [new(), new(tracksGeneration: false)];
            var journals = stores.Select(s => new OfflineJournal(s)).ToArray();
            var next = 0;
            for (var step = 0; step < 40; step++)
            {
                var before = new OfflineJournal(stores[0]).Read();
                var operation = random.Next(9);
                // Torn bytes only ever land at a complete end, as a torn write does.
                if (operation == 6 && before.TornTail) operation = 0;
                var id = operation is 1 or 2 && next > 0 ? random.Next(next).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var entry = operation == 2 ? Entry(id) with { Hash = "other" } : Entry(id);
                var cut = random.Next(OfflineJournal.Encode(entry).Length + 1);
                var corrupt = before.ValidLength > 0 ? random.Next(before.ValidLength) : -1; // committed: reads and appends fail closed
                var outcomes = new string[2];
                for (var j = 0; j < 2; j++)
                {
                    var store = stores[j];
                    try
                    {
                        switch (operation)
                        {
                            case 3: store.CrashAfter = cut; journals[j].Append(entry); break;
                            case 4: store.CrashOnFlush = true; journals[j].Append(entry); break;
                            case 5: new OfflineJournal(store).Append(entry); break;
                            case 6: store.Bytes.AddRange(OfflineJournal.Encode(entry).AsSpan(0, cut).ToArray()); break;
                            case 7:
                                if (corrupt < 0) break;
                                store.Bytes[corrupt] ^= 0xff;
                                try { journals[j].Read(); journals[j].Append(entry); }
                                finally { store.Bytes[corrupt] ^= 0xff; }
                                break;
                            case 8: journals[j] = new OfflineJournal(store); break;
                            default: journals[j].Append(entry); break;
                        }
                        outcomes[j] = "ok";
                    }
                    catch (Exception error) when (error is SimulatedCrash or InvalidDataException) { outcomes[j] = error.GetType().Name; }
                    finally { store.CrashAfter = null; store.CrashOnFlush = false; }
                }
                Assert.Equal(outcomes[0], outcomes[1]);
                Assert.Equal(stores[0].Bytes, stores[1].Bytes);
                var fresh = new OfflineJournal(stores[0]).Read();
                foreach (var journal in journals)
                {
                    var read = journal.Read();
                    Assert.Equal(fresh.Entries, read.Entries);
                    Assert.Equal(fresh.ValidLength, read.ValidLength);
                    Assert.Equal(fresh.TornTail, read.TornTail);
                }
                var first = fresh.Entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var (key, value) in first)
                {
                    Assert.True(journals[0].TryGet(key, out var found));
                    Assert.Equal(value, found);
                }
                Assert.False(journals[0].TryGet("missing", out _));
                var sinks = journals.Select(_ => new RecordingSink()).ToArray();
                for (var j = 0; j < 2; j++) { journals[j].Replay(sinks[j]); journals[j].Replay(sinks[j]); }
                Assert.Equal(sinks[0].Applied, sinks[1].Applied);
            }
        }
    }
}
