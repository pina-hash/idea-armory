using Armory.Core;

namespace Armory.Core.Tests;

// Folding a folder of one's own back into the shared one (0.5.0, ledger 0437): every row of the
// table, that nothing is removed without the same bytes in the shared folder, that nothing is
// overwritten, and that planning the folders as they are after any prefix of the plan never asks
// for a step that would lose bytes (a power cut mid-fold, then the next start).
public sealed class FolderFoldTests
{
    private const string P = "Robot 2027/Drivetrain/Plate.SLDPRT";

    private static FoldStep Step(bool waiting, string? sharedHash, bool free = true, bool open = false, string hash = "aa")
        => FolderFold.StepFor(new FoldSource(P, hash, waiting, open), sharedHash is null ? null : new FoldTarget(sharedHash, free));

    [Fact]
    public void Each_case_of_the_table_gets_its_one_action()
    {
        // Nothing in the shared folder at that path: the file moves in, its record too when it is work.
        Assert.Equal(new FoldStep(P, FoldAction.MoveIn, false), Step(waiting: false, sharedHash: null));
        Assert.Equal(new FoldStep(P, FoldAction.MoveIn, true), Step(waiting: true, sharedHash: null));
        // The same bytes there: the own copy goes; its owner's record comes in only over a free copy.
        Assert.Equal(new FoldStep(P, FoldAction.RemoveDuplicate, false), Step(waiting: false, sharedHash: "AA", free: false));
        Assert.Equal(new FoldStep(P, FoldAction.RemoveDuplicate, true), Step(waiting: true, sharedHash: "aa"));
        Assert.Equal(new FoldStep(P, FoldAction.LeaveConflict, false), Step(waiting: true, sharedHash: "aa", free: false));
        // Different bytes: a team version nobody works on goes aside; the owner's work replaces a
        // free team copy (set aside, never deleted); work over work stays where it is.
        Assert.Equal(new FoldStep(P, FoldAction.SetAsideOwnCopy, false), Step(waiting: false, sharedHash: "bb"));
        Assert.Equal(new FoldStep(P, FoldAction.SetAsideOwnCopy, false), Step(waiting: false, sharedHash: "bb", free: false));
        Assert.Equal(new FoldStep(P, FoldAction.SetAsideTeamCopyThenMoveIn, true), Step(waiting: true, sharedHash: "bb"));
        Assert.Equal(new FoldStep(P, FoldAction.LeaveConflict, false), Step(waiting: true, sharedHash: "bb", free: false));
        // Open in SolidWorks (or unreadable): nothing at all, whatever the shared folder holds.
        foreach (var shared in new[] { null, "aa", "bb" })
            foreach (var waiting in new[] { false, true })
                Assert.Equal(new FoldStep(P, FoldAction.LeaveOpen, false), Step(waiting, shared, open: true));
    }

    [Fact]
    public void A_record_comes_in_only_with_waiting_work_and_a_folder_is_finished_only_when_nothing_is_left()
    {
        var steps = FolderFold.Plan(
            [
                new FoldSource("B/two.SLDPRT", "22", false, false),
                new FoldSource("A/one.SLDPRT", "11", true, false),
                new FoldSource("C/three.SLDASM", "33", true, false),
            ],
            path => path.StartsWith("C/", StringComparison.Ordinal) ? new FoldTarget("99", false) : null);
        Assert.Equal(["A/one.SLDPRT", "B/two.SLDPRT", "C/three.SLDASM"], steps.Select(s => s.Path));
        Assert.Equal([true, false, false], steps.Select(s => s.ImportRecord));
        Assert.All(steps.Where(s => s.ImportRecord), s => Assert.Contains(s.Action, new[] { FoldAction.MoveIn, FoldAction.RemoveDuplicate, FoldAction.SetAsideTeamCopyThenMoveIn }));
        Assert.False(FolderFold.Finished(steps));
        Assert.True(FolderFold.Finished(steps.Take(2)));
        Assert.True(FolderFold.Finished([]));
    }

    // The fold as a little world: two folders of path -> (hash, waiting); an archive that only
    // grows. Every step is carried out the way the host does it, and after every prefix (a power
    // cut) the plan is made again from what the folders hold then. Every byte string that existed
    // at the start still exists somewhere at the end, the own folder ends empty or with only
    // conflicts and open files, and a second full run changes nothing.
    [Fact]
    public void Planning_again_after_any_power_cut_loses_no_bytes_and_ends_where_one_run_ends()
    {
        var random = new Random(437);
        for (var round = 0; round < 400; round++)
        {
            var own = new Dictionary<string, (string Hash, bool Waiting)>(StringComparer.OrdinalIgnoreCase);
            var shared = new Dictionary<string, (string Hash, bool Free)>(StringComparer.OrdinalIgnoreCase);
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < 12; i++)
            {
                var path = $"P{random.Next(3)}/f{i}.SLDPRT";
                var hash = "h" + random.Next(6);
                if (random.Next(3) > 0) own[path] = (hash, random.Next(2) == 0);
                if (random.Next(3) > 0) shared[path] = (random.Next(2) == 0 ? hash : "h" + random.Next(6), random.Next(3) > 0);
                if (random.Next(10) == 0) open.Add(path);
            }
            var before = own.Values.Select(v => v.Hash).Concat(shared.Values.Select(v => v.Hash)).ToHashSet();
            var archive = new List<string>();
            var cut = random.Next(0, 13);
            var done = 0;
            // First run, cut off after `cut` steps; then runs to the end.
            for (var run = 0; run < 3; run++)
            {
                var plan = PlanNow();
                foreach (var step in plan)
                {
                    if (run == 0 && done++ >= cut) break;
                    Apply(step);
                }
            }
            var after = own.Values.Select(v => v.Hash).Concat(shared.Values.Select(v => v.Hash)).Concat(archive).ToHashSet();
            Assert.Subset(after, before);
            Assert.All(own.Keys, path => Assert.True(open.Contains(path) || PlanNow().Single(s => s.Path == path).Action == FoldAction.LeaveConflict, path));
            var settled = PlanNow();
            Assert.All(settled, s => Assert.Contains(s.Action, new[] { FoldAction.LeaveConflict, FoldAction.LeaveOpen }));

            IReadOnlyList<FoldStep> PlanNow() => FolderFold.Plan(own.Select(o => new FoldSource(o.Key, o.Value.Hash, o.Value.Waiting, open.Contains(o.Key))),
                path => shared.TryGetValue(path, out var s) ? new FoldTarget(s.Hash, s.Free) : null);

            void Apply(FoldStep step)
            {
                var file = own[step.Path];
                switch (step.Action)
                {
                    case FoldAction.RemoveDuplicate:
                        Assert.Equal(file.Hash, shared[step.Path].Hash); // never without the same bytes there
                        own.Remove(step.Path);
                        if (step.ImportRecord) shared[step.Path] = (file.Hash, false);
                        break;
                    case FoldAction.MoveIn:
                        Assert.False(shared.ContainsKey(step.Path)); // never over anything
                        shared[step.Path] = (file.Hash, !file.Waiting);
                        own.Remove(step.Path);
                        break;
                    case FoldAction.SetAsideOwnCopy:
                        archive.Add(file.Hash);
                        own.Remove(step.Path);
                        break;
                    case FoldAction.SetAsideTeamCopyThenMoveIn:
                        Assert.True(shared[step.Path].Free); // never over somebody's work
                        archive.Add(shared[step.Path].Hash);
                        shared[step.Path] = (file.Hash, false);
                        own.Remove(step.Path);
                        break;
                }
            }
        }
    }

    [Fact]
    public void The_summary_says_what_moved_what_waits_for_whom_and_what_stayed()
    {
        const string Shared = @"C:\IDEA\Armory";
        Assert.Equal(@"Moved 3 projects into C:\IDEA\Armory, kept 2 files waiting for Maria. Each person's work goes to Armory under their own sign-in when they pick their name.",
            FolderFold.Summary(Shared, 3, [("Maria", 2)], [], 1));
        Assert.Equal(@"Moved 1 project into C:\IDEA\Armory, kept 1 file waiting for Alex and 3 files waiting for Maria. Each person's work goes to Armory under their own sign-in when they pick their name.",
            FolderFold.Summary(Shared, 1, [("Alex", 1), ("Maria", 3), ("Sam", 0)], [], 2));
        Assert.Equal(@"Moved 2 projects into C:\IDEA\Armory.", FolderFold.Summary(Shared, 2, [], [], 1));
        Assert.Equal(@"Everything was already in C:\IDEA\Armory.", FolderFold.Summary(Shared, 0, [], [], 1));
        Assert.Equal(@"1 file stayed in C:\IDEA\Armory-jordan because both copies have work in them or the file is open. Open both, keep the one you want, then delete the other.",
            FolderFold.Summary(Shared, 0, [], [(@"C:\IDEA\Armory-jordan", 1)], 0));
        Assert.Null(FolderFold.Summary(Shared, 0, [], [(@"C:\IDEA\Armory-jordan", 0)], 0));
        foreach (var words in new[] { FolderFold.Summary(Shared, 3, [("Maria", 2)], [(@"C:\IDEA\Armory-jordan", 4)], 1)! })
            Assert.DoesNotContain('\u2014', words);
    }
}
