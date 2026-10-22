using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Armory.Agent.Tests;

// The field round of 2026-10-22 (idea-app docs/feedback/2026-10-22-armory, item 1): the Armory
// folder can never be a project folder or sit inside one, and a computer already in that state
// (IDEA-06: C:\IDEA\Armory\FRC 2026 Off-Season, with the project downloaded again into
// ...\FRC 2026 Off-Season\FRC 2026 Off-Season) is fixed in one click with nothing uploaded twice
// and no change lost.
public sealed class VaultRootTests
{
    private const string Project = "FRC 2026 Off-Season";
    private static readonly IReadOnlyList<ProjectShape> Shapes =
        [new ProjectShape(Project, new HashSet<string>(["00 Robot Assembly", "01 Drivetrain"], StringComparer.OrdinalIgnoreCase)),
         new ProjectShape("Blade Team 4", new HashSet<string>(["Body.SLDPRT"], StringComparer.OrdinalIgnoreCase))];

    private sealed class Facts(IEnumerable<string> armoryFolders, Dictionary<string, string[]>? children = null) : IVaultFolderFacts
    {
        private readonly HashSet<string> armory = new(armoryFolders, StringComparer.OrdinalIgnoreCase);
        public bool HoldsArmory(string folder) => armory.Contains(folder);
        public IReadOnlyCollection<string> Children(string folder) => children is not null && children.TryGetValue(folder, out var c) ? c : [];
    }

    [Fact]
    public void A_project_folder_inside_the_Armory_folder_is_corrected_to_the_Armory_folder()
    {
        var facts = new Facts([@"C:\IDEA\Armory"]);
        var verdict = VaultRootRule.Judge(@"C:\IDEA\Armory\FRC 2026 Off-Season", Shapes, facts);
        Assert.Equal(@"C:\IDEA\Armory", verdict.UseInstead);
        Assert.Equal(Project, verdict.Project);
        Assert.False(verdict.Refused);
        Assert.Equal(@"C:\IDEA\Armory\FRC 2026 Off-Season is the FRC 2026 Off-Season project folder inside your Armory folder, so Armory uses C:\IDEA\Armory, the folder that holds all your projects.", verdict.Words);
        // Deeper in it, and in a folder of it that is no project: the same Armory folder.
        Assert.Equal(@"C:\IDEA\Armory", VaultRootRule.Judge(@"C:\IDEA\Armory\FRC 2026 Off-Season\00 Robot Assembly", Shapes, facts).UseInstead);
        Assert.Equal(@"C:\IDEA\Armory", VaultRootRule.Judge(@"C:\IDEA\Armory\Personal", Shapes, facts).UseInstead);
        // Nested Armory folders (the doubled one has a .armory of its own now): the outermost.
        Assert.Equal(@"C:\IDEA\Armory", VaultRootRule.Judge(@"C:\IDEA\Armory\FRC 2026 Off-Season\Sub",
            Shapes, new Facts([@"C:\IDEA\Armory", @"C:\IDEA\Armory\FRC 2026 Off-Season"])).UseInstead);
    }

    [Fact]
    public void A_folder_named_for_a_project_with_its_contents_is_corrected_to_the_folder_that_holds_it()
    {
        var facts = new Facts([], new() { [@"D:\Team\FRC 2026 Off-Season"] = ["00 Robot Assembly", "notes.txt"] });
        var verdict = VaultRootRule.Judge(@"D:\Team\FRC 2026 Off-Season", Shapes, facts);
        Assert.Equal(@"D:\Team", verdict.UseInstead);
        Assert.Equal(@"D:\Team\FRC 2026 Off-Season is the FRC 2026 Off-Season project folder, so Armory uses the folder that holds it, D:\Team.", verdict.Words);
        Assert.Equal(@"D:\Team", VaultRootRule.Judge(@"D:\Team\FRC 2026 Off-Season\00 Robot Assembly", Shapes, facts).UseInstead);
        // Named for a project but holding none of its tree: a folder like any other.
        Assert.True(VaultRootRule.Judge(@"D:\Team\FRC 2026 Off-Season", Shapes, new Facts([], new() { [@"D:\Team\FRC 2026 Off-Season"] = ["homework.docx"] })).Ok);
        // At the top of a drive there is no folder around it: refused, with what to choose.
        var top = VaultRootRule.Judge(@"D:\FRC 2026 Off-Season", Shapes, new Facts([], new() { [@"D:\FRC 2026 Off-Season"] = ["01 Drivetrain"] }));
        Assert.True(top.Refused);
        Assert.Null(top.UseInstead);
        Assert.Contains("Choose the folder that holds your project folders", top.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_folders_and_a_students_own_folder_beside_the_shared_one_are_fine()
    {
        var facts = new Facts([@"C:\IDEA\Armory"]);
        Assert.True(VaultRootRule.Judge(@"C:\IDEA\Armory", Shapes, facts).Ok);
        Assert.True(VaultRootRule.Judge(@"C:\IDEA\Armory-alex", Shapes, facts).Ok);
        Assert.True(VaultRootRule.Judge(@"C:\Users\IDEA\Desktop\matthew g", Shapes, facts).Ok);
        Assert.True(VaultRootRule.Judge(@"C:\IDEA\Armory", [], facts).Ok);
    }

    [Fact]
    public void The_window_s_projects_give_the_rule_their_folder_and_top_names()
    {
        var row = new Armory.Agent.Engine.View.FileRowView("f1", "Readme.txt", Project + "/Readme.txt", "synced",
            new Armory.Agent.Engine.View.CheckoutView("available", "Available", null, null, null, null), false, false, null, null, null, false);
        var view = new Armory.Agent.Engine.View.ProjectView("p", Project, false, "student", false,
            [new("", Project, 1, [row]), new("00 Robot Assembly", "00 Robot Assembly", 0, []), new("00 Robot Assembly/Parts", "Parts", 0, [])], 2025, 0);
        var shape = Assert.Single(VaultRootRule.Shapes([view]));
        Assert.Equal(Project, shape.Folder);
        Assert.Equal(["00 Robot Assembly", "Readme.txt"], shape.TopNames.Order(StringComparer.Ordinal).ToArray());
    }

    // ---- Fix it, on real folders ------------------------------------------------------------

    private sealed class Lab : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "armory-vaultroot-" + Guid.NewGuid().ToString("N")[..8]);
        internal string Target => Path.Combine(Root, "Armory");
        internal string Bad => Path.Combine(Target, Project);
        internal string Doubled => Path.Combine(Bad, Project);
        internal static readonly DateTimeOffset Now = new(2026, 10, 22, 21, 30, 0, TimeSpan.Zero);

        internal Lab() => Directory.CreateDirectory(Target);

        internal static string Write(string file, string text, DateTime? at = null, bool readOnly = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            if (at is { } when) File.SetLastWriteTimeUtc(file, when);
            if (readOnly) File.SetAttributes(file, FileAttributes.ReadOnly);
            return file;
        }

        // A state document with the project and each file's base hash (vault-relative keys).
        internal static void State(string root, params (string Key, string BaseText)[] files)
        {
            var records = new JsonObject();
            foreach (var (key, text) in files) records[key] = new JsonObject { ["path"] = key, ["baseHash"] = Hash(text) };
            var document = new JsonObject
            {
                ["schema"] = 2,
                ["projects"] = new JsonObject { [Guid.NewGuid().ToString()] = new JsonObject { ["name"] = Project, ["folder"] = Project, ["usable"] = true } },
                ["files"] = records,
            };
            Write(Path.Combine(root, ".armory", "state.json"), document.ToJsonString());
        }

        internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        internal string[] FilesUnder(string folder)
            => Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray()
                : [];

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // IDEA-06 as it was found: the Armory folder C:\IDEA\Armory (its own records, the project's
    // files from an earlier day) set to its own project folder, which then got the whole project
    // downloaded into a folder of the same name inside it, one file checked out and changed there.
    private static void Idea06(Lab lab)
    {
        var day = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(2026, 10, 22, 21, 10, 0, DateTimeKind.Utc);
        // C:\IDEA\Armory: its records, and the project's files as it last had them.
        Lab.State(lab.Target,
            (Project + "/00 Robot Assembly/Same.SLDPRT", "same"),
            (Project + "/00 Robot Assembly/Older.SLDPRT", "team v1"),
            (Project + "/00 Robot Assembly/EditedThere.SLDPRT", "team v1"),
            (Project + "/00 Robot Assembly/Full Assembly.SLDASM", "asm v1"),
            (Project + "/00 Robot Assembly/BothChanged.SLDPRT", "team v1"));
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "Same.SLDPRT"), "same", day, readOnly: true);
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "Older.SLDPRT"), "team v1", day, readOnly: true);
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "EditedThere.SLDPRT"), "my edit from Tuesday", day);
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "Full Assembly.SLDASM"), "asm v1", day, readOnly: true);
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "BothChanged.SLDPRT"), "my edit from Tuesday", day);
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "OnlyHere.SLDPRT"), "only in the old folder", day);
        // The wrong Armory folder's records, and what it downloaded (and what was changed there).
        Lab.State(lab.Bad,
            (Project + "/00 Robot Assembly/Same.SLDPRT", "same"),
            (Project + "/00 Robot Assembly/Older.SLDPRT", "team v2"),
            (Project + "/00 Robot Assembly/EditedThere.SLDPRT", "team v2"),
            (Project + "/00 Robot Assembly/Full Assembly.SLDASM", "asm v2"),
            (Project + "/00 Robot Assembly/BothChanged.SLDPRT", "team v2"),
            (Project + "/01 Drivetrain/New.SLDPRT", "new part"));
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "Same.SLDPRT"), "same", today, readOnly: true);
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "Older.SLDPRT"), "team v2", today, readOnly: true);
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "EditedThere.SLDPRT"), "team v2", today, readOnly: true);
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "Full Assembly.SLDASM"), "asm v2 with today's checked-out edit", today);
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "BothChanged.SLDPRT"), "today's edit", today);
        Lab.Write(Path.Combine(lab.Doubled, "01 Drivetrain", "New.SLDPRT"), "new part", today, readOnly: true);
        Lab.Write(Path.Combine(lab.Doubled, "01 Drivetrain", "desktop.ini"), "[.ShellClassInfo]", today);
        Lab.Write(Path.Combine(lab.Bad, ".armory", "journal.bin"), "safe copies of saves", today);
    }

    [Fact]
    public void Fix_it_folds_the_doubled_folder_in_with_nothing_twice_and_no_change_lost()
    {
        using var lab = new Lab();
        Idea06(lab);

        var result = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);

        Assert.True(result.Done, result.Problem);
        // The doubled folder is gone; the project folder holds one copy of every file.
        Assert.False(Directory.Exists(lab.Doubled));
        Assert.Equal(
            ["00 Robot Assembly/BothChanged.SLDPRT", "00 Robot Assembly/EditedThere.SLDPRT", "00 Robot Assembly/Full Assembly.SLDASM", "00 Robot Assembly/Older.SLDPRT",
             "00 Robot Assembly/OnlyHere.SLDPRT", "00 Robot Assembly/Same.SLDPRT", "01 Drivetrain/New.SLDPRT"],
            lab.FilesUnder(lab.Bad).Where(f => !f.StartsWith(".armory/", StringComparison.Ordinal)).ToArray());
        string Text(string name) => File.ReadAllText(Path.Combine(lab.Bad, name.Replace('/', Path.DirectorySeparatorChar)));
        // Identical: one copy. The team's older version gives way to its newer one; a change made
        // in the old folder stays over the team's version that was downloaded; today's checked-out
        // change stays over the old team version; a file only one side had is there.
        Assert.Equal("same", Text("00 Robot Assembly/Same.SLDPRT"));
        Assert.Equal("team v2", Text("00 Robot Assembly/Older.SLDPRT"));
        Assert.Equal("my edit from Tuesday", Text("00 Robot Assembly/EditedThere.SLDPRT"));
        Assert.Equal("asm v2 with today's checked-out edit", Text("00 Robot Assembly/Full Assembly.SLDASM"));
        Assert.Equal("new part", Text("01 Drivetrain/New.SLDPRT"));
        Assert.Equal("only in the old folder", Text("00 Robot Assembly/OnlyHere.SLDPRT"));
        // Changed in both places: the newer in place, the other kept beside the Armory folder,
        // outside it (never uploaded), at the same path.
        Assert.Equal("today's edit", Text("00 Robot Assembly/BothChanged.SLDPRT"));
        Assert.Equal(1, result.KeptCopies);
        Assert.Equal(lab.Target + " kept copies 2026-10-22", result.KeptFolder);
        Assert.Equal("my edit from Tuesday", File.ReadAllText(Path.Combine(result.KeptFolder!, Project, "00 Robot Assembly", "BothChanged.SLDPRT")));
        Assert.False(result.KeptFolder!.StartsWith(lab.Target + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        // The wrong folder's records are set aside inside the right folder's, never deleted.
        Assert.False(Directory.Exists(Path.Combine(lab.Bad, ".armory")));
        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(lab.Target, ".armory", "set-aside")));
        Assert.Equal("safe copies of saves", File.ReadAllText(Path.Combine(aside, "journal.bin")));
        Assert.True(File.Exists(Path.Combine(lab.Target, ".armory", "state.json")));
        // Moved: the newer team version, today's checked-out change, today's change made in both
        // places, and the new part. Already there: the identical copy, and the team's version a
        // change in the old folder stands over. Kept: the older of the two changes.
        Assert.Equal((4, 2, 1), (result.Moved, result.AlreadyThere, result.KeptCopies));
        // Windows' own folder file was not moved in: its folder went with it.
        Assert.False(File.Exists(Path.Combine(lab.Bad, "01 Drivetrain", "desktop.ini")));
    }

    [Fact]
    public void Fix_it_is_safe_when_the_doubled_folder_was_already_moved_out_by_hand()
    {
        using var lab = new Lab();
        Idea06(lab);
        // Mr. Pina moved the doubled folder to C:\IDEA\Armory-old-IDEA06 by hand.
        var byHand = Path.Combine(lab.Root, "Armory-old-IDEA06");
        Directory.Move(lab.Doubled, byHand);
        var before = lab.FilesUnder(lab.Bad).Where(f => !f.StartsWith(".armory/", StringComparison.Ordinal)).ToArray();

        var result = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);

        Assert.True(result.Done, result.Problem);
        Assert.Equal(0, result.Moved + result.AlreadyThere + result.KeptCopies);
        Assert.Equal(before, lab.FilesUnder(lab.Bad).Where(f => !f.StartsWith(".armory/", StringComparison.Ordinal)).ToArray());
        Assert.Equal(7, lab.FilesUnder(byHand).Length);
        Assert.False(Directory.Exists(Path.Combine(lab.Bad, ".armory")));
        // And when the whole wrong folder is gone too: nothing to do, and still done.
        Directory.Delete(lab.Bad, recursive: true);
        foreach (var f in Directory.EnumerateFiles(lab.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Assert.True(VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now).Done);
    }

    [Fact]
    public void Fix_it_waits_for_SolidWorks_and_goes_on_from_where_it_stopped()
    {
        using var lab = new Lab();
        Idea06(lab);
        var marker = Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "~$Full Assembly.SLDASM"), "open");

        var stopped = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);

        Assert.False(stopped.Done);
        Assert.Equal("SolidWorks still has Full Assembly.SLDASM open. Close SolidWorks, then click Fix it again.", stopped.Problem);
        Assert.Equal(0, stopped.Moved + stopped.AlreadyThere);
        Assert.Equal(8, lab.FilesUnder(lab.Doubled).Length);

        File.Delete(marker);
        var done = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);
        Assert.True(done.Done, done.Problem);
        Assert.False(Directory.Exists(lab.Doubled));
        // Run again: nothing left to do, nothing changes.
        var files = lab.FilesUnder(lab.Root);
        var again = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);
        Assert.True(again.Done);
        Assert.Equal(0, again.Moved + again.AlreadyThere + again.KeptCopies);
        Assert.Equal(files, lab.FilesUnder(lab.Root));
    }

    [Fact]
    public void Without_records_in_the_right_folder_the_wrong_folders_records_become_its_own()
    {
        using var lab = new Lab();
        // A project folder copied somewhere and chosen as the Armory folder: the folder around it
        // had never been an Armory folder.
        Lab.Write(Path.Combine(lab.Bad, "00 Robot Assembly", "Same.SLDPRT"), "same");
        Lab.State(lab.Bad, (Project + "/00 Robot Assembly/Same.SLDPRT", "same"));
        Lab.Write(Path.Combine(lab.Doubled, "00 Robot Assembly", "Same.SLDPRT"), "same");
        Assert.False(Directory.Exists(Path.Combine(lab.Target, ".armory")));

        var verdict = VaultRootRule.Judge(lab.Bad, VaultStateFile.Read(lab.Bad).Projects, VaultFolderFacts.Disk);
        Assert.Equal(lab.Target, verdict.UseInstead);
        var result = VaultRootRepair.Run(lab.Bad, lab.Target, Lab.Now);

        Assert.True(result.Done, result.Problem);
        Assert.Equal(1, result.AlreadyThere);
        Assert.True(File.Exists(Path.Combine(lab.Target, ".armory", "state.json")));
        Assert.Equal(Lab.Hash("same"), VaultStateFile.Read(lab.Target).Bases[Project + "/00 Robot Assembly/Same.SLDPRT"]);
        Assert.Equal(["00 Robot Assembly/Same.SLDPRT"], lab.FilesUnder(lab.Bad));
    }
}
