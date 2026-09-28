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

    [WindowsFact]
    public void Readonly_attributes_follow_all_lock_ownership_states()
    {
        using var vault = new TestVault();
        File.WriteAllBytes(vault.File("part.txt"), [1]);
        using var policy = new ReadOnlyPolicy(vault.Paths);
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        {
            policy.Apply(TestVault.PathValue(), ownership);
            Assert.Equal(ownership == LockOwnership.OtherPerson, (File.GetAttributes(vault.File("part.txt")) & FileAttributes.ReadOnly) != 0);
        }
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
