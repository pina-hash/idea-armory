using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Stage E3 (v2-design.md 4.1): the engine works on its own thread, moves several files at once,
// stops all of them at a crash, keeps a storage refusal one file's problem, answers File detail
// while a pass moves files, and reads the server at most twice a pass (not again when nothing
// moved).
public sealed class ConcurrencyTests
{
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    [PostgresFact]
    public async Task The_engine_works_on_its_own_thread_never_the_callers()
    {
        await using var t = await TeamAsync();
        var steps = new ConcurrentBag<string?>();
        var views = new ConcurrentBag<string?>();
        t.A.Engine.CrashPoint = _ => steps.Add(Thread.CurrentThread.Name);
        t.A.Engine.ViewChanged += _ => views.Add(Thread.CurrentThread.Name);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        Assert.True((await t.A.CheckInAsync(Plate)).Ok);
        Assert.Equal("Plate.SLDPRT", (await t.A.Engine.GetFileDetailAsync(await t.FileId("Plate.SLDPRT")))!.Name);
        Assert.NotEmpty(steps);
        Assert.All(steps, name => Assert.Equal("Armory engine", name));
        Assert.All(views, name => Assert.Equal("Armory engine", name));
        Assert.NotEqual("Armory engine", Thread.CurrentThread.Name);
    }

    // File detail reads what was published, never waiting for a pass: here a pass is waiting on
    // slow file storage (3 seconds a request) while the student opens a file's detail.
    [PostgresFact]
    public async Task File_detail_answers_while_a_pass_moves_files()
    {
        var slowStorage = new LatencyProfile(TimeSpan.FromSeconds(3), 0, 0, TimeSpan.Zero, TimeSpan.Zero);
        await using var t = await TeamAsync(latency: slowStorage);
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        for (var i = 0; i < 3; i++) t.A.Write($"Robot 2027/Drivetrain/Part-{i}.SLDPRT", $"part {i}");
        var moving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.A.Engine.ActivityChanged += a => { if (a.Active.Count > 0) moving.TrySetResult(); };
        var pass = t.A.SyncAsync();
        await moving.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();
        var detail = await t.A.Engine.GetFileDetailAsync(file);
        watch.Stop();
        Assert.False(pass.IsCompleted, "the pass ended before File detail answered");
        Assert.Equal("Plate.SLDPRT", detail!.Name);
        Assert.Single(detail.History);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"File detail took {watch.Elapsed.TotalSeconds:F1} s");
        await pass;
        Assert.Equal(3, await t.World.CountAsync("select count(*) from armory_files f where f.project_id=@p and f.name like 'Part-%' and f.current_version_id is not null", ("p", t.Project)));
    }

    // A crash in one of several files moving at once: every other one stops at its next step,
    // nothing of the crashed engine runs or saves after its pass threw, and the next engine over
    // the same stores finishes every file with exactly one version.
    [PostgresFact]
    public async Task A_crash_among_files_moving_at_once_stops_them_all_and_replays_to_one_version_each()
    {
        await using var t = await TeamAsync();
        var paths = Enumerable.Range(0, 12).Select(i => $"Robot 2027/Batch/Part-{i:D2}.SLDPRT").ToArray();
        foreach (var path in paths) t.A.Write(path, "bytes of " + path);
        var blobs = 0;
        var crashed = false;
        var after = new List<string>();
        t.A.CrashPoint = point =>
        {
            if (crashed) { after.Add(point); return; }
            if (point == "after-blob" && ++blobs == 4)
            {
                crashed = true;
                throw new SimulatedCrash(point);
            }
        };
        t.A.Restart();
        await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
        Assert.Empty(after); // no unit went past another step after the crash
        var (saves, document) = (t.A.State.Saves, t.A.State.Load());
        await Task.Delay(1000);
        Assert.Equal(saves, t.A.State.Saves);
        Assert.Equal(document, t.A.State.Load());
        Assert.Empty(after);

        t.A.CrashPoint = null;
        t.A.Restart();
        await t.A.SyncTimesAsync(2);
        foreach (var path in paths) Assert.Equal(1, await t.Versions(await t.FileId(Path.GetFileName(path))));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions s join armory_files f on f.id=s.file_id where f.project_id=@p", ("p", t.Project)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", t.Project)));
        Assert.All(paths, p => Assert.True(t.A.Disk.IsReadOnly(p)));
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }

    // Saves before server writes are a group commit: units that are ready while the save before
    // is still being written share the next one, so a slow disk makes fewer, larger saves rather
    // than a queue of them. Here every save takes 50 ms while 36 new files go in: at most one
    // save for every two server writes (a queue of saves was about one for each).
    [PostgresFact]
    public async Task A_slow_disk_makes_fewer_larger_saves()
    {
        await using var t = await TeamAsync();
        await t.A.SyncAsync();
        var paths = Enumerable.Range(0, 36).Select(i => $"Robot 2027/Batch/Part-{i:D2}.SLDPRT").ToArray();
        foreach (var path in paths) t.A.Write(path, "bytes of " + path);
        t.A.State.Delay = TimeSpan.FromMilliseconds(50);
        var (saves, rpc, reads) = (t.A.State.Saves, t.A.Network.RpcRequests, Reads(t));
        await t.A.SyncAsync();
        saves = t.A.State.Saves - saves;
        var after = Reads(t);
        var writes = t.A.Network.RpcRequests - rpc - (after.Projects + after.Changes + after.Files - reads.Projects - reads.Changes - reads.Files);
        foreach (var path in paths) Assert.Equal(1, await t.Versions(await t.FileId(Path.GetFileName(path))));
        Assert.All(paths, path => Assert.True(t.A.Disk.IsReadOnly(path)));
        Assert.True(writes >= 2 * paths.Length, $"{writes} server writes for {paths.Length} new files");
        Assert.True(2 * saves <= writes, $"{saves} saves of the state document for {writes} server writes");
    }

    // File storage refusing one file (or not answering in time) is that file's problem: the pass
    // stays online, the other files go, the one file says so in one item and goes next time.
    [PostgresFact]
    public async Task A_storage_refusal_fails_that_one_file_and_the_rest_go_on()
    {
        await using var t = await TeamAsync();
        var paths = Enumerable.Range(0, 3).Select(i => $"Robot 2027/Drivetrain/Part-{i}.SLDPRT").ToArray();
        foreach (var path in paths) t.A.Write(path, "bytes of " + path);
        var refused = Hash("bytes of " + paths[1]);
        Func<HttpRequestMessage, HttpResponseMessage?> refuse(HttpMethod method) =>
            request => request.Method == method && request.RequestUri!.AbsolutePath.EndsWith(refused, StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;

        t.A.Network.StorageFault = refuse(HttpMethod.Put);
        var report = await t.A.SyncAsync();
        Assert.True(report.Online);
        Assert.Equal(new long[] { 1, 0, 1 }, await VersionsOf(t, paths));
        var card = t.A.Card(NoticeKinds.CantSend)!;
        var item = Assert.Single(card.Items);
        Assert.Equal(paths[1], item.Path);
        Assert.Equal("Part-1.SLDPRT didn't go through this time", card.Title);
        t.A.Network.StorageFault = null;
        await t.A.SyncAsync();
        Assert.Equal(new long[] { 1, 1, 1 }, await VersionsOf(t, paths));
        Assert.Null(t.A.Card(NoticeKinds.CantSend));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", t.Project)));

        // Downloads the same way.
        t.B.Network.StorageFault = refuse(HttpMethod.Get);
        report = await t.B.SyncAsync();
        Assert.True(report.Online);
        Assert.Equal([true, false, true], paths.Select(p => t.B.Read(p) is not null).ToArray());
        Assert.Equal(paths[1], Assert.Single(t.B.Card(NoticeKinds.CantSend)!.Items).Path);
        t.B.Network.StorageFault = null;
        await t.B.SyncAsync();
        Assert.All(paths, p => Assert.Equal("bytes of " + p, t.B.Text(p)));
        Assert.Null(t.B.Card(NoticeKinds.CantSend));
    }

    // A pass reads the server at most twice (at its start, and once more after it wrote), and a
    // pass after nothing moved reads the change feed and not the project's files.
    [PostgresFact]
    public async Task The_server_is_read_at_most_twice_a_pass_and_not_again_when_nothing_moved()
    {
        await using var t = await TeamAsync();
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.A.Write(Plate, "plate v1");
        t.A.Write(Bracket, "bracket v1");
        var (projects, changes, files) = Reads(t);
        await t.A.SyncAsync();
        Assert.Equal(projects + 2, Reads(t).Projects);
        Assert.Equal(2, await t.Versions(await t.FileId("Plate.SLDPRT")) + await t.Versions(await t.FileId("Bracket.SLDPRT")));

        // The next pass reads this computer's own releases in the change feed (and the files once
        // more); after that, a pass with nothing new reads no files.
        await t.A.SyncAsync();
        (projects, changes, files) = Reads(t);
        await t.A.SyncAsync();
        Assert.Equal((projects + 1, changes + 1, files), Reads(t));

        // The team moved something: the change feed says so, and the files are read again.
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        (projects, changes, files) = Reads(t);
        await t.A.SyncAsync();
        Assert.Equal((projects + 1, changes + 1, files + 1), Reads(t));
        Assert.Equal("Checked out by Maria Lopez on student B lab PC", t.A.Row(Plate).Checkout.Label);
    }

    private static (long Projects, long Changes, long Files) Reads(Team t)
        => (t.World.Supabase.RpcCount("armory_my_projects"), t.World.Supabase.RpcCount("armory_list_changes"), t.World.Supabase.RpcCount("armory_project_files"));

    private static async Task<long[]> VersionsOf(Team t, string[] paths)
    {
        var counts = new long[paths.Length];
        for (var i = 0; i < paths.Length; i++)
            counts[i] = await t.World.CountAsync("select count(*) from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p and f.name=@n",
                ("p", t.Project), ("n", Path.GetFileName(paths[i])));
        return counts;
    }
}
