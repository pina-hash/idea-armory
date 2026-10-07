using System.Text;
using Armory.Agent.Engine;
using Armory.Core;

namespace Armory.EndToEnd.Tests;

// The Linux double the end-to-end proof runs on keeps the Windows semantics of the v2
// platform members (docs/platform/read-only.md, docs/platform/vault-actions.md). No server
// needed, so these run everywhere.
public sealed class PortableVaultFileSystemTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "armory-portable-" + Guid.NewGuid().ToString("N"));
    private readonly PortableVaultFileSystem disk;
    private static readonly VaultPath Plate = PortableVaultFileSystem.P("Robot 2027/Gearbox/Plate.SLDPRT");

    public PortableVaultFileSystemTests() => disk = new PortableVaultFileSystem(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(disk.Full(path))!);
        File.WriteAllText(disk.Full(path), text);
    }
    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void The_read_only_bit_follows_the_v2_rule_and_is_reported_by_the_scan()
    {
        Write(Plate.Value, "v1");
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        {
            disk.ApplyLockAttribute(Plate, ownership);
            Assert.Equal(ownership != LockOwnership.ThisDevice, disk.IsReadOnly(Plate.Value));
            Assert.Equal(ownership != LockOwnership.ThisDevice, Assert.Single(disk.Scan().Files).ReadOnly);
            Assert.Equal(ownership, disk.Attributes[Plate.Value]);
        }
        disk.ApplyLockAttributes([(Plate, LockOwnership.Free)]);
        Assert.True(disk.IsReadOnly(Plate.Value));
        Assert.Equal(1, disk.AttributeBatches);
        // A missing file gets no bit, and a new file at a path never inherits an old file's bit.
        disk.ApplyLockAttribute(PortableVaultFileSystem.P("Robot 2027/Later.SLDPRT"), LockOwnership.Free);
        Assert.False(disk.IsReadOnly("Robot 2027/Later.SLDPRT"));
        disk.ClearReadOnly(Plate.Value);
        Assert.False(disk.IsReadOnly(Plate.Value));
    }

    [Fact]
    public void Replace_with_read_only_lands_read_only_and_a_read_only_file_stays_read_only()
    {
        Assert.True(disk.Replace(Plate, null, Bytes("v1"), readOnly: true).Succeeded);
        Assert.True(disk.IsReadOnly(Plate.Value));
        Assert.True(disk.Replace(Plate, Hash("v1"), Bytes("v2")).Succeeded);
        Assert.True(disk.IsReadOnly(Plate.Value));
        disk.ApplyLockAttribute(Plate, LockOwnership.ThisDevice);
        Assert.True(disk.Replace(Plate, Hash("v2"), Bytes("v3")).Succeeded);
        Assert.False(disk.IsReadOnly(Plate.Value));
        // The bit travels with a move and leaves with a recovery move.
        disk.ApplyLockAttribute(Plate, LockOwnership.OtherPerson);
        var moved = PortableVaultFileSystem.P("Robot 2027/Plate.SLDPRT");
        Assert.True(disk.Move(Plate, moved, Hash("v3")).Succeeded);
        Assert.True(disk.IsReadOnly(moved.Value));
        Assert.True(disk.MoveToRecovery(moved, Hash("v3")).Succeeded);
        Assert.False(disk.IsReadOnly(moved.Value));
    }

    [Fact]
    public void A_student_folder_rename_is_reported_once_and_refused_while_a_file_inside_is_open()
    {
        Write("Robot 2027/CopyDesignTemp/Gear.SLDPRT", "g");
        Write("Robot 2027/CopyDesignTemp/Sub/Shaft.SLDPRT", "s");
        Directory.CreateDirectory(disk.Full("Robot 2027/Empty"));
        disk.ApplyLockAttribute(PortableVaultFileSystem.P("Robot 2027/CopyDesignTemp/Gear.SLDPRT"), LockOwnership.Free);
        Assert.Equal(["Robot 2027", "Robot 2027/CopyDesignTemp", "Robot 2027/CopyDesignTemp/Sub", "Robot 2027/Empty"], disk.Scan().Folders);

        disk.Open("Robot 2027/CopyDesignTemp/Gear.SLDPRT");
        Assert.Throws<IOException>(() => disk.RenameFolderAsStudent("Robot 2027/CopyDesignTemp", "Robot 2027/Gearbox"));
        disk.Close("Robot 2027/CopyDesignTemp/Gear.SLDPRT");
        disk.RenameFolderAsStudent("Robot 2027/CopyDesignTemp", "Robot 2027/Gearbox");
        var scan = disk.Scan();
        Assert.Equal([new FolderMove("Robot 2027/CopyDesignTemp", "Robot 2027/Gearbox")], scan.FolderMoves);
        Assert.True(scan.Files.Single(f => f.Path.Value == "Robot 2027/Gearbox/Gear.SLDPRT").ReadOnly);
        Assert.Empty(disk.Scan().FolderMoves!);
    }

    [Fact]
    public void MoveFolder_refuses_an_open_file_an_existing_target_and_a_move_into_itself()
    {
        Write("Robot 2027/Gearbox/Gear.SLDPRT", "g");
        Directory.CreateDirectory(disk.Full("Robot 2027/Taken"));
        var gear = PortableVaultFileSystem.P("Robot 2027/Gearbox/Gear.SLDPRT");
        disk.ApplyLockAttribute(gear, LockOwnership.Free);
        disk.Open(gear.Value);
        Assert.False(disk.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Drivetrain").Succeeded);
        disk.Close(gear.Value);
        Assert.False(disk.MoveFolder("Robot 2027/Gearbox", "Robot 2027/taken").Succeeded);
        Assert.False(disk.MoveFolder("Robot 2027/Gearbox", "Robot 2027/Gearbox/Inner").Succeeded);
        Assert.False(disk.MoveFolder("Robot 2027/Missing", "Robot 2027/Other").Succeeded);
        Assert.True(disk.MoveFolder("Robot 2027/Gearbox", "Robot 2028/Gearbox").Succeeded);
        Assert.True(disk.IsReadOnly("Robot 2028/Gearbox/Gear.SLDPRT"));
        Assert.Equal(LockOwnership.Free, disk.Attributes["Robot 2028/Gearbox/Gear.SLDPRT"]);
        Assert.Equal([new FolderMove("Robot 2027/Gearbox", "Robot 2028/Gearbox")], disk.MovedFolders);
        // The agent's own move is not a student's.
        Assert.Empty(disk.Scan().FolderMoves!);
    }

    [Fact]
    public void DeleteEmptyFolder_CopyIn_and_Launch_keep_the_Windows_rules()
    {
        Directory.CreateDirectory(disk.Full("Robot 2027/Empty/Nested"));
        File.WriteAllText(disk.Full("Robot 2027/Empty/Thumbs.db"), "t");
        Write("Robot 2027/Kept/Gear.SLDPRT", "g");
        Assert.False(disk.DeleteEmptyFolder("Robot 2027/Kept"));
        Assert.True(disk.DeleteEmptyFolder("Robot 2027/Empty"));
        Assert.False(Directory.Exists(disk.Full("Robot 2027/Empty")));
        Assert.True(disk.DeleteEmptyFolder("Robot 2027/Empty"));

        var outside = Path.Combine(root + "-outside", "Bracket.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        try
        {
            File.WriteAllText(outside, "b");
            Assert.True(disk.CopyIn(outside, PortableVaultFileSystem.P("Robot 2027/Kept/Bracket.SLDPRT")).Succeeded);
            Assert.False(disk.CopyIn(outside, PortableVaultFileSystem.P("Robot 2027/Kept/bracket.sldprt")).Succeeded);
            Assert.False(disk.CopyIn(outside, PortableVaultFileSystem.P("Robot 2027/Kept/Gear.SLDPRT")).Succeeded);
            Assert.Equal("g", File.ReadAllText(disk.Full("Robot 2027/Kept/Gear.SLDPRT")));
        }
        finally { Directory.Delete(root + "-outside", recursive: true); }

        Write("Robot 2027/Kept/tool.exe", "x");
        Assert.False(disk.Launch(PortableVaultFileSystem.P("Robot 2027/Kept/tool.exe")).Succeeded);
        Assert.False(disk.Launch(PortableVaultFileSystem.P("Robot 2027/Kept/Missing.SLDPRT")).Succeeded);
        Assert.True(disk.Launch(PortableVaultFileSystem.P("Robot 2027/Kept/Gear.SLDPRT")).Succeeded);
        Assert.Equal(["Robot 2027/Kept/Gear.SLDPRT"], disk.Launched);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
