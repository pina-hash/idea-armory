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
        // Outside the racy window, so an unchanged file is trusted.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
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

    [WindowsFact]
    public void A_full_rescan_without_a_cache_age_rehashes_only_what_changed()
    {
        using var vault = new TestVault();
        foreach (var i in Enumerable.Range(0, 20)) File.WriteAllText(vault.File($"part-{i:D2}.txt"), "bytes " + i);
        foreach (var file in Directory.EnumerateFiles(vault.Root)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        using var scanner = new LocalChangeDetector(vault.Paths);
        Assert.Equal(20, scanner.Scan().HashesComputed);
        Assert.Equal(0, scanner.Scan(fullRescan: true).HashesComputed);
        File.WriteAllText(vault.File("part-03.txt"), "changed bytes");
        File.SetLastWriteTimeUtc(vault.File("part-03.txt"), DateTime.UtcNow.AddMinutes(-1).AddSeconds(5));
        Assert.Equal(1, scanner.Scan(fullRescan: true).HashesComputed);
    }

    [WindowsFact]
    public void An_edit_inside_the_racy_window_is_rehashed_even_with_the_same_size_and_time()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllText(file, "first");
        var written = File.GetLastWriteTimeUtc(file);
        using var scanner = new LocalChangeDetector(vault.Paths);
        var first = scanner.Scan();
        // Same size, same last-write time, within two seconds of the hash: not trusted.
        File.WriteAllText(file, "other");
        File.SetLastWriteTimeUtc(file, written);
        var second = scanner.Scan();
        Assert.Equal(1, second.HashesComputed);
        Assert.NotEqual(first.Files[0].Hash, second.Files[0].Hash);
        Assert.Equal(TestVault.Hash("other"u8.ToArray()), second.Files[0].Hash);
    }

    [WindowsFact]
    public void A_folder_rename_is_one_move_by_directory_id_and_rehashes_nothing()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Robot/CopyDesignTemp/Sub/Deep"));
        Directory.CreateDirectory(vault.File("Robot/Other"));
        var inside = new[] { "Robot/CopyDesignTemp/a.txt", "Robot/CopyDesignTemp/Sub/b.txt", "Robot/CopyDesignTemp/Sub/Deep/c.txt" };
        foreach (var name in inside) File.WriteAllText(vault.File(name), name);
        foreach (var file in Directory.EnumerateFiles(vault.Root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        using var scanner = new LocalChangeDetector(vault.Paths);
        var first = scanner.Scan();
        Assert.Equal(["Robot", "Robot/CopyDesignTemp", "Robot/CopyDesignTemp/Sub", "Robot/CopyDesignTemp/Sub/Deep", "Robot/Other"], first.Folders.Select(f => f.Path));
        Assert.All(first.Folders, f => Assert.NotNull(f.FolderId));

        Directory.Move(vault.File("Robot/CopyDesignTemp"), vault.File("Robot/Gearbox"));
        var moved = scanner.Scan();
        var move = Assert.Single(moved.FolderMoves);
        Assert.Equal("Robot/CopyDesignTemp", move.Before);
        Assert.Equal("Robot/Gearbox", move.After);
        Assert.Equal(first.Folders.Single(f => f.Path == "Robot/CopyDesignTemp").FolderId, move.FolderId);
        Assert.Empty(moved.Renames);
        Assert.Equal(0, moved.HashesComputed);
        Assert.Equal(3, moved.HashesReused);
        Assert.Equal(["Robot/Gearbox/a.txt", "Robot/Gearbox/Sub/b.txt", "Robot/Gearbox/Sub/Deep/c.txt"], moved.Files.Where(f => f.Path.Value.StartsWith("Robot/Gearbox/", StringComparison.Ordinal)).Select(f => f.Path.Value).Order(StringComparer.Ordinal));
        Assert.Empty(scanner.Scan().FolderMoves);

        // A rename above and a rename below at once: top-most first, each applicable in order.
        Directory.Move(vault.File("Robot"), vault.File("Robot 2028"));
        Directory.Move(vault.File("Robot 2028/Gearbox/Sub"), vault.File("Robot 2028/Gearbox/Shafts"));
        Directory.Move(vault.File("Robot 2028/Other"), vault.File("Robot 2028/Gearbox/Other"));
        var nested = scanner.Scan();
        Assert.Equal([("Robot", "Robot 2028"), ("Robot 2028/Other", "Robot 2028/Gearbox/Other"), ("Robot 2028/Gearbox/Sub", "Robot 2028/Gearbox/Shafts")],
            nested.FolderMoves.Select(m => (m.Before, m.After)));
        Assert.Equal(0, nested.HashesComputed);
        Assert.Empty(nested.Renames);

        // The agent's own move is absorbed: not reported, not re-hashed.
        Directory.Move(vault.File("Robot 2028/Gearbox"), vault.File("Robot 2028/Drivetrain"));
        scanner.Absorb("Robot 2028/Gearbox", "Robot 2028/Drivetrain");
        var absorbed = scanner.Scan();
        Assert.Empty(absorbed.FolderMoves);
        Assert.Empty(absorbed.Renames);
        Assert.Equal(0, absorbed.HashesComputed);
        Assert.Equal(0, absorbed.HashesReused);
    }

    [WindowsFact]
    public void Hashing_never_blocks_a_rename_of_the_file_or_its_folder()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Gearbox"));
        File.WriteAllText(vault.File("Gearbox/part.txt"), "bytes");
        using var scanner = new LocalChangeDetector(vault.Paths);
        Exception? refused = null;
        var renamed = 0;
        scanner.WhileHashing = _ =>
        {
            if (Interlocked.Exchange(ref renamed, 1) != 0) return;
            try { Directory.Move(vault.File("Gearbox"), vault.File("Drivetrain")); }
            catch (Exception error) { refused = error; }
        };
        _ = scanner.Scan();
        Assert.Null(refused);
        Assert.True(File.Exists(vault.File("Drivetrain/part.txt")));
        scanner.WhileHashing = null;
        Assert.Equal("Drivetrain/part.txt", Assert.Single(scanner.Scan().Files).Path.Value);
    }

    [WindowsFact]
    public async Task Markers_attribute_changes_and_folder_events_wake_the_engine_but_private_files_do_not()
    {
        using var vault = new TestVault();
        File.WriteAllText(vault.File("part.SLDPRT"), "bytes");
        Directory.CreateDirectory(vault.File(".armory"));
        using var scanner = new LocalChangeDetector(vault.Paths);
        async Task<bool> Wakes(Action change)
        {
            await Task.Delay(300);
            var before = scanner.HintCount;
            change();
            for (var i = 0; i < 40 && scanner.HintCount == before; i++) await Task.Delay(50);
            return scanner.HintCount != before;
        }
        Assert.True(await Wakes(() => File.WriteAllBytes(vault.File("~$part.SLDPRT"), [0])));
        Assert.True(await Wakes(() => File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.ReadOnly)));
        Assert.True(await Wakes(() => Directory.CreateDirectory(vault.File("Gearbox"))));
        Assert.False(await Wakes(() => File.WriteAllBytes(vault.File(".armory/state.json"), [0])));
        Assert.False(await Wakes(() => File.WriteAllBytes(vault.File("desktop.ini"), [0])));
        var scan = scanner.Scan();
        Assert.Equal(["~$part.SLDPRT"], scan.Markers);
        Assert.True(Assert.Single(scan.Files).ReadOnly);
    }
}
