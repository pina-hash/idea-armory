using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace Armory.Agent.Tests;

// The native build's ArmoryShell.exe (tools/build-native.ps1) in publish/native/x64, or null.
internal static class NativeShell
{
    internal static string? Folder { get; } = Find();
    internal static string Exe => Path.Combine(Folder!, "ArmoryShell.exe");

    private static string? Find()
    {
        var root = Repo.FindRoot();
        if (root is null) return null;
        var folder = Path.Combine(root, "publish", "native", "x64");
        return File.Exists(Path.Combine(folder, "ArmoryShell.exe")) ? folder : null;
    }
}

// Windows with ArmoryShell.exe built; skipped elsewhere (Linux CI has neither).
public sealed class NativeShellFactAttribute : FactAttribute
{
    public NativeShellFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Runs the native ArmoryShell.exe; Windows only.";
        else if (NativeShell.Folder is null) Skip = "publish/native/x64 has no ArmoryShell.exe (run tools/build-native.ps1).";
    }
}

// T5 (docs/agent/EXPLORER.md): the line ArmoryShell.exe sends, the pipe's name, and a real pipe
// with real forwarders on Windows.
public sealed class ShellInboxTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static bool Parse(string line, out ShellRequest? request) => ShellLine.TryParse(Encoding.UTF8.GetBytes(line), At, out request);

    [Fact]
    public void A_forwarder_line_parses_into_verb_path_and_tick()
    {
        Assert.True(Parse("1\tcheckout\tC:\\IDEA\\Armory\\Robot 2027\\Élan.SLDPRT\t5676541\n", out var request));
        Assert.Equal(new ShellRequest(ShellVerb.CheckOut, @"C:\IDEA\Armory\Robot 2027\Élan.SLDPRT", 5676541, At), request);
        foreach (var (name, verb) in new[] { ("checkoutopen", ShellVerb.CheckOutAndOpen), ("checkin", ShellVerb.CheckIn), ("undo", ShellVerb.Undo),
            ("show", ShellVerb.Show), ("forcecheckin", ShellVerb.ForceCheckIn) })
        {
            Assert.True(Parse($"1\t{name}\tD:\\\t0\n", out var parsed));
            Assert.Equal(verb, parsed!.Verb);
            Assert.Equal(name, ShellVerbNames.Name(verb));
        }
        Assert.Equal(Encoding.UTF8.GetBytes("1\tundo\tC:\\a b\\ñ.prt\t42\n"), ShellLine.Format(ShellVerb.Undo, @"C:\a b\ñ.prt", 42));
    }

    [Theory]
    [InlineData("1\tcheckout\tC:\\a.prt\t1")]                 // no line feed
    [InlineData("2\tcheckout\tC:\\a.prt\t1\n")]               // another protocol version
    [InlineData("1\tCheckout\tC:\\a.prt\t1\n")]               // verbs are lower case
    [InlineData("1\tdelete\tC:\\a.prt\t1\n")]
    [InlineData("1\tcheckout\t\t1\n")]                        // no path
    [InlineData("1\tcheckout\tC:\\a\rb.prt\t1\n")]            // a control character
    [InlineData("1\tcheckout\tC:\\\"a\".prt\t1\n")]           // a quote
    [InlineData("1\tcheckout\tC:\\a.prt\t-1\n")]
    [InlineData("1\tcheckout\tC:\\a.prt\t1.5\n")]
    [InlineData("1\tcheckout\tC:\\a.prt\t\n")]
    [InlineData("1\tcheckout\tC:\\a.prt\t99999999999999999999999\n")]
    [InlineData("1\tcheckout\tC:\\a.prt\t1\textra\n")]
    [InlineData("1\tcheckout\tC:\\a.prt\n")]
    [InlineData("\n")]
    [InlineData("")]
    public void A_line_that_is_not_exactly_the_protocol_is_refused(string line)
    {
        Assert.False(Parse(line, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void Invalid_utf8_too_long_lines_and_too_long_paths_are_refused()
    {
        Assert.False(ShellLine.TryParse([(byte)'1', 9, 0xFF, 0xFE, 9, (byte)'1', 10], At, out _));
        Assert.False(Parse("1\tcheckout\t" + new string('a', ShellLine.MaxPathLength + 1) + "\t1\n", out _));
        Assert.True(Parse("1\tcheckout\t" + new string('a', ShellLine.MaxPathLength) + "\t1\n", out _));
        Assert.False(ShellLine.TryParse(new byte[ShellLine.MaxBytes + 1], At, out _));
    }

    [Fact]
    public void The_pipe_is_the_single_instance_name_plus_shell_and_tests_may_name_their_own()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var real = new AgentPaths(@"C:\Users\ana\AppData\Local\IDEA Armory", false);
        Assert.Equal("IDEA-Armory-Agent-S-1-5-21-1-2-3-1001-shell", ShellInbox.PipeName(real, sid));
        var test = new AgentPaths(@"C:\Temp\Armory Test", true);
        Assert.Equal("IDEA-Armory-Agent-" + sid + test.InstanceSuffix + "-shell", ShellInbox.PipeName(test, sid));
        Assert.Matches("^-[0-9a-f]{16}$", test.InstanceSuffix);
        var before = Environment.GetEnvironmentVariable(ShellInbox.PipeVariable);
        try
        {
            Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, "armory-test-pipe");
            // Only a test instance (ARMORY_DATA_DIR) may move the pipe.
            Assert.Equal("armory-test-pipe", ShellInbox.PipeName(test, sid));
            Assert.Equal("IDEA-Armory-Agent-S-1-5-21-1-2-3-1001-shell", ShellInbox.PipeName(real, sid));
        }
        finally { Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, before); }
    }

    // A private pipe served by a ShellInbox, with the environment a forwarder needs to find it.
    [SupportedOSPlatform("windows")]
    private sealed class TestInbox : IDisposable
    {
        private readonly string? dataBefore = Environment.GetEnvironmentVariable(AgentPaths.DataFolderVariable);
        private readonly string? pipeBefore = Environment.GetEnvironmentVariable(ShellInbox.PipeVariable);
        internal readonly TempFolder Data = new();
        internal readonly ConcurrentQueue<ShellBatch> Batches = new();
        internal readonly ConcurrentQueue<DateTimeOffset> Arrivals = new();
        internal readonly ShellInbox Inbox;
        internal readonly string Name = "armory-test-" + Guid.NewGuid().ToString("N");

        internal TestInbox()
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataFolderVariable, Data.Root);
            Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, Name);
            Inbox = new ShellInbox(AgentPaths.Resolve(), Batches.Enqueue);
            Assert.Equal(Name, Inbox.Name);
            Inbox.Start();
        }

        internal IReadOnlyList<string> AllPaths() => Batches.SelectMany(b => b.Paths).ToArray();

        public void Dispose()
        {
            Inbox.Dispose();
            Environment.SetEnvironmentVariable(AgentPaths.DataFolderVariable, dataBefore);
            Environment.SetEnvironmentVariable(ShellInbox.PipeVariable, pipeBefore);
            Data.Dispose();
        }
    }

    private static Process Forward(string verb, string path)
    {
        var start = new ProcessStartInfo(NativeShell.Exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add(path);
        return Process.Start(start)!;
    }

    private static void WaitUntil(Func<bool> done, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(50);
        Assert.True(done(), "Timed out.");
    }

    [NativeShellFact]
    [SupportedOSPlatform("windows")]
    public void Armory_shell_delivers_one_line_and_exits_zero()
    {
        using var inbox = new TestInbox();
        using var forwarder = Forward("checkin", @"C:\IDEA\Armory\Robot 2027\Élan Bracket.SLDPRT");
        Assert.True(forwarder.WaitForExit(30_000));
        Assert.Equal(0, forwarder.ExitCode);
        WaitUntil(() => !inbox.Batches.IsEmpty);
        var batch = Assert.Single(inbox.Batches);
        Assert.Equal(ShellVerb.CheckIn, batch.Verb);
        Assert.Equal([@"C:\IDEA\Armory\Robot 2027\Élan Bracket.SLDPRT"], batch.Paths);
    }

    [NativeShellFact]
    [SupportedOSPlatform("windows")]
    public void A_bad_verb_or_a_control_character_exits_two_and_sends_nothing()
    {
        using var inbox = new TestInbox();
        foreach (var (verb, path) in new[] { ("delete", @"C:\IDEA\Armory\a.prt"), ("checkout", "C:\\IDEA\\Armory\\a\tb.prt"), ("checkout", "") })
        {
            using var forwarder = Forward(verb, path);
            Assert.True(forwarder.WaitForExit(30_000));
            Assert.Equal(2, forwarder.ExitCode);
        }
        Thread.Sleep(600);
        Assert.Empty(inbox.Batches);
    }

    [NativeShellFact]
    [SupportedOSPlatform("windows")]
    public void A_hundred_forwarders_started_back_to_back_are_all_delivered_and_close_together()
    {
        using var inbox = new TestInbox();
        var watch = Stopwatch.StartNew();
        var forwarders = Enumerable.Range(0, 100).Select(i => Forward("checkout", $@"C:\IDEA\Armory\Robot 2027\Bulk\Part {i:D3}.SLDPRT")).ToArray();
        foreach (var forwarder in forwarders)
        {
            Assert.True(forwarder.WaitForExit(60_000));
            Assert.Equal(0, forwarder.ExitCode);
            forwarder.Dispose();
        }
        var delivered = watch.Elapsed;
        WaitUntil(() => inbox.AllPaths().Count == 100);
        Assert.Equal(100, inbox.AllPaths().Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(inbox.Batches, b => Assert.Equal(ShellVerb.CheckOut, b.Verb));
        Console.WriteLine($"100 forwarders delivered in {delivered.TotalMilliseconds:N0} ms as {inbox.Batches.Count} batch(es)");
        // The 100th path closes a batch at once; a slow runner may split a selection in two.
        Assert.InRange(inbox.Batches.Count, 1, 2);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task A_line_the_inbox_refuses_is_answered_0x15_and_never_reaches_a_batch()
    {
        using var inbox = new TestInbox();
        async Task<int> Send(byte[] line)
        {
            await using var client = new NamedPipeClientStream(".", inbox.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000);
            await client.WriteAsync(line);
            var answer = new byte[1];
            return await client.ReadAsync(answer) == 1 ? answer[0] : -1;
        }
        Assert.Equal(ShellLine.Refused, await Send(Encoding.UTF8.GetBytes("1\tdelete\tC:\\a.prt\t1\n")));
        Assert.Equal(ShellLine.Refused, await Send(Encoding.UTF8.GetBytes("9\tcheckout\tC:\\a.prt\t1\n")));
        Assert.Equal(ShellLine.Ack, await Send(ShellLine.Format(ShellVerb.Show, @"C:\IDEA\Armory\Robot 2027", 7)));
        WaitUntil(() => !inbox.Batches.IsEmpty);
        var batch = Assert.Single(inbox.Batches);
        Assert.Equal(ShellVerb.Show, batch.Verb);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_second_inbox_on_the_same_pipe_cannot_start()
    {
        using var inbox = new TestInbox();
        using var second = new ShellInbox(inbox.Name, _ => { });
        Assert.Throws<IOException>(second.Start);
    }
}
