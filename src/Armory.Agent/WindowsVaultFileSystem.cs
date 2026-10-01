using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Agent;

// The engine's view of a real Windows vault (src/Armory.Agent.Engine/Platform.cs), composed
// from Armory.Platform.Windows. Every mutation refuses rather than lose bytes: nothing here
// deletes a vault file, and every move or replace is preceded by an open check and a hash
// check. The agent's own data lives in <root>\.armory, which the scan never reports.
public sealed class WindowsVaultFileSystem : IVaultFileSystem, IDisposable
{
    public static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(30);
    private const string StagingExtension = ".staging";
    private readonly WindowsPaths paths;
    private readonly LocalChangeDetector changes;
    private readonly OpenFileDetector openFiles = new();
    private readonly SafeFileReplace replacer;
    private readonly ReadOnlyPolicy readOnly;
    private readonly TimeProvider clock;
    private readonly string privateFolder;
    private readonly string stagingFolder;
    private readonly string recoveryFolder;
    private readonly object gate = new();
    // Read-only bits that could not be put back yet; Scan retries them and reports them.
    private readonly HashSet<VaultPath> readOnlyToRestore = [];
    private DateTimeOffset lastFullScan = DateTimeOffset.MinValue;

    public WindowsVaultFileSystem(string root, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(root);
        paths = new WindowsPaths(root);
        privateFolder = paths.PrivateDirectory();
        stagingFolder = Path.Combine(privateFolder, "staging");
        recoveryFolder = Path.Combine(privateFolder, "recovery");
        Directory.CreateDirectory(stagingFolder);
        var created = new List<IDisposable>();
        try
        {
            // Both take an exclusive ownership handle in .armory, so a second agent on the same
            // vault stops here, before it can touch this one's files.
            replacer = new SafeFileReplace(paths);
            created.Add(replacer);
            readOnly = new ReadOnlyPolicy(paths);
            created.Add(readOnly);
            // Staged downloads are scratch copies of bytes the server already holds.
            foreach (var orphan in Directory.EnumerateFiles(stagingFolder, "*" + StagingExtension)) TryDelete(orphan);
            // Re-apply every recorded attribute intent before anything can edit or sync.
            readOnly.Recover(new Dictionary<VaultPath, LockOwnership>());
            changes = new LocalChangeDetector(paths, maximumCacheAge: TimeSpan.FromMinutes(25), clock: this.clock);
        }
        catch
        {
            foreach (var item in created) item.Dispose();
            throw;
        }
    }

    public string Root => paths.Root;
    public string PrivateFolder => privateFolder;
    // Grows with every file-system notification under the vault (outside .armory); the host
    // polls it and wakes the engine. The scan, not the hint, is the inventory.
    public long HintCount => changes.HintCount;

    public VaultScan Scan()
    {
        lock (gate)
        {
            List<string> problems = [];
            RetryReadOnly(problems);
            var now = clock.GetUtcNow();
            var full = now - lastFullScan >= FullScanInterval;
            var scan = changes.Scan(full);
            if (full) lastFullScan = now;
            problems.AddRange(scan.Problems);
            var files = scan.Files.Select(f => new LocalFile(f.Path, f.Hash, f.Size)).ToArray();
            var markers = Markers(problems);
            return new VaultScan(files, markers, problems, scan.Renames.Select(r => new LocalMove(r.Before, r.After)).ToArray());
        }
    }

    // Vault-relative, forward-slash paths of every "~$*" file. .armory, "~$" folders and
    // reparse points are never entered.
    private IReadOnlyList<string> Markers(List<string> problems)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        List<string> markers = [];
        var pending = new Stack<string>();
        pending.Push(paths.Root);
        while (pending.TryPop(out var folder))
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(folder).GetFileSystemInfos("*", options); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problems.Add(error.Message); continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (IOException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (entry.Name.Equals(".armory", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith("~$", StringComparison.Ordinal)) continue;
                    pending.Push(entry.FullName);
                }
                else if (entry.Name.StartsWith("~$", StringComparison.Ordinal))
                {
                    markers.Add(Path.GetRelativePath(paths.Root, entry.FullName).Replace('\\', '/'));
                }
            }
        }
        markers.Sort(StringComparer.OrdinalIgnoreCase);
        return markers;
    }

    public bool IsOpen(VaultPath path)
    {
        // A path that cannot be resolved is treated as open, so nothing acts on it.
        if (!paths.TryResolve(path, out var file, out _)) return true;
        return openFiles.Inspect(file!).IsOpen;
    }

    public Stream OpenRead(VaultPath path)
        => new FileStream(paths.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

    public ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (gate)
        {
            if (!paths.TryResolve(path, out var file, out var problem)) return ReplaceOutcome.Refused(problem ?? "The path is not valid in this vault.");
            // SafeFileReplace's documented limitation still applies (docs/platform/safe-replace.md):
            // it is not an atomic compare-and-replace against a non-cooperating writer, which can
            // save between its final check and the rename, so the engine replaces only closed
            // files and rechecks first. MoveFileEx cannot replace a read-only file: the bit is
            // cleared only for the replace and always put back (on the new bytes after success,
            // on the old bytes after a refusal).
            var wasReadOnly = false;
            if (expectedHash is not null && File.Exists(file))
            {
                try
                {
                    var attributes = File.GetAttributes(file!);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file!, attributes & ~FileAttributes.ReadOnly);
                        wasReadOnly = true;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (wasReadOnly) RestoreReadOnly(path, file!);
                    return ReplaceOutcome.Refused(error.Message);
                }
            }
            ReplaceResult result;
            try { result = replacer.Replace(path, expectedHash, content); }
            finally { if (wasReadOnly) RestoreReadOnly(path, file!); }
            return result.Succeeded ? ReplaceOutcome.Done : ReplaceOutcome.Refused(result.Problem ?? "The file could not be replaced.");
        }
    }

    public ReplaceOutcome MoveToRecovery(VaultPath path, string expectedHash)
    {
        lock (gate)
        {
            if (!paths.TryResolve(path, out var file, out var problem)) return ReplaceOutcome.Refused(problem ?? "The path is not valid in this vault.");
            if (!File.Exists(file)) return ReplaceOutcome.Refused("The file is not there any more.");
            var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var relative = path.Value.Replace('/', '\\');
            var target = Path.Combine(recoveryFolder, stamp, relative);
            for (var n = 2; File.Exists(WindowsPaths.Extended(target)) || Directory.Exists(WindowsPaths.Extended(target)); n++)
                target = Path.Combine(recoveryFolder, stamp + "-" + n.ToString(CultureInfo.InvariantCulture), relative);
            return MoveChecked(file!, WindowsPaths.Extended(target), expectedHash);
        }
    }

    public ReplaceOutcome Move(VaultPath from, VaultPath to, string expectedHash)
    {
        lock (gate)
        {
            if (!paths.TryResolve(from, out var source, out var problem)) return ReplaceOutcome.Refused(problem ?? "The path is not valid in this vault.");
            if (!paths.TryResolve(to, out var destination, out problem)) return ReplaceOutcome.Refused(problem ?? "The new path is not valid in this vault.");
            if (string.Equals(from.Value, to.Value, StringComparison.Ordinal)) return ReplaceOutcome.Done;
            // A case-only rename names the same file, so the destination is "occupied" by itself.
            var caseOnly = from == to;
            if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
                return ReplaceOutcome.Refused($"Something named {to.Name} is already in that folder.");
            return MoveChecked(source!, destination!, expectedHash);
        }
    }

    public void ApplyLockAttribute(VaultPath path, LockOwnership ownership)
    {
        lock (gate)
        {
            readOnly.Apply(path, ownership);
            readOnlyToRestore.Remove(path);
        }
    }

    public void EnsureFolder(string vaultRelativeFolder)
    {
        var folder = (vaultRelativeFolder ?? string.Empty).Trim('/', '\\');
        if (folder.Length == 0) { Directory.CreateDirectory(paths.Root); return; }
        if (!paths.TryResolve(folder, out var absolute, out var problem)) throw new IOException(problem);
        Directory.CreateDirectory(absolute!);
    }

    public Stream CreateStaging(out string stagingName)
    {
        stagingName = Guid.NewGuid().ToString("N") + StagingExtension;
        return new FileStream(Path.Combine(stagingFolder, stagingName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920);
    }

    public void DeleteStaging(string stagingName)
    {
        // Only names CreateStaging made: no separators, our extension, inside staging.
        if (string.IsNullOrEmpty(stagingName) || stagingName != Path.GetFileName(stagingName) ||
            !stagingName.EndsWith(StagingExtension, StringComparison.Ordinal) || stagingName.StartsWith('.'))
            throw new ArgumentException("Not a staging file name.", nameof(stagingName));
        var file = Path.Combine(stagingFolder, stagingName);
        if (File.Exists(file)) File.Delete(file);
    }

    // Null when the file is closed and still has the expected bytes; otherwise the refusal.
    private ReplaceOutcome? CheckUnchanged(string file, string expectedHash)
    {
        if (!File.Exists(file)) return ReplaceOutcome.Refused("The file is not there any more.");
        var open = openFiles.Inspect(file);
        if (open.IsOpen) return ReplaceOutcome.Refused("The file is open" + (open.Processes.Count > 0 ? " in " + string.Join(", ", open.Processes.Select(p => p.Name)) : "") + ".");
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                return ReplaceOutcome.Refused("The file changed since Armory last looked at it.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ReplaceOutcome.Refused(error.Message);
        }
        return null;
    }

    // The open check and the hash check, then the target folder, then a same-volume rename
    // that never overwrites. The source is never deleted: on any failure it stays where it
    // was, and a refused move creates no folder.
    private ReplaceOutcome MoveChecked(string source, string destination, string expectedHash)
    {
        try
        {
            var check = CheckUnchanged(source, expectedHash);
            if (check is not null) return check;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: false);
            return ReplaceOutcome.Done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return ReplaceOutcome.Refused(error.Message);
        }
    }

    private void RestoreReadOnly(VaultPath path, string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file)) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                readOnlyToRestore.Remove(path);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4) { readOnlyToRestore.Add(path); return; }
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private void RetryReadOnly(List<string> problems)
    {
        foreach (var path in readOnlyToRestore.ToArray())
        {
            if (!paths.TryResolve(path, out var file, out _)) { readOnlyToRestore.Remove(path); continue; }
            RestoreReadOnly(path, file!);
            if (readOnlyToRestore.Contains(path)) problems.Add($"{path.Value}: Armory could not make this file read-only again yet.");
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        changes.Dispose();
        readOnly.Dispose();
        replacer.Dispose();
    }
}

// The engine's snapshot store over DurableSnapshotStore (<root>\.armory\snapshots).
public sealed class WindowsSnapshotStore(DurableSnapshotStore inner) : ISnapshotStore, IDisposable
{
    public SavedSnapshot Capture(string id, VaultPath path, string author, Stream source) => inner.Capture(id, path, author, source);
    public IReadOnlyList<SavedSnapshot> Enumerate() => inner.Enumerate();
    public Stream OpenRead(string id) => inner.OpenRead(id);
    public void Dispose() => inner.Dispose();
}
