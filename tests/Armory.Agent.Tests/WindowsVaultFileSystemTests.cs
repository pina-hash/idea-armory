using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.Agent.Tests;

// Real NTFS folders under %TEMP%. Every test owns a fresh vault and disposes the adapter
// before the folder is removed.
public sealed class WindowsVaultFileSystemTests
{
    private static readonly byte[] A = [1, 2, 3, 4];
    private static readonly byte[] B = [5, 6, 7];
    private static readonly byte[] C = [8, 9];

    [WindowsFact]
    public void Scan_reports_files_and_markers_and_ignores_office_files_and_the_private_folder()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot 2027/Drivetrain"));
        File.WriteAllBytes(vault.File("Robot 2027/Drivetrain/Gearbox.SLDASM"), A);
        File.WriteAllBytes(vault.File("Robot 2027/Drivetrain/~$Gearbox.SLDASM"), [0]);
        File.SetAttributes(vault.File("Robot 2027/Drivetrain/~$Gearbox.SLDASM"), FileAttributes.Hidden);
        File.WriteAllBytes(vault.File("~$Top.SLDPRT"), [0]);
        File.WriteAllBytes(vault.File("desktop.ini"), [0]);
        File.WriteAllBytes(vault.File("Robot 2027/Thumbs.db"), [0]);
        using var files = new WindowsVaultFileSystem(vault.Root);
        File.WriteAllBytes(vault.File(".armory/~$private.txt"), [0]);
        File.WriteAllBytes(vault.File(".armory/notes.txt"), [0]);

        var scan = files.Scan();

        var file = Assert.Single(scan.Files);
        Assert.Equal("Robot 2027/Drivetrain/Gearbox.SLDASM", file.Path.Value);
        Assert.Equal(TempFolder.Hash(A), file.Hash);
        Assert.Equal(A.Length, file.Size);
        Assert.Equal(["Robot 2027/Drivetrain/~$Gearbox.SLDASM", "~$Top.SLDPRT"], scan.Markers);
        Assert.Empty(scan.Problems);
        Assert.Equal(Path.GetFullPath(vault.Root), files.Root);
    }

    [WindowsFact]
    public void Replace_refuses_a_destination_that_changed_and_keeps_its_bytes()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var outcome = files.Replace(TempFolder.PathValue("part.SLDPRT"), TempFolder.Hash(B), new MemoryStream(C));
        Assert.False(outcome.Succeeded);
        Assert.Equal(A, File.ReadAllBytes(vault.File("part.SLDPRT")));
        Assert.True(files.Replace(TempFolder.PathValue("new.SLDPRT"), null, new MemoryStream(C)).Succeeded);
        Assert.False(files.Replace(TempFolder.PathValue("new.SLDPRT"), null, new MemoryStream(B)).Succeeded);
        Assert.Equal(C, File.ReadAllBytes(vault.File("new.SLDPRT")));
    }

    [WindowsFact]
    public void Replace_over_a_read_only_file_succeeds_and_restores_read_only()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.ReadOnly);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var outcome = files.Replace(TempFolder.PathValue("part.SLDPRT"), TempFolder.Hash(A), new MemoryStream(C));
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(C, File.ReadAllBytes(vault.File("part.SLDPRT")));
        Assert.True((File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) != 0);
    }

    [WindowsFact]
    public void A_refused_replace_never_leaves_read_only_cleared()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.ReadOnly);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var outcome = files.Replace(TempFolder.PathValue("part.SLDPRT"), TempFolder.Hash(B), new MemoryStream(C));
        Assert.False(outcome.Succeeded);
        Assert.Equal(A, File.ReadAllBytes(vault.File("part.SLDPRT")));
        Assert.True((File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) != 0);
    }

    [WindowsFact]
    public void Replace_refuses_an_open_destination()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        using (new FileStream(vault.File("part.SLDPRT"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(files.IsOpen(TempFolder.PathValue("part.SLDPRT")));
            Assert.False(files.Replace(TempFolder.PathValue("part.SLDPRT"), TempFolder.Hash(A), new MemoryStream(C)).Succeeded);
        }
        Assert.False(files.IsOpen(TempFolder.PathValue("part.SLDPRT")));
        Assert.Equal(A, File.ReadAllBytes(vault.File("part.SLDPRT")));
    }

    [WindowsFact]
    public void MoveToRecovery_moves_into_the_recovery_folder_and_never_deletes()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot"));
        File.WriteAllBytes(vault.File("Robot/part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var path = TempFolder.PathValue("Robot/part.SLDPRT");

        Assert.False(files.MoveToRecovery(path, TempFolder.Hash(B)).Succeeded);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Robot/part.SLDPRT")));
        using (new FileStream(vault.File("Robot/part.SLDPRT"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(files.MoveToRecovery(path, TempFolder.Hash(A)).Succeeded);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Robot/part.SLDPRT")));

        var outcome = files.MoveToRecovery(path, TempFolder.Hash(A));
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.False(File.Exists(vault.File("Robot/part.SLDPRT")));
        var kept = Assert.Single(Directory.EnumerateFiles(vault.File(".armory/recovery"), "part.SLDPRT", SearchOption.AllDirectories));
        Assert.Equal(A, File.ReadAllBytes(kept));
        Assert.Matches(@"[\\/]recovery[\\/]\d{8}-\d{6}(-\d+)?[\\/]Robot[\\/]part\.SLDPRT$", kept);
        Assert.Empty(files.Scan().Files);
    }

    [WindowsFact]
    public void Move_checks_the_hash_and_never_overwrites()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("a.SLDPRT"), A);
        File.WriteAllBytes(vault.File("taken.SLDPRT"), B);
        using var files = new WindowsVaultFileSystem(vault.Root);
        Assert.False(files.Move(TempFolder.PathValue("a.SLDPRT"), TempFolder.PathValue("taken.SLDPRT"), TempFolder.Hash(A)).Succeeded);
        Assert.False(files.Move(TempFolder.PathValue("a.SLDPRT"), TempFolder.PathValue("Moved/b.SLDPRT"), TempFolder.Hash(B)).Succeeded);
        Assert.False(Directory.Exists(vault.File("Moved")), "A refused move must not create its target folder.");
        var outcome = files.Move(TempFolder.PathValue("a.SLDPRT"), TempFolder.PathValue("Moved/b.SLDPRT"), TempFolder.Hash(A));
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Moved/b.SLDPRT")));
        Assert.Equal(B, File.ReadAllBytes(vault.File("taken.SLDPRT")));
        Assert.False(File.Exists(vault.File("a.SLDPRT")));
    }

    // The v2 rule (CheckoutRules.IsReadOnlyOnDisk): read-only unless THIS device holds the
    // file's check out. Free, OtherPerson and MyOtherDevice are read-only; only ThisDevice is
    // writable, and the single and batch calls agree.
    [WindowsFact]
    public void ApplyLockAttribute_sets_and_clears_read_only()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var path = TempFolder.PathValue("part.SLDPRT");
        bool ReadOnly() => (File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) != 0;
        files.ApplyLockAttribute(path, LockOwnership.OtherPerson);
        Assert.True(ReadOnly());
        files.ApplyLockAttribute(path, LockOwnership.ThisDevice);
        Assert.False(ReadOnly());
        files.ApplyLockAttribute(path, LockOwnership.Free);
        Assert.True(ReadOnly());
        files.ApplyLockAttribute(path, LockOwnership.ThisDevice);
        files.ApplyLockAttribute(path, LockOwnership.MyOtherDevice);
        Assert.True(ReadOnly());
        files.ApplyLockAttribute(path, LockOwnership.ThisDevice);
        Assert.False(ReadOnly());
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        {
            files.ApplyLockAttributes([(path, ownership)]);
            Assert.Equal(ownership != LockOwnership.ThisDevice, ReadOnly());
        }
        Assert.Equal(File.GetAttributes(vault.File("part.SLDPRT")).HasFlag(FileAttributes.ReadOnly), Assert.Single(files.Scan().Files).ReadOnly);
    }

    [WindowsFact]
    public void Staging_files_live_in_the_private_folder_and_are_cleaned_at_startup()
    {
        using var vault = new TempFolder();
        string name;
        using (var files = new WindowsVaultFileSystem(vault.Root))
        {
            using (var staging = files.CreateStaging(out name)) staging.Write(C);
            Assert.True(File.Exists(vault.File(".armory/staging/" + name)));
            files.DeleteStaging(name);
            Assert.False(File.Exists(vault.File(".armory/staging/" + name)));
            using (files.CreateStaging(out name)) { }
            Assert.Throws<ArgumentException>(() => files.DeleteStaging("../part.SLDPRT"));
            files.EnsureFolder("Robot 2027/Intake");
            Assert.True(Directory.Exists(vault.File("Robot 2027/Intake")));
        }
        using (new WindowsVaultFileSystem(vault.Root))
            Assert.False(File.Exists(vault.File(".armory/staging/" + name)));
    }

    [WindowsFact]
    public void ApplyLockAttributes_applies_the_rule_to_a_batch_and_survives_a_restart()
    {
        using var vault = new TempFolder();
        var names = Enumerable.Range(0, 50).Select(i => $"Robot/part-{i:D2}.SLDPRT").ToArray();
        Directory.CreateDirectory(vault.File("Robot"));
        foreach (var name in names) File.WriteAllBytes(vault.File(name), A);
        using (var files = new WindowsVaultFileSystem(vault.Root))
        {
            files.ApplyLockAttributes(names.Select((name, i) => (TempFolder.PathValue(name), i == 0 ? LockOwnership.ThisDevice : LockOwnership.Free)).ToArray());
            Assert.False(File.GetAttributes(vault.File(names[0])).HasFlag(FileAttributes.ReadOnly));
            Assert.All(names.Skip(1), name => Assert.True(File.GetAttributes(vault.File(name)).HasFlag(FileAttributes.ReadOnly)));
            var scan = files.Scan();
            Assert.Equal(names.Skip(1).Order(StringComparer.OrdinalIgnoreCase), scan.Files.Where(f => f.ReadOnly).Select(f => f.Path.Value).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(scan.Problems);
        }
        // A bit cleared by hand comes back from the durable intent at the next start.
        File.SetAttributes(vault.File(names[1]), FileAttributes.Normal);
        using (new WindowsVaultFileSystem(vault.Root))
            Assert.True(File.GetAttributes(vault.File(names[1])).HasFlag(FileAttributes.ReadOnly));
    }

    // 0.3.3: a check out or a check in of many files sets their bits in one batch (one manifest
    // write), before it returns, and says which it could not set (none here).
    [WindowsFact]
    public void ApplyLockAttributesNow_sets_a_thousand_bits_in_one_call()
    {
        using var vault = new TempFolder();
        var names = Enumerable.Range(0, 1000).Select(i => $"Robot/part-{i:D4}.SLDPRT").ToArray();
        Directory.CreateDirectory(vault.File("Robot"));
        foreach (var name in names) File.WriteAllBytes(vault.File(name), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(files.ApplyLockAttributesNow([.. names.Select(name => (TempFolder.PathValue(name), LockOwnership.Free))]));
        watch.Stop();
        Assert.All(names, name => Assert.True(File.GetAttributes(vault.File(name)).HasFlag(FileAttributes.ReadOnly)));
        Assert.Empty(files.ApplyLockAttributesNow([.. names.Select(name => (TempFolder.PathValue(name), LockOwnership.ThisDevice))]));
        Assert.All(names, name => Assert.False(File.GetAttributes(vault.File(name)).HasFlag(FileAttributes.ReadOnly)));
        Assert.Empty(files.Scan().Problems);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"1,000 bits took {watch.Elapsed.TotalSeconds:F1} s");
    }

    // A file the scan hashed is known unchanged without reading it again while nothing touched it
    // (check out reuses the scan's hash, 0.3.3); a write since, or a hash taken right after a write,
    // is not trusted.
    [WindowsFact]
    public void A_file_is_unchanged_since_the_scan_until_it_is_written()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot"));
        File.WriteAllBytes(vault.File("Robot/plate.SLDPRT"), A);
        File.SetLastWriteTimeUtc(vault.File("Robot/plate.SLDPRT"), DateTime.UtcNow.AddMinutes(-5));
        using var files = new WindowsVaultFileSystem(vault.Root);
        var scanned = Assert.Single(files.Scan().Files);
        Assert.NotNull(scanned.Stamp);
        Assert.True(files.UnchangedSinceScan(scanned));
        File.WriteAllBytes(vault.File("Robot/plate.SLDPRT"), B);
        Assert.False(files.UnchangedSinceScan(scanned));
        var again = Assert.Single(files.Scan().Files);
        Assert.False(files.UnchangedSinceScan(again)); // hashed within two seconds of its write
    }

    [WindowsFact]
    public void Scan_reports_the_read_only_bit_and_its_change()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        Assert.False(Assert.Single(files.Scan().Files).ReadOnly);
        File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.ReadOnly);
        Assert.True(Assert.Single(files.Scan().Files).ReadOnly);
        File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.Normal);
        var cleared = Assert.Single(files.Scan().Files);
        Assert.False(cleared.ReadOnly);
        Assert.Equal(TempFolder.Hash(A), cleared.Hash);
    }

    [WindowsFact]
    public void Replace_with_read_only_never_lets_the_new_bytes_appear_writable()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("old.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        // A download of a new file and a newer version over a writable file both land read-only.
        Assert.True(files.Replace(TempFolder.PathValue("new.SLDPRT"), null, new MemoryStream(C), readOnly: true).Succeeded);
        Assert.True(File.GetAttributes(vault.File("new.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(C, File.ReadAllBytes(vault.File("new.SLDPRT")));
        var outcome = files.Replace(TempFolder.PathValue("old.SLDPRT"), TempFolder.Hash(A), new MemoryStream(B), readOnly: true);
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.True(File.GetAttributes(vault.File("old.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(B, File.ReadAllBytes(vault.File("old.SLDPRT")));
        // Without readOnly a writable destination stays writable.
        Assert.True(files.Replace(TempFolder.PathValue("plain.SLDPRT"), null, new MemoryStream(C)).Succeeded);
        Assert.False(File.GetAttributes(vault.File("plain.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        // A refused read-only download leaves no staged file behind (the staged copy was read-only).
        Assert.False(files.Replace(TempFolder.PathValue("new.SLDPRT"), null, new MemoryStream(B), readOnly: true).Succeeded);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public void Scan_reports_folders_and_a_folder_rename_as_one_move_by_directory_id()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot 2027/CopyDesignTemp/Sub"));
        Directory.CreateDirectory(vault.File("Robot 2027/Empty"));
        File.WriteAllBytes(vault.File("Robot 2027/CopyDesignTemp/Gearbox.SLDASM"), A);
        File.WriteAllBytes(vault.File("Robot 2027/CopyDesignTemp/Sub/Shaft.SLDPRT"), B);
        File.WriteAllBytes(vault.File("Robot 2027/CopyDesignTemp/Sub/Gear.SLDPRT"), C);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var first = files.Scan();
        Assert.Equal(["Robot 2027", "Robot 2027/CopyDesignTemp", "Robot 2027/CopyDesignTemp/Sub", "Robot 2027/Empty"], first.Folders);
        // The first scan of a new vault has nothing to compare with: it cannot tell, which is
        // not the same as "nothing moved".
        Assert.Null(first.FolderMoves);
        Assert.Null(first.Renames);

        Directory.Move(vault.File("Robot 2027/CopyDesignTemp"), vault.File("Robot 2027/Gearbox"));
        File.Move(vault.File("Robot 2027/Gearbox/Sub/Gear.SLDPRT"), vault.File("Robot 2027/Gearbox/Sub/Spur Gear.SLDPRT"));
        var renamed = files.Scan();
        Assert.Equal([new FolderMove("Robot 2027/CopyDesignTemp", "Robot 2027/Gearbox")], renamed.FolderMoves);
        // Only the file that also moved on its own is a rename, from where the folder move left it.
        var rename = Assert.Single(renamed.Renames!);
        Assert.Equal("Robot 2027/Gearbox/Sub/Gear.SLDPRT", rename.From.Value);
        Assert.Equal("Robot 2027/Gearbox/Sub/Spur Gear.SLDPRT", rename.To.Value);
        Assert.Equal(["Robot 2027", "Robot 2027/Empty", "Robot 2027/Gearbox", "Robot 2027/Gearbox/Sub"], renamed.Folders);
        Assert.Equal(first.Files.Select(f => f.Hash).Order(), renamed.Files.Select(f => f.Hash).Order());
        // Reported once.
        Assert.Empty(files.Scan().FolderMoves!);
    }

    // The review's gap: the destination's bit used to be cleared before the new bytes were even
    // staged, so a file nobody checked out was writable for as long as a large download took
    // to copy, hash and check. It stays read-only the whole time now.
    [WindowsFact]
    public void Replace_keeps_a_read_only_file_read_only_while_the_new_bytes_are_staged()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        File.SetAttributes(vault.File("part.SLDPRT"), FileAttributes.ReadOnly);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var bytes = new byte[8 << 20];
        new Random(7).NextBytes(bytes);
        var content = new WatchingStream(bytes, () => File.GetAttributes(vault.File("part.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        var outcome = files.Replace(TempFolder.PathValue("part.SLDPRT"), TempFolder.Hash(A), content);
        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.True(content.Seen.Count > 10);
        Assert.All(content.Seen, Assert.True);
        Assert.True(File.GetAttributes(vault.File("part.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(bytes, File.ReadAllBytes(vault.File("part.SLDPRT")));
    }

    // Under v2 a read-only bit means "the server has this file". An intent belongs to the file
    // it was made for: when the file leaves its path (moved to recovery, renamed by the agent,
    // deleted in Explorer), a new file at that path is one the server does not have and stays
    // writable after a restart, while the files the server has get their bits back.
    [WindowsFact]
    public void Read_only_intents_follow_the_file_not_the_path()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot"));
        string[] names = ["Robot/recovered.SLDPRT", "Robot/renamed.SLDPRT", "Robot/deleted.SLDPRT", "Robot/replaced.SLDPRT"];
        foreach (var name in names) File.WriteAllBytes(vault.File(name), A);
        bool ReadOnly(string name) => File.GetAttributes(vault.File(name)).HasFlag(FileAttributes.ReadOnly);
        using (var files = new WindowsVaultFileSystem(vault.Root))
        {
            files.ApplyLockAttributes([.. names.Select(name => (TempFolder.PathValue(name), LockOwnership.Free))]);
            Assert.All(names, name => Assert.True(ReadOnly(name)));
            Assert.True(files.MoveToRecovery(TempFolder.PathValue("Robot/recovered.SLDPRT"), TempFolder.Hash(A)).Succeeded);
            Assert.True(files.Move(TempFolder.PathValue("Robot/renamed.SLDPRT"), TempFolder.PathValue("Robot/Plate.SLDPRT"), TempFolder.Hash(A)).Succeeded);
            File.SetAttributes(vault.File("Robot/deleted.SLDPRT"), FileAttributes.Normal);
            File.Delete(vault.File("Robot/deleted.SLDPRT"));
            // A download replaces the fourth file: its intent follows the new bytes.
            Assert.True(files.Replace(TempFolder.PathValue("Robot/replaced.SLDPRT"), TempFolder.Hash(A), new MemoryStream(B), readOnly: true).Succeeded);
            // A file checked out here (writable) is deleted in Explorer, and the server's version
            // comes down again, read-only: the old file's "writable" intent must not follow it.
            File.WriteAllBytes(vault.File("Robot/mine.SLDPRT"), A);
            files.ApplyLockAttribute(TempFolder.PathValue("Robot/mine.SLDPRT"), LockOwnership.ThisDevice);
            File.Delete(vault.File("Robot/mine.SLDPRT"));
            Assert.True(files.Replace(TempFolder.PathValue("Robot/mine.SLDPRT"), null, new MemoryStream(B), readOnly: true).Succeeded);
            // New files the server does not have (a re-added copy, a draft) at the old paths.
            foreach (var name in names.Take(3)) File.WriteAllBytes(vault.File(name), C);
        }
        // While Armory was closed, someone cleared the bits of the two files the server has.
        File.SetAttributes(vault.File("Robot/Plate.SLDPRT"), FileAttributes.Normal);
        File.SetAttributes(vault.File("Robot/replaced.SLDPRT"), FileAttributes.Normal);
        using (var restarted = new WindowsVaultFileSystem(vault.Root))
        {
            Assert.All(names.Take(3), name => Assert.False(ReadOnly(name), name));
            Assert.True(ReadOnly("Robot/Plate.SLDPRT"));
            Assert.True(ReadOnly("Robot/replaced.SLDPRT"));
            Assert.True(ReadOnly("Robot/mine.SLDPRT"));
            Assert.Empty(restarted.Scan().Problems);
        }
        // The manifest holds the two files that are still there, and nothing else.
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(vault.File(".armory/read-only.json")));
        Assert.Equal(["Robot/Plate.SLDPRT", "Robot/replaced.SLDPRT"],
            manifest.RootElement.GetProperty("intents").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [WindowsFact]
    public void MoveFolder_moves_a_closed_folder_and_refuses_open_files_existing_targets_and_long_paths()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot 2027/Gearbox/Sub"));
        Directory.CreateDirectory(vault.File("Robot 2027/Taken"));
        File.WriteAllBytes(vault.File("Robot 2027/Gearbox/Sub/Shaft.SLDPRT"), A);
        File.SetAttributes(vault.File("Robot 2027/Gearbox/Sub/Shaft.SLDPRT"), FileAttributes.ReadOnly);
        using var files = new WindowsVaultFileSystem(vault.Root);
        files.ApplyLockAttribute(TempFolder.PathValue("Robot 2027/Gearbox/Sub/Shaft.SLDPRT"), LockOwnership.Free);
        _ = files.Scan();

        Assert.False(files.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Taken").Succeeded);
        Assert.False(files.MoveFolder("Robot 2027/Missing", "Robot 2027/Other").Succeeded);
        Assert.False(files.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Gearbox/Sub/Inner").Succeeded);
        Assert.False(files.MoveFolder("", "Robot 2028").Succeeded);
        // The new folder name fits, but the file inside it would not.
        var room = 240 - (Path.GetFullPath(vault.Root).Length + 1 + "Robot 2027/".Length);
        var tooLong = files.MoveFolder("Robot 2027/Gearbox", "Robot 2027/" + new string('g', room - 5));
        Assert.False(tooLong.Succeeded);
        Assert.Contains("too long", tooLong.Problem);
        using (new FileStream(vault.File("Robot 2027/Gearbox/Sub/Shaft.SLDPRT"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var open = files.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Drivetrain");
            Assert.False(open.Succeeded);
            Assert.Contains("Shaft.SLDPRT", open.Problem);
        }
        Assert.True(File.Exists(vault.File("Robot 2027/Gearbox/Sub/Shaft.SLDPRT")));
        Assert.False(Directory.Exists(vault.File("Robot 2027/Drivetrain")));

        var moved = files.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Drivetrain/Gearbox");
        Assert.True(moved.Succeeded, moved.Problem);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Robot 2027/Drivetrain/Gearbox/Sub/Shaft.SLDPRT")));
        Assert.True(File.GetAttributes(vault.File("Robot 2027/Drivetrain/Gearbox/Sub/Shaft.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        Assert.False(Directory.Exists(vault.File("Robot 2027/Gearbox")));
        // The agent's own move is not reported as a student's.
        Assert.Empty(files.Scan().FolderMoves!);
        // A case-only rename is allowed.
        Assert.True(files.MoveFolder("Robot 2027/Drivetrain", "Robot 2027/drivetrain").Succeeded);
        Assert.Contains("drivetrain", Directory.GetDirectories(vault.File("Robot 2027")).Select(Path.GetFileName));
        // The read-only intent moved with the folder: a restart applies it at the new path.
        File.SetAttributes(vault.File("Robot 2027/drivetrain/Gearbox/Sub/Shaft.SLDPRT"), FileAttributes.Normal);
        files.Dispose();
        using var restarted = new WindowsVaultFileSystem(vault.Root);
        Assert.True(File.GetAttributes(vault.File("Robot 2027/drivetrain/Gearbox/Sub/Shaft.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
    }

    [WindowsFact]
    public void DeleteEmptyFolder_removes_only_folders_without_files()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot/Empty/Nested"));
        File.WriteAllBytes(vault.File("Robot/Empty/desktop.ini"), [0]);
        File.SetAttributes(vault.File("Robot/Empty/desktop.ini"), FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        File.WriteAllBytes(vault.File("Robot/Empty/Nested/Thumbs.db"), [0]);
        Directory.CreateDirectory(vault.File("Robot/Kept/Nested"));
        File.WriteAllBytes(vault.File("Robot/Kept/Nested/part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);

        Assert.False(files.DeleteEmptyFolder("Robot/Kept"));
        Assert.True(File.Exists(vault.File("Robot/Kept/Nested/part.SLDPRT")));
        Assert.False(files.DeleteEmptyFolder("Robot"));
        Assert.False(files.DeleteEmptyFolder(""));
        Assert.True(files.DeleteEmptyFolder("Robot/Empty"));
        Assert.False(Directory.Exists(vault.File("Robot/Empty")));
        Assert.True(files.DeleteEmptyFolder("Robot/Empty"), "A folder that is already gone is gone.");
        Assert.True(Directory.Exists(vault.File("Robot/Kept/Nested")));
    }

    [WindowsFact]
    public void CopyIn_copies_through_staging_and_never_overwrites()
    {
        using var vault = new TempFolder();
        using var outside = new TempFolder();
        File.WriteAllBytes(outside.File("Bracket.SLDPRT"), A);
        File.WriteAllBytes(outside.File("Other.SLDPRT"), B);
        File.SetAttributes(outside.File("Other.SLDPRT"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(outside.File("Folder"));
        using var files = new WindowsVaultFileSystem(vault.Root);

        var copied = files.CopyIn(outside.File("Bracket.SLDPRT"), TempFolder.PathValue("Robot 2027/New/Bracket.SLDPRT"));
        Assert.True(copied.Succeeded, copied.Problem);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Robot 2027/New/Bracket.SLDPRT")));
        Assert.Equal(A, File.ReadAllBytes(outside.File("Bracket.SLDPRT")));
        // Never overwrites, whatever the case of the name.
        Assert.False(files.CopyIn(outside.File("Other.SLDPRT"), TempFolder.PathValue("Robot 2027/New/bracket.sldprt")).Succeeded);
        Assert.Equal(A, File.ReadAllBytes(vault.File("Robot 2027/New/Bracket.SLDPRT")));
        // A copy of a read-only file arrives writable: the server does not have it yet.
        Assert.True(files.CopyIn(outside.File("Other.SLDPRT"), TempFolder.PathValue("Robot 2027/New/Other.SLDPRT")).Succeeded);
        Assert.False(File.GetAttributes(vault.File("Robot 2027/New/Other.SLDPRT")).HasFlag(FileAttributes.ReadOnly));
        Assert.False(files.CopyIn(outside.File("Missing.SLDPRT"), TempFolder.PathValue("Robot 2027/New/Missing.SLDPRT")).Succeeded);
        Assert.False(files.CopyIn(outside.File("Folder"), TempFolder.PathValue("Robot 2027/New/Folder")).Succeeded);
        Assert.False(files.CopyIn("relative.SLDPRT", TempFolder.PathValue("Robot 2027/New/relative.SLDPRT")).Succeeded);
        Assert.False(files.CopyIn(outside.File("Bracket.SLDPRT"), TempFolder.PathValue("Robot 2027/New/desktop.ini")).Succeeded);
        File.WriteAllBytes(vault.File(".armory/private.txt"), C);
        Assert.False(files.CopyIn(vault.File(".armory/private.txt"), TempFolder.PathValue("Robot 2027/New/private.txt")).Succeeded);
        // A source someone is writing is refused, not torn.
        using (new FileStream(outside.File("Bracket.SLDPRT"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.False(files.CopyIn(outside.File("Bracket.SLDPRT"), TempFolder.PathValue("Robot 2027/New/Writing.SLDPRT")).Succeeded);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/staging")));
        Assert.Equal(["Robot 2027/New/Bracket.SLDPRT", "Robot 2027/New/Other.SLDPRT"], files.Scan().Files.Select(f => f.Path.Value));
    }

    [WindowsFact]
    public void CopyIn_refuses_a_symbolic_link()
    {
        using var vault = new TempFolder();
        using var outside = new TempFolder();
        File.WriteAllBytes(outside.File("Target.SLDPRT"), A);
        try { File.CreateSymbolicLink(outside.File("Link.SLDPRT"), outside.File("Target.SLDPRT")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; } // needs Developer Mode or elevation
        using var files = new WindowsVaultFileSystem(vault.Root);
        Assert.False(files.CopyIn(outside.File("Link.SLDPRT"), TempFolder.PathValue("Link.SLDPRT")).Succeeded);
        Assert.False(File.Exists(vault.File("Link.SLDPRT")));
    }

    [WindowsFact]
    public void Launch_refuses_programs_and_scripts_and_opens_documents_through_the_shell()
    {
        using var vault = new TempFolder();
        Directory.CreateDirectory(vault.File("Robot"));
        foreach (var name in new[] { "Plate.SLDPRT", "tool.exe", "run.CMD", "setup.ps1", "go.lnk", "site.url", "x.bat", "y.vbs", "z.js", "w.msi", "v.hta", "u.reg", "t.scr" })
            File.WriteAllBytes(vault.File("Robot/" + name), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        List<System.Diagnostics.ProcessStartInfo> started = [];
        files.StartShell = started.Add;
        foreach (var name in new[] { "tool.exe", "run.CMD", "setup.ps1", "go.lnk", "site.url", "x.bat", "y.vbs", "z.js", "w.msi", "v.hta", "u.reg", "t.scr" })
        {
            var refused = files.Launch(TempFolder.PathValue("Robot/" + name));
            Assert.False(refused.Succeeded, name);
            Assert.Contains("runs a program", refused.Problem);
        }
        Assert.False(files.Launch(TempFolder.PathValue("Robot/Missing.SLDPRT")).Succeeded);
        Assert.Empty(started);

        Assert.True(files.Launch(TempFolder.PathValue("Robot/Plate.SLDPRT")).Succeeded);
        var start = Assert.Single(started);
        Assert.True(start.UseShellExecute);
        Assert.Equal("open", start.Verb);
        Assert.Equal(Path.Combine(Path.GetFullPath(vault.Root), "Robot", "Plate.SLDPRT"), start.FileName);
        Assert.DoesNotContain(@"\\?\", start.FileName);
        Assert.Equal(Path.Combine(Path.GetFullPath(vault.Root), "Robot"), start.WorkingDirectory);

        files.StartShell = _ => throw new System.ComponentModel.Win32Exception(1155);
        var none = files.Launch(TempFolder.PathValue("Robot/Plate.SLDPRT"));
        Assert.False(none.Succeeded);
        Assert.Equal("No program on this computer opens .SLDPRT files.", none.Problem);
        // A program that is there but did not answer the shell (a failed DDE conversation, a
        // missing DLL: SolidWorks not running yet) is opened through File Explorer instead, as a
        // double-click does (v0.2.1; v0.2.0 answered "Wait a moment" and opened nothing).
        foreach (var code in new[] { 1156, 1157 })
        {
            List<System.Diagnostics.ProcessStartInfo> tried = [];
            files.StartShell = s => { lock (tried) tried.Add(s); if (s.UseShellExecute) throw new System.ComponentModel.Win32Exception(code); };
            var opened = files.Launch(TempFolder.PathValue("Robot/Plate.SLDPRT"));
            Assert.True(opened.Succeeded, opened.Problem);
            SpinWait.SpinUntil(() => { lock (tried) return tried.Count == 2; }, TimeSpan.FromSeconds(5));
            lock (tried)
            {
                Assert.Equal(2, tried.Count);
                Assert.Equal("explorer.exe", tried[1].FileName);
                Assert.Equal("\"" + Path.Combine(Path.GetFullPath(vault.Root), "Robot", "Plate.SLDPRT") + "\"", tried[1].Arguments);
            }
        }
    }

    // A download body that looks at the destination every time Armory reads from it.
    private sealed class WatchingStream(byte[] bytes, Func<bool> look) : Stream
    {
        private int position;
        public List<bool> Seen { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Seen.Add(look());
            var count = Math.Min(buffer.Length, Math.Min(64 * 1024, bytes.Length - position));
            bytes.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
