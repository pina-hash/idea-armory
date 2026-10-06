using System.ComponentModel;
using System.Security.Cryptography;
using Armory.Core;

namespace Armory.Platform.Windows;

// ReadOnly is FILE_ATTRIBUTE_READONLY as found by this scan (read from the same handle that
// gives the id, size and time, so it costs nothing).
public sealed record LocalFileState(VaultPath Path, string FileId, long Size, DateTime LastWriteUtc, string Hash, DateTimeOffset HashedAt,
    bool ReadOnly = false);
public sealed record LocalRename(VaultPath Before, VaultPath After, string FileId);
// A vault-relative folder ("Robot 2027/Gearbox", validated and NFC like VaultPath) and its
// NTFS directory id, or null when the directory could not be opened during this scan.
public sealed record LocalFolderState(string Path, string? FolderId);
// A folder proven moved by its directory id. Before is expressed after the earlier moves in
// the same list are applied, so the list can be applied in order.
public sealed record LocalFolderMove(string Before, string After, string FolderId);
public sealed record LocalScan(IReadOnlyList<LocalFileState> Files, IReadOnlyList<LocalRename> Renames, IReadOnlyList<string> Problems,
    int HashesComputed, bool FullRescan, int OverflowCount)
{
    public IReadOnlyList<LocalFolderState> Folders { get; init; } = [];
    public IReadOnlyList<LocalFolderMove> FolderMoves { get; init; } = [];
    // Vault-relative, forward-slash paths of every "~$*" file outside .armory and "~$" folders.
    public IReadOnlyList<string> Markers { get; init; } = [];
    // Files whose hash was reused by NTFS id, size and time after only their path changed.
    public int HashesReused { get; init; }
}

public sealed class LocalChangeDetector : IDisposable
{
    // A file written this close to the moment it was hashed can change again without a new
    // last-write time (coarse or lazily updated timestamps), so its hash is not trusted yet.
    public static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);
    private readonly WindowsPaths paths;
    private readonly FileSystemWatcher watcher;
    private readonly TimeSpan? maximumCacheAge;
    private readonly TimeProvider clock;
    private Dictionary<VaultPath, LocalFileState> cache = [];
    private Dictionary<string, LocalFolderState> folderCache = new(StringComparer.Ordinal);
    private int fullRescanRequested = 1;
    private int overflowCount;
    private long hints;
    public int OverflowCount => Volatile.Read(ref overflowCount);
    public long HintCount => Interlocked.Read(ref hints);
    // Tests deliberately stall delivery to provoke a real kernel notification overflow.
    internal Action? AfterHint { get; set; }
    // Tests act while the scan holds a file's hashing handle open.
    internal Action<string>? WhileHashing { get; set; }

    // maximumCacheAge: when set, a full rescan also re-hashes files hashed longer ago than this.
    // When null (the agent), a hash is reused as long as the file's id, size and last-write time
    // are unchanged and it is outside the racy window.
    public LocalChangeDetector(WindowsPaths paths, TimeSpan? maximumCacheAge = null, int watcherBufferSize = 65536, TimeProvider? clock = null)
    {
        this.paths = paths;
        if (maximumCacheAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumCacheAge));
        this.maximumCacheAge = maximumCacheAge;
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(paths.Root);
        watcher = new(paths.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = watcherBufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
        };
        watcher.Created += Hint;
        watcher.Changed += Hint;
        watcher.Deleted += Hint;
        watcher.Renamed += Hint;
        watcher.Error += (_, args) =>
        {
            if (args.GetException() is InternalBufferOverflowException) Interlocked.Increment(ref overflowCount);
            Interlocked.Exchange(ref fullRescanRequested, 1);
        };
        watcher.EnableRaisingEvents = true;
    }

    // Any change outside .armory wakes the engine: files, folders, a cleared read-only bit, and
    // SolidWorks "~$" markers (so the check-out prompt appears at once). Hints are never
    // inventory; desktop.ini and Thumbs.db churn from Explorer is not worth a pass.
    private void Hint(object sender, FileSystemEventArgs args)
    {
        var relative = Path.GetRelativePath(paths.Root, args.FullPath);
        if (VaultIgnore.IsIgnored(relative) && !IsMarker(relative)) return;
        Interlocked.Increment(ref hints);
        AfterHint?.Invoke();
    }

    // A "~$" file that is not inside .armory or a "~$" folder.
    public static bool IsMarker(string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/');
        if (!segments[^1].StartsWith("~$", StringComparison.Ordinal)) return false;
        return !segments[..^1].Any(s => s.Equals(".armory", StringComparison.OrdinalIgnoreCase) || s.StartsWith("~$", StringComparison.Ordinal));
    }

    // Always enumerate the truth. Hints can prompt this method, but are never the inventory.
    // fullRescan re-hashes by age only when this detector has a maximum cache age; otherwise
    // every scan already enumerates everything and re-hashes exactly the files whose id, size
    // or last-write time changed, plus files still inside the racy window.
    public LocalScan Scan(bool fullRescan = false)
    {
        var requested = Interlocked.Exchange(ref fullRescanRequested, 0) != 0;
        var full = fullRescan || requested;
        var now = clock.GetUtcNow();
        var next = new Dictionary<VaultPath, LocalFileState>();
        var folders = new Dictionary<string, LocalFolderState>(StringComparer.Ordinal);
        List<string> markers = [];
        List<string> problems = [];
        // Only unique IDs are evidence. Hard links deliberately suppress guessing.
        var oldById = cache.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var hashes = 0;
        var reused = 0;
        foreach (var file in Enumerate(problems, folders, markers))
        {
            var relative = Path.GetRelativePath(paths.Root, file);
            if (!VaultPath.TryCreate(relative, out var path, out var problem, paths.Root, paths.MaximumLength))
            { problems.Add($"{relative}: {problem}"); continue; }
            try
            {
                // Sharing delete as well as read: a student can rename or delete the file, or
                // a folder above it, while it is being hashed.
                using var handle = new FileStream(WindowsPaths.Extended(file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                WhileHashing?.Invoke(file);
                if (!NativeMethods.GetFileInformationByHandle(handle.SafeFileHandle, out var info))
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                var id = NativeMethods.FileId(info);
                var size = ((long)info.SizeHigh << 32) | info.SizeLow;
                var write = DateTime.FromFileTimeUtc(((long)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
                var readOnly = (info.Attributes & NativeMethods.FileAttributeReadOnly) != 0;
                // The same file at the same path, or the same file moved here (a rename, or a
                // folder rename above it), whose hash can be reused when nothing else changed.
                var atPath = cache.TryGetValue(path, out var cached) && cached.FileId == id ? cached : null;
                var basis = atPath ?? (oldById.TryGetValue(id, out var moved) ? moved : null);
                var mustHash = basis is null || basis.Size != size || basis.LastWriteUtc != write || IsRacy(basis) ||
                    (full && maximumCacheAge is { } age && now - basis.HashedAt >= age);
                var hash = mustHash ? Convert.ToHexStringLower(SHA256.HashData(handle)) : basis!.Hash;
                if (mustHash) hashes++;
                else if (atPath is null) reused++;
                next.Add(path, new(path, id, size, write, hash, mustHash ? now : basis!.HashedAt, readOnly));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                problems.Add($"{relative}: {error.Message}");
                if (cache.TryGetValue(path, out var previous)) next[path] = previous;
            }
        }
        var folderMoves = FolderMovesBetween(folderCache, folders);
        var newById = next.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        // A file that only rode along with a folder move is not a rename of its own. A file that
        // also moved by itself is reported from where the folder moves left it.
        List<LocalRename> renames = [];
        foreach (var file in newById.Values)
        {
            if (!oldById.TryGetValue(file.FileId, out var old) || old.Path == file.Path) continue;
            var before = Translate(old.Path.Value, folderMoves);
            if (string.Equals(before, file.Path.Value, StringComparison.Ordinal)) continue;
            var from = VaultPath.TryCreate(before, out var translated, out _, paths.Root, paths.MaximumLength) ? translated : old.Path;
            if (from != file.Path) renames.Add(new LocalRename(from, file.Path, file.FileId));
        }
        renames.Sort((a, b) => a.Before.CompareTo(b.Before));
        // Enumeration failure cannot authorize inferring deletions in an unreadable subtree.
        // An entry whose id was found at another path did move, so only that path is kept.
        if (problems.Count == 0) { cache = next; folderCache = folders; }
        else
        {
            var fileIds = next.Values.Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in cache.Where(p => !next.ContainsKey(p.Key) && fileIds.Contains(p.Value.FileId)).Select(p => p.Key).ToArray()) cache.Remove(stale);
            foreach (var item in next) cache[item.Key] = item.Value;
            var folderIds = folders.Values.Where(f => f.FolderId is not null).Select(f => f.FolderId!).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in folderCache.Where(p => !folders.ContainsKey(p.Key) && p.Value.FolderId is not null && folderIds.Contains(p.Value.FolderId)).Select(p => p.Key).ToArray())
                folderCache.Remove(stale);
            foreach (var item in folders) folderCache[item.Key] = item.Value;
            Interlocked.Exchange(ref fullRescanRequested, 1);
        }
        markers.Sort(StringComparer.OrdinalIgnoreCase);
        return new(cache.Values.OrderBy(f => f.Path).ToArray(), renames, problems, hashes, full, OverflowCount)
        {
            Folders = folderCache.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            FolderMoves = folderMoves,
            Markers = markers,
            HashesReused = reused,
        };
    }

    // The agent moved a folder itself (IVaultFileSystem.MoveFolder): carry the cached ids and
    // hashes along, so the next scan neither reports the move as a student's nor re-hashes.
    public void Absorb(string from, string to)
    {
        var moves = new[] { new LocalFolderMove(from, to, string.Empty) };
        var folders = new Dictionary<string, LocalFolderState>(StringComparer.Ordinal);
        foreach (var folder in folderCache.Values)
        {
            var path = Translate(folder.Path, moves);
            folders[path] = folder with { Path = path };
        }
        folderCache = folders;
        var files = new Dictionary<VaultPath, LocalFileState>();
        foreach (var file in cache.Values)
        {
            var value = Translate(file.Path.Value, moves);
            if (string.Equals(value, file.Path.Value, StringComparison.Ordinal)) files[file.Path] = file;
            else if (VaultPath.TryCreate(value, out var path, out _, paths.Root, paths.MaximumLength)) files[path] = file with { Path = path };
        }
        cache = files;
    }

    private static bool IsRacy(LocalFileState file) => (file.HashedAt.UtcDateTime - file.LastWriteUtc).Duration() < RacyWindow;

    // Directories whose id moved since the last scan, top-most first. A move that the moves
    // before it already explain (a subfolder riding along) is left out.
    private static IReadOnlyList<LocalFolderMove> FolderMovesBetween(Dictionary<string, LocalFolderState> before, Dictionary<string, LocalFolderState> after)
    {
        static Dictionary<string, string> ById(Dictionary<string, LocalFolderState> folders) => folders.Values.Where(f => f.FolderId is not null)
            .GroupBy(f => f.FolderId!).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Path, StringComparer.Ordinal);
        var oldById = ById(before);
        var candidates = ById(after)
            .Where(pair => oldById.TryGetValue(pair.Key, out var old) && !string.Equals(old, pair.Value, StringComparison.Ordinal))
            .Select(pair => new LocalFolderMove(oldById[pair.Key], pair.Value, pair.Key))
            .OrderBy(m => m.After.Count(c => c == '/')).ThenBy(m => m.After, StringComparer.Ordinal);
        List<LocalFolderMove> moves = [];
        foreach (var move in candidates)
        {
            var from = Translate(move.Before, moves);
            if (!string.Equals(from, move.After, StringComparison.Ordinal)) moves.Add(move with { Before = from });
        }
        return moves;
    }

    // Where a path from before the moves is after applying them in order (NTFS names are
    // case-insensitive, so prefixes are too).
    internal static string Translate(string path, IReadOnlyList<LocalFolderMove> moves)
    {
        foreach (var move in moves)
        {
            if (string.Equals(path, move.Before, StringComparison.OrdinalIgnoreCase)) path = move.After;
            else if (path.StartsWith(move.Before + "/", StringComparison.OrdinalIgnoreCase)) path = move.After + path[move.Before.Length..];
        }
        return path;
    }

    // One walk of the tree: files are yielded for hashing, folders (with their directory ids)
    // and "~$" markers are collected. .armory, ignored names and reparse points are never
    // entered; a reparse point is reported, never followed.
    private IEnumerable<string> Enumerate(List<string> problems, Dictionary<string, LocalFolderState> folders, List<string> markers)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(paths.Root);
        while (pending.TryPop(out var directory))
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", options); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problems.Add(error.Message); continue; }
            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(paths.Root, entry.FullName);
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (IOException) { continue; }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                if (VaultIgnore.IsIgnored(relative))
                {
                    if (!isDirectory && !isLink && IsMarker(relative)) markers.Add(relative.Replace('\\', '/'));
                    continue;
                }
                if (isLink) { problems.Add($"Reparse point excluded: {entry.FullName}"); continue; }
                if (!isDirectory) { yield return entry.FullName; continue; }
                pending.Push(entry.FullName);
                if (VaultPath.TryCreate(relative, out var folder, out _, paths.Root, paths.MaximumLength))
                    folders[folder.Value] = new(folder.Value, NativeMethods.DirectoryId(entry.FullName));
            }
        }
    }

    public void Dispose() => watcher.Dispose();
}
