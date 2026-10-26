using System.Security.Cryptography;
using System.Text;
using Armory.Core;

namespace Armory.Agent.Engine.Tests;

// Folding a folder of one's own into the shared one on a real disk (0.5.0, ledger 0437;
// FolderFoldRunner): every row of the table, nothing lost, a conflict and an open file left where
// they are, and a power cut at every single step finished by the next start exactly as one run
// would have finished it.
public sealed class FoldRunnerTests : IDisposable
{
    private const string Alex = "alex.kim@students.test", Maria = "maria.lopez@students.test";
    private static readonly Guid Project = Guid.Parse("28906722-28e6-4337-8f24-d9c602f66c35");
    private readonly string temp = Path.Combine(Path.GetTempPath(), "armory-fold-" + Guid.NewGuid().ToString("N"));
    private string Shared => Path.Combine(temp, "Armory");
    private string Own => Path.Combine(temp, "Armory-maria");
    private readonly Guid mariaDevice = Guid.NewGuid(), mariaOldDevice = Guid.NewGuid();

    public void Dispose()
    {
        if (!Directory.Exists(temp)) return;
        foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(temp, recursive: true);
    }

    private static string H(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string P(string name) => "Robot 2027/Drivetrain/" + name;

    private static void Put(string root, string path, string text)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string? Read(string root, string path)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static FileStateStore StoreOf(string root) => new(Path.Combine(root, ".armory", "state.json"));

    private static FileState Record(string path, string text, string? baseText = null) => new()
    {
        Path = path, ProjectId = Project, FileId = Guid.NewGuid(), BaseId = Guid.NewGuid().ToString(), BaseHash = H(baseText ?? text),
    };

    // The two folders as a 0.4.x lab computer leaves them:
    //   Same.SLDPRT    in both, the same team version: the own copy goes.
    //   OnlyMine.SLDPRT only in the own folder, a team version: moves in.
    //   Old.SLDPRT     the own folder has an older team version: set aside, the shared one stays.
    //   Plate.SLDPRT   Maria's check out with her change, over a team copy nobody works on: the
    //                  team copy goes aside, hers comes in held for her.
    //   New.SLDPRT     Maria's new file, never added: comes in held for her.
    //   Busy.SLDPRT    Maria's change, but Alex has it checked out in the shared folder: both stay.
    //   Open.SLDPRT    open in SolidWorks (a ~$ marker beside it): stays.
    private void Lab()
    {
        var shared = new EngineState { Email = Alex, DeviceId = Guid.NewGuid() };
        shared.Projects[Project] = new ProjectState { Id = Project, Name = "Robot 2027", Folder = "Robot 2027" };
        var own = new EngineState { Email = Maria, DeviceId = mariaDevice };
        own.FormerDevices.Add(mariaOldDevice);
        own.Projects[Project] = new ProjectState { Id = Project, Name = "Robot 2027", Folder = "Robot 2027" };
        void Both(string name, string sharedText, string ownText, string? ownBase = null)
        {
            Put(Shared, P(name), sharedText);
            shared.Files[P(name)] = Record(P(name), sharedText);
            Put(Own, P(name), ownText);
            own.Files[P(name)] = Record(P(name), ownText, ownBase);
        }
        Both("Same.SLDPRT", "same", "same");
        Both("Old.SLDPRT", "old v2", "old v1");
        Both("Plate.SLDPRT", "plate v1", "Maria's plate", ownBase: "plate v1");
        own.Files[P("Plate.SLDPRT")].CheckOut = $"{mariaDevice}:checkout:4";
        own.Files[P("Plate.SLDPRT")].AppliedOwnership = LockOwnership.ThisDevice;
        own.Files[P("Plate.SLDPRT")].Entries.Add($"{mariaDevice}:save:5");
        Both("Busy.SLDPRT", "Alex's busy", "Maria's busy", ownBase: "busy v1");
        shared.Files[P("Busy.SLDPRT")].BaseHash = H("busy v1");
        shared.Files[P("Busy.SLDPRT")].AppliedOwnership = LockOwnership.ThisDevice;
        Both("Open.SLDPRT", "open v1", "Maria's open", ownBase: "open v1");
        Put(Own, P("~$Open.SLDPRT"), "marker");
        Put(Own, P("OnlyMine.SLDPRT"), "only mine");
        own.Files[P("OnlyMine.SLDPRT")] = Record(P("OnlyMine.SLDPRT"), "only mine");
        Put(Own, P("New.SLDPRT"), "Maria's new part");
        Put(Own, ".armory/journal.bin", "Maria's journal");
        StoreOf(Shared).Save(shared.Serialize());
        StoreOf(Own).Save(own.Serialize());
    }

    private HashSet<string> AllBytes()
        => Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("state.json", StringComparison.Ordinal) && !f.EndsWith(".pending", StringComparison.Ordinal))
            .Select(f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).ToHashSet();

    private FoldOutcome Fold(IFoldDisk? disk = null, List<string>? log = null)
        => FolderFoldRunner.Run(Shared, Own, disk ?? new FoldDisk(), StoreOf, log is null ? null : log.Add, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Each_file_goes_where_the_table_says_and_nothing_is_lost()
    {
        Lab();
        var before = AllBytes();
        var names = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).GroupBy(f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).ToDictionary(g => g.Key, g => string.Join(" ", g));
        var outcome = Fold();
        foreach (var missing in before.Except(AllBytes())) Assert.Fail("lost " + names[missing]);
        Assert.Equal((Maria, 2, 2, false), (outcome.Owner, outcome.HeldFiles, outcome.Left, outcome.Removed));
        Assert.Equal(["Robot 2027"], outcome.Projects);
        // In the shared folder now.
        Assert.Equal(("same", "old v2", "Maria's plate", "Maria's new part", "only mine"),
            (Read(Shared, P("Same.SLDPRT")), Read(Shared, P("Old.SLDPRT")), Read(Shared, P("Plate.SLDPRT")), Read(Shared, P("New.SLDPRT")), Read(Shared, P("OnlyMine.SLDPRT"))));
        // Left where they were: work over work, and an open file.
        Assert.Equal(("Alex's busy", "Maria's busy"), (Read(Shared, P("Busy.SLDPRT")), Read(Own, P("Busy.SLDPRT"))));
        Assert.Equal(("open v1", "Maria's open"), (Read(Shared, P("Open.SLDPRT")), Read(Own, P("Open.SLDPRT"))));
        Assert.Null(Read(Own, P("Same.SLDPRT")));
        // Set aside, never deleted.
        var folded = Path.Combine(Shared, ".armory", "folded", "Armory-maria");
        Assert.Equal("old v1", Read(folded, "own/" + P("Old.SLDPRT")));
        Assert.Equal("plate v1", Read(folded, "team/" + P("Plate.SLDPRT")));
        Assert.Subset(AllBytes(), before);
        // Maria's records came in held for her, with her devices, and only for her two files.
        var shared = EngineState.Load(StoreOf(Shared));
        var held = shared.Held[Maria];
        Assert.Equal([P("New.SLDPRT"), P("Plate.SLDPRT")], held.Files.Keys.Order(StringComparer.Ordinal));
        var plate = held.Files[P("Plate.SLDPRT")];
        Assert.Equal(($"{mariaDevice}:checkout:4", LockOwnership.ThisDevice, H("plate v1")), (plate.CheckOut, plate.AppliedOwnership, plate.BaseHash));
        Assert.Empty(plate.Entries); // her save is the file on disk, kept again under her sign-in when she is back
        Assert.Null(plate.LastCaptured);
        Assert.Equal(H("Maria's plate"), held.Hashes[P("Plate.SLDPRT")]);
        Assert.Equal(Project, held.Files[P("New.SLDPRT")].ProjectId);
        Assert.Null(held.Files[P("New.SLDPRT")].FileId);
        Assert.Equal([mariaDevice, mariaOldDevice], shared.People[Maria].Devices);
        // The shared folder's own note of the team's Plate gave way to hers; Alex's Busy is untouched.
        Assert.False(shared.Files.ContainsKey(P("Plate.SLDPRT")));
        Assert.Equal(LockOwnership.ThisDevice, shared.Files[P("Busy.SLDPRT")].AppliedOwnership);
        Assert.Equal(Alex, shared.Email);
        // The own folder stays (two files are still in it) with its records.
        Assert.True(File.Exists(Path.Combine(Own, ".armory", "journal.bin")));

        // The next start: the same two files left, nothing else done.
        var again = Fold();
        Assert.Equal((0, 2, false), (again.HeldFiles, again.Left, again.Removed));
        Assert.Empty(again.Projects);
        // Once Maria saves her open part and closes it, and Alex's Busy is settled (here: he removed
        // his and the own copy is the one to keep), the next start finishes and the folder goes.
        File.Delete(Path.Combine(Own, P("~$Open.SLDPRT").Replace('/', Path.DirectorySeparatorChar)));
        File.Delete(Path.Combine(Shared, P("Busy.SLDPRT").Replace('/', Path.DirectorySeparatorChar)));
        var state = EngineState.Load(StoreOf(Shared));
        state.Files.Remove(P("Busy.SLDPRT"));
        StoreOf(Shared).Save(state.Serialize());
        before = AllBytes();
        var last = Fold();
        Assert.Equal((2, 0, true), (last.HeldFiles, last.Left, last.Removed));
        Assert.False(Directory.Exists(Own));
        Assert.Equal("Maria's journal", Read(folded, "armory/journal.bin"));
        Assert.Equal("Maria's open", Read(Shared, P("Open.SLDPRT")));
        Assert.Equal("open v1", Read(folded, "team/" + P("Open.SLDPRT")));
        Assert.Subset(AllBytes(), before);
    }

    // A disk that stops dead (a power cut) after a number of changes: nothing after it happens.
    private sealed class CutDisk(int changes) : IFoldDisk
    {
        private readonly FoldDisk real = new();
        private int left = changes;
        internal sealed class PowerCut : Exception;
        private void Change() { if (left-- <= 0) throw new PowerCut(); }
        public IReadOnlyList<(string Path, string? Hash)> Files(string root) => real.Files(root);
        public string? Hash(string root, string path) => real.Hash(root, path);
        public bool OpenInSolidWorks(string root, string path) => real.OpenInSolidWorks(root, path);
        public void Move(string fromRoot, string fromPath, string toFullPath) { Change(); real.Move(fromRoot, fromPath, toFullPath); }
        public void Delete(string root, string path) { Change(); real.Delete(root, path); }
        public void MoveFolder(string from, string to) { Change(); real.MoveFolder(from, to); }
        public bool FolderExists(string folder) => real.FolderExists(folder);
        public bool FileExists(string fullPath) => real.FileExists(fullPath);
        public void RemoveEmptyFolders(string root) { Change(); real.RemoveEmptyFolders(root); }
    }

    // What the folders hold, as text, for comparing two runs (the state documents by what matters).
    private string Picture()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.EndsWith("state.json", StringComparison.Ordinal) || file.EndsWith(".pending", StringComparison.Ordinal)) continue;
            text.Append(Path.GetRelativePath(temp, file)).Append('=').Append(File.ReadAllText(file)).Append('\n');
        }
        var shared = EngineState.Load(StoreOf(Shared));
        foreach (var (who, held) in shared.Held) text.Append("held ").Append(who).Append(": ").AppendJoin(",", held.Files.Keys.Order(StringComparer.Ordinal)).Append('\n');
        text.Append("records: ").AppendJoin(",", shared.Files.Keys.Order(StringComparer.Ordinal));
        return text.ToString();
    }

    [Fact]
    public void A_power_cut_at_any_step_is_finished_by_the_next_start_as_one_run_would_finish()
    {
        // One run without a cut, finished (the open file closed and Busy settled first), is the goal.
        string Finish(Func<int, IFoldDisk>? first)
        {
            if (Directory.Exists(temp)) Dispose();
            Lab();
            File.Delete(Path.Combine(Own, P("~$Open.SLDPRT").Replace('/', Path.DirectorySeparatorChar)));
            File.Delete(Path.Combine(Own, P("Busy.SLDPRT").Replace('/', Path.DirectorySeparatorChar)));
            var before = AllBytes();
            if (first is not null)
            {
                try { Fold(first(0)); }
                catch (CutDisk.PowerCut) { }
            }
            Fold();
            Assert.Subset(AllBytes(), before);
            return Picture();
        }
        var goal = Finish(null);
        Assert.Contains("held " + Maria + ": " + P("New.SLDPRT") + "," + P("Open.SLDPRT") + "," + P("Plate.SLDPRT"), goal);
        Assert.False(Directory.Exists(Own));
        // Every place a run can stop: before the first change, between any two, before the last.
        for (var cut = 0; cut < 12; cut++)
        {
            var at = cut;
            Assert.Equal(goal, Finish(_ => new CutDisk(at)));
            Assert.False(Directory.Exists(Own), "after a cut at " + at);
        }
    }

    [Fact]
    public void A_folder_nobody_owns_gives_up_only_copies_the_shared_folder_already_has()
    {
        Put(Shared, P("Same.SLDPRT"), "same");
        Put(Own, P("Same.SLDPRT"), "same");
        Put(Own, P("Stray.SLDPRT"), "a file somebody put here by hand");
        var outcome = Fold();
        Assert.Equal((null, 0, 1, false), (outcome.Owner, outcome.HeldFiles, outcome.Left, outcome.Removed));
        Assert.Null(Read(Own, P("Same.SLDPRT")));
        Assert.Equal("a file somebody put here by hand", Read(Own, P("Stray.SLDPRT")));
        Assert.Null(Read(Shared, P("Stray.SLDPRT")));
    }

    [Fact]
    public void The_summary_reads_the_outcomes_in_plain_words()
    {
        Lab();
        var outcome = Fold();
        var words = FolderFold.Summary(Shared, outcome.Projects.Count, [("Maria", outcome.HeldFiles)], [(Own, outcome.Left)], outcome.Removed ? 1 : 0);
        Assert.Equal($"Moved 1 project into {Shared}, kept 2 files waiting for Maria. Each person's work goes to Armory under their own sign-in when they pick their name. " +
            $"2 files stayed in {Own} because both copies have work in them or the file is open. Open both, keep the one you want, then delete the other.", words);
    }
}
