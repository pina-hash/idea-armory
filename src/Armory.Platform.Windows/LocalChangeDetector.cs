using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Armory.Core;

namespace Armory.Platform.Windows;

// ReadOnly is FILE_ATTRIBUTE_READONLY as found by this scan (read from the same handle that
// gives the id, size and time, so it costs nothing). Unread: this scan could not read the file
// (another program holds it for writing, as SolidWorks holds a part it has open, or a problem
// hid it) and carried its previous entry over as it was: the hash, size, time and read-only
// bit are the last ones read, not the disk's now. The next scan that can read it hashes it
// again, whatever its size and time say (feedback N4).
public sealed record LocalFileState(VaultPath Path, string FileId, long Size, DateTime LastWriteUtc, string Hash, DateTimeOffset HashedAt,
    bool ReadOnly = false, bool Unread = false);
public sealed record LocalRename(VaultPath Before, VaultPath After, string FileId);
// A vault-relative folder ("Robot 2027/Gearbox", validated and NFC like VaultPath) and its
// NTFS directory id, or null when the directory could not be opened during this scan.
public sealed record LocalFolderState(string Path, string? FolderId);
// A folder proven moved by its directory id. Before is expressed after the earlier moves in
// the same list are applied, so the list can be applied in order.
public sealed record LocalFolderMove(string Before, string After, string FolderId);
// Renames is null when this scan could not check for renames: the first scan after a start
// has no earlier file map to compare with. An empty list means checked, none.
public sealed record LocalScan(IReadOnlyList<LocalFileState> Files, IReadOnlyList<LocalRename>? Renames, IReadOnlyList<string> Problems,
    int HashesComputed, bool FullRescan, int OverflowCount)
{
    public IReadOnlyList<LocalFolderState> Folders { get; init; } = [];
    // Null when this scan cannot prove every folder move: there is no earlier folder map (the
    // first scan after a start, unless .armory\folder-ids.json holds one), a folder that left
    // its path had no readable id, or its id was not found while another folder's id was
    // unreadable. An empty list means checked, none.
    public IReadOnlyList<LocalFolderMove>? FolderMoves { get; init; } = [];
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
    internal const string FolderMapName = "folder-ids.json";
    // The file map (path, NTFS id, size, last-write time, hash) kept across starts, so the first
    // scan after a start does not hash the whole vault again (0.3.3: 10 to 38 seconds seen).
    internal const string FileMapName = "file-hashes.json";
    // Written at most this often while files change, and when the detector is disposed.
    internal static readonly TimeSpan FileMapEvery = TimeSpan.FromSeconds(60);
    private readonly WindowsPaths paths;
    private readonly FileSystemWatcher watcher;
    private readonly TimeSpan? maximumCacheAge;
    private readonly TimeProvider clock;
    private readonly string folderMapFile;
    private readonly string fileMapFile;
    // The last run's file map, used by the first scan of this one only: a hash is taken from it
    // only for the same file (NTFS id) at the same path, with the same size and last-write time,
    // hashed outside the racy window, as any later scan reuses its own.
    private Dictionary<VaultPath, LocalFileState> saved = [];
    private bool fileMapDirty;
    private long fileMapWritten;
    private Dictionary<VaultPath, LocalFileState> cache = [];
    private bool cacheKnown;
    private Dictionary<string, LocalFolderState> folderCache = new(StringComparer.Ordinal);
    private bool folderCacheKnown;
    // The folder map last written to folder-ids.json. It trails the in-memory map by one scan:
    // the moves a scan reports are written only when the next scan starts (the engine came back
    // for another pass), so a crash before the engine handled them reports them again after the
    // restart instead of losing them. The agent's own moves (Absorb) are written at once.
    private Dictionary<string, string?>? durableFolders;
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
        folderMapFile = Path.Combine(paths.PrivateDirectory(), FolderMapName);
        fileMapFile = Path.Combine(paths.PrivateDirectory(), FileMapName);
        LoadFolderMap();
        LoadFileMap();
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
    //
    // A problem keeps only what it could hide, as it was: what is under a directory that could
    // not be listed, an entry that could not be read, or a reparse point (never entered); a file
    // that could not be opened at its own path; and the file or folder whose id turns up on an
    // entry this scan could not take in (a name or path the vault refuses, a file that could not
    // be opened). Every other missing file or folder is reported missing, so one long Pack and
    // Go path never stops a deletion elsewhere from showing.
    // A canceled token stops it between files (the agent quitting); what it had read is dropped.
    public LocalScan Scan(bool fullRescan = false, CancellationToken cancellationToken = default)
    {
        var requested = Interlocked.Exchange(ref fullRescanRequested, 0) != 0;
        var full = fullRescan || requested;
        var now = clock.GetUtcNow();
        Acknowledge();
        var next = new Dictionary<VaultPath, LocalFileState>();
        var folders = new Dictionary<string, LocalFolderState>(StringComparer.Ordinal);
        var walk = new Walk(paths.Root);
        // Only unique IDs are evidence. Hard links deliberately suppress guessing.
        var oldById = cache.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var hashes = 0;
        var reused = 0;
        foreach (var file in Enumerate(walk, folders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(paths.Root, file);
            if (!VaultPath.TryCreate(relative, out var path, out var problem, paths.Root, paths.MaximumLength))
            {
                walk.Problems.Add($"{relative}: {problem}");
                walk.Unread(file, NativeMethods.EntryId(file));
                continue;
            }
            string? id = null;
            try
            {
                // Sharing delete as well as read: a student can rename or delete the file, or
                // a folder above it, while it is being hashed.
                using var handle = new FileStream(WindowsPaths.Extended(file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                WhileHashing?.Invoke(file);
                if (!NativeMethods.GetFileInformationByHandle(handle.SafeFileHandle, out var info))
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                id = NativeMethods.FileId(info);
                var size = ((long)info.SizeHigh << 32) | info.SizeLow;
                var write = DateTime.FromFileTimeUtc(((long)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
                var readOnly = (info.Attributes & NativeMethods.FileAttributeReadOnly) != 0;
                // The same file at the same path, or the same file moved here (a rename, or a
                // folder rename above it), whose hash can be reused when nothing else changed.
                var atPath = cache.TryGetValue(path, out var cached) && cached.FileId == id ? cached
                    : !cacheKnown && saved.TryGetValue(path, out var kept) && kept.FileId == id ? kept : null;
                var basis = atPath ?? (oldById.TryGetValue(id, out var moved) ? moved : null);
                // An entry carried over unread says nothing about the bytes now: a writer can
                // change them and put the size and time back (feedback N4).
                var mustHash = basis is null || basis.Unread || basis.Size != size || basis.LastWriteUtc != write || IsRacy(basis) ||
                    (full && maximumCacheAge is { } age && now - basis.HashedAt >= age);
                var hash = mustHash ? Convert.ToHexStringLower(SHA256.HashData(handle)) : basis!.Hash;
                if (mustHash) hashes++;
                else if (atPath is null) reused++;
                next.Add(path, new(path, id, size, write, hash, mustHash ? now : basis!.HashedAt, readOnly));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                walk.Problems.Add($"{relative}: {error.Message}");
                walk.Unread(file, id ?? NativeMethods.EntryId(file));
                // Something is at this path: never a deletion of what was here. What was read
                // last time is kept, marked unread, so nothing trusts it as the disk's bytes now.
                if (cache.TryGetValue(path, out var previous)) next[path] = previous with { Unread = true };
            }
        }

        // What a problem could hide is kept as it was; everything else that is gone is gone.
        var foundFiles = next.Values.Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
        List<string> keptFiles = [];
        foreach (var (path, old) in cache)
        {
            if (next.ContainsKey(path) || foundFiles.Contains(old.FileId)) continue;
            if (!walk.Hides(path.Value, old.FileId, folder: false)) continue;
            next[path] = old with { Unread = true };
            keptFiles.Add(path.Value);
        }
        var foundFolders = folders.Values.Where(f => f.FolderId is not null).Select(f => f.FolderId!).ToHashSet(StringComparer.Ordinal);
        var keptAncestors = keptFiles.SelectMany(Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, old) in folderCache)
        {
            if (folders.ContainsKey(path) || (old.FolderId is not null && foundFolders.Contains(old.FolderId))) continue;
            if (keptAncestors.Contains(path) || walk.Hides(path, old.FolderId, folder: true)) folders[path] = old;
        }

        IReadOnlyList<LocalFolderMove>? folderMoves = null;
        if (folderCacheKnown)
        {
            string[]? taken = null;
            // A temporary name for a cycle is valid here and is no folder or file, before or after.
            bool IsFree(string name)
            {
                taken ??= [.. folderCache.Keys, .. folders.Keys, .. cache.Keys.Select(p => p.Value), .. next.Keys.Select(p => p.Value)];
                return VaultPath.TryCreate(name, out _, out _, paths.Root, paths.MaximumLength) && !taken.Any(path => Inside(path, name));
            }
            folderMoves = FolderMovesBetween(Map(folderCache), Map(folders), walk.UnidentifiedFolder, IsFree);
        }
        List<LocalRename>? renames = null;
        if (cacheKnown)
        {
            renames = [];
            var newById = next.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            // A file that only rode along with a folder move is not a rename of its own. A file
            // that also moved by itself is reported from where the folder moves left it.
            foreach (var file in newById.Values)
            {
                if (!oldById.TryGetValue(file.FileId, out var old) || old.Path == file.Path) continue;
                var before = Translate(old.Path.Value, folderMoves ?? []);
                if (string.Equals(before, file.Path.Value, StringComparison.Ordinal)) continue;
                var from = VaultPath.TryCreate(before, out var translated, out _, paths.Root, paths.MaximumLength) ? translated : old.Path;
                if (from != file.Path) renames.Add(new LocalRename(from, file.Path, file.FileId));
            }
            renames = RenameOrder(renames);
        }
        fileMapDirty |= hashes > 0 || reused > 0 || next.Count != cache.Count || !cacheKnown || next.Keys.Any(path => !cache.ContainsKey(path));
        cache = next;
        cacheKnown = true;
        saved = [];
        folderCache = folders;
        folderCacheKnown = true;
        if (fileMapDirty && (fileMapWritten == 0 || clock.GetElapsedTime(fileMapWritten) >= FileMapEvery)) SaveFileMap();
        if (walk.Problems.Count > 0) Interlocked.Exchange(ref fullRescanRequested, 1);
        walk.Markers.Sort(StringComparer.OrdinalIgnoreCase);
        return new(cache.Values.OrderBy(f => f.Path).ToArray(), renames, walk.Problems, hashes, full, OverflowCount)
        {
            Folders = folderCache.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            FolderMoves = folderMoves,
            Markers = walk.Markers,
            HashesReused = reused,
        };
    }

    // The agent moved a folder itself (IVaultFileSystem.MoveFolder): carry the cached ids and
    // hashes along, so the next scan neither reports the move as a student's nor re-hashes, and
    // write the moved map at once, so a restart does not report it either.
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
        fileMapDirty = true;
        if (durableFolders is null) return;
        var durable = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (path, id) in durableFolders) durable[Translate(path, moves)] = id;
        if (SaveFolderMap(durable)) durableFolders = durable;
    }

    private static bool IsRacy(LocalFileState file) => (file.HashedAt.UtcDateTime - file.LastWriteUtc).Duration() < RacyWindow;

    private static Dictionary<string, string?> Map(Dictionary<string, LocalFolderState> folders)
        => folders.Values.ToDictionary(f => f.Path, f => f.FolderId, StringComparer.Ordinal);

    // Every folder above a vault path ("a/b/c.txt" gives "a" and "a/b").
    private static IEnumerable<string> Ancestors(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) yield return path[..slash];
    }

    // path is folder or inside it ("" is the whole vault), ignoring case as NTFS does.
    private static bool Inside(string path, string folder)
        => folder.Length == 0 || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    // The moves that take the folders of `before` (path to directory id) to their places in
    // `after`, in an order that can be applied one by one: each From is where the folder is
    // once the earlier moves are done, and each To is free at that moment (nothing there and
    // nothing under it) and not inside a folder that still has to move. A folder that only
    // rode along with a move above it is not listed. So a chain (Gearbox to "Gearbox old", then
    // "Gearbox v2" to Gearbox) vacates Gearbox first, and a cycle (A and B swapped), or a
    // folder in the way of another, is first moved aside under a temporary name ("A (moving)")
    // in the same top-level folder. Null when the moves cannot be proven: a folder that left
    // its path had no id, or its id was not found while a folder's id in `after` was unreadable;
    // or no order and no free temporary name exists.
    internal static IReadOnlyList<LocalFolderMove>? FolderMovesBetween(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after,
        bool unidentifiedAfter, Func<string, bool> isFreeName)
    {
        static Dictionary<string, string> ById(IReadOnlyDictionary<string, string?> folders) => folders.Where(f => f.Value is not null)
            .GroupBy(f => f.Value!).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Key, StringComparer.Ordinal);
        var oldById = ById(before);
        var newById = ById(after);
        foreach (var (path, id) in before)
        {
            if (after.ContainsKey(path)) continue;
            if (id is null || (!newById.ContainsKey(id) && unidentifiedAfter)) return null;
        }
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, path) in oldById)
        {
            if (!newById.TryGetValue(id, out var to)) continue;
            current[id] = path;
            target[id] = to;
        }
        List<LocalFolderMove> moves = [];
        bool Pending(string id) => !string.Equals(current[id], target[id], StringComparison.Ordinal);
        bool CanMove(string id, string to)
        {
            var from = current[id];
            var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (Inside(to, from) || Inside(from, to))) return false;
            foreach (var (other, at) in current)
            {
                if (other == id) continue;
                // The folder that ends up above the target is not there yet: placing this one
                // first would leave it in that folder's way.
                if (Pending(other) && Inside(to, target[other]) && !string.Equals(to, target[other], StringComparison.OrdinalIgnoreCase)) return false;
                // Folders inside the one moving go with it.
                if (Inside(at, from)) continue;
                // Something is at the target or under it, or the target is inside a folder
                // that still has to move (it would be carried off with it).
                if (Inside(at, to)) return false;
                if (Pending(other) && Inside(to, at)) return false;
            }
            return true;
        }
        void Apply(string id, string to)
        {
            var from = current[id];
            moves.Add(new LocalFolderMove(from, to, id));
            foreach (var other in current.Keys.ToArray())
                if (Inside(current[other], from)) current[other] = to + current[other][from.Length..];
        }
        // What keeps a folder from its target: itself when the target is inside it or above
        // it, the top-most folder at or under the target, and the top-most folder that still
        // has to move and holds the target (it would carry the folder off).
        IEnumerable<string> Blockers(string id)
        {
            var from = current[id];
            var to = target[id];
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (Inside(to, from) || Inside(from, to))) yield return id;
            string? TopMost(Func<string, string, bool> holds) => current
                .Where(pair => pair.Key != id && !Inside(pair.Value, from) && holds(pair.Key, pair.Value))
                .OrderBy(pair => pair.Value.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault();
            if (TopMost((_, at) => Inside(at, to)) is { } occupant) yield return occupant;
            if (TopMost((other, at) => Pending(other) && Inside(to, at)) is { } holder) yield return holder;
        }
        // Temporary names beside the folder, then beside each folder above it, never above the
        // top level it is in (a folder inside a project stays inside that project).
        IEnumerable<string> Temporaries(string id)
        {
            var path = current[id];
            var slash = path.LastIndexOf('/');
            var leaf = path[(slash + 1)..];
            var level = slash < 0 ? "" : path[..slash];
            while (true)
            {
                for (var n = 1; n <= 20; n++) yield return (level.Length == 0 ? "" : level + "/") + leaf + (n == 1 ? " (moving)" : $" (moving {n})");
                var up = level.LastIndexOf('/');
                if (up < 0) yield break;
                level = level[..up];
            }
        }
        var limit = 4 * current.Count + 8;
        for (var step = 0; ; step++)
        {
            var pending = current.Keys.Where(Pending)
                .OrderBy(id => target[id].Count(c => c == '/')).ThenBy(id => target[id], StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            if (pending.Length == 0) return moves;
            if (step > limit) return null;
            var ready = pending.FirstOrDefault(id => CanMove(id, target[id]));
            if (ready is not null) { Apply(ready, target[ready]); continue; }
            // A cycle, or a folder in the way: move the first blocker aside under a temporary
            // name outside the target it blocks, then go on.
            var aside = pending
                .SelectMany(id => Blockers(id).SelectMany(blocker => Temporaries(blocker)
                    .Where(name => !Inside(name, target[id])).Select(name => (Blocker: blocker, Name: name))))
                .FirstOrDefault(candidate => isFreeName(candidate.Name) && CanMove(candidate.Blocker, candidate.Name));
            if (aside.Blocker is null) return null;
            Apply(aside.Blocker, aside.Name);
        }
    }

    // File renames in path order, except that a rename onto a path another rename leaves comes
    // after that one, so a chain applies one by one (Plate to "Plate old", then "Plate v2" to
    // Plate). Each From and each To is unique, so the renames form chains and cycles; a cycle
    // (two files swapped) has no such order, and its renames are listed next to each other.
    internal static List<LocalRename> RenameOrder(IEnumerable<LocalRename> renames)
    {
        var sorted = renames.OrderBy(r => r.Before).ToList();
        var leaving = new Dictionary<string, LocalRename>(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in sorted) leaving.TryAdd(rename.Before.Value, rename);
        var placed = new HashSet<LocalRename>(ReferenceEqualityComparer.Instance);
        var ordered = new List<LocalRename>(sorted.Count);
        foreach (var start in sorted)
        {
            // Follow what must go first (the rename leaving this one's target) until a rename
            // that waits for nothing, or back around a cycle, then place them last to first.
            var chain = new List<LocalRename>();
            var seen = new HashSet<LocalRename>(ReferenceEqualityComparer.Instance);
            for (var at = start; at is not null && !placed.Contains(at) && seen.Add(at);)
            {
                chain.Add(at);
                at = leaving.TryGetValue(at.After.Value, out var first) && !ReferenceEquals(first, at) ? first : null;
            }
            for (var i = chain.Count - 1; i >= 0; i--) if (placed.Add(chain[i])) ordered.Add(chain[i]);
        }
        return ordered;
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

    // What one walk found besides files and folders: markers, problems, and what each problem
    // could hide.
    private sealed class Walk(string root)
    {
        internal List<string> Problems { get; } = [];
        internal List<string> Markers { get; } = [];
        // Vault-relative paths ("" is the whole vault) whose contents this walk could not see.
        internal List<string> Blind { get; } = [];
        // Ids seen on entries the walk could not take in.
        internal HashSet<string> UnreadIds { get; } = new(StringComparer.Ordinal);
        // Folders holding a file whose id could not be read either.
        internal HashSet<string> UnidentifiedParents { get; } = new(StringComparer.OrdinalIgnoreCase);
        // A folder of this walk whose id could not be read.
        internal bool UnidentifiedFolder { get; set; }

        internal string Relative(string fullPath)
        {
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            return relative == "." ? string.Empty : relative;
        }

        internal void Unread(string fullPath, string? id)
        {
            if (id is not null) { UnreadIds.Add(id); return; }
            var relative = Relative(fullPath);
            var slash = relative.LastIndexOf('/');
            UnidentifiedParents.Add(slash < 0 ? string.Empty : relative[..slash]);
        }

        internal bool Hides(string path, string? id, bool folder)
        {
            if (id is not null && UnreadIds.Contains(id)) return true;
            if (Blind.Any(blind => Inside(path, blind))) return true;
            if (folder) return false;
            var slash = path.LastIndexOf('/');
            return UnidentifiedParents.Contains(slash < 0 ? string.Empty : path[..slash]);
        }
    }

    // One walk of the tree: files are yielded for hashing, folders (with their directory ids)
    // and "~$" markers are collected. .armory, ignored names and reparse points are never
    // entered; a reparse point is reported, never followed.
    private IEnumerable<string> Enumerate(Walk walk, Dictionary<string, LocalFolderState> folders)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(paths.Root);
        while (pending.TryPop(out var directory))
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory).GetFileSystemInfos("*", options); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                walk.Problems.Add(error.Message);
                walk.Blind.Add(walk.Relative(directory));
                continue;
            }
            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(paths.Root, entry.FullName);
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    walk.Problems.Add($"{relative}: {error.Message}");
                    walk.Blind.Add(walk.Relative(entry.FullName));
                    continue;
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                if (VaultIgnore.IsIgnored(relative))
                {
                    if (!isDirectory && !isLink && IsMarker(relative)) walk.Markers.Add(relative.Replace('\\', '/'));
                    continue;
                }
                if (isLink)
                {
                    walk.Problems.Add($"Reparse point excluded: {entry.FullName}");
                    walk.Blind.Add(walk.Relative(entry.FullName));
                    continue;
                }
                if (!isDirectory) { yield return entry.FullName; continue; }
                pending.Push(entry.FullName);
                var id = NativeMethods.DirectoryId(entry.FullName);
                if (VaultPath.TryCreate(relative, out var folder, out var problem, paths.Root, paths.MaximumLength))
                {
                    folders[folder.Value] = new(folder.Value, id);
                    if (id is null) walk.UnidentifiedFolder = true;
                }
                else
                {
                    walk.Problems.Add($"{relative}: {problem}");
                    if (id is not null) walk.UnreadIds.Add(id);
                    else walk.UnidentifiedFolder = true;
                }
            }
        }
    }

    // The previous run's folder map, if it can be read. Anything unreadable is no map at all,
    // so the first scan reports FolderMoves as unknown rather than guessing.
    private void LoadFolderMap()
    {
        try
        {
            if (!File.Exists(folderMapFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllBytes(folderMapFile));
            if (saved is null) return;
            var folders = new Dictionary<string, LocalFolderState>(StringComparer.Ordinal);
            foreach (var (path, id) in saved)
            {
                if (!VaultPath.TryCreate(path, out var folder, out _, paths.Root, int.MaxValue) || folder.Value != path) return;
                folders[path] = new(path, id);
            }
            folderCache = folders;
            folderCacheKnown = true;
            durableFolders = new(saved, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    // The scan before this one was handed to the engine, which is now back for another pass:
    // its folder map becomes the one a restart compares with.
    private void Acknowledge()
    {
        if (!folderCacheKnown) return;
        var map = Map(folderCache);
        if (durableFolders is not null && durableFolders.Count == map.Count &&
            map.All(pair => durableFolders.TryGetValue(pair.Key, out var id) && id == pair.Value)) return;
        if (SaveFolderMap(map)) durableFolders = map;
    }

    // The last run's file map: {"version": 1, "files": {"<path>": {"id", "size", "write", "hash",
    // "hashedAt"}}} with times in ticks. Anything unreadable is no map at all (every file is
    // hashed, as before 0.3.3); an entry whose path the vault no longer takes is skipped.
    private void LoadFileMap()
    {
        try
        {
            if (!File.Exists(fileMapFile)) return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(fileMapFile));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1 || !root.TryGetProperty("files", out var files)) return;
            var map = new Dictionary<VaultPath, LocalFileState>();
            foreach (var entry in files.EnumerateObject())
            {
                if (!VaultPath.TryCreate(entry.Name, out var path, out _, paths.Root, paths.MaximumLength) || path.Value != entry.Name) continue;
                var e = entry.Value;
                map[path] = new(path, e.GetProperty("id").GetString()!, e.GetProperty("size").GetInt64(),
                    new DateTime(e.GetProperty("write").GetInt64(), DateTimeKind.Utc), e.GetProperty("hash").GetString()!,
                    new DateTimeOffset(e.GetProperty("hashedAt").GetInt64(), TimeSpan.Zero));
            }
            saved = map;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException) { saved = []; }
    }

    // The file map as this scan left it (entries it could not read are left out: they are hashed
    // again anyway), write-through and then an atomic rename. Not written: the next scan tries
    // again, and until then a restart hashes more files, never fewer.
    private void SaveFileMap()
    {
        var temp = fileMapFile + ".pending";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                using (var json = new Utf8JsonWriter(output))
                {
                    json.WriteStartObject();
                    json.WriteNumber("version", 1);
                    json.WriteStartObject("files");
                    foreach (var file in cache.Values)
                    {
                        if (file.Unread) continue;
                        json.WriteStartObject(file.Path.Value);
                        json.WriteString("id", file.FileId);
                        json.WriteNumber("size", file.Size);
                        json.WriteNumber("write", file.LastWriteUtc.Ticks);
                        json.WriteString("hash", file.Hash);
                        json.WriteNumber("hashedAt", file.HashedAt.UtcTicks);
                        json.WriteEndObject();
                    }
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                output.Flush(true);
            }
            NativeMethods.Move(temp, fileMapFile, replace: true);
            fileMapDirty = false;
            fileMapWritten = clock.GetTimestamp();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
    }

    // Write-through temp file, then an atomic rename. Not written: the next scan tries again,
    // and until then a restart reports more moves, never fewer.
    private bool SaveFolderMap(Dictionary<string, string?> map)
    {
        var temp = folderMapFile + ".pending";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, map); output.Flush(true); }
            NativeMethods.Move(temp, folderMapFile, replace: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    public void Dispose()
    {
        watcher.Dispose();
        if (fileMapDirty && cacheKnown) SaveFileMap();
    }
}
