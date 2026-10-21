using System.Diagnostics;
using System.Text.Json;

namespace Armory.Agent.Tests;

// The real IdeaArmory.exe as child processes, each with its own ARMORY_DATA_DIR.
public sealed class AgentProcessTests
{
    [WindowsFact]
    public void Second_launch_exits_and_quit_stops_the_first_cleanly()
    {
        using var data = new TempFolder();
        WriteSettings(data);
        var folder = AgentExe.Folder();
        var started = "started " + AgentExe.Version(folder);
        var log = data.File("logs/agent.log");
        using var first = AgentExe.Start(folder, data.Root, "--background");
        try
        {
            Assert.True(WaitUntil(() => AgentExe.ReadShared(log).Contains(started, StringComparison.Ordinal) || first.HasExited, TimeSpan.FromSeconds(60)),
                "The first instance did not log that it started.");
            Assert.False(first.HasExited, "The first instance exited early: " + AgentExe.ReadShared(log));

            using (var second = AgentExe.Start(folder, data.Root))
            {
                Assert.True(second.WaitForExit(30000), "The second launch did not exit.");
                Assert.Equal(0, second.ExitCode);
            }
            using (var background = AgentExe.Start(folder, data.Root, "--background"))
            {
                Assert.True(background.WaitForExit(30000), "The second --background launch did not exit.");
                Assert.Equal(0, background.ExitCode);
            }
            Assert.False(first.HasExited);

            using (var quit = AgentExe.Start(folder, data.Root, "--quit"))
            {
                Assert.True(quit.WaitForExit(60000), "--quit did not return.");
                Assert.Equal(0, quit.ExitCode);
            }
            Assert.True(first.WaitForExit(30000), "The first instance did not exit after --quit.");
            Assert.Equal(0, first.ExitCode);
            var text = AgentExe.ReadShared(log);
            Assert.Contains("stopped", text);
            Assert.DoesNotContain("crash", text);
            Assert.Single(text.Split('\n'), line => line.Contains(started, StringComparison.Ordinal));
        }
        finally { AgentExe.Stop(first); }
    }

    [WindowsFact]
    public void Quit_with_nothing_running_returns_at_once()
    {
        using var data = new TempFolder();
        using var quit = AgentExe.Start(AgentExe.Folder(), data.Root, "--quit");
        Assert.True(quit.WaitForExit(30000));
        Assert.Equal(0, quit.ExitCode);
    }

    [WindowsFact]
    public void Check_prints_one_json_line_and_its_exit_code_matches_what_it_found()
    {
        using var data = new TempFolder();
        WriteSettings(data);
        var folder = AgentExe.Folder();
        var (code, json) = RunCheck(folder, data.Root);
        Assert.Equal(["vaultRoot", "version", "webView2Runtime", "wwwroot"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(AgentExe.Version(folder), json.RootElement.GetProperty("version").GetString());
        Assert.True(json.RootElement.GetProperty("wwwroot").GetBoolean());
        Assert.Equal(data.File("vault"), json.RootElement.GetProperty("vaultRoot").GetString());
        var runtime = json.RootElement.GetProperty("webView2Runtime");
        Assert.Equal(runtime.ValueKind == JsonValueKind.String ? 0 : 1, code);
        Assert.False(Directory.Exists(data.File("logs")), "--check must not start the app.");
    }

    [WindowsFact]
    public void Check_fails_when_the_page_files_are_missing()
    {
        using var data = new TempFolder();
        using var copy = new TempFolder();
        var folder = AgentExe.Folder();
        // The whole app without its wwwroot folder. Every file beside IdeaArmory.exe is copied,
        // never a list of names: a list went stale when 0.4.0 added Velopack.dll, and the copy
        // then died in Main before --check could print anything.
        CopyApp(folder, copy.Root, skip: "wwwroot");
        Assert.False(Directory.Exists(Path.Combine(copy.Root, "wwwroot")));
        var (code, json) = RunCheck(copy.Root, data.Root);
        Assert.False(json.RootElement.GetProperty("wwwroot").GetBoolean());
        Assert.Equal(@"C:\IDEA\Armory", json.RootElement.GetProperty("vaultRoot").GetString());
        Assert.Equal(1, code);

        // The control: the same copy with wwwroot put back is a whole app, so the missing page
        // files above are the only reason it failed.
        CopyApp(Path.Combine(folder, "wwwroot"), Path.Combine(copy.Root, "wwwroot"), skip: null);
        var (wholeCode, whole) = RunCheck(copy.Root, data.Root);
        Assert.True(whole.RootElement.GetProperty("wwwroot").GetBoolean());
        Assert.Equal(whole.RootElement.GetProperty("webView2Runtime").ValueKind == JsonValueKind.String ? 0 : 1, wholeCode);
    }

    private static void CopyApp(string from, string to, string? skip)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (skip is not null && relative != top && top.Equals(skip, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static (int Code, JsonDocument Json) RunCheck(string folder, string dataFolder)
    {
        using var check = AgentExe.Start(folder, dataFolder, "--check");
        var output = check.StandardOutput.ReadToEndAsync();
        Assert.True(check.WaitForExit(60000), "--check did not exit.");
        var lines = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines);
        return (check.ExitCode, JsonDocument.Parse(line));
    }

    private static void WriteSettings(TempFolder data)
        => File.WriteAllText(data.File("settings.json"), JsonSerializer.Serialize(new { vaultRoot = data.File("vault"), startAtSignIn = false, theme = "system" }));

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(200);
        }
        return condition();
    }
}
