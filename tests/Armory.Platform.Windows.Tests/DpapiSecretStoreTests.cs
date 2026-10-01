using System.Text;

namespace Armory.Platform.Windows.Tests;

public sealed class DpapiSecretStoreTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("{\"refresh_token\":\"plain-refresh-secret-5669\"}");

    [WindowsFact]
    public void Written_secret_reads_back_byte_for_byte()
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        store.Write("armory-session", Secret);
        Assert.Equal(Secret, store.Read("armory-session"));
        // A fresh instance (as after a restart) reads the same bytes.
        Assert.Equal(Secret, new DpapiSecretStore(vault.File("secrets")).Read("armory-session"));
        Assert.Empty(Directory.EnumerateFiles(vault.File("secrets"), "*.pending"));
    }

    [WindowsFact]
    public void File_on_disk_never_contains_the_plaintext()
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        store.Write("armory-session", Secret);
        var file = Assert.Single(Directory.EnumerateFiles(vault.File("secrets")));
        var raw = File.ReadAllBytes(file);
        Assert.Equal(-1, raw.AsSpan().IndexOf("plain-refresh-secret-5669"u8));
        Assert.Equal(-1, raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes("plain-refresh-secret-5669")));
    }

    [WindowsFact]
    public void Delete_removes_the_secret_and_a_missing_name_reads_null()
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        Assert.Null(store.Read("never-written"));
        store.Write("armory-session", Secret);
        store.Delete("armory-session");
        Assert.Null(store.Read("armory-session"));
        Assert.Empty(Directory.EnumerateFiles(vault.File("secrets")));
        store.Delete("armory-session");
    }

    [WindowsFact]
    public void Overwrite_replaces_the_old_secret()
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        store.Write("armory-session", Secret);
        store.Write("armory-session", [7, 8, 9]);
        Assert.Equal(new byte[] { 7, 8, 9 }, store.Read("armory-session"));
        Assert.Single(Directory.EnumerateFiles(vault.File("secrets")));
    }

    // Decision (documented on DpapiSecretStore): a corrupted blob reads as null, so the
    // computer shows "Connect this computer" instead of crashing; the problem is reported.
    [WindowsFact]
    public void Corrupted_file_reads_as_null_and_reports_a_problem()
    {
        using var vault = new TestVault();
        List<string> problems = [];
        var store = new DpapiSecretStore(vault.File("secrets"), problems.Add);
        store.Write("armory-session", Secret);
        var file = Assert.Single(Directory.EnumerateFiles(vault.File("secrets")));
        var raw = File.ReadAllBytes(file);
        raw[^5] ^= 0x5A;
        File.WriteAllBytes(file, raw);
        Assert.Null(store.Read("armory-session"));
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.Null(store.Read("armory-session"));
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.DoesNotContain("plain-refresh-secret", p));
    }

    [WindowsFact]
    public void Blob_copied_to_another_name_does_not_decrypt()
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        store.Write("armory-session", Secret);
        File.Copy(Path.Combine(vault.File("secrets"), "armory-session.secret"), Path.Combine(vault.File("secrets"), "other.secret"));
        Assert.Null(store.Read("other"));
    }

    [WindowsTheory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    public void Unsafe_names_are_refused(string name)
    {
        using var vault = new TestVault();
        var store = new DpapiSecretStore(vault.File("secrets"));
        Assert.Throws<ArgumentException>(() => store.Write(name, Secret));
        Assert.Throws<ArgumentException>(() => store.Read(name));
    }
}
