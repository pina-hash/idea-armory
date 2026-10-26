using System.Text.Json;
using Armory.Agent.Engine;
using Rules = Armory.Core.SharedComputer;

namespace Armory.Agent;

// The first start of 0.5.0 on a lab computer (ledger 0437; docs/agent/PROFILES.md, "Carrying
// existing computers forward"). Armory 0.3.3 to 0.4.x gave a student a folder of their own
// (C:\IDEA\Armory-alex) when the shared folder held someone else's work; 0.5.0 has one folder per
// project per computer. At every start, before any engine runs, each such folder is folded into the
// computer's Armory folder (FolderFoldRunner, the rules in Armory.Core.FolderFold), and the next
// person sees what happened in one sentence on Home for a week ("Moved 3 projects into
// C:\IDEA\Armory, kept 2 files waiting for Maria."). A fold cut off by a power cut is finished by
// the next start; one that finds nothing to do says nothing.
internal sealed partial class AgentHost
{
    private static readonly TimeSpan FoldNoteFor = TimeSpan.FromDays(7);
    private const string FoldNoteFile = "note.json";
    private string? foldNote;

    private sealed record FoldNote(string Words, DateTimeOffset At);

    // Under the lifecycle gate, before the first runtime starts (StartAsync).
    private void FoldOwnFolders()
    {
        var root = Settings.VaultRoot;
        // A computer whose shared mode was turned off while its student worked in a folder of their
        // own (settings point at C:\IDEA\Armory-alex): that folder folds into C:\IDEA\Armory, and
        // the computer goes on there.
        if (!SharedComputer && SharedRootOf(root) is { } around)
        {
            log.Info($"fold: {root} is a folder of one's own beside {around}; Armory goes on in {around}");
            SaveSettingsValue(settings with { VaultRoot = around });
            root = around;
        }
        var own = OwnFolders(root);
        if (own.Count > 0) log.Info($"fold: {own.Count} {(own.Count == 1 ? "folder" : "folders")} of one's own beside {root}: {string.Join(", ", own)}");
        List<FoldOutcome> outcomes = [];
        var disks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [parts.DiskFolder(root)] = root };
        foreach (var folder in own) disks[parts.DiskFolder(folder)] = folder;
        foreach (var folder in own)
        {
            try
            {
                outcomes.Add(FolderFoldRunner.Run(parts.DiskFolder(root), parts.DiskFolder(folder), parts.FoldDisk,
                    disk => parts.StateOf(disks.GetValueOrDefault(disk) ?? disk), line => log.Info(line), Now));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Nothing of it is lost: the folder stays as it is, and the next start tries again.
                log.Error("fold: " + folder + " could not be folded now; it stays as it is", error);
            }
        }
        // Everyone works in the one folder from now on.
        if (profiles is not null)
            SaveIndex(ix => ix with { Profiles = ix.Profiles.Select(p => Rules.SameFolder(p.Folder, root) ? p : p with { Folder = root, Waiting = null }).ToList() });
        var words = SummaryOf(root, outcomes);
        if (words is not null)
        {
            log.Info("fold: " + words);
            WriteFoldNote(root, new FoldNote(words, Now));
        }
        foldNote = ReadFoldNote(root);
    }

    // The Armory folder a folder of one's own sits beside (C:\IDEA\Armory for C:\IDEA\Armory-alex),
    // when that one is an Armory folder (its own records are there); null otherwise.
    private string? SharedRootOf(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        for (var dash = trimmed.IndexOf('-', slash + 1); dash > 0; dash = trimmed.IndexOf('-', dash + 1))
        {
            var candidate = trimmed[..dash];
            if (Rules.IsOwnFolder(candidate, trimmed) && IsArmoryFolder(candidate)) return candidate;
        }
        return null;
    }

    // Every folder of one's own beside root that holds Armory's records, and every one a profile
    // names (a folder whose records are gone still has files to bring back).
    private List<string> OwnFolders(string root)
    {
        var found = new List<string>();
        void Add(string folder)
        {
            if (!Rules.IsOwnFolder(root, folder) || found.Any(f => Rules.SameFolder(f, folder))) return;
            if (parts.FoldDisk.FolderExists(parts.DiskFolder(folder))) found.Add(folder.TrimEnd('\\', '/'));
        }
        lock (profileGate) foreach (var p in index.Profiles) Add(p.Folder);
        foreach (var folder in parts.FoldersBeside(root)) if (IsArmoryFolder(folder)) Add(folder);
        return found;
    }

    private bool IsArmoryFolder(string folder)
    {
        try { return parts.StateOf(folder).Load() is { Length: > 0 }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // The one sentence for Home, with first names from the profiles here (else from the address).
    private string? SummaryOf(string root, IReadOnlyList<FoldOutcome> outcomes)
    {
        if (outcomes.Count == 0) return null;
        var projects = outcomes.SelectMany(o => o.Projects).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var waiting = outcomes.Where(o => o.Owner is not null && o.HeldFiles > 0).GroupBy(o => o.Owner!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (FirstOf(NameFor(g.Key)), g.Sum(o => o.HeldFiles))).ToList();
        var left = outcomes.Where(o => o.Left > 0).Select(o => (o.Folder, o.Left)).ToList();
        var removed = outcomes.Count(o => o.Removed);
        if (projects == 0 && waiting.Count == 0 && left.Count == 0 && removed == 0) return null;
        return Armory.Core.FolderFold.Summary(root, projects, waiting, left, removed);
    }

    private string NameFor(string email)
    {
        lock (profileGate) return index.FindEmail(email) is { } known ? NameOf(known) : Rules.NameFromEmail(email);
    }

    private string FoldNotePath(string root) => Path.Combine(parts.DiskFolder(root), ".armory", FolderFoldRunner.FoldedFolder, FoldNoteFile);

    private void WriteFoldNote(string root, FoldNote note)
    {
        try
        {
            var path = FoldNotePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(note));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Error("fold: could not keep the note for Home", error); }
    }

    // The note, while it is under a week old.
    private string? ReadFoldNote(string root)
    {
        try
        {
            var path = FoldNotePath(root);
            if (!File.Exists(path)) return null;
            var note = JsonSerializer.Deserialize<FoldNote>(File.ReadAllBytes(path));
            return note is { Words.Length: > 0 } && Now - note.At < FoldNoteFor ? note.Words : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // For a test: the note as Home shows it.
    internal string? FoldNoteNow => foldNote;
}
