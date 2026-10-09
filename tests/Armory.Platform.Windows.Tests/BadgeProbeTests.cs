using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Armory.Core;
using Microsoft.Win32;

namespace Armory.Platform.Windows.Tests;

// The native build (tools/build-native.ps1) in publish/native/x64, or null when it is not there.
internal static class NativeBuild
{
    internal static string? Folder { get; } = Find();

    internal static string Probe => Path.Combine(Folder!, "BadgeProbe.exe");
    internal static string Dll => Path.Combine(Folder!, "ArmoryBadges.dll");

    private static string? Find()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (!File.Exists(Path.Combine(folder.FullName, "Armory.sln"))) continue;
            var native = Path.Combine(folder.FullName, "publish", "native", "x64");
            return File.Exists(Path.Combine(native, "BadgeProbe.exe")) && File.Exists(Path.Combine(native, "ArmoryBadges.dll")) ? native : null;
        }
        return null;
    }
}

// Windows with the native build present; skipped elsewhere (Linux CI has neither).
public sealed class NativeBuildFactAttribute : FactAttribute
{
    public NativeBuildFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Runs the native ArmoryBadges.dll; Windows only.";
        else if (NativeBuild.Folder is null) Skip = "publish/native/x64 has no BadgeProbe.exe and ArmoryBadges.dll (run tools/build-native.ps1).";
    }
}

// T7 (docs/agent/EXPLORER.md): the C# writer and the native reader agree, byte for byte and path
// for path, and the handlers stay fast, drop badges when Armory stops, and leave a heartbeat.
[SupportedOSPlatform("windows")]
public sealed class BadgeProbeTests
{
    private const string Root = @"C:\IDEA\Armory";

    private static List<BadgeEntry> Files(int bulk)
    {
        var files = new List<BadgeEntry>
        {
            new("Robot 2027/Drivetrain/Plate.SLDPRT", BadgeState.Mine),
            new("Robot 2027/Arm/Élan Bracket.SLDPRT", BadgeState.Locked),
            new("Robot 2027/Arm/Broken.SLDASM", BadgeState.Attention),
            new("Robot 2027/Arm/Gear.SLDPRT", BadgeState.Synced),
            new("Class 2026/ñandú.sldprt", BadgeState.Mine),
            new("Class 2026/Lesson/Cube.SLDPRT", BadgeState.Synced),
            new("Class 2026/Ωmega/Ünit.SLDPRT", BadgeState.Locked),
            new(new string('a', 1020) + ".prt", BadgeState.Locked),
        };
        for (var i = 0; i < bulk; i++) files.Add(new($"Robot 2027/Bulk/Part {i:D5}.SLDPRT", (BadgeState)(1 + i % 4)));
        return files;
    }

    private static List<string> Paths(int bulk)
    {
        var paths = new List<string>
        {
            Root + @"\Robot 2027\Drivetrain\Plate.SLDPRT", @"c:\idea\armory\robot 2027\drivetrain\plate.sldprt", Root + @"\Robot 2027\Drivetrain\",
            Root + @"\Robot 2027", Root + @"\Robot 2027\Arm", Root + @"\Robot 2027\Arm\élan bracket.SLDPRT", Root + @"\Robot 2027\Arm\ÉLAN BRACKET.sldprt",
            Root + @"\Robot 2027\Arm\Broken.SLDASM", Root + @"\Robot 2027\Arm\GEAR.sldprt", Root + @"\CLASS 2026\ÑANDÚ.SLDPRT", Root + @"\Class 2026",
            Root + @"\Class 2026\Lesson", Root + @"\Class 2026\Lesson\Cube.SLDPRT", Root + @"\class 2026\ωMEGA\üNIT.sldprt",
            Root + @"\Robot 2027\Drivetrain\Other.SLDPRT", Root, Root + @"\", Root + @"\\", @"C:\IDEA\ArmoryX\Robot 2027", @"C:\IDEA\Armor",
            @"C:\Users\student\Documents\Plate.SLDPRT", @"D:\IDEA\Armory\Robot 2027", @"\\server\share\Robot 2027",
            Root + @"\" + new string('A', 1020) + ".PRT", Root + @"\" + new string('a', 1020) + @".prt\", Root + @"\" + new string('a', 1021) + ".prt",
            Root + @"\Robot 2027\Bulk",
        };
        foreach (var i in new[] { 0, 1, 2, 3, 4, bulk / 2, bulk - 2, bulk - 1 })
            paths.Add($@"{Root}\Robot 2027\Bulk\Part {i:D5}.SLDPRT");
        return paths;
    }

    // "path<TAB>expected" for each path, the expected badge from the C# lookup.
    private static string Queries(byte[] table, long generation, IEnumerable<string> paths, bool none = false) =>
        string.Join("\n", paths.Select(p => p + "\t" + (none ? 0 : (int)BadgeTable.Lookup(table, generation, p, ShellFold.Fold))));

    private static (int Exit, string Output) Run(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("BadgeProbe did not finish in three minutes.");
        }
        return (process.ExitCode, output.Result + errors.Result);
    }

    private static double Number(string output, string pattern) =>
        double.Parse(Assert.Single(Regex.Matches(output, pattern)).Groups[1].Value, CultureInfo.InvariantCulture);

    [NativeBuildFact]
    public void The_native_handlers_answer_like_the_csharp_lookup_stay_fast_and_drop_badges_when_armory_stops()
    {
        using var folder = new TempFiles();
        const long generation = 0x0001_0000_0000_002A;
        var table = BadgeTable.Build(Root, BadgeRules.WithFolders(Files(5_000)), generation, ShellFold.Fold);
        var queries = Queries(table, generation, Paths(5_000));
        Assert.Contains("\t4", queries);
        Assert.Contains("\t1", queries);
        File.WriteAllBytes(folder.File("table.bin"), table);
        File.WriteAllText(folder.File("queries.txt"), queries, new UTF8Encoding(false));
        var (exit, output) = Run(NativeBuild.Probe, folder.File("table.bin"), folder.File("queries.txt"), "--dll", NativeBuild.Dll);
        Console.WriteLine(output);
        Assert.True(exit == 0, output);
        Assert.Contains(" 0 mismatches", output);
        Assert.Contains("after unpublish: 0 queries still have a badge", output);
        Assert.True(Number(output, @"publisher exit: badges gone (\d+) ms") <= 1500, output);
        // Generous on a shared CI runner: Explorer asks once per icon and badge.
        Assert.True(Number(output, @"inside the vault: median (\d+) ns") < 5_000, output);
        Assert.True(Number(output, @"outside the vault: median (\d+) ns") < 5_000, output);
    }

    [NativeBuildFact]
    public void The_dll_reads_what_badge_publisher_publishes_and_nothing_after_clear()
    {
        using var folder = new TempFiles();
        var name = BadgePublisherTests.PrivateName();
        var entries = BadgeRules.WithFolders(Files(200));
        var expected = BadgeTable.Build(Root, entries, 1, ShellFold.Fold);
        File.WriteAllText(folder.File("published.txt"), Queries(expected, 1, Paths(200)), new UTF8Encoding(false));
        File.WriteAllText(folder.File("none.txt"), Queries(expected, 1, Paths(200), none: true), new UTF8Encoding(false));
        using var publisher = new BadgePublisher(name);
        publisher.Publish(Root, entries);
        var published = Run(NativeBuild.Probe, "--attach", folder.File("published.txt"), "--dll", NativeBuild.Dll, "--section", name);
        Assert.True(published.Exit == 0, published.Output);
        publisher.Clear();
        var cleared = Run(NativeBuild.Probe, "--attach", folder.File("none.txt"), "--dll", NativeBuild.Dll, "--section", name);
        Assert.True(cleared.Exit == 0, cleared.Output);
        publisher.Publish(Root, entries);
        var again = Run(NativeBuild.Probe, "--attach", folder.File("published.txt"), "--dll", NativeBuild.Dll, "--section", name);
        Assert.True(again.Exit == 0, again.Output);
    }

    [NativeBuildFact]
    public void Inside_a_process_named_explorer_exe_the_dll_writes_the_four_heartbeats()
    {
        using var folder = new TempFiles();
        string[] names = ["SeenAttention", "SeenMine", "SeenLocked", "SeenSynced", "ExplorerPid"];
        using var key = Registry.CurrentUser.CreateSubKey(BadgeHealth.UserKey, writable: true);
        var before = names.ToDictionary(n => n, n => (Value: key.GetValue(n), Kind: key.GetValue(n) is null ? RegistryValueKind.Unknown : key.GetValueKind(n)));
        try
        {
            File.Copy(NativeBuild.Probe, folder.File("explorer.exe"));
            var (exit, output) = Run(folder.File("explorer.exe"), "--attach", "--dll", NativeBuild.Dll, "--section", BadgePublisherTests.PrivateName());
            Assert.True(exit == 0, output);
            foreach (var badge in new[] { "Attention", "Mine", "Locked", "Synced" })
                Assert.Contains($"heartbeat Seen{badge}: written", output);
            Assert.Equal(RegistryValueKind.QWord, key.GetValueKind("SeenSynced"));
        }
        finally
        {
            // Put back whatever a real Explorer had written.
            foreach (var (name, (value, kind)) in before)
                if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
                else key.SetValue(name, value, kind);
        }
    }
}

// A uniquely named folder under the temp directory, removed afterwards.
internal sealed class TempFiles : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "Armory-Badge-Tests");
    internal string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
    internal TempFiles() => Directory.CreateDirectory(Root);
    internal string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        var actual = Path.GetFullPath(Root);
        if (!actual.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        for (var attempt = 0; attempt < 10 && Directory.Exists(actual); attempt++)
        {
            try { Directory.Delete(actual, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
