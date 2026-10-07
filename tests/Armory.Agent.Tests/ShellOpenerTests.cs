using System.ComponentModel;
using System.Diagnostics;

namespace Armory.Agent.Tests;

// v0.2.1, Open (ShellOpener): the shell's open runs on an STA thread of its own, never the
// caller's; a failed DDE conversation (1156, 1157: SolidWorks not running yet) or a shell call
// that never comes back opens the file through File Explorer instead; and the caller waits a
// second at most. A fake StartShell stands in for Process.Start, so these run on every host.
public sealed class ShellOpenerTests
{
    private const string Plate = @"C:\IDEA\Armory\Robot 2027\Drivetrain\Plate.SLDPRT";

    private sealed class Calls
    {
        private readonly List<(ProcessStartInfo Start, ApartmentState Apartment, int Thread)> list = [];
        public void Add(ProcessStartInfo start)
        {
            var apartment = OperatingSystem.IsWindows() ? Thread.CurrentThread.GetApartmentState() : ApartmentState.Unknown;
            lock (list) list.Add((start, apartment, Environment.CurrentManagedThreadId));
        }
        public IReadOnlyList<(ProcessStartInfo Start, ApartmentState Apartment, int Thread)> Seen { get { lock (list) return list.ToArray(); } }
        public bool WaitFor(int count, TimeSpan within) => SpinWait.SpinUntil(() => Seen.Count >= count, within);
    }

    [Fact]
    public void A_failed_DDE_conversation_opens_the_file_through_File_Explorer()
    {
        foreach (var code in new[] { 1156, 1157 })
        {
            var calls = new Calls();
            var log = new List<string>();
            var opener = new ShellOpener(start =>
            {
                calls.Add(start);
                if (start.UseShellExecute) throw new Win32Exception(code);
            }) { Log = line => { lock (log) log.Add(line); } };
            Assert.Null(opener.Open(Plate));
            Assert.True(calls.WaitFor(2, TimeSpan.FromSeconds(5)));
            var (shell, explorer) = (calls.Seen[0].Start, calls.Seen[1].Start);
            Assert.True(shell.UseShellExecute);
            Assert.Equal("open", shell.Verb);
            Assert.Equal(Plate, shell.FileName);
            // Exactly what a double-click in File Explorer runs: explorer.exe with the quoted path.
            Assert.Equal("explorer.exe", explorer.FileName);
            Assert.Equal("\"" + Plate + "\"", explorer.Arguments);
            Assert.False(explorer.UseShellExecute);
            lock (log) Assert.Contains(log, l => l.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_shell_runs_on_an_STA_thread_of_its_own()
    {
        var calls = new Calls();
        Assert.Null(new ShellOpener(calls.Add).Open(Plate));
        var (start, apartment, thread) = Assert.Single(calls.Seen);
        Assert.Equal(Plate, start.FileName);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
        if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.STA, apartment);
    }

    // No program for the type, or the student canceled: said as it is, never retried through Explorer.
    [Fact]
    public void No_program_or_a_cancel_is_the_answer_and_not_retried()
    {
        foreach (var code in new[] { 1155, 1223 })
        {
            var calls = new Calls();
            var error = new ShellOpener(start => { calls.Add(start); throw new Win32Exception(code); }).Open(Plate);
            Assert.Equal(code, Assert.IsType<Win32Exception>(error).NativeErrorCode);
            Assert.Single(calls.Seen);
        }
    }

    // A shell call that never comes back (DDE waiting on a program that is still starting): the
    // caller has its answer within about a second, and Explorer is asked after FallbackAfter.
    [Fact]
    public void A_shell_call_that_never_returns_holds_nobody_and_falls_back()
    {
        var calls = new Calls();
        using var never = new ManualResetEventSlim();
        var opener = new ShellOpener(start =>
        {
            calls.Add(start);
            if (start.UseShellExecute) never.Wait();
        }) { AnswerWithin = TimeSpan.FromMilliseconds(300), FallbackAfter = TimeSpan.FromMilliseconds(600) };
        var watch = Stopwatch.StartNew();
        Assert.Null(opener.Open(Plate));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Open took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(calls.WaitFor(2, TimeSpan.FromSeconds(5)));
        Assert.Equal("explorer.exe", calls.Seen[1].Start.FileName);
        never.Set();
    }
}
