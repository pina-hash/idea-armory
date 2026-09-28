using Armory.Core;
using Xunit.Abstractions;

namespace Armory.Platform.Windows.Tests;

public sealed class LocalStateTests(ITestOutputHelper output)
{
    [WindowsTheory]
    [InlineData("~$part.SLDPRT")][InlineData("project/~$part.SLDASM")][InlineData("desktop.ini")]
    [InlineData("folder/Thumbs.db")][InlineData(".armory/a")][InlineData("a/.ARMORY/b")]
    public void Ignored_paths_never_enter_inventory(string path) => Assert.True(VaultIgnore.IsIgnored(path));

    [WindowsFact]
    public void Cache_avoids_unchanged_hashes_and_aged_rescan_catches_restored_timestamp()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllText(file, "first");
        using var scanner = new LocalChangeDetector(vault.Paths, TimeSpan.Zero);
        var first = scanner.Scan();
        Assert.Equal(1, first.HashesComputed);
        Assert.Equal(0, scanner.Scan().HashesComputed);
        var timestamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "other");
        File.SetLastWriteTimeUtc(file, timestamp);
        var refreshed = scanner.Scan(fullRescan: true);
        Assert.Equal(1, refreshed.HashesComputed);
        Assert.NotEqual(first.Files[0].Hash, refreshed.Files[0].Hash);
    }

    [WindowsFact]
    public void Rename_uses_real_NTFS_identity()
    {
        using var vault = new TestVault();
        File.WriteAllText(vault.File("part.txt"), "bytes");
        using var scanner = new LocalChangeDetector(vault.Paths);
        var first = scanner.Scan();
        File.Move(vault.File("part.txt"), vault.File("renamed.txt"));
        var next = scanner.Scan();
        var rename = Assert.Single(next.Renames);
        Assert.Equal(first.Files[0].FileId, rename.FileId);
        Assert.Equal("part.txt", rename.Before.Value);
        Assert.Equal("renamed.txt", rename.After.Value);
    }

    [WindowsFact]
    public void Real_ignored_files_are_excluded_from_full_scan()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File(".armory"));
        foreach (var file in new[] { "~$part.SLDPRT", "desktop.ini", "Thumbs.db", ".armory/private.txt", "visible.txt" })
            File.WriteAllText(vault.File(file), "bytes");
        using var scanner = new LocalChangeDetector(vault.Paths);
        Assert.Equal("visible.txt", Assert.Single(scanner.Scan(fullRescan: true).Files).Path.Value);
    }

    [WindowsFact]
    public async Task Unreadable_file_is_reported_without_inventing_a_deletion()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllText(file, "bytes");
        using var scanner = new LocalChangeDetector(vault.Paths);
        var first = scanner.Scan();
        using var child = new ChildProcess("hold", file);
        Assert.Equal("READY", await child.ReadLine());
        var blocked = scanner.Scan(fullRescan: true);
        Assert.NotEmpty(blocked.Problems);
        Assert.Equal(first.Files[0], Assert.Single(blocked.Files));
    }

    [WindowsFact]
    public async Task Five_thousand_files_with_real_watcher_overflow_match_clean_scan()
    {
        using var vault = new TestVault();
        using var scanner = new LocalChangeDetector(vault.Paths, TimeSpan.Zero, 4096);
        using var stalled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockedOnce = 0;
        scanner.AfterHint = () =>
        {
            if (Interlocked.Exchange(ref blockedOnce, 1) == 0)
            { stalled.Set(); release.Wait(TimeSpan.FromSeconds(60)); }
        };
        File.WriteAllText(vault.File("trigger.txt"), "trigger");
        Assert.True(stalled.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Parallel.For(0, 5000, i => File.WriteAllText(vault.File($"file-{i:D5}.txt"), $"created-{i}"));
        }
        finally { release.Set(); }
        for (var attempt = 0; scanner.OverflowCount == 0 && attempt < 100; attempt++) await Task.Delay(50);
        Assert.True(scanner.OverflowCount > 0, "The undersized watcher must actually overflow; an injected error is not sufficient.");
        var created = scanner.Scan();
        Assert.Equal(5001, created.Files.Count);
        Parallel.For(0, 5000, i => File.WriteAllText(vault.File($"file-{i:D5}.txt"), $"edited--{i}"));
        Parallel.For(0, 5000, i => File.Move(vault.File($"file-{i:D5}.txt"), vault.File($"renamed-{i:D5}.txt")));
        var renamed = scanner.Scan();
        Assert.Equal(5000, renamed.Renames.Count);
        Parallel.For(0, 2500, i => File.Delete(vault.File($"renamed-{i:D5}.txt")));
        var final = scanner.Scan(fullRescan: true);
        using var clean = new LocalChangeDetector(vault.Paths, TimeSpan.Zero);
        var truth = clean.Scan(fullRescan: true);
        Assert.Empty(final.Problems);
        Assert.Empty(truth.Problems);
        Assert.Equal(truth.Files.Select(f => (f.Path, f.FileId, f.Size, f.LastWriteUtc, f.Hash)),
            final.Files.Select(f => (f.Path, f.FileId, f.Size, f.LastWriteUtc, f.Hash)));
        Assert.Equal(2501, final.Files.Count);
        output.WriteLine($"CHANGE_TEST created=5000 edited=5000 renamed=5000 deleted=2500 actual_overflows={scanner.OverflowCount} final_files={final.Files.Count} matches_clean_scan=true");
    }

    [WindowsFact]
    public void Emoji_case_250_character_path_and_reserved_name_on_real_disk()
    {
        using var vault = new TestVault(32700);
        Assert.True(vault.Paths.TryResolve("😀.txt", out var emoji, out _));
        File.WriteAllText(emoji!, "emoji");
        Assert.Equal("emoji", File.ReadAllText(emoji!));
        File.WriteAllText(vault.File("Case.txt"), "first");
        File.WriteAllText(vault.File("case.TXT"), "second");
        Assert.Single(Directory.EnumerateFiles(vault.Root, "*ase.*"));
        var leaf = new string('p', 250 - vault.Root.Length - 1 - 4) + ".txt";
        Assert.True(vault.Paths.TryResolve(leaf, out var longPath, out var problem), problem);
        File.WriteAllText(longPath!, "long");
        Assert.Equal(250, longPath!.Length - 4);
        Assert.Equal("long", File.ReadAllText(longPath));
        Assert.False(vault.Paths.TryResolve("CON.txt", out _, out var reserved));
        Assert.Contains("reserved Windows device", reserved);
        Assert.DoesNotContain(Directory.EnumerateFiles(vault.Root), f => Path.GetFileName(f) == "CON.txt");
    }

    [WindowsFact]
    public void Durable_snapshots_recover_each_save_in_capture_order()
    {
        using var vault = new TestVault();
        using (var snapshots = new DurableSnapshotStore(vault.Paths))
        {
            snapshots.Capture("device:1", TestVault.PathValue(), "Alex", new MemoryStream([1]));
            snapshots.Capture("device:2", TestVault.PathValue(), "Alex", new MemoryStream([2]));
            Assert.Throws<InvalidDataException>(() => snapshots.Capture("device:1", TestVault.PathValue(), "Alex", new MemoryStream([3])));
        }
        using var recovered = new DurableSnapshotStore(vault.Paths);
        Assert.Equal(["device:1", "device:2"], recovered.Enumerate().Select(s => s.Id));
        using var saved = recovered.OpenRead("device:1");
        Assert.Equal(1, saved.ReadByte());
        using var journalStore = new DurableJournalStore(vault.File("save-journal"));
        var journal = new OfflineJournal(journalStore);
        new SaveRecorder(recovered, journal).Recover();
        Assert.Equal(2, journal.Read().Entries.Count);
    }
}
