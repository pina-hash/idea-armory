using System.Security.Cryptography;
using System.Text.Json;

namespace Armory.Agent;

// The Armory folder can never be a project folder or sit inside one (0.4.2, the field round of
// 2026-10-22). On IDEA-06 a student chose C:\IDEA\Armory\FRC 2026 Off-Season, the project folder
// of the Armory folder C:\IDEA\Armory, as the Armory folder: Armory downloaded all 1,496 project
// files again into ...\FRC 2026 Off-Season\FRC 2026 Off-Season\, and the 1,498 files already there
// became "outside every project". Settings now refuses or corrects such a folder (VaultRootRule),
// and a computer already in that state is offered one Fix it (VaultRootRepair) and syncs nothing
// until then, so it never uploads the doubled folder.
//
// What marks the folders on disk: an Armory folder holds Armory's own ".armory" folder (its state,
// journal and safe copies). A project folder holds no marker of its own: it is a top-level folder
// of an Armory folder named for a project (ProjectState.Folder), so it is recognized by its name
// and its contents (a top-level name the project's tree has).

// A project as the rule sees it: its folder name and the names at the top of its tree.
internal sealed record ProjectShape(string Folder, IReadOnlySet<string> TopNames);

// What the rule found for a folder. UseInstead: the folder Armory uses instead (the Armory folder
// around it, or the folder that holds the project folder). Refused: no folder can be used instead
// (a project folder at the top of a drive). Words: one plain sentence for the student.
internal sealed record VaultRootVerdict(string? UseInstead, string? Project, string Words, bool Refused = false)
{
    internal static readonly VaultRootVerdict Fine = new(null, null, "");
    internal bool Ok => UseInstead is null && !Refused;
}

// The disk facts the rule reads (VaultFolderFacts on the real disk, a test's own otherwise).
internal interface IVaultFolderFacts
{
    // The folder holds Armory's own ".armory" folder: it is (or was) an Armory folder.
    bool HoldsArmory(string folder);
    // The names directly inside the folder (files and folders), or none when it is missing.
    IReadOnlyCollection<string> Children(string folder);
}

internal sealed class VaultFolderFacts : IVaultFolderFacts
{
    internal static readonly VaultFolderFacts Disk = new();

    public bool HoldsArmory(string folder)
    {
        try { return Directory.Exists(Path.Combine(folder, ".armory")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public IReadOnlyCollection<string> Children(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().Take(10_000).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}

internal static class VaultRootRule
{
    // A folder Armory would use, judged against the account's projects and the disk:
    // 1. Inside an Armory folder (a folder above it holds ".armory"): that Armory folder is used
    //    (the outermost one, when they nest).
    // 2. Else a project folder, or inside one: a folder named for one of the account's projects
    //    whose contents match that project's tree (a top-level name in common). The folder that
    //    holds it is used; at the top of a drive there is none, so the choice is refused.
    internal static VaultRootVerdict Judge(string root, IReadOnlyList<ProjectShape> projects, IVaultFolderFacts facts)
    {
        var (prefix, separator, segments) = Split(root);
        if (segments.Count == 0) return VaultRootVerdict.Fine;
        string Join(int count) => count == 0 ? prefix : prefix + string.Join(separator, segments.Take(count));
        // 1. The outermost Armory folder above it.
        for (var i = 1; i < segments.Count; i++)
        {
            var above = Join(i);
            if (!facts.HoldsArmory(above)) continue;
            var next = segments[i];
            var project = projects.FirstOrDefault(p => Same(p.Folder, next))?.Folder;
            var words = project is not null && i + 1 == segments.Count
                ? $"{root} is the {project} project folder inside your Armory folder, so Armory uses {above}, the folder that holds all your projects."
                : project is not null
                    ? $"{root} is inside the {project} project folder of your Armory folder, so Armory uses {above}, the folder that holds all your projects."
                    : $"{root} is inside your Armory folder, so Armory uses {above}, the folder that holds all your projects.";
            return new VaultRootVerdict(above, project, words);
        }
        // 2. The outermost folder that is a project folder by its name and contents.
        for (var i = 1; i <= segments.Count; i++)
        {
            var name = segments[i - 1];
            var project = projects.FirstOrDefault(p => Same(p.Folder, name));
            if (project is null || project.TopNames.Count == 0) continue;
            var folder = Join(i);
            if (!facts.Children(folder).Any(project.TopNames.Contains)) continue;
            if (i == 1)
                return new VaultRootVerdict(null, project.Folder,
                    $"{folder} is the {project.Folder} project folder. Choose the folder that holds your project folders instead, like C:\\IDEA\\Armory.", Refused: true);
            var parent = Join(i - 1);
            var words = i == segments.Count
                ? $"{root} is the {project.Folder} project folder, so Armory uses the folder that holds it, {parent}."
                : $"{root} is inside the {project.Folder} project folder, so Armory uses the folder that holds it, {parent}.";
            return new VaultRootVerdict(parent, project.Folder, words);
        }
        return VaultRootVerdict.Fine;
    }

    // The projects as the window shows them: each project's folder and the names at its top
    // (its top-level folders and the files directly in it).
    internal static IReadOnlyList<ProjectShape> Shapes(IEnumerable<Armory.Agent.Engine.View.ProjectView> projects)
        => projects.Select(p => new ProjectShape(p.Name, p.Folders
            .SelectMany(f => f.Path.Length == 0 ? f.Files.Select(file => file.Name) : f.Path.Contains('/') ? [] : new[] { f.Name })
            .ToHashSet(StringComparer.OrdinalIgnoreCase))).ToArray();

    // Two lists of shapes as one (the same project folder's names together).
    internal static IReadOnlyList<ProjectShape> Merge(params IReadOnlyList<ProjectShape>[] lists)
        => lists.SelectMany(l => l).GroupBy(p => p.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectShape(g.First().Folder, g.SelectMany(p => p.TopNames).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToArray();

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // "C:\IDEA\Armory" is ("C:\", '\', [IDEA, Armory]); "/tmp/x" is ("/", '/', [tmp, x]).
    internal static (string Prefix, char Separator, List<string> Segments) Split(string path)
    {
        var separator = path.Contains('\\') ? '\\' : '/';
        string prefix;
        string rest;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') { prefix = path[..3]; rest = path[3..]; }
        else if (path.StartsWith(separator)) { prefix = separator.ToString(); rest = path[1..]; }
        else { prefix = ""; rest = path; }
        return (prefix, separator, rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).ToList());
    }
}

// What an Armory folder's own state document says that the rule and the repair need: its project
// folders, the names at the top of each, and the hash of the version each file was last in step
// with (its base). Read straight from <root>\.armory\state.json; a missing or unreadable one is
// empty, never an error.
internal sealed class VaultStateFile
{
    internal static readonly VaultStateFile Empty = new([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private VaultStateFile(IReadOnlyList<ProjectShape> projects, IReadOnlyDictionary<string, string> bases)
    {
        Projects = projects;
        Bases = bases;
    }

    internal IReadOnlyList<ProjectShape> Projects { get; }
    // Vault-relative path ("Project/Folder/Part.SLDPRT") to the hash of its base version.
    internal IReadOnlyDictionary<string, string> Bases { get; }

    internal static VaultStateFile Read(string root)
    {
        var file = Path.Combine(root, ".armory", "state.json");
        try
        {
            if (!File.Exists(file)) return Empty;
            return Parse(File.ReadAllBytes(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Empty; }
    }

    // A state document's bytes (null or unreadable: empty).
    internal static VaultStateFile Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return Empty;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var top = document.RootElement;
            var folders = new List<string>();
            if (top.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
                foreach (var project in projects.EnumerateObject())
                    if (project.Value.ValueKind == JsonValueKind.Object && project.Value.TryGetProperty("folder", out var folder) && folder.GetString() is { Length: > 0 } name)
                        folders.Add(name);
            var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tops = folders.ToDictionary(f => f, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (top.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
                foreach (var record in files.EnumerateObject())
                {
                    var key = record.Name;
                    if (record.Value.ValueKind == JsonValueKind.Object && record.Value.TryGetProperty("baseHash", out var hash) && hash.GetString() is { Length: > 0 } h)
                        bases[key] = h;
                    var parts = key.Split('/');
                    if (parts.Length >= 2 && tops.TryGetValue(parts[0], out var names)) names.Add(parts[1]);
                }
            return new VaultStateFile(tops.Select(t => new ProjectShape(t.Key, t.Value)).ToArray(), bases);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return Empty; }
    }
}

// What a Fix it did, or why it stopped. Problem: one plain sentence when it stopped before the end
// (nothing was switched; running it again goes on from where it stopped).
internal sealed record VaultRepairResult(int Moved, int AlreadyThere, int KeptCopies, string? KeptFolder, string? Problem)
{
    internal bool Done => Problem is null;
}

// Fix it (0.4.2): Armory goes back to the folder the rule names, and what the wrong folder holds is
// folded into it, file by file, with nothing uploaded twice and no change lost:
// - Each project folder Q in the wrong Armory folder (bad\Q, what Armory downloaded there) goes to
//   target\Q. On IDEA-06 bad is target\P, so bad\P\X lands at bad\X: the doubled folder folds into
//   the project folder it sits in.
// - A file not there yet is moved. A byte-identical copy already there makes the moved one a
//   duplicate: it is deleted (its bytes are in place).
// - Two different copies: a copy equal to the version Armory last had in step for that place (its
//   base, from that folder's state document) is the team's version, kept in Armory's history, so
//   it gives way to the other. Two changed copies are both kept: the newer one in place, the other
//   in a kept-copies folder beside the Armory folder, outside it (never uploaded), at the same path.
//   Nothing is ever written over.
// - The wrong folder's own .armory (its state, journal and safe copies of saves) becomes the right
//   folder's when that one has none (its records name the same places after the fold), and is set
//   aside inside the right folder's .armory otherwise. Never deleted.
// Safe to run again after it stopped part way, and when the doubled folder (or the whole wrong
// folder) was already moved away by hand: only what is still there is folded.
internal static class VaultRootRepair
{
    internal static VaultRepairResult Run(string bad, string target, DateTimeOffset now, Action<string>? log = null, CancellationToken ct = default)
    {
        int moved = 0, same = 0, kept = 0;
        string? keptFolder = null;
        var badState = VaultStateFile.Read(bad);
        var targetState = VaultStateFile.Read(target);
        var targetHeldArmory = VaultFolderFacts.Disk.HoldsArmory(target);
        // The project folders Armory made in the wrong folder: its own state names them; a folder
        // named for a project the right folder knows counts too.
        var projects = VaultRootRule.Merge(badState.Projects, targetState.Projects).Select(p => p.Folder).ToArray();
        string? current = null;
        try
        {
            if (Directory.Exists(bad))
            {
                foreach (var project in projects)
                {
                    var from = Path.Combine(bad, project);
                    if (!Directory.Exists(from)) continue;
                    // SolidWorks has a document open from it: nothing is moved until it is closed.
                    if (Directory.EnumerateFiles(from, "~$*", SearchOption.AllDirectories).FirstOrDefault() is { } open)
                        return Stopped($"SolidWorks still has {Path.GetFileName(open)[2..]} open. Close SolidWorks, then click Fix it again.");
                }
                foreach (var project in projects)
                {
                    var from = Path.Combine(bad, project);
                    if (!Directory.Exists(from)) continue;
                    var to = Path.Combine(target, project);
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        current = file;
                        var relative = Path.GetRelativePath(from, file);
                        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.Equals(".armory", StringComparison.OrdinalIgnoreCase))) continue;
                        // Windows' own folder files stay behind and go with their empty folder.
                        if (IsWindowsOwn(Path.GetFileName(file))) continue;
                        var key = project + "/" + relative.Replace('\\', '/');
                        var place = Path.Combine(to, relative);
                        if (!File.Exists(place))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(place)!);
                            File.Move(file, place, overwrite: false);
                            moved++;
                            continue;
                        }
                        var hereHash = Hash(file);
                        var placeHash = Hash(place);
                        if (hereHash == placeHash)
                        {
                            Delete(file);
                            same++;
                            continue;
                        }
                        var hereChanged = !badState.Bases.TryGetValue(key, out var hereBase) || hereBase != hereHash;
                        var placeChanged = !targetState.Bases.TryGetValue(key, out var placeBase) || placeBase != placeHash;
                        if (!hereChanged && placeChanged)
                        {
                            // The moved copy is the team's version, in its history: the changed one stays.
                            Delete(file);
                            same++;
                            continue;
                        }
                        if (hereChanged && !placeChanged)
                        {
                            Delete(place);
                            File.Move(file, place, overwrite: false);
                            moved++;
                            continue;
                        }
                        if (!hereChanged && !placeChanged)
                        {
                            // Two of the team's versions: the newer one (by its date) stays.
                            if (File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(place))
                            {
                                Delete(place);
                                File.Move(file, place, overwrite: false);
                                moved++;
                            }
                            else
                            {
                                Delete(file);
                                same++;
                            }
                            continue;
                        }
                        // Both changed: the newer stays, the other is kept beside the Armory folder.
                        keptFolder ??= KeptFolder(target, now);
                        var aside = Free(Path.Combine(keptFolder, project, relative));
                        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                        if (File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(place))
                        {
                            File.Move(place, aside, overwrite: false);
                            File.Move(file, place, overwrite: false);
                            moved++;
                        }
                        else File.Move(file, aside, overwrite: false);
                        kept++;
                    }
                    current = null;
                    RemoveEmpty(from);
                }
                // The wrong folder's own records: the right folder's when it has none, else set aside.
                var records = Path.Combine(bad, ".armory");
                if (Directory.Exists(records))
                {
                    current = records;
                    if (!targetHeldArmory) Directory.Move(records, Path.Combine(target, ".armory"));
                    else
                    {
                        var aside = Free(Path.Combine(target, ".armory", "set-aside", now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)));
                        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                        Directory.Move(records, aside);
                    }
                    current = null;
                }
            }
            log?.Invoke($"fix the Armory folder: {bad} folded into {target}: {moved:N0} moved, {same:N0} already there, {kept:N0} kept copies" +
                (keptFolder is null ? "" : " in " + keptFolder));
            return new VaultRepairResult(moved, same, kept, keptFolder, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("fix the Armory folder stopped at " + (current ?? bad) + ": " + error.Message);
            var name = current is null ? null : Path.GetFileName(current);
            return Stopped(name is null
                ? "Armory couldn't finish fixing its folder. Close SolidWorks and File Explorer windows in it, then click Fix it again."
                : $"Armory couldn't move {name}. Close any program using it, then click Fix it again.");
        }

        VaultRepairResult Stopped(string words) => new(moved, same, kept, keptFolder, words);
    }

    // Beside the Armory folder, never inside it: C:\IDEA\Armory kept copies 2026-10-22.
    internal static string KeptFolder(string target, DateTimeOffset now)
        => Path.TrimEndingDirectorySeparator(target) + " kept copies " + now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // The path, or "name (2).ext", "name (3).ext" while that one is taken.
    private static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // Folders left with nothing but Windows' own folder files are removed, deepest first.
    private static void RemoveEmpty(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var child in Directory.EnumerateDirectories(folder)) RemoveEmpty(child);
        var left = Directory.EnumerateFileSystemEntries(folder).ToArray();
        if (left.Any(e => Directory.Exists(e) || !IsWindowsOwn(Path.GetFileName(e)))) return;
        foreach (var file in left) { File.SetAttributes(file, FileAttributes.Normal); Delete(file); }
        Directory.Delete(folder);
    }

    // Armory keeps files nobody has checked out read-only: the bit goes first, or Windows refuses.
    private static void Delete(string file)
    {
        var attributes = File.GetAttributes(file);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        File.Delete(file);
    }

    private static bool IsWindowsOwn(string name) => name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    // SHA-256, lowercase hex: the same address Armory gives a file's bytes (ContentAddress).
    private static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
