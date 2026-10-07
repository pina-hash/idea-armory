namespace Armory.Platform.Windows.Tests;

// LocalChangeDetector.FolderMovesBetween as plain logic, so it runs on every host: the list it
// returns can be applied one move at a time, every target is free when its move comes, and
// applying the whole list ends exactly where the scan found each folder.
public sealed class FolderMoveOrderTests
{
    private static bool Inside(string path, string folder)
        => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<LocalFolderMove>? Order(Dictionary<string, string?> before, Dictionary<string, string?> after, bool unidentified = false)
        => LocalChangeDetector.FolderMovesBetween(before, after, unidentified,
            name => !before.Keys.Concat(after.Keys).Any(path => Inside(path, name)));

    // Applies the moves to the folders that are still there (by id), checking each one as
    // Windows and the server would: the source is where the list says, the target is free
    // (nothing at it or under it, ignoring case, unless it is a case-only rename), and a
    // folder never moves into itself. Returns where each folder ends.
    private static Dictionary<string, string> Apply(Dictionary<string, string?> before, Dictionary<string, string?> after, IReadOnlyList<LocalFolderMove> moves)
    {
        var kept = after.Values.Where(id => id is not null).ToHashSet();
        var tree = before.Where(pair => pair.Value is not null && kept.Contains(pair.Value)).ToDictionary(pair => pair.Value!, pair => pair.Key);
        foreach (var move in moves)
        {
            var moving = Assert.Single(tree, pair => pair.Value == move.Before);
            Assert.Equal(moving.Key, move.FolderId);
            if (!string.Equals(move.Before, move.After, StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(Inside(move.After, move.Before), $"{move.Before} moves into itself");
                Assert.DoesNotContain(tree.Values, path => Inside(path, move.After));
            }
            foreach (var id in tree.Keys.ToArray())
                if (Inside(tree[id], move.Before)) tree[id] = move.After + tree[id][move.Before.Length..];
        }
        return tree;
    }

    private static string Describe(Dictionary<string, string?> folders) => string.Join(", ", folders.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"));

    private static void AssertEndsAtTheScan(Dictionary<string, string?> before, Dictionary<string, string?> after, IReadOnlyList<LocalFolderMove>? moves)
    {
        Assert.True(moves is not null, $"No order from [{Describe(before)}] to [{Describe(after)}]");
        var ended = Apply(before, after, moves);
        foreach (var (path, id) in after)
            if (id is not null && ended.TryGetValue(id, out var at)) Assert.Equal(path, at);
    }

    [Fact]
    public void A_chain_vacates_the_name_before_it_is_taken()
    {
        Dictionary<string, string?> before = new() { ["R"] = "r", ["R/Gearbox"] = "g", ["R/Gearbox/Sub"] = "gs", ["R/Gearbox v2"] = "v", ["R/Gearbox v2/Sub"] = "vs" };
        Dictionary<string, string?> after = new() { ["R"] = "r", ["R/Gearbox old"] = "g", ["R/Gearbox old/Sub"] = "gs", ["R/Gearbox"] = "v", ["R/Gearbox/Sub"] = "vs" };
        var moves = Order(before, after);
        Assert.Equal([("R/Gearbox", "R/Gearbox old"), ("R/Gearbox v2", "R/Gearbox")], moves!.Select(m => (m.Before, m.After)));
        AssertEndsAtTheScan(before, after, moves);
    }

    [Fact]
    public void A_swap_goes_through_a_temporary_name()
    {
        Dictionary<string, string?> before = new() { ["A"] = "a", ["B"] = "b", ["B/Sub"] = "bs" };
        Dictionary<string, string?> after = new() { ["A"] = "b", ["A/Sub"] = "bs", ["B"] = "a" };
        var moves = Order(before, after);
        Assert.Equal([("A", "A (moving)"), ("B", "A"), ("A (moving)", "B")], moves!.Select(m => (m.Before, m.After)));
        AssertEndsAtTheScan(before, after, moves);
        // Three folders in a ring.
        Dictionary<string, string?> ring = new() { ["A"] = "a", ["B"] = "b", ["C"] = "c" };
        Dictionary<string, string?> turned = new() { ["B"] = "a", ["C"] = "b", ["A"] = "c" };
        AssertEndsAtTheScan(ring, turned, Order(ring, turned));
    }

    [Fact]
    public void A_rename_above_and_below_comes_top_most_first()
    {
        Dictionary<string, string?> before = new() { ["Robot"] = "r", ["Robot/Gearbox"] = "g", ["Robot/Gearbox/Sub"] = "s", ["Robot/Gearbox/Sub/Deep"] = "d", ["Robot/Other"] = "o" };
        Dictionary<string, string?> after = new()
        {
            ["Robot 2028"] = "r", ["Robot 2028/Gearbox"] = "g", ["Robot 2028/Gearbox/Shafts"] = "s", ["Robot 2028/Gearbox/Shafts/Deep"] = "d",
            ["Robot 2028/Gearbox/Other"] = "o",
        };
        var moves = Order(before, after);
        Assert.Equal([("Robot", "Robot 2028"), ("Robot 2028/Other", "Robot 2028/Gearbox/Other"), ("Robot 2028/Gearbox/Sub", "Robot 2028/Gearbox/Shafts")],
            moves!.Select(m => (m.Before, m.After)));
        AssertEndsAtTheScan(before, after, moves);
        // A case-only rename is one move, and nothing moved is no move.
        Assert.Equal([("Gearbox", "gearbox")], Order(new() { ["Gearbox"] = "g" }, new() { ["gearbox"] = "g" })!.Select(m => (m.Before, m.After)));
        Assert.Empty(Order(before, before)!);
    }

    [Fact]
    public void Moves_are_unknown_when_a_folder_that_left_could_have_moved_unseen()
    {
        // A folder that left its path had no id: it may have moved.
        Assert.Null(Order(new() { ["A"] = null }, new() { ["B"] = "b" }));
        // Its id is nowhere, and a folder of this scan had no id: it may be that one.
        Assert.Null(Order(new() { ["A"] = "a" }, new() { ["B"] = null }, unidentified: true));
        // Its id is nowhere and every id was read: it is gone, which is checked, none.
        Assert.Empty(Order(new() { ["A"] = "a" }, new() { ["B"] = "b" })!);
        // A folder without an id that stayed where it was proves nothing either way.
        Assert.Empty(Order(new() { ["A"] = null }, new() { ["A"] = null })!);
    }

    // A folder in the way that does not move by itself: the student moved X out of D,
    // deleted D, and renamed X to D. X goes aside first, inside the same project.
    [Fact]
    public void A_folder_onto_the_path_above_it_goes_aside_first()
    {
        Dictionary<string, string?> before = new() { ["P"] = "p", ["P/D"] = "d", ["P/D/X"] = "x" };
        Dictionary<string, string?> after = new() { ["P"] = "p", ["P/D"] = "x" };
        var moves = Order(before, after);
        Assert.Equal([("P/D/X", "P/X (moving)"), ("P/X (moving)", "P/D")], moves!.Select(m => (m.Before, m.After)));
        // The old folder that held the name and the one that takes it, nested the other way.
        Dictionary<string, string?> nested = new() { ["P"] = "p", ["P/Gearbox"] = "g", ["P/Gearbox/B"] = "b", ["P/Gearbox v2"] = "v" };
        Dictionary<string, string?> swapped = new() { ["P"] = "p", ["P/Gearbox"] = "v", ["P/Gearbox/Old"] = "g", ["P/Gearbox/Old/B"] = "b" };
        AssertEndsAtTheScan(nested, swapped, Order(nested, swapped));
    }

    // Random trees and random student work in Explorer (renames, moves into other folders
    // and into the other project, deletions, new folders), with names chosen so chains, swaps,
    // case-only renames and reused names happen often. The two project folders stay.
    [Fact]
    public void Random_student_folder_work_always_gives_an_order_that_ends_at_the_scan()
    {
        var random = new Random(20261006);
        string[] names = ["A", "B", "Gearbox", "gearbox", "Gearbox v2", "Gearbox old", "Sub", "CopyDesignTemp"];
        var next = 0;
        var moved = 0;
        for (var round = 0; round < 3000; round++)
        {
            var tree = new Dictionary<string, string>(StringComparer.Ordinal) { ["p"] = "P", ["q"] = "Q" };
            bool Free(string path) => !tree.Values.Any(p => Inside(p, path));
            string? Place(string parent)
            {
                var path = parent + "/" + names[random.Next(names.Length)];
                return Free(path) ? path : null;
            }
            for (var i = 0; i < 2 + random.Next(10); i++)
            {
                var parents = tree.Values.ToArray();
                if (Place(parents[random.Next(parents.Length)]) is { } path) tree[$"f{next++}"] = path;
            }
            var before = tree.ToDictionary(pair => pair.Value, pair => (string?)pair.Key, StringComparer.Ordinal);
            for (var op = 0; op < 1 + random.Next(6); op++)
            {
                var ids = tree.Keys.Where(key => key is not "p" and not "q").ToArray();
                if (ids.Length == 0) break;
                var id = ids[random.Next(ids.Length)];
                var from = tree[id];
                switch (random.Next(5))
                {
                    case 0:
                        foreach (var gone in tree.Where(pair => Inside(pair.Value, from)).Select(pair => pair.Key).ToArray()) tree.Remove(gone);
                        break;
                    case 1:
                        var parents = tree.Values.ToArray();
                        if (Place(parents[random.Next(parents.Length)]) is { } created) tree[$"f{next++}"] = created;
                        break;
                    default:
                        var targets = tree.Values.Where(p => !Inside(p, from)).ToArray();
                        var parent = random.Next(3) == 0 ? targets[random.Next(targets.Length)] : from[..from.LastIndexOf('/')];
                        var to = parent + "/" + names[random.Next(names.Length)];
                        var caseOnly = string.Equals(to, from, StringComparison.OrdinalIgnoreCase);
                        if (to == from || (!caseOnly && !Free(to))) break;
                        foreach (var key in tree.Keys.ToArray())
                            if (Inside(tree[key], from)) tree[key] = to + tree[key][from.Length..];
                        break;
                }
            }
            var after = tree.ToDictionary(pair => pair.Value, pair => (string?)pair.Key, StringComparer.Ordinal);
            var moves = Order(before, after);
            AssertEndsAtTheScan(before, after, moves);
            // A temporary name never leaves its project.
            Assert.All(moves!.Where(m => m.After.Contains(" (moving", StringComparison.Ordinal)),
                m => Assert.Equal(m.Before[..m.Before.IndexOf('/')], m.After[..m.After.IndexOf('/')]));
            moved += moves!.Count;
        }
        Assert.True(moved > 1000, $"Only {moved} moves in all rounds: the generator is not exercising the order.");
    }
    private static LocalRename Rename(string from, string to) => new(TestVault.PathValue(from), TestVault.PathValue(to), from);

    // File renames come in an order that applies one by one: Plate to "Plate old", then
    // "Plate v2" to Plate, although "Plate v2.txt" sorts before "Plate.txt". A case-only rename
    // waits for nothing, and a swap is listed whole (it has no such order).
    [Fact]
    public void File_renames_come_in_an_order_that_can_be_applied()
    {
        var chain = LocalChangeDetector.RenameOrder([Rename("R/Plate.txt", "R/Plate old.txt"), Rename("R/Plate v2.txt", "R/Plate.txt"),
            Rename("R/a.txt", "R/A.txt"), Rename("R/Plate v3.txt", "R/Plate v2.txt")]);
        Assert.Equal([("R/a.txt", "R/A.txt"), ("R/Plate.txt", "R/Plate old.txt"), ("R/Plate v2.txt", "R/Plate.txt"), ("R/Plate v3.txt", "R/Plate v2.txt")],
            chain.Select(r => (r.Before.Value, r.After.Value)));
        var present = new HashSet<string>(["R/Plate.txt", "R/Plate v2.txt", "R/Plate v3.txt", "R/a.txt"], StringComparer.OrdinalIgnoreCase);
        foreach (var rename in chain)
        {
            Assert.True(present.Remove(rename.Before.Value), rename.Before.Value);
            Assert.True(present.Add(rename.After.Value), $"{rename.After.Value} is still taken");
        }
        var swap = LocalChangeDetector.RenameOrder([Rename("R/b.txt", "R/c.txt"), Rename("R/c.txt", "R/b.txt"), Rename("R/z.txt", "R/y.txt")]);
        Assert.Equal(3, swap.Count);
        Assert.Equal(["R/b.txt", "R/c.txt"], swap.Take(2).Select(r => r.Before.Value).Order(StringComparer.Ordinal));
    }
}
