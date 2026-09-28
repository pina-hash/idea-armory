using System.Buffers.Binary;
using System.Text;
using Armory.Core;
using Xunit.Abstractions;

namespace Armory.Platform.Windows.Tests;

public sealed class DurableJournalTests(ITestOutputHelper output)
{
    [WindowsFact]
    public void Crc32C_matches_standard_check_vector()
        => Assert.Equal(0xe3069283u, Crc32C.Compute(Encoding.ASCII.GetBytes("123456789")));

    [WindowsFact]
    public void Core_journal_round_trips_through_real_store()
    {
        using var vault = new TestVault();
        var file = vault.File("journal.dat");
        var entry = new JournalEntry("device:1", IntentKind.Upload, "part.txt", "hash", "capture", "Alex");
        using (var store = new DurableJournalStore(file)) { new OfflineJournal(store).Append(entry); }
        using var reopened = new DurableJournalStore(file);
        Assert.Equal(entry, Assert.Single(new OfflineJournal(reopened).Read().Entries));
        Assert.Equal(0, reopened.Recovery.DroppedBytes);
    }

    [WindowsFact]
    public void Core_incomplete_suffix_can_be_removed_but_committed_entries_cannot()
    {
        using var vault = new TestVault();
        using var store = new DurableJournalStore(vault.File("journal"));
        var first = new JournalEntry("1", IntentKind.AcquireLock, "part.txt", null, null, "Alex");
        var journal = new OfflineJournal(store);
        journal.Append(first);
        Assert.Throws<InvalidDataException>(() => store.TruncateIncompleteTail(0));
        store.Append(OfflineJournal.Encode(first with { Id = "2" }).AsSpan(0, 10));
        Assert.True(journal.Read().TornTail);
        journal.Append(first with { Id = "3" });
        Assert.Equal(["1", "3"], journal.Read().Entries.Select(e => e.Id));
    }

    [WindowsFact]
    public async Task Killed_writer_200_times_keeps_only_ordered_complete_unique_records()
    {
        using var vault = new TestVault();
        var torn = 0;
        long dropped = 0;
        var totalRecords = 0;
        for (var run = 0; run < 200; run++)
        {
            var file = vault.File($"kill-{run}.journal");
            using (var child = new ChildProcess("journal-writer", file))
            {
                Assert.Equal("READY", await child.ReadLine());
                Assert.Equal("0", await child.ReadLine());
                // Vary the cut across a tight append loop rather than injecting a fake error.
                if (run % 5 > 0) await Task.Delay(run % 5);
                child.Kill();
            }
            using (var reopened = new DurableJournalStore(file))
            {
                Assert.False(reopened.Recovery.CorruptionDetected, $"Unexpected committed corruption in run {run}");
                if (reopened.Recovery.DroppedBytes > 0) { torn++; dropped += reopened.Recovery.DroppedBytes; }
                var bytes = reopened.ReadAll();
                const int size = 256 * 1024;
                Assert.Equal(0, bytes.Length % size);
                var count = bytes.Length / size;
                Assert.True(count > 0);
                for (var i = 0; i < count; i++)
                {
                    Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * size, 4)));
                    Assert.False(bytes.AsSpan(i * size + 4, size - 4).ContainsAnyExcept((byte)i));
                }
                totalRecords += count;
            }
            File.Delete(file);
        }
        output.WriteLine($"KILL_TEST runs=200 torn_tails={torn} dropped_bytes={dropped} duplicates=0 complete_records={totalRecords}");
    }

    [WindowsFact]
    public void Flipped_middle_record_blocks_replay_and_preserves_corruption_evidence()
    {
        using var vault = new TestVault();
        var file = vault.File("journal.dat");
        using (var store = new DurableJournalStore(file)) { store.Append([1, 2, 3]); store.Append([4, 5, 6]); store.Append([7, 8, 9]); }
        var original = File.ReadAllBytes(file);
        original[19 + 16 + 1] ^= 0xff;
        File.WriteAllBytes(file, original);
        using (var reopened = new DurableJournalStore(file))
        {
            Assert.True(reopened.Recovery.CorruptionDetected);
            Assert.Equal(38, reopened.Recovery.DroppedBytes);
            Assert.Equal(original, File.ReadAllBytes(reopened.Recovery.EvidencePath!));
            Assert.Throws<InvalidDataException>(() => reopened.ReadAll());
            Assert.Throws<InvalidDataException>(() => reopened.Append([1]));
        }
        using var again = new DurableJournalStore(file);
        Assert.True(again.Recovery.CorruptionDetected);
        Assert.Throws<InvalidDataException>(() => again.ReadAll());
    }

    [WindowsFact]
    public void Every_physical_torn_tail_boundary_recovers_on_real_disk()
    {
        using var vault = new TestVault();
        var full = vault.File("complete.dat");
        using (var store = new DurableJournalStore(full)) { store.Append([1, 2, 3]); store.Append([4, 5, 6, 7]); }
        var bytes = File.ReadAllBytes(full);
        for (var length = 19; length < bytes.Length; length++)
        {
            var file = vault.File("cut.dat");
            File.WriteAllBytes(file, bytes[..length]);
            using var reopened = new DurableJournalStore(file);
            Assert.Equal(new byte[] { 1, 2, 3 }, reopened.ReadAll());
            Assert.Equal(length - 19, reopened.Recovery.DroppedBytes);
            reopened.Append([8]);
            Assert.Equal(new byte[] { 1, 2, 3, 8 }, reopened.ReadAll());
        }
    }
}
