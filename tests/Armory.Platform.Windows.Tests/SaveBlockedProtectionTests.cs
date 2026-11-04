using Armory.Core;

namespace Armory.Platform.Windows.Tests;

public sealed class SaveBlockedProtectionTests
{
    [WindowsFact]
    public void Save_explanations_require_Armory_intent_for_this_file_identity_and_a_current_read_only_bit()
    {
        using var vault = new TestVault();
        var path = TestVault.PathValue(); var file = vault.File("part.txt");
        File.WriteAllText(file, "original");
        using var policy = new ReadOnlyPolicy(vault.Paths);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.False(policy.IsProtectedReadOnly(path)); // a Windows bit alone is not Armory intent
        policy.Apply(path, LockOwnership.Free);
        var writes = policy.AttributeWrites;
        Assert.True(policy.IsProtectedReadOnly(path));
        Assert.Equal(writes, policy.AttributeWrites); // read never applies or changes protection
        File.SetAttributes(file, FileAttributes.Normal);
        Assert.False(policy.IsProtectedReadOnly(path));
        File.Move(file, vault.File("original-kept.txt")); // keep original/id alive, so NTFS cannot reuse it
        File.WriteAllText(file, "new file at the old path");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.False(policy.IsProtectedReadOnly(path));
        Assert.Equal("new file at the old path", File.ReadAllText(file));
        Assert.Equal("original", File.ReadAllText(vault.File("original-kept.txt")));
        Assert.Equal(writes, policy.AttributeWrites);
    }
    [WindowsFact]
    public async Task Reading_protection_does_not_wait_for_another_durable_intent_write()
    {
        using var vault = new TestVault();
        File.WriteAllText(vault.File("part.txt"), "original");
        File.WriteAllText(vault.File("other.txt"), "other");
        using var policy = new ReadOnlyPolicy(vault.Paths);
        policy.Apply(TestVault.PathValue(), LockOwnership.Free);
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        policy.AfterIntentPersisted = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); };
        var writing = Task.Run(() => policy.Apply(TestVault.PathValue("other.txt"), LockOwnership.Free));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(policy.IsProtectedReadOnly(TestVault.PathValue()));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { finish.Set(); await writing; }
    }

}
