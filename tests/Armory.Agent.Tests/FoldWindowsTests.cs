using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Agent.Engine;

namespace Armory.Agent.Tests;

// Folding a folder of one's own into the shared one on real NTFS (0.5.0, ledger 0437;
// FolderFoldRunner over FoldDisk, the app's own disk): read-only files (Armory makes every file
// nobody has checked out read-only) move and are removed as the plan says; Explorer's desktop.ini
// and Thumbs.db never keep a finished folder; a file another program holds open (SolidWorks, here a
// child process holding it without delete sharing) stays where it is and moves at the next start;
// and a power cut right after the records are saved is finished by the next start. Windows only.
public sealed class FoldWindowsTests : IDisposable
{
    private const string Maria = "maria.lopez@students.test", Alex = "alex.kim@students.test";
    private static readonly Guid Project = Guid.Parse("28906722-28e6-4337-8f24-d9c602f66c35");
    private readonly TempFolder temp = new();
    private string Shared => temp.File("Armory");
    private string Own => temp.File("Armory-maria");
    private static string P(string name) => "Robot 2027/Drivetrain/" + name;

    public void Dispose()
    {
        if (Directory.Exists(temp.Root))
            foreach (var file in Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        temp.Dispose();
    }

    private static string H(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Put(string root, string path, string text, bool readOnly = true)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.SetAttributes(full, FileAttributes.Normal);
        File.WriteAllText(full, text);
        if (readOnly) File.SetAttributes(full, FileAttributes.ReadOnly);
        return full;
    }

    private static string? Read(string root, string path)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static JsonObject Record(string path, string baseText, bool checkedOut = false)
    {
        var record = new JsonObject
        {
            ["path"] = path, ["projectId"] = Project.ToString(), ["fileId"] = Guid.NewGuid().ToString(), ["baseId"] = Guid.NewGuid().ToString(), ["baseHash"] = H(baseText),
        };
        if (checkedOut)
        {
            record["checkOut"] = Guid.NewGuid() + ":checkout:9";
            record["appliedOwnership"] = 1;
        }
        return record;
    }

    private static void State(string root, string email, JsonObject files)
    {
        var document = new JsonObject
        {
            ["schema"] = 2, ["email"] = email, ["deviceId"] = Guid.NewGuid().ToString(), ["sequence"] = 1024,
            ["projects"] = new JsonObject { [Project.ToString()] = new JsonObject { ["id"] = Project.ToString(), ["name"] = "Robot 2027", ["folder"] = "Robot 2027" } },
            ["files"] = files,
        };
        new FileStateStore(Path.Combine(root, ".armory", "state.json")).Save(Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    private static FileStateStore StoreOf(string root) => new(Path.Combine(root, ".armory", "state.json"));

    private FoldOutcome Fold(IFoldDisk? disk = null) => FolderFoldRunner.Run(Shared, Own, disk ?? new FoldDisk(), StoreOf);

    // The lab as 0.4.x leaves it: Same (identical, read-only both sides), Mine (only in the own
    // folder, read-only), Plate (Maria's check out, writable, over Alex's read-only team copy).
    private void Lab()
    {
        Put(Shared, P("Same.SLDPRT"), "same");
        Put(Shared, P("Plate.SLDPRT"), "plate v1");
        State(Shared, Alex, new JsonObject { [P("Same.SLDPRT")] = Record(P("Same.SLDPRT"), "same"), [P("Plate.SLDPRT")] = Record(P("Plate.SLDPRT"), "plate v1") });
        Put(Own, P("Same.SLDPRT"), "same");
        Put(Own, P("Mine.SLDPRT"), "mine v1");
        Put(Own, P("Plate.SLDPRT"), "Maria's plate", readOnly: false);
        Put(Own, "desktop.ini", "[.ShellClassInfo]");
        File.SetAttributes(Path.Combine(Own, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        Put(Own, P("Thumbs.db"), "thumbs", readOnly: false);
        Put(Own, ".armory/journal.bin", "Maria's journal", readOnly: false);
        State(Own, Maria, new JsonObject
        {
            [P("Same.SLDPRT")] = Record(P("Same.SLDPRT"), "same"), [P("Mine.SLDPRT")] = Record(P("Mine.SLDPRT"), "mine v1"),
            [P("Plate.SLDPRT")] = Record(P("Plate.SLDPRT"), "plate v1", checkedOut: true),
        });
    }

    [WindowsFact]
    public void Read_only_files_fold_on_NTFS_and_a_finished_folder_goes_with_Explorer_s_own_files()
    {
        Lab();
        var outcome = Fold();
        Assert.Equal((Maria, 1, 0, true), (outcome.Owner, outcome.HeldFiles, outcome.Left, outcome.Removed));
        Assert.False(Directory.Exists(Own));
        Assert.Equal(("same", "mine v1", "Maria's plate"), (Read(Shared, P("Same.SLDPRT")), Read(Shared, P("Mine.SLDPRT")), Read(Shared, P("Plate.SLDPRT"))));
        // A moved file keeps its bits as they were; the engine sets them by its own rule next.
        Assert.True(File.GetAttributes(Path.Combine(Shared, P("Mine.SLDPRT").Replace('/', Path.DirectorySeparatorChar))).HasFlag(FileAttributes.ReadOnly));
        var folded = Path.Combine(Shared, ".armory", "folded", "Armory-maria");
        Assert.Equal("plate v1", Read(folded, "team/" + P("Plate.SLDPRT")));
        Assert.Equal("Maria's journal", Read(folded, "armory/journal.bin"));
        Assert.Equal(1, SyncEngine.HeldOf(StoreOf(Shared))[Maria]);
    }

    [WindowsFact]
    public void A_file_another_program_holds_open_stays_and_moves_at_the_next_start()
    {
        Lab();
        var plate = Path.Combine(Own, P("Plate.SLDPRT").Replace('/', Path.DirectorySeparatorChar));
        // SolidWorks keeps a document open without letting it be renamed or deleted: a child
        // process holds it the same way until it is told to let go.
        var ready = temp.File("ready.txt");
        var release = temp.File("release.txt");
        using var holder = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"$f=[IO.File]::Open('{plate}','Open','Read','Read'); Set-Content '{ready}' 'x'; while(-not (Test-Path '{release}')){{Start-Sleep -Milliseconds 100}}; $f.Close()\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        var watch = Stopwatch.StartNew();
        while (!File.Exists(ready)) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the holder never opened the file"); Thread.Sleep(50); }
        var first = Fold();
        Assert.Equal((1, false), (first.Left, first.Removed));
        Assert.Equal("Maria's plate", File.ReadAllText(plate));
        // The team's copy went aside first (never deleted), and Maria's held file couldn't follow:
        // Plate's place in the shared folder waits empty, held for her, and nothing was lost. Once
        // the program lets go, the next start brings her file in.
        Assert.Null(Read(Shared, P("Plate.SLDPRT")));
        Assert.Equal("plate v1", Read(Path.Combine(Shared, ".armory", "folded", "Armory-maria"), "team/" + P("Plate.SLDPRT")));
        File.WriteAllText(release, "x");
        Assert.True(holder.WaitForExit(30_000));
        var next = Fold();
        Assert.Equal((0, true), (next.Left, next.Removed));
        Assert.Equal("Maria's plate", Read(Shared, P("Plate.SLDPRT")));
        Assert.Equal("plate v1", Read(Path.Combine(Shared, ".armory", "folded", "Armory-maria"), "team/" + P("Plate.SLDPRT")));
    }

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

    [WindowsFact]
    public void A_power_cut_at_each_step_on_NTFS_is_finished_by_the_next_start()
    {
        for (var cut = 0; cut < 6; cut++)
        {
            Dispose();
            Lab();
            try { Fold(new CutDisk(cut)); }
            catch (CutDisk.PowerCut) { }
            var outcome = Fold();
            Assert.Equal((0, true), (outcome.Left, outcome.Removed));
            Assert.Equal(("same", "mine v1", "Maria's plate"), (Read(Shared, P("Same.SLDPRT")), Read(Shared, P("Mine.SLDPRT")), Read(Shared, P("Plate.SLDPRT"))));
            Assert.Equal("plate v1", Read(Path.Combine(Shared, ".armory", "folded", "Armory-maria"), "team/" + P("Plate.SLDPRT")));
            Assert.Equal(1, SyncEngine.HeldOf(StoreOf(Shared))[Maria]);
        }
    }
}
