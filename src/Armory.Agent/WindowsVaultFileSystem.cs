using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Armory.Agent.Engine;
using Armory.Core;
using Armory.Platform.Windows;

namespace Armory.Agent;

// The engine's view of a real Windows vault (src/Armory.Agent.Engine/Platform.cs), composed
// from Armory.Platform.Windows. Every mutation refuses rather than lose bytes: nothing here
// deletes a vault file (DeleteEmptyFolder deletes only Windows' own folder metadata,
// desktop.ini and Thumbs.db, and stale "~$" markers, in a folder with nothing else in it), and
// every move or replace is preceded by an open check and a hash check. The agent's own data
// lives in <root>\.armory, which the scan never reports.
public sealed class WindowsVaultFileSystem : IVaultFileSystem, IDisposable
{
    private const string StagingExtension = ".staging";
    private readonly WindowsPaths paths;
    private readonly LocalChangeDetector changes;
    private readonly OpenFileDetector openFiles = new();
    private readonly SafeFileReplace replacer;
    private readonly ReadOnlyPolicy policy;
    private readonly TimeProvider clock;
    private readonly string privateFolder;
    private readonly string stagingFolder;
    private readonly string recoveryFolder;
    private readonly object gate = new();
    // Read-only bits that could not be put back yet; Scan retries them and reports them.
    private readonly HashSet<VaultPath> readOnlyToRestore = [];
    // Rule bits a batch could not change yet; Scan retries them and reports them.
    private readonly Dictionary<VaultPath, LockOwnership> attributeRetry = [];
    // Reported once, by the next Scan.
    private readonly List<string> pendingProblems = [];

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
            policy = new ReadOnlyPolicy(paths);
            created.Add(policy);
            // Staged downloads are scratch copies of bytes the server already holds.
            foreach (var orphan in Directory.EnumerateFiles(stagingFolder, "*" + StagingExtension)) TryDelete(orphan);
            // Re-apply every recorded attribute intent before anything can edit or sync.
            foreach (var problem in policy.Recover(new Dictionary<VaultPath, LockOwnership>()))
                pendingProblems.Add("A saved read-only setting could not be applied: " + problem);
            // No age-based re-hash: a hash is reused while the file's NTFS id, size and
            // last-write time are unchanged and it is outside the racy window.
            changes = new LocalChangeDetector(paths, clock: this.clock);
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

    // One walk of the vault: files (hashing only what changed), folders with their directory
    // ids, folder moves, and "~$" markers. .armory, "~$" folders and reparse points are never
    // entered. Every scan enumerates everything; hints only make one come sooner. Renames is
    // null on the first scan after a start (no earlier file map); FolderMoves is null when the
    // moves cannot be proven (no folder map in .armory yet, or an unreadable folder id).
    public VaultScan Scan()
    {
        lock (gate)
        {
            List<string> problems = [.. pendingProblems];
            pendingProblems.Clear();
            RetryReadOnly(problems);
            var scan = changes.Scan();
            problems.AddRange(scan.Problems);
            var files = scan.Files.Select(f => new LocalFile(f.Path, f.Hash, f.Size, f.ReadOnly)).ToArray();
            return new VaultScan(files, scan.Markers, problems,
                scan.Renames?.Select(r => new LocalMove(r.Before, r.After)).ToArray(),
                scan.Folders.Select(f => f.Path).ToArray(),
                scan.FolderMoves?.Select(m => new FolderMove(m.Before, m.After)).ToArray());
        }
    }

    public bool IsOpen(VaultPath path)
    {
        // A path that cannot be resolved is treated as open, so nothing acts on it.
        if (!paths.TryResolve(path, out var file, out _)) return true;
        return openFiles.Inspect(file!).IsOpen;
    }

    // Captures and reads share delete as the scan does: a student can rename the file, or a
    // folder above it, while Armory reads it. The open handle still reads the same bytes, and
    // nobody can write them meanwhile (write is not shared).
    public Stream OpenRead(VaultPath path)
        => new FileStream(paths.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan);

    public ReplaceOutcome Replace(VaultPath path, string? expectedHash, Stream content, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (gate)
        {
            if (!paths.TryResolve(path, out _, out var problem)) return ReplaceOutcome.Refused(problem ?? "The path is not valid in this vault.");
            // SafeFileReplace's documented limitation still applies (docs/platform/safe-replace.md):
            // it is not an atomic compare-and-replace against a non-cooperating writer, which can
            // save between its final check and the rename, so the engine replaces only closed
            // files and rechecks first. A read-only destination is never made writable here:
            // SafeFileReplace stages, hashes and checks with the bit in place and renames over
            // it (FileRenameInfoEx, ignoring the read-only attribute), so a file nobody checked
            // out is never writable while SolidWorks could open it. With readOnly, or over a
            // read-only destination, the new bytes are read-only from their first moment.
            // The file being replaced, so its read-only intent (if it was made for this very
            // file) can follow the new bytes. Unknown means the intent is dropped instead.
            string? previous = null;
            try { previous = policy.CurrentFileId(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var result = replacer.Replace(path, expectedHash, content, readOnly: readOnly);
            if (result.ReadOnlyNotRestored) readOnlyToRestore.Add(path);
            if (!result.Succeeded) return ReplaceOutcome.Refused(result.Problem ?? "The file could not be replaced.");
            readOnlyToRestore.Remove(path);
            try { policy.Renew(path, previous); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { pendingProblems.Add($"{path.Value}: the read-only setting could not follow the new bytes: {error.Message}"); }
            return ReplaceOutcome.Done;
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
            var outcome = MoveChecked(file!, WindowsPaths.Extended(target), expectedHash);
            // The path no longer holds this file, so it keeps no read-only intent.
            if (outcome.Succeeded) AfterLeaving(path, () => policy.Forget(path));
            return outcome;
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
            var outcome = MoveChecked(source!, destination!, expectedHash);
            // The same file at its new path: its read-only intent goes with it.
            if (outcome.Succeeded) AfterLeaving(from, () => policy.Move(from, to));
            return outcome;
        }
    }

    // The file already moved; an intent that cannot follow is reported, never a failed move.
    private void AfterLeaving(VaultPath path, Action update)
    {
        readOnlyToRestore.Remove(path);
        attributeRetry.Remove(path);
        try { update(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { pendingProblems.Add($"{path.Value}: the read-only setting could not follow the file: {error.Message}"); }
    }

    public void ApplyLockAttribute(VaultPath path, LockOwnership ownership)
    {
        lock (gate)
        {
            policy.Apply(path, ownership);
            readOnlyToRestore.Remove(path);
            attributeRetry.Remove(path);
        }
    }

    public void ApplyLockAttributes(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        lock (gate)
        {
            List<(VaultPath Path, LockOwnership Ownership)> valid = [];
            foreach (var item in attributes)
            {
                if (!Enum.IsDefined(item.Ownership)) throw new ArgumentOutOfRangeException(nameof(attributes));
                if (paths.TryResolve(item.Path, out _, out var problem)) valid.Add(item);
                else pendingProblems.Add($"{item.Path.Value}: {problem}");
            }
            var failed = ApplyBatch(valid);
            foreach (var (path, ownership) in valid)
            {
                readOnlyToRestore.Remove(path);
                if (failed.ContainsKey(path)) attributeRetry[path] = ownership;
                else attributeRetry.Remove(path);
            }
        }
    }

    // Folder moves (decision D16, contract C2 and C5): one Directory.Move after checking that
    // nothing inside is open and that every resulting path fits WindowsPaths' limit. A file
    // opened between the check and the move makes Windows refuse the move itself; one opened
    // with delete sharing would move with the folder, the same documented limit as Replace's
    // concurrent-writer case (docs/platform/safe-replace.md).
    public ReplaceOutcome MoveFolder(string from, string to)
    {
        lock (gate)
        {
            if (!TryFolder(from, out var fromPath, out var source, out var problem)) return ReplaceOutcome.Refused(problem!);
            if (!TryFolder(to, out var toPath, out var target, out problem)) return ReplaceOutcome.Refused(problem!);
            if (string.Equals(fromPath, toPath, StringComparison.Ordinal)) return ReplaceOutcome.Done;
            var name = Leaf(fromPath);
            var caseOnly = string.Equals(fromPath, toPath, StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(source)) return ReplaceOutcome.Refused($"The folder {name} is not there any more.");
            if (!caseOnly && toPath.StartsWith(fromPath + "/", StringComparison.OrdinalIgnoreCase))
                return ReplaceOutcome.Refused($"{name} cannot be moved into itself.");
            if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
                return ReplaceOutcome.Refused($"Something named {Leaf(toPath)} is already there.");
            List<string> files = [];
            try
            {
                foreach (var (entry, inside) in Walk(source!))
                {
                    if ((entry.Attributes & FileAttributes.Directory) == 0) files.Add(entry.FullName);
                    // Only the length can newly fail: every name inside stays the same.
                    var moved = toPath + "/" + inside;
                    if (VaultIgnore.IsIgnored(moved) || !VaultPath.TryCreate(moved, out _, out _, paths.Root, int.MaxValue) ||
                        VaultPath.TryCreate(moved, out _, out _, paths.Root, paths.MaximumLength)) continue;
                    return ReplaceOutcome.Refused($"{entry.Name} would have too long a path in {Leaf(toPath)}. Choose a shorter name.");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ReplaceOutcome.Refused(error.Message); }
            var open = openFiles.InspectAll(files, out var first);
            if (open.IsOpen)
            {
                var what = first is null ? "A file in " + name : Path.GetFileName(first);
                var where = open.Processes.Count > 0 ? " in " + string.Join(", ", open.Processes.Select(p => p.Name).Distinct()) : "";
                return ReplaceOutcome.Refused($"{what} is open{where}. Close it, then try again.");
            }
            var parent = Path.GetDirectoryName(target!)!;
            var createdParent = !Directory.Exists(parent);
            try
            {
                Directory.CreateDirectory(parent);
                Directory.Move(source!, target!);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (createdParent) TryDeleteFolder(parent);
                return ReplaceOutcome.Refused(error.Message);
            }
            changes.Absorb(fromPath, toPath);
            RekeyPending(fromPath, toPath);
            // The folder already moved: intents that cannot be saved yet are reported (and
            // saved by the next write), never a failed move.
            try { policy.Rekey(fromPath, toPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { pendingProblems.Add($"{toPath}: the read-only settings could not follow the folder yet: {error.Message}"); }
            return ReplaceOutcome.Done;
        }
    }

    // Removes a folder whose whole subtree holds no file but Windows' own folder metadata
    // (desktop.ini, Thumbs.db) and stale "~$" markers (a marker whose document is not in the
    // folder; one still held open by SolidWorks cannot be deleted, and then nothing is). Those
    // are deleted one by one, then each folder, deepest first, with a non-recursive delete, so
    // a file saved into it meanwhile stops the removal instead of being lost.
    public bool DeleteEmptyFolder(string folder)
    {
        lock (gate)
        {
            if (!TryFolder(folder, out _, out var absolute, out _)) return false;
            if (!Directory.Exists(absolute)) return !File.Exists(absolute);
            List<string> metadata = [];
            List<string> folders = [absolute!];
            try
            {
                foreach (var (entry, _) in Walk(absolute!))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (entry.Name.Equals(".armory", StringComparison.OrdinalIgnoreCase)) return false;
                        folders.Add(entry.FullName);
                    }
                    else if (IsFolderMetadata(entry.Name)) metadata.Add(entry.FullName);
                    else return false;
                }
                foreach (var file in metadata)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                foreach (var item in folders.OrderByDescending(f => f.Length)) Directory.Delete(item, recursive: false);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    // Add files: a staged copy in .armory\staging, renamed into place with no overwrite, so a
    // scan never sees half-copied bytes. Cloud placeholders (OneDrive files on demand) are
    // reparse points too, but they are the file itself; only links (symbolic links and
    // junctions, which point somewhere else) are refused.
    public ReplaceOutcome CopyIn(string sourceFullPath, VaultPath to)
    {
        if (string.IsNullOrWhiteSpace(sourceFullPath) || !Path.IsPathFullyQualified(sourceFullPath))
            return ReplaceOutcome.Refused("Armory can only add a file from a folder on this computer.");
        FileInfo source;
        try
        {
            source = new FileInfo(sourceFullPath);
            if (Directory.Exists(sourceFullPath)) return ReplaceOutcome.Refused($"{source.Name} is a folder. Add the files in it.");
            if (!source.Exists) return ReplaceOutcome.Refused($"{source.Name} is not there any more.");
            if (source.LinkTarget is not null) return ReplaceOutcome.Refused($"{source.Name} is a link to another file. Add the file itself.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return ReplaceOutcome.Refused(error.Message); }
        if (Shell.Plain(source.FullName).StartsWith(Shell.Plain(privateFolder) + "\\", StringComparison.OrdinalIgnoreCase))
            return ReplaceOutcome.Refused("Armory does not add its own private files.");
        if (VaultIgnore.IsIgnored(to.Value)) return ReplaceOutcome.Refused($"Armory does not keep {to.Name} files.");
        if (!paths.TryResolve(to, out var target, out var problem)) return ReplaceOutcome.Refused(problem ?? "That name does not work in this vault.");
        if (File.Exists(target) || Directory.Exists(target)) return ReplaceOutcome.Refused($"Something named {to.Name} is already in that folder.");
        var temp = Path.Combine(stagingFolder, Guid.NewGuid().ToString("N") + StagingExtension);
        try
        {
            // Read sharing only: a source someone is writing right now is refused, not torn.
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            lock (gate)
            {
                if (File.Exists(target) || Directory.Exists(target)) return ReplaceOutcome.Refused($"Something named {to.Name} is already in that folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target!)!);
                File.Move(temp, target!, overwrite: false);
            }
            return ReplaceOutcome.Done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ReplaceOutcome.Refused(error.Message);
        }
        finally { TryDelete(temp); }
    }

    // Decision D14: the plain path from VaultLocator, never a program or script, opened by the
    // shell with its default program (SolidWorks for SolidWorks files).
    public ReplaceOutcome Launch(VaultPath path)
    {
        if (!VaultLocator.TryResolve(Root, path.Value, out var file)) return ReplaceOutcome.Refused($"Armory cannot open {path.Name}.");
        if (LaunchPolicy.IsRefused(path.Name, Environment.GetEnvironmentVariable("PATHEXT")))
            return ReplaceOutcome.Refused($"Armory does not open {Path.GetExtension(path.Name)} files, because opening one runs a program.");
        if (!File.Exists(file)) return ReplaceOutcome.Refused($"{path.Name} is not on this computer yet.");
        var plain = Shell.Plain(file!);
        var start = new ProcessStartInfo(plain) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(plain) ?? Root };
        try
        {
            StartShell(start);
            return ReplaceOutcome.Done;
        }
        // ERROR_NO_ASSOCIATION is the only "no program" answer. A failed DDE conversation or a
        // missing DLL (1156, 1157) happens when the program is there but busy starting.
        catch (Win32Exception error) when (error.NativeErrorCode is 1155)
        { return ReplaceOutcome.Refused($"No program on this computer opens {Path.GetExtension(path.Name)} files."); }
        catch (Win32Exception error) when (error.NativeErrorCode is 1156 or 1157)
        { return ReplaceOutcome.Refused($"Windows could not open {path.Name}. Wait a moment, then try again."); }
        catch (Win32Exception error) when (error.NativeErrorCode is 1223)
        { return ReplaceOutcome.Refused($"Opening {path.Name} was canceled."); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        { return ReplaceOutcome.Refused($"Windows could not open {path.Name}: {error.Message}"); }
    }

    // ShellExecute; tests replace it so nothing really opens.
    internal Action<ProcessStartInfo> StartShell { get; set; } = start => { using var _ = Process.Start(start); };

    public void EnsureFolder(string vaultRelativeFolder)
    {
        var folder = (vaultRelativeFolder ?? string.Empty).Trim('/', '\\');
        if (folder.Length == 0) { Directory.CreateDirectory(paths.Root); return; }
        if (!paths.TryResolve(folder, out var absolute, out var problem)) throw new IOException(problem);
        Directory.CreateDirectory(absolute!);
    }

    public bool FolderExists(string vaultRelativeFolder)
    {
        var folder = (vaultRelativeFolder ?? string.Empty).Trim('/', '\\');
        if (folder.Length == 0) return Directory.Exists(paths.Root);
        return paths.TryResolve(folder, out var absolute, out _) && Directory.Exists(absolute);
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
        // One batch, which writes the manifest only if an intent was not recorded yet.
        List<(VaultPath Path, LockOwnership Ownership)> retry = [];
        foreach (var (path, ownership) in attributeRetry.ToArray())
        {
            if (paths.TryResolve(path, out _, out _)) retry.Add((path, ownership));
            else attributeRetry.Remove(path);
        }
        if (retry.Count == 0) return;
        var failed = ApplyBatch(retry);
        foreach (var (path, ownership) in retry)
        {
            if (!failed.ContainsKey(path)) { attributeRetry.Remove(path); continue; }
            problems.Add($"{path.Value}: Armory could not make this file {(ReadOnlyPolicy.IsReadOnly(ownership) ? "read-only" : "writable")} yet.");
        }
    }

    // One ApplyMany. When the manifest itself cannot be written, no bit changed: every file in
    // the batch is returned, to be retried by the next scan, and the reason is reported once.
    private Dictionary<VaultPath, string> ApplyBatch(IReadOnlyList<(VaultPath Path, LockOwnership Ownership)> items)
    {
        var failed = new Dictionary<VaultPath, string>();
        try
        {
            foreach (var (path, problem) in policy.ApplyMany(items)) failed[path] = problem;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            pendingProblems.Add("Armory could not save its read-only settings yet: " + error.Message);
            foreach (var (path, _) in items) failed[path] = error.Message;
        }
        return failed;
    }

    // A vault-relative folder (not the root, not .armory or a "~$" folder), validated like a
    // VaultPath, and its extended absolute path.
    private bool TryFolder(string? folder, out string relative, out string? absolute, out string? problem)
    {
        relative = (folder ?? string.Empty).Replace('\\', '/').Trim('/');
        absolute = null;
        if (relative.Length == 0) { problem = "That is the whole vault, not a folder in it."; return false; }
        if (!VaultPath.TryCreate(relative, out var path, out problem, paths.Root, paths.MaximumLength)) return false;
        relative = path.Value;
        if (VaultIgnore.IsIgnored(relative)) { problem = "That folder belongs to Armory or Windows."; return false; }
        return paths.TryResolve(path, out absolute, out problem);
    }

    private static string Leaf(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    // Desktop.ini and Thumbs.db are made by Windows itself; a "~$" file is a SolidWorks marker.
    private static bool IsFolderMetadata(string name)
        => name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("~$", StringComparison.Ordinal);

    // Every entry under a folder, with its path inside that folder (forward slashes). Hidden
    // and system entries are included; reparse points are listed but never entered.
    private static IEnumerable<(FileSystemInfo Entry, string Inside)> Walk(string folder)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in new DirectoryInfo(current).GetFileSystemInfos("*", options))
            {
                yield return (entry, Path.GetRelativePath(folder, entry.FullName).Replace('\\', '/'));
                if ((entry.Attributes & FileAttributes.Directory) != 0 && (entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry.FullName);
            }
        }
    }

    // Pending bit repairs follow a folder the agent moved.
    private void RekeyPending(string from, string to)
    {
        VaultPath? Moved(VaultPath path) => path.Value.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase) &&
            VaultPath.TryCreate(to + path.Value[from.Length..], out var moved, out _, paths.Root, paths.MaximumLength) ? moved : null;
        foreach (var path in readOnlyToRestore.ToArray())
            if (Moved(path) is { } moved) { readOnlyToRestore.Remove(path); readOnlyToRestore.Add(moved); }
        foreach (var (path, ownership) in attributeRetry.ToArray())
            if (Moved(path) is { } moved) { attributeRetry.Remove(path); attributeRetry[moved] = ownership; }
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        policy.Dispose();
        replacer.Dispose();
    }
}

// Decision D14: file types that run code when opened are never launched from the vault, even
// when a teammate synced one: programs, scripts, installers, shortcuts and everything the
// computer's PATHEXT lists as runnable. The review of 0.2.0 extended D14's list with the types
// ShellExecute also runs without PATHEXT naming them: shortcuts and shell commands (.pif .scf
// .website .settingcontent-ms .theme .themepack .deskthemepack), app and add-in installers
// (.application .appinstaller .appx .appxbundle .msix .msixbundle .xbap .vsto .jnlp .diagcab),
// scriptlets and consoles (.wsc .sct .ws .msc .gadget .inf .shb .shs .chm .xll .ade .adp), and
// the scripts an installed runtime associates (.py .pyw .pyz .pyzw .pyc .pyo .sh .pl .rb .ahk
// .au3 and the PowerShell data and console types). A denylist, not an allowlist, so every CAD
// exchange type (STEP, IGES, STL, DXF, DWG, Parasolid) still opens.
internal static class LaunchPolicy
{
    private static readonly string[] Refused =
    [
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp", ".scr",
        ".lnk", ".url", ".reg", ".cpl", ".jar", ".appref-ms",
        ".pif", ".scf", ".website", ".settingcontent-ms", ".theme", ".themepack", ".deskthemepack",
        ".application", ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".xbap", ".vsto", ".jnlp", ".diagcab",
        ".wsc", ".sct", ".ws", ".msc", ".gadget", ".inf", ".shb", ".shs", ".chm", ".xll", ".ade", ".adp",
        ".py", ".pyw", ".pyz", ".pyzw", ".pyc", ".pyo", ".sh", ".pl", ".rb", ".ahk", ".au3",
        ".psd1", ".ps1xml", ".psc1", ".psc2", ".ps2", ".ps2xml", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml",
    ];

    internal static bool IsRefused(string fileName, string? pathExt)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) return false;
        if (Refused.Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;
        return (pathExt ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
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
