using Armory.Core;

namespace Armory.Platform.Windows.Tests;

public sealed class ReplaceAndLockTests
{
    [WindowsFact]
    public void Atomic_replace_succeeds_and_private_staging_is_hidden_and_empty()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1, 2, 3]);
        using var replace = new SafeFileReplace(vault.Paths);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1, 2, 3]), new MemoryStream([4, 5, 6]));
        Assert.True(result.Succeeded, result.Problem);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(vault.File("part.txt")));
        Assert.True((File.GetAttributes(vault.File(".armory")) & FileAttributes.Hidden) != 0);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task Other_process_exclusive_handle_refuses_replace_and_reports_process()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllBytes(file, [1, 2, 3]);
        using var child = new ChildProcess("hold", file);
        Assert.Equal("READY", await child.ReadLine());
        var status = new OpenFileDetector().Inspect(file);
        Assert.True(status.IsOpen);
        Assert.Contains(status.Processes, p => p.Id == child.Process.Id && !string.IsNullOrWhiteSpace(p.Name));
        using var replace = new SafeFileReplace(vault.Paths);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1, 2, 3]), new MemoryStream([4]));
        Assert.False(result.Succeeded);
        child.Kill();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public void Destination_saved_between_plan_and_replace_is_preserved()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using var replace = new SafeFileReplace(vault.Paths);
        replace.AfterStaging = _ => File.WriteAllBytes(vault.File("part.txt"), [2]);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([3]));
        Assert.False(result.Succeeded);
        Assert.Contains("changed", result.Problem);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(vault.File("part.txt")));
    }

    [WindowsFact]
    public void Unexpected_new_destination_is_never_overwritten()
    {
        using var vault = new TestVault();
        using var replace = new SafeFileReplace(vault.Paths);
        Assert.True(replace.Replace(TestVault.PathValue(), null, new MemoryStream([1])).Succeeded);
        Assert.False(replace.Replace(TestVault.PathValue(), null, new MemoryStream([2])).Succeeded);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(vault.File("part.txt")));
    }

    [WindowsFact]
    public void Failed_native_replace_preserves_readonly_destination_and_cleans_stage()
    {
        using var vault = new TestVault();
        var file = vault.File("part.txt");
        File.WriteAllBytes(file, [1]);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        using var replace = new SafeFileReplace(vault.Paths);
        Assert.False(replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([2])).Succeeded);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(file));
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task Process_crash_after_staging_preserves_old_bytes_and_restart_cleans_temp()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using (var child = new ChildProcess("stage", vault.Root, TestVault.Hash([1])))
        { Assert.Equal("STAGED", await child.ReadLine()); child.Kill(); }
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(vault.File("part.txt")));
        using var restart = new SafeFileReplace(vault.Paths);
        Assert.Equal(1, restart.CleanedOrphans);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    // The v2 rule: read-only unless THIS device holds the check out.
    [WindowsFact]
    public void Readonly_attributes_follow_all_lock_ownership_states()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        File.SetAttributes(vault.File("part.txt"), FileAttributes.Hidden);
        using var policy = new ReadOnlyPolicy(vault.Paths);
        foreach (var ownership in Enum.GetValues<LockOwnership>().Concat(Enum.GetValues<LockOwnership>().Reverse()))
        {
            policy.Apply(TestVault.PathValue(), ownership);
            Assert.Equal(ownership != LockOwnership.ThisDevice, (File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
            Assert.Equal(ownership != LockOwnership.ThisDevice, ReadOnlyPolicy.IsReadOnly(ownership));
            Assert.True(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.Hidden), "Other attributes are kept.");
        }
    }

    [WindowsFact]
    public void A_batch_of_read_only_intents_is_persisted_once_and_one_failure_never_stops_the_rest()
    {
        using var vault = new TestVault();
        var names = Enumerable.Range(0, 200).Select(i => $"part-{i:D3}.txt").ToArray();
        foreach (var name in names) File.WriteAllBytes(vault.File(name), [1]);
        using var policy = new ReadOnlyPolicy(vault.Paths);
        var persisted = 0;
        policy.AfterIntentPersisted = () => persisted++;
        var failed = policy.ApplyMany(names.Select((name, i) => (TestVault.PathValue(name), i % 2 == 0 ? LockOwnership.ThisDevice : LockOwnership.Free)).ToArray());
        Assert.Empty(failed);
        Assert.Equal(1, persisted);
        for (var i = 0; i < names.Length; i++)
            Assert.Equal(i % 2 != 0, File.GetAttributes(vault.File(names[i])).HasFlag(FileAttributes.ReadOnly));
        // Applying the same rule again changes no attribute (no change notification).
        var before = names.Select(name => File.GetLastWriteTimeUtc(vault.File(name))).ToArray();
        Assert.Empty(policy.ApplyMany(names.Select((name, i) => (TestVault.PathValue(name), i % 2 == 0 ? LockOwnership.ThisDevice : LockOwnership.Free)).ToArray()));
        Assert.Equal(before, names.Select(name => File.GetLastWriteTimeUtc(vault.File(name))));
        Assert.Equal(2, persisted);
        Assert.Empty(policy.ApplyMany([]));
        Assert.Equal(2, persisted);
        // A missing file is not an error: its intent waits in the manifest.
        Assert.Empty(policy.ApplyMany([(TestVault.PathValue("later.txt"), LockOwnership.Free)]));
        File.WriteAllBytes(vault.File("later.txt"), [1]);
        policy.Dispose();
        using var restarted = new ReadOnlyPolicy(vault.Paths);
        Assert.Empty(restarted.Recover(new Dictionary<VaultPath, LockOwnership>()));
        Assert.True(File.GetAttributes(vault.File("later.txt")).HasFlag(FileAttributes.ReadOnly));
    }

    [WindowsFact]
    public void A_moved_folder_keeps_its_read_only_intents()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Gearbox"));
        File.WriteAllBytes(vault.File("Gearbox/part.txt"), [1]);
        using (var policy = new ReadOnlyPolicy(vault.Paths))
        {
            policy.Apply(TestVault.PathValue("Gearbox/part.txt"), LockOwnership.Free);
            Directory.Move(vault.File("Gearbox"), vault.File("Drivetrain"));
            policy.Rekey("Gearbox", "Drivetrain");
        }
        File.SetAttributes(vault.File("Drivetrain/part.txt"), FileAttributes.Normal);
        using var restarted = new ReadOnlyPolicy(vault.Paths);
        restarted.Recover(new Dictionary<VaultPath, LockOwnership>());
        Assert.True(File.GetAttributes(vault.File("Drivetrain/part.txt")).HasFlag(FileAttributes.ReadOnly));
    }

    [WindowsFact]
    public void Replace_with_read_only_stages_a_read_only_copy_so_the_file_is_never_writable()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using var replace = new SafeFileReplace(vault.Paths);
        bool? stagedReadOnly = null;
        replace.AfterStaging = temp => stagedReadOnly = File.GetAttributes(temp).HasFlag(FileAttributes.ReadOnly);
        var result = replace.Replace(TestVault.PathValue(), TestVault.Hash([1]), new MemoryStream([2]), readOnly: true);
        Assert.True(result.Succeeded, result.Problem);
        Assert.True(stagedReadOnly);
        Assert.True(File.GetAttributes(vault.File("part.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(vault.File("part.txt")));
        // A refused read-only replace still removes its read-only staged copy.
        replace.AfterStaging = null;
        Assert.False(replace.Replace(TestVault.PathValue("new.txt"), TestVault.Hash([9]), new MemoryStream([3]), readOnly: true).Succeeded);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public void A_read_only_staged_orphan_is_cleaned_at_startup()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File(".armory/downloads"));
        File.WriteAllBytes(vault.File(".armory/downloads/orphan.pending"), [1]);
        File.SetAttributes(vault.File(".armory/downloads/orphan.pending"), FileAttributes.ReadOnly);
        using var replace = new SafeFileReplace(vault.Paths);
        Assert.Equal(1, replace.CleanedOrphans);
        Assert.Empty(Directory.EnumerateFiles(vault.File(".armory/downloads"), "*.pending"));
    }

    [WindowsFact]
    public async Task One_inspection_finds_the_open_file_in_a_folder()
    {
        using var vault = new TestVault();
        Directory.CreateDirectory(vault.File("Gearbox"));
        var files = Enumerable.Range(0, 30).Select(i => vault.File($"Gearbox/part-{i:D2}.txt")).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, [1]);
        var detector = new OpenFileDetector();
        Assert.False(detector.InspectAll(files, out var none).IsOpen);
        Assert.Null(none);
        using var child = new ChildProcess("hold", files[17]);
        Assert.Equal("READY", await child.ReadLine());
        var status = detector.InspectAll(files, out var first);
        Assert.True(status.IsOpen);
        Assert.Equal(files[17], first);
        Assert.Contains(status.Processes, p => p.Id == child.Process.Id);
    }

    [WindowsFact]
    public async Task Crash_mid_attribute_update_recovers_to_current_users_lock()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using (var child = new ChildProcess("readonly-crash", vault.Root))
        { Assert.Equal("PERSISTED", await child.ReadLine()); child.Kill(); }
        Assert.True((File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
        using var recovered = new ReadOnlyPolicy(vault.Paths);
        recovered.Recover(new Dictionary<VaultPath, LockOwnership> { [TestVault.PathValue()] = LockOwnership.ThisDevice });
        Assert.False((File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
    }
}
