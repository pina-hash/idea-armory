using System.Diagnostics;
using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// A computer joining a project that already has thousands of files (v0.2.1, the field report:
// a student's first sign-in with 2,000+ files on the server died silently, again and again).
// The new computer runs its loop the way the app does, on an engine thread with a small stack
// (256 KB) so that any depth growing with the number of files fails here and never on a
// student's PC, and every view it raises is serialized the way the window posts it.
public sealed class FirstSyncTests(ITestOutputHelper output)
{
    private const int Files = 3_000;

    [PostgresFact]
    public async Task A_new_computer_downloads_3000_files_on_a_small_stack()
    {
        await using var t = await TeamAsync();
        // The project: mixed folders, some deep, some names shared across folders.
        List<string> paths = [];
        for (var i = 0; i < Files; i++)
        {
            var folder = (i % 10) switch
            {
                0 => "Robot 2027/Deep/" + string.Join('/', Enumerable.Range(0, 1 + i % 12).Select(d => $"L{d}")),
                1 or 2 => $"Robot 2027/Drivetrain/Gearbox {i % 7}",
                3 => "Robot 2027",
                _ => $"Robot 2027/Assemblies/Sub {i % 40:D2}",
            };
            var name = i % 97 == 0 ? $"Shared-{i % 5}.SLDPRT" : $"Part-{i:D4}.SLDPRT";
            paths.Add($"{folder}/{name}");
            t.A.Write($"{folder}/{name}", $"part {i} {new string('x', i % 300)}");
        }
        for (var pass = 0; pass < 6; pass++) await t.A.SyncAsync();
        var onServer = await t.World.CountAsync("select count(*) from armory_files where project_id=@p and deleted_at is null and current_version_id is not null", ("p", t.Project));
        Assert.True(onServer > Files - 100, $"only {onServer} files reached the server");

        // Maria's new computer: an empty vault, its loop on a small stack.
        var c = await t.World.ComputerAsync("student C new laptop", Maria);
        c.EngineStackBytes = 256 * 1024;
        c.PassSlice = TimeSpan.FromSeconds(2);
        // How deep the engine thread's stack gets at any step of a file (every crash point).
        var deepest = 0;
        c.CrashPoint = _ => deepest = Math.Max(deepest, new StackTrace().FrameCount);
        c.Restart();
        var views = 0;
        var repeated = 0;
        long largest = 0;
        string? last = null;
        c.Views += view =>
        {
            var json = BridgeMessages.ViewMessage(view);
            Interlocked.Increment(ref views);
            if (json == last) Interlocked.Increment(ref repeated);
            last = json;
            if (json.Length > Interlocked.Read(ref largest)) Interlocked.Exchange(ref largest, json.Length);
        };
        var watch = Stopwatch.StartNew();
        c.Engine.Start();
        while (true)
        {
            var here = Directory.Exists(c.Disk.Full("Robot 2027"))
                ? Directory.EnumerateFiles(c.Disk.Full("Robot 2027"), "*.SLDPRT", SearchOption.AllDirectories).Count() : 0;
            if (here >= onServer) break;
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(4), $"only {here} of {onServer} files arrived in 4 minutes; log: {string.Join(" | ", c.Logged.TakeLast(5))}");
            await Task.Delay(250);
        }
        var took = watch.Elapsed;
        await c.Engine.StopAsync();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FIRST SYNC files={onServer} seconds={took.TotalSeconds:F1} views={views} largest_view_bytes={largest} deepest_stack_frames={deepest}"));
        // A view is raised only when something in it changed, and a row costs a few hundred bytes.
        Assert.Equal(0, repeated);
        Assert.True(largest < 500L * onServer, $"the largest view was {largest:N0} bytes for {onServer:N0} files");
        // The stack never grows with the number of files (each step runs as one queued item).
        Assert.True(deepest < 150, $"the engine's stack reached {deepest} frames");
        // The log says what each pass that moved files did.
        Assert.Contains(c.Logged, l => l.StartsWith("pass: moving ", StringComparison.Ordinal));
        Assert.Contains(c.Logged, l => l.StartsWith("pass: ended after ", StringComparison.Ordinal) && l.Contains(" downloaded", StringComparison.Ordinal));
        Assert.DoesNotContain(c.Logged, l => l.StartsWith("engine:", StringComparison.Ordinal));
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }
}
