using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Armory.Agent.Tests;

// The real IdeaArmory.exe as a child process with its own ARMORY_DATA_DIR (never signed in, so
// every request ends at "Connect this computer first."): a notification's second launch hands
// its link to the running one over the shell pipe and exits, the pipe takes only the protocol's
// lines, and Explorer-style forwarders of one right-click become one batch.
[SupportedOSPlatform("windows")]
public sealed class ShellProcessTests
{
    private const string Link = "idea-armory:act?t=AAAAAAAAAAAAAAAAAAAAAA&a=checkout";

    private static Process Start(string folder, TempFolder data, string? testPipe, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(folder, "IdeaArmory.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[AgentPaths.DataFolderVariable] = data.Root;
        start.Environment[AgentPaths.SiteVariable] = "http://127.0.0.1:9";
        if (testPipe is null) start.Environment.Remove(ShellInbox.PipeVariable);
        else start.Environment[ShellInbox.PipeVariable] = testPipe;
        return Process.Start(start) ?? throw new InvalidOperationException("IdeaArmory.exe did not start.");
    }

    private static void WriteSettings(TempFolder data)
        => File.WriteAllText(data.File("settings.json"), JsonSerializer.Serialize(new { vaultRoot = data.File("vault"), startAtSignIn = false, theme = "system" }));

    private static void WaitFor(string log, string words, Process running, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (!AgentExe.ReadShared(log).Contains(words, StringComparison.Ordinal) && !running.HasExited && watch.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(100);
        Assert.False(running.HasExited, "Armory exited early: " + AgentExe.ReadShared(log));
        Assert.True(AgentExe.ReadShared(log).Contains(words, StringComparison.Ordinal), $"Armory's log never said \"{words}\": " + AgentExe.ReadShared(log));
    }

    private static void Quit(string folder, TempFolder data, Process first)
    {
        try
        {
            using var quit = Start(folder, data, null, "--quit");
            Assert.True(quit.WaitForExit(60000), "--quit did not return.");
            Assert.True(first.WaitForExit(30000), "Armory did not exit after --quit.");
        }
        finally { AgentExe.Stop(first); }
    }

    private static string PipeOf(TempFolder data) => ShellInbox.PipeName(new AgentPaths(data.Root, true), WindowsIdentity.GetCurrent().User!.Value);

    private static async Task<int> SendAsync(string pipe, byte[] line)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        await client.WriteAsync(line);
        var answer = new byte[1];
        return await client.ReadAsync(answer) == 1 ? answer[0] : -1;
    }

    private static int Count(string text, string words) => text.Split('\n').Count(l => l.Contains(words, StringComparison.Ordinal));

    [WindowsFact]
    public void A_protocol_launch_forwards_to_the_running_agent_and_exits()
    {
        using var data = new TempFolder();
        WriteSettings(data);
        var folder = AgentExe.Folder();
        var log = data.File("logs/agent.log");
        var started = "started " + AgentExe.Version(folder);
        using var first = Start(folder, data, null, "--background");
        try
        {
            WaitFor(log, "shell: listening on", first);
            using (var second = Start(folder, data, null, Link))
            {
                Assert.True(second.WaitForExit(30000), "The link's launch did not exit.");
                Assert.Equal(0, second.ExitCode);
            }
            WaitFor(log, "shell: an unknown, used or old link opens the window", first);
            // Anything that is not exactly a link reaches it as "open the window", and nothing else.
            using (var third = Start(folder, data, null, "idea-armory:act?t=\"x\"&a=checkout"))
            {
                Assert.True(third.WaitForExit(30000), "The odd link's launch did not exit.");
                Assert.Equal(0, third.ExitCode);
            }
            WaitFor(log, "shell: a link that isn't one of Armory's opens the window", first);
            var text = AgentExe.ReadShared(log);
            Assert.Equal(2, Count(text, "shell: uri, 1 item"));
            Assert.Equal(1, Count(text, started));
            Assert.DoesNotContain("crash", text);
            Assert.False(first.HasExited);
        }
        finally { Quit(folder, data, first); }
        Assert.Contains("stopped", AgentExe.ReadShared(log));
    }

    [WindowsFact]
    public async Task The_running_agents_pipe_takes_only_protocol_lines_and_one_right_click_is_one_batch()
    {
        using var data = new TempFolder();
        WriteSettings(data);
        var folder = AgentExe.Folder();
        var log = data.File("logs/agent.log");
        using var first = Start(folder, data, null, "--background");
        try
        {
            WaitFor(log, "shell: listening on", first);
            var pipe = PipeOf(data);
            Assert.Equal(ShellLine.Refused, await SendAsync(pipe, Encoding.UTF8.GetBytes("1\tdelete\tC:\\IDEA\\Armory\\a.prt\t1\n")));
            Assert.Equal(ShellLine.Refused, await SendAsync(pipe, Encoding.UTF8.GetBytes("not a line at all\n")));
            Assert.Equal(ShellLine.Refused, await SendAsync(pipe, Encoding.UTF8.GetBytes("1\tcheckin\tC:\\a\"b.prt\t1\n")));
            // Three forwarders of one right-click, a few milliseconds apart: one batch.
            var vault = data.File("vault");
            foreach (var name in new[] { "Plate.SLDPRT", "Gear.SLDPRT", "Hub.SLDPRT" })
                Assert.Equal(ShellLine.Ack, await SendAsync(pipe, ShellLine.Format(ShellVerb.CheckIn, Path.Combine(vault, "Robot 2027", name), (ulong)Environment.TickCount64)));
            WaitFor(log, "shell: checkin, 3 items", first);
            Thread.Sleep(1000);
            var text = AgentExe.ReadShared(log);
            Assert.Equal(1, Count(text, "shell: checkin,"));
            Assert.Equal(3, Count(text, "shell pipe: refused a line"));
            Assert.False(first.HasExited);
        }
        finally { Quit(folder, data, first); }
    }

    // The same with the real ArmoryShell.exe, as File Explorer starts it once per selected item
    // (a test pipe, so the forwarder accepts this build's IdeaArmory.exe in another folder).
    [NativeShellFact]
    public void Explorer_style_forwarders_for_one_right_click_reach_the_running_agent_as_one_batch()
    {
        using var data = new TempFolder();
        WriteSettings(data);
        var folder = AgentExe.Folder();
        var log = data.File("logs/agent.log");
        var pipe = "armory-shell-process-test-" + Guid.NewGuid().ToString("N");
        using var first = Start(folder, data, pipe, "--background");
        try
        {
            WaitFor(log, "shell: listening on " + pipe, first);
            var vault = data.File("vault");
            var forwarders = new[] { "Plate.SLDPRT", "Gear.SLDPRT", "Hub.SLDPRT", "Arm.SLDPRT" }.Select(name =>
            {
                var start = new ProcessStartInfo(NativeShell.Exe) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("undo");
                start.ArgumentList.Add(Path.Combine(vault, "Robot 2027", name));
                start.Environment[AgentPaths.DataFolderVariable] = data.Root;
                start.Environment[ShellInbox.PipeVariable] = pipe;
                return Process.Start(start)!;
            }).ToArray();
            foreach (var forwarder in forwarders)
            {
                Assert.True(forwarder.WaitForExit(30000));
                Assert.Equal(0, forwarder.ExitCode);
                forwarder.Dispose();
            }
            WaitFor(log, "shell: undo, 4 items", first);
            Thread.Sleep(1000);
            Assert.Equal(1, Count(AgentExe.ReadShared(log), "shell: undo,"));
        }
        finally { Quit(folder, data, first); }
    }
}
