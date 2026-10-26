using System.Security.Cryptography;
using System.Text.Json;
using Armory.Core;

namespace Armory.Agent.Engine;

// The disk the fold works on (FoldDisk on the real disk; a test may give its own). Paths are
// vault-relative with '/' between folders, under a root folder.
public interface IFoldDisk
{
    // Every file under root, outside .armory and the files the engine ignores (desktop.ini,
    // Thumbs.db, SolidWorks' ~$ markers), with its bytes' SHA-256 (null: it could not be read now).
    IReadOnlyList<(string Path, string? Hash)> Files(string root);
    // The file's bytes' SHA-256 now (null: missing or unreadable).
    string? Hash(string root, string path);
    // SolidWorks has the document open (its ~$ marker is beside it).
    bool OpenInSolidWorks(string root, string path);
    // Moves a file, never over another one (an IOException if to is taken), making its folders.
    void Move(string fromRoot, string fromPath, string toFullPath);
    // Removes a file (read-only or not).
    void Delete(string root, string path);
    // Moves a whole folder to a place that does not exist yet.
    void MoveFolder(string from, string to);
    bool FolderExists(string folder);
    bool FileExists(string fullPath);
    // Removes every folder under root that holds no file, then root itself if it is empty.
    void RemoveEmptyFolders(string root);
}

// What one folder of one's own came to: how many project folders things moved into, the files of
// its owner that came in held for them, the files left in it (conflicts, open files), and whether
// the folder is gone.
public sealed record FoldOutcome(string Folder, string? Owner, IReadOnlyList<string> Projects, int HeldFiles, int Left, bool Removed);

// Folds every folder of one's own (C:\IDEA\Armory-alex) back into the computer's one Armory folder
// (0.5.0, ledger 0437; the plan's rules are Armory.Core.FolderFold's). Run by the host at start,
// before any engine runs on either folder, and again at every start until nothing is left. Each
// step is decided from what the disk and the two state documents hold at that moment, so a run cut
// off anywhere (a power cut) is finished by the next one and nothing is done twice:
//
//   1. the owner's records for the files with their waiting work go into the shared folder's state
//      document first, held for them (EngineState.Held), saved before any file moves, so the next
//      person's engine never takes a moved-in file of theirs for its own;
//   2. then each file, re-checked against the disk just before: removed only where the shared
//      folder has the same bytes, moved in only where nothing is, and anything that would be
//      replaced moved aside into the shared folder's .armory\folded\<folder>\ (never deleted);
//   3. a folder with nothing left in it has its own .armory (its journal and kept saves, which
//      hold every save its owner made there) moved into .armory\folded\<folder>\armory, and goes.
public static class FolderFoldRunner
{
    public const string FoldedFolder = "folded";

    public static FoldOutcome Run(string sharedRoot, string ownRoot, IFoldDisk disk, Func<string, IEngineStateStore> stateOf, Action<string>? log = null, DateTimeOffset? now = null)
    {
        var ownName = Path.GetFileName(ownRoot.TrimEnd('\\', '/'));
        var archive = Path.Combine(sharedRoot, ".armory", FoldedFolder, ownName);
        var ownStore = stateOf(ownRoot);
        var own = EngineState.Load(ownStore);
        var owner = own.Email;
        var sharedStore = stateOf(sharedRoot);
        var shared = EngineState.Load(sharedStore);
        var files = disk.Files(ownRoot);
        log?.Invoke($"fold: {ownRoot} ({files.Count} files, {(owner is null ? "nobody's" : owner + "'s")}) into {sharedRoot}");

        // What waits in the own folder is its owner's: a record with work, bytes that are not the
        // version the record last matched, or a file the folder never had a record of.
        bool Waiting(string path, string hash)
        {
            // A folder nobody is known to own has no work that could be theirs, but a copy the shared
            // folder already has can still go (the same bytes are there).
            if (owner is null) return disk.Hash(sharedRoot, path) != hash;
            if (!own.Files.TryGetValue(path, out var st)) return true;
            if (st.Purged) return false;
            return HasWork(st, own) || st.BaseHash != hash;
        }
        // Held already for this folder's owner: an earlier run decided this path is theirs and was cut
        // off before the file moved (a power cut). Held for anyone else: theirs.
        bool HeldForOwner(string path) => owner is not null && shared.Held.TryGetValue(owner, out var mine) && mine.Files.ContainsKey(path);
        bool HeldForOther(string path) => shared.Held.Any(h => (owner is null || !string.Equals(h.Key, owner, StringComparison.OrdinalIgnoreCase)) && h.Value.Files.ContainsKey(path));
        FoldTarget? Target(string path)
        {
            var hash = disk.Hash(sharedRoot, path);
            if (hash is null) return disk.FileExists(Path.Combine(sharedRoot, path)) ? new FoldTarget("", false) : null;
            var free = !HeldForOther(path) && (HeldForOwner(path) ||
                shared.Files.TryGetValue(path, out var st) && !st.Purged && !HasWork(st, shared) && st.BaseHash == hash);
            return new FoldTarget(hash, free);
        }
        var sources = files.Select(f => new FoldSource(f.Path, f.Hash ?? "", f.Hash is not null && Waiting(f.Path, f.Hash), f.Hash is null || disk.OpenInSolidWorks(ownRoot, f.Path))).ToList();
        var plan = FolderFold.Plan(sources, Target).ToList();
        // With nobody known to own the folder, nobody's work can come in held for them: it stays. A
        // record the shared folder keeps at that path with work of its own (or held for anyone) is
        // somebody's too, whatever the disk shows (a file of theirs gone from the disk): it stays.
        bool Taken(string path) => HeldForOther(path) || shared.Files.TryGetValue(path, out var st) && HasWork(st, shared);
        plan = plan.Select(s => s.ImportRecord && (owner is null || Taken(s.Path)) ? s with { Action = FoldAction.LeaveConflict, ImportRecord = false } : s).ToList();

        // 1. The owner's records, held for them, before any file moves.
        var imports = plan.Where(s => s.ImportRecord).ToList();
        if (imports.Count > 0)
        {
            if (!shared.Held.TryGetValue(owner!, out var held)) shared.Held[owner!] = held = new HeldWork { Since = now ?? DateTimeOffset.UtcNow };
            var hashes = sources.ToDictionary(s => s.Path, s => s.Hash, StringComparer.OrdinalIgnoreCase);
            foreach (var step in imports)
            {
                // The shared folder's own note of the team's version at that path (no work in it) gives
                // way: the owner's record says everything about the file from now on.
                shared.Files.Remove(step.Path);
                held.Files[step.Path] = HeldCopy(step.Path, own.Files.GetValueOrDefault(step.Path), own);
                held.Hashes[step.Path] = hashes[step.Path];
            }
            // Their devices here are theirs on the shared folder too, so their check outs stay theirs.
            if (!shared.People.TryGetValue(owner!, out var person)) shared.People[owner!] = person = new PersonState();
            foreach (var device in new[] { own.DeviceId }.OfType<Guid>().Concat(own.FormerDevices).Concat(own.People.Values.SelectMany(p => p.Devices)))
                if (!person.Devices.Contains(device)) person.Devices.Add(device);
            sharedStore.Save(shared.Serialize());
            log?.Invoke($"fold: {imports.Count} of {owner}'s files held for them in {sharedRoot}");
        }

        // 2. Each file, checked against the disk again just before it moves.
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var left = 0;
        foreach (var step in plan)
        {
            var source = sources.First(s => string.Equals(s.Path, step.Path, StringComparison.OrdinalIgnoreCase));
            try
            {
                switch (step.Action)
                {
                    case FoldAction.RemoveDuplicate:
                        if (disk.Hash(sharedRoot, step.Path) != source.Hash || disk.Hash(ownRoot, step.Path) != source.Hash) { left++; continue; }
                        disk.Delete(ownRoot, step.Path);
                        log?.Invoke($"fold: removed {step.Path} from {ownName}; the same bytes are in {sharedRoot}");
                        break;
                    case FoldAction.MoveIn:
                        if (disk.FileExists(Path.Combine(sharedRoot, step.Path))) { left++; continue; }
                        disk.Move(ownRoot, step.Path, Path.Combine(sharedRoot, step.Path));
                        projects.Add(Top(step.Path));
                        log?.Invoke($"fold: moved {step.Path} into {sharedRoot}");
                        break;
                    case FoldAction.SetAsideOwnCopy:
                        disk.Move(ownRoot, step.Path, Aside(disk, archive, "own", step.Path));
                        log?.Invoke($"fold: {step.Path} from {ownName} was an older or newer team version; set aside in {archive}");
                        break;
                    case FoldAction.SetAsideTeamCopyThenMoveIn:
                        var target = Target(step.Path);
                        if (target is null || !target.Free) { left++; continue; }
                        disk.Move(sharedRoot, step.Path, Aside(disk, archive, "team", step.Path));
                        disk.Move(ownRoot, step.Path, Path.Combine(sharedRoot, step.Path));
                        projects.Add(Top(step.Path));
                        log?.Invoke($"fold: moved {owner}'s {step.Path} into {sharedRoot}; the team's copy it replaced is set aside in {archive}");
                        break;
                    default:
                        left++;
                        log?.Invoke($"fold: left {step.Path} in {ownName} ({(step.Action == FoldAction.LeaveOpen ? "open or unreadable" : "both copies have work in them")})");
                        break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Open in SolidWorks, or held by another program: it stays, for the next start.
                left++;
                log?.Invoke($"fold: {step.Path} stays in {ownName} for now: {error.Message}");
            }
        }

        // 3. Nothing left: the folder's own records and kept saves go aside, and the folder goes.
        var removed = false;
        if (left == 0 && disk.Files(ownRoot).Count == 0)
        {
            try
            {
                var keep = Path.Combine(ownRoot, ".armory");
                if (disk.FolderExists(keep)) disk.MoveFolder(keep, FreeFolder(disk, Path.Combine(archive, "armory")));
                disk.RemoveEmptyFolders(ownRoot);
                removed = !disk.FolderExists(ownRoot);
                log?.Invoke(removed ? $"fold: {ownRoot} is folded into {sharedRoot} and removed; its records are in {archive}" : $"fold: {ownRoot} is empty but could not be removed yet");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"fold: {ownRoot} is empty but could not be removed yet: {error.Message}");
            }
        }
        return new FoldOutcome(ownRoot, owner, projects.Order(StringComparer.OrdinalIgnoreCase).ToList(), imports.Count, left, removed);
    }

    // The owner's record as it comes into the shared folder, held for them: the same file, base,
    // check out and lock knowledge. What pointed into the own folder's journal and kept saves (saves
    // not sent, a write in flight) is left behind: the file's bytes on disk are its latest save, and
    // the owner's engine keeps them again (a fresh capture under their sign-in) when they are back.
    // Every earlier save stays in .armory\folded\<folder>\armory.
    private static FileState HeldCopy(string path, FileState? from, EngineState own)
    {
        if (from is null) return new FileState { Path = path, ProjectId = ProjectOf(own, path) };
        var copy = JsonSerializer.Deserialize<FileState>(JsonSerializer.SerializeToUtf8Bytes(from, EngineState.Options), EngineState.Options)!;
        copy.Path = path;
        copy.Entries.Clear();
        copy.Drafts.Clear();
        copy.CreateEntry = null;
        copy.DeleteEntry = null;
        copy.MarkerEntry = null;
        copy.Inflight = null;
        copy.LastCaptured = null;
        copy.Sides.Clear();
        return copy;
    }

    private static Guid ProjectOf(EngineState state, string path)
    {
        var top = Top(path);
        return state.Projects.Values.FirstOrDefault(p => string.Equals(p.Folder, top, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;
    }

    // The same test as SyncEngine.HasWork, on a document with no engine.
    private static bool HasWork(FileState st, EngineState state)
        => st.AppliedOwnership == LockOwnership.ThisDevice || st.CheckOut is not null || st.Request != CheckoutRequest.None || st.AutoCheckIn || st.TransientLock ||
           st.Entries.Concat(st.Drafts).Concat(new[] { st.CreateEntry, st.DeleteEntry, st.MarkerEntry }.OfType<string>()).Any(e => !state.Completed.Contains(e)) ||
           st.Inflight is not null || st.LocalMoveTo is not null;

    private static string Top(string path) => path.Split('/')[0];

    // Where a copy goes aside: archive\<kind>\<path>, or with " (2)", " (3)" before the extension
    // when that place is taken (a second run never writes over the first one's copy).
    private static string Aside(IFoldDisk disk, string archive, string kind, string path)
    {
        var full = Path.Combine(archive, kind, path.Replace('/', Path.DirectorySeparatorChar));
        if (!disk.FileExists(full)) return full;
        var folder = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        for (var n = 2; ; n++)
        {
            var next = Path.Combine(folder, $"{name} ({n}){extension}");
            if (!disk.FileExists(next)) return next;
        }
    }

    private static string FreeFolder(IFoldDisk disk, string folder)
    {
        if (!disk.FolderExists(folder)) return folder;
        for (var n = 2; ; n++) if (!disk.FolderExists($"{folder} ({n})")) return $"{folder} ({n})";
    }
}

// The real disk (portable: Windows in the app, any platform in tests).
public sealed class FoldDisk : IFoldDisk
{
    public IReadOnlyList<(string Path, string? Hash)> Files(string root)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        List<(string, string?)> found = [];
        foreach (var full in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (Ignored(relative)) continue;
            found.Add((relative, HashOf(full)));
        }
        return found.OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The engine's own ignore list (VaultIgnore in the Windows platform): its records, Explorer's
    // own files and SolidWorks' ~$ markers.
    internal static bool Ignored(string relative)
        => relative.Split('/').Any(segment =>
            segment.StartsWith("~$", StringComparison.OrdinalIgnoreCase) || segment.Equals(".armory", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase));

    public string? Hash(string root, string path) => HashOf(Full(root, path));

    private static string? HashOf(string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool OpenInSolidWorks(string root, string path)
    {
        var full = Full(root, path);
        return File.Exists(Path.Combine(Path.GetDirectoryName(full)!, "~$" + Path.GetFileName(full)));
    }

    public void Move(string fromRoot, string fromPath, string toFullPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(toFullPath)!);
        File.Move(Full(fromRoot, fromPath), toFullPath, overwrite: false);
    }

    public void Delete(string root, string path)
    {
        var full = Full(root, path);
        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
        File.Delete(full);
    }

    public void MoveFolder(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to.TrimEnd('\\', '/'))!);
        Directory.Move(from, to);
    }

    public bool FolderExists(string folder) => Directory.Exists(folder);
    public bool FileExists(string fullPath) => File.Exists(fullPath);

    // Never a recursive delete: Explorer's own desktop.ini and Thumbs.db are removed one by one, and
    // only folders with nothing in them go. A SolidWorks ~$ marker, or any other file, keeps its
    // folder (and the root) where it is.
    public void RemoveEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).ToList())
        {
            var name = Path.GetFileName(file);
            if (!name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        foreach (var folder in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(f => f.Length).ToList())
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
}
