using System.ComponentModel;
using System.Security.Cryptography;
using Armory.Core;

namespace Armory.Platform.Windows;

public sealed record LocalFileState(VaultPath Path, string FileId, long Size, DateTime LastWriteUtc, string Hash, DateTimeOffset HashedAt);
public sealed record LocalRename(VaultPath Before, VaultPath After, string FileId);
public sealed record LocalScan(IReadOnlyList<LocalFileState> Files, IReadOnlyList<LocalRename> Renames, IReadOnlyList<string> Problems,
    int HashesComputed, bool FullRescan, int OverflowCount);

public sealed class LocalChangeDetector : IDisposable
{
    private readonly WindowsPaths paths;
    private readonly FileSystemWatcher watcher;
    private readonly TimeSpan maximumCacheAge;
    private readonly TimeProvider clock;
    private Dictionary<VaultPath, LocalFileState> cache = [];
    private int fullRescanRequested = 1;
    private int overflowCount;
    private long hints;
    public int OverflowCount => Volatile.Read(ref overflowCount);
    public long HintCount => Interlocked.Read(ref hints);
    // Tests deliberately stall delivery to provoke a real kernel notification overflow.
    internal Action? AfterHint { get; set; }
    public LocalChangeDetector(WindowsPaths paths, TimeSpan? maximumCacheAge = null, int watcherBufferSize = 8192, TimeProvider? clock = null)
    {
        this.paths = paths;
        this.maximumCacheAge = maximumCacheAge ?? TimeSpan.FromMinutes(5);
        if (this.maximumCacheAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumCacheAge));
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(paths.Root);
        watcher = new(paths.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = watcherBufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
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
    private void Hint(object sender, FileSystemEventArgs args)
    {
        if (VaultIgnore.IsIgnored(Path.GetRelativePath(paths.Root, args.FullPath))) return;
        Interlocked.Increment(ref hints);
        AfterHint?.Invoke();
    }
    // Always enumerate the truth. Hints can prompt this method, but are never the inventory.
    public LocalScan Scan(bool fullRescan = false)
    {
        var requested = Interlocked.Exchange(ref fullRescanRequested, 0) != 0;
        var full = fullRescan || requested;
        var now = clock.GetUtcNow();
        var next = new Dictionary<VaultPath, LocalFileState>();
        List<string> problems = [];
        var hashes = 0;
        foreach (var file in Enumerate(paths.Root, problems))
        {
            var relative = Path.GetRelativePath(paths.Root, file);
            if (!VaultPath.TryCreate(relative, out var path, out var problem, paths.Root, paths.MaximumLength))
            { problems.Add($"{relative}: {problem}"); continue; }
            try
            {
                using var handle = new FileStream(WindowsPaths.Extended(file), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!NativeMethods.GetFileInformationByHandle(handle.SafeFileHandle, out var info))
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                var id = NativeMethods.FileId(handle.SafeFileHandle);
                var size = ((long)info.SizeHigh << 32) | info.SizeLow;
                var write = DateTime.FromFileTimeUtc(((long)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
                cache.TryGetValue(path, out var previous);
                var mustHash = previous is null || previous.FileId != id || previous.Size != size || previous.LastWriteUtc != write ||
                    (full && now - previous.HashedAt >= maximumCacheAge) || (requested && OverflowCount > 0);
                var hash = mustHash ? Convert.ToHexStringLower(SHA256.HashData(handle)) : previous!.Hash;
                if (mustHash) hashes++;
                next.Add(path, new(path, id, size, write, hash, mustHash ? now : previous!.HashedAt));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                problems.Add($"{relative}: {error.Message}");
                if (cache.TryGetValue(path, out var previous)) next[path] = previous;
            }
        }
        // Only unique IDs are rename evidence. Hard links deliberately suppress guessing.
        var oldById = cache.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var newById = next.Values.GroupBy(f => f.FileId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var renames = newById.Values.Where(f => oldById.TryGetValue(f.FileId, out var old) && old.Path != f.Path)
            .Select(f => new LocalRename(oldById[f.FileId].Path, f.Path, f.FileId)).OrderBy(r => r.Before).ToArray();
        // Enumeration failure cannot authorize inferring deletions in an unreadable subtree.
        if (problems.Count == 0) cache = next;
        else { foreach (var item in next) cache[item.Key] = item.Value; Interlocked.Exchange(ref fullRescanRequested, 1); }
        return new(cache.Values.OrderBy(f => f.Path).ToArray(), renames, problems, hashes, full, OverflowCount);
    }
    private static IEnumerable<string> Enumerate(string root, List<string> problems)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problems.Add(error.Message); continue; }
            foreach (var entry in entries)
            {
                if (VaultIgnore.IsIgnored(Path.GetRelativePath(root, entry))) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { problems.Add($"Reparse point excluded: {entry}"); continue; }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
    public void Dispose() => watcher.Dispose();
}
