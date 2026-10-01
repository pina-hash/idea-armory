using Armory.Core;

namespace Armory.Agent.Engine;

public sealed record LocalFile(VaultPath Path, string Hash, long Size);
// Files excludes the platform's ignore list (Armory.Platform.Windows.VaultIgnore). Markers
// are the vault-relative paths of "~$<name>" files, which SolidWorks creates beside a
// document it has open (docs/spike/solidworks-lock-file.md). Problems never imply deletion.
public sealed record VaultScan(IReadOnlyList<LocalFile> Files, IReadOnlyList<string> Markers, IReadOnlyList<string> Problems);
public sealed record ReplaceOutcome(bool Succeeded, string? Problem)
{
    public static ReplaceOutcome Done { get; } = new(true, null);
    public static ReplaceOutcome Refused(string problem) => new(false, problem);
}

// The engine's only view of the disk. Windows: WindowsVaultFileSystem in Armory.Agent,
// composed from Armory.Platform.Windows (LocalChangeDetector, OpenFileDetector,
// SafeFileReplace, ReadOnlyPolicy). Every mutation must refuse rather than lose bytes.
public interface IVaultFileSystem
{
    string Root { get; }
    VaultScan Scan();
    // Whether an application has the file open right now (Restart Manager on Windows).
    bool IsOpen(VaultPath path);
    Stream OpenRead(VaultPath path);
    // Writes content at path. expectedHash is the hash the destination must still have, or
    // null when the destination must not exist. Refuses an open or changed destination.
    ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content);
    // Moves a file whose bytes are already preserved out of the vault tree, into the
    // agent's recovery folder. Never deletes. Refuses an open or changed file.
    ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash);
    // Applies a server rename. Refuses an open or changed file or an occupied destination.
    ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash);
    // Read-only for files someone else is editing; writable otherwise.
    void ApplyLockAttribute(VaultPath path, LockOwnership ownership);
    void EnsureFolder(string vaultRelativeFolder);
    // A writable scratch file on the vault's volume for staging a download.
    Stream CreateStaging(out string stagingName);
    void DeleteStaging(string stagingName);
}

public interface ISnapshotStore : ISaveSnapshotStore
{
    Stream OpenRead(string id);
}

// Durable, atomic replacement of the engine's whole state document.
public interface IEngineStateStore
{
    byte[]? Load();
    void Save(byte[] state);
}

// Portable state store: write-through temp file, flush, then rename over the old file.
public sealed class FileStateStore(string path) : IEngineStateStore
{
    public byte[]? Load() => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void Save(byte[] state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".pending";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(state);
            output.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
