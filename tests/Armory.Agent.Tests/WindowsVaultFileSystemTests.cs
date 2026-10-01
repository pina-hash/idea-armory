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

    [WindowsFact]
    public void ApplyLockAttribute_sets_and_clears_read_only()
    {
        using var vault = new TempFolder();
        File.WriteAllBytes(vault.File("part.SLDPRT"), A);
        using var files = new WindowsVaultFileSystem(vault.Root);
        var path = TempFolder.PathValue("part.SLDPRT");
        files.ApplyLockAttribute(path, LockOwnership.OtherPerson);
        Assert.True((File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) != 0);
        files.ApplyLockAttribute(path, LockOwnership.ThisDevice);
        Assert.True((File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) == 0);
        files.ApplyLockAttribute(path, LockOwnership.OtherPerson);
        files.ApplyLockAttribute(path, LockOwnership.Free);
        Assert.True((File.GetAttributes(vault.File("part.SLDPRT")) & FileAttributes.ReadOnly) == 0);
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
}
