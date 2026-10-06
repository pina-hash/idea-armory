using Armory.Core;

namespace Armory.Agent.Engine;

// ReadOnly is the file's read-only bit as the scan found it on disk (FILE_ATTRIBUTE_READONLY on
// Windows), so the engine can put back a bit someone cleared (docs/platform/read-only.md).
public sealed record LocalFile(VaultPath Path, string Hash, long Size, bool ReadOnly = false);
// Files excludes the platform's ignore list (Armory.Platform.Windows.VaultIgnore). Markers
// are the vault-relative paths of "~$<name>" files, which SolidWorks creates beside a
// document it has open (docs/spike/solidworks-lock-file.md). Problems never imply deletion.
// Renames are moves the platform proved by file identity (NTFS file id); the engine also
// recognizes a tracked file's exact bytes reappearing at one new path in the same project.
// Folders are every vault-relative directory ("Robot 2027/Gearbox": forward slashes, no
// leading or trailing slash, valid VaultPath segments), empty ones included, excluding the
// root, .armory, "~$" folders and reparse points. FolderMoves are directories the platform
// proved moved since the previous scan by directory identity (the NTFS directory id), listed
// once: only the top-most moved directory, in the order to apply them, each From being the
// path after the earlier moves in the list. A file that only rode along with a folder move is
// not repeated in Renames; a file that also moved on its own is listed in Renames with From
// expressed after every folder move. A move the agent made itself (MoveFolder) is not
// reported. Null means the platform cannot tell (no directory identity).
public sealed record VaultScan(IReadOnlyList<LocalFile> Files, IReadOnlyList<string> Markers, IReadOnlyList<string> Problems,
    IReadOnlyList<LocalMove>? Renames = null, IReadOnlyList<string>? Folders = null, IReadOnlyList<FolderMove>? FolderMoves = null);
public sealed record LocalMove(VaultPath From, VaultPath To);
public sealed record FolderMove(string From, string To);
public sealed record ReplaceOutcome(bool Succeeded, string? Problem)
{
    public static ReplaceOutcome Done { get; } = new(true, null);
    public static ReplaceOutcome Refused(string problem) => new(false, problem);
}

// The engine's only view of the disk. Windows: WindowsVaultFileSystem in Armory.Agent,
// composed from Armory.Platform.Windows (LocalChangeDetector, OpenFileDetector,
// SafeFileReplace, ReadOnlyPolicy). Every mutation must refuse rather than lose bytes.
// Folder arguments are vault-relative like VaultScan.Folders.
public interface IVaultFileSystem
{
    string Root { get; }
    VaultScan Scan();
    // Whether an application has the file open right now (Restart Manager on Windows).
    bool IsOpen(VaultPath path);
    Stream OpenRead(VaultPath path);
    // Writes content at path. expectedHash is the hash the destination must still have, or
    // null when the destination must not exist. Refuses an open or changed destination.
    // readOnly: the read-only rule says this file is read-only on this computer, so the new
    // bytes appear read-only from their first moment (the bit is set on the staged copy before
    // it is renamed into place). A destination that was read-only stays read-only either way.
    ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content, bool readOnly = false);
    // Moves a file whose bytes are already preserved out of the vault tree, into the
    // agent's recovery folder. Never deletes. Refuses an open or changed file.
    ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash);
    // Applies a server rename. Refuses an open or changed file or an occupied destination.
    ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash);
    // Moves a whole folder in one step (a project or folder renamed on the site, or a refused
    // rename put back). Refuses when the source is missing, any file inside is open, the target
    // already exists (a case-only rename is allowed), the target is inside the source, or a
    // resulting path would not fit the platform's path limit. Never overwrites or merges.
    ReplaceOutcome MoveFolder(string from, string to);
    // Removes a folder (and its empty subfolders) that holds no file except metadata Windows
    // makes by itself (desktop.ini, Thumbs.db, a stale SolidWorks "~$" marker); never removes
    // any other file and never the vault root. True when the folder is gone afterwards.
    bool DeleteEmptyFolder(string folder);
    // Copies a file from outside the vault to a new vault path: through private staging, then
    // renamed into place. Never overwrites; refuses links (symbolic links, junctions), files
    // under .armory, ignored names, and a source someone is writing.
    ReplaceOutcome CopyIn(string sourceFullPath, VaultPath to);
    // Opens the file in its default program (SolidWorks for SolidWorks files). Refuses
    // programs, scripts and shortcuts (decision D14), and says plainly when no program on
    // this computer opens the type.
    ReplaceOutcome Launch(VaultPath path);
    // The v2 read-only rule (Armory.Core CheckoutRules.IsReadOnlyOnDisk): a file the server has
    // is read-only unless THIS device holds its check out (ownership ThisDevice); Free,
    // OtherPerson and MyOtherDevice are read-only. Files the server does not have are never
    // passed here and stay writable. The intent is durable before the bit changes.
    void ApplyLockAttribute(VaultPath path, LockOwnership ownership);
    // The same rule for many files with one durable intent write. A bit that cannot be
    // changed now is retried by the next Scan and reported in its Problems; one file never
    // stops the others.
    void ApplyLockAttributes(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> attributes);
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
