namespace Armory.Core;

// Folding a folder of one's own back into the computer's one Armory folder (0.5.0, ledger 0437).
// Armory 0.3.3 to 0.4.x gave a student C:\IDEA\Armory-alex when the shared folder held someone
// else's work; 0.5.0 has one folder per project per computer, so on its first start every such
// folder is folded into C:\IDEA\Armory. This is the plan, file by file, from what the two folders
// hold now; the host carries it out (FolderFoldRunner) and plans again on every start until nothing
// is left, so a power cut in the middle is finished by the next start and nothing is done twice.
//
// The rules, all of them about never losing bytes:
//   - a file is removed from the own folder only when the shared folder has the very same bytes
//     at the same path (proven by hash);
//   - nothing is ever overwritten: bytes that would be replaced are moved aside into the shared
//     folder's .armory\folded\ (a rename on the same disk), never deleted;
//   - a file with its owner's waiting work (checked out, a save not sent, changed, new) comes in
//     only where the shared folder's copy is the team's version that nobody has work on, and its
//     record comes in held for its owner, so it is sent under their sign-in and nobody else's;
//   - where both copies are somebody's work, or SolidWorks has the file open, nothing moves: it
//     stays where it is and the summary says so.

// One file in the own folder (a vault-relative path): its bytes' hash, whether it holds its
// owner's waiting work, and whether it is open (SolidWorks' ~$ marker beside it, or it could not
// be read).
public sealed record FoldSource(string Path, string Hash, bool Waiting, bool Open);

// The shared folder's file at that path: its bytes' hash, and Free when those bytes are a team
// version nobody has waiting work on there (an untracked file is not free: it may be someone's).
public sealed record FoldTarget(string Hash, bool Free);

public enum FoldAction
{
    // The shared folder has these bytes at this path: the own copy goes.
    RemoveDuplicate,
    // The shared folder has nothing at this path: the file moves in.
    MoveIn,
    // The own copy is an older or newer team version than the shared one, nobody's work: it goes
    // aside into .armory\folded\, the shared copy stays.
    SetAsideOwnCopy,
    // The owner's work, over a team copy nobody has work on: the team copy goes aside into
    // .armory\folded\, then the owner's file moves in.
    SetAsideTeamCopyThenMoveIn,
    // Both copies are somebody's work: both stay where they are.
    LeaveConflict,
    // SolidWorks has it open, or it could not be read: it waits for the next start.
    LeaveOpen,
}

// ImportRecord: the owner's record for this file comes into the shared folder's state, held for
// them (only ever for a file with waiting work that the plan brings in or finds already there).
public sealed record FoldStep(string Path, FoldAction Action, bool ImportRecord);

public static class FolderFold
{
    public static IReadOnlyList<FoldStep> Plan(IEnumerable<FoldSource> own, Func<string, FoldTarget?> shared)
    {
        List<FoldStep> steps = [];
        foreach (var file in own.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            steps.Add(StepFor(file, shared(file.Path)));
        return steps;
    }

    public static FoldStep StepFor(FoldSource file, FoldTarget? target)
    {
        if (file.Open) return new(file.Path, FoldAction.LeaveOpen, false);
        if (target is null) return new(file.Path, FoldAction.MoveIn, file.Waiting);
        var same = string.Equals(file.Hash, target.Hash, StringComparison.OrdinalIgnoreCase);
        if (!file.Waiting) return new(file.Path, same ? FoldAction.RemoveDuplicate : FoldAction.SetAsideOwnCopy, false);
        if (!target.Free) return new(file.Path, FoldAction.LeaveConflict, false);
        return new(file.Path, same ? FoldAction.RemoveDuplicate : FoldAction.SetAsideTeamCopyThenMoveIn, true);
    }

    // Whether the own folder is finished with: every file in it went somewhere (nothing was left
    // for a conflict or an open file), so its own .armory can go aside and the folder can go.
    public static bool Finished(IEnumerable<FoldStep> steps) => steps.All(s => s.Action is not (FoldAction.LeaveConflict or FoldAction.LeaveOpen));

    // The one screen the next person sees after the update ("Moved 3 projects into
    // C:\IDEA\Armory, kept 2 saves waiting for Maria."): projects is how many project folders
    // anything moved into; waiting is each owner's first name and how many of their files came in
    // held for them; left is each own folder that still holds files, and how many. Null when
    // nothing happened worth saying.
    public static string? Summary(string sharedRoot, int projects, IReadOnlyList<(string Name, int Files)> waiting, IReadOnlyList<(string Folder, int Files)> left, int removedFolders)
    {
        List<string> sentences = [];
        var kept = waiting.Where(w => w.Files > 0).ToList();
        if (projects > 0 || removedFolders > 0 || kept.Count > 0)
        {
            var moved = projects > 0 ? $"Moved {Count(projects, "project", "projects")} into {sharedRoot}" : $"Everything was already in {sharedRoot}";
            sentences.Add(kept.Count == 0 ? moved + "." :
                moved + ", kept " + Join(kept.Select(w => $"{Count(w.Files, "file", "files")} waiting for {w.Name}").ToList()) + ".");
            if (kept.Count > 0) sentences.Add("Each person's work goes to Armory under their own sign-in when they pick their name.");
        }
        foreach (var (folder, files) in left.Where(l => l.Files > 0))
            sentences.Add($"{Count(files, "file", "files")} stayed in {folder} because both copies have work in them or the file is open. Open both, keep the one you want, then delete the other.");
        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    private static string Count(int n, string one, string many) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => parts[0] + " and " + parts[1],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
