using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
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
    // nothing of the crashed engine runs, applies an answer or serializes its state after the
    // crash (the saves serialized before it finish, as they would in a real crash), and the next
    // engine over the same stores finishes every file with exactly one version. With a slow disk
    // (40 ms a save) a group commit is nearly always waiting when the crash comes, and answers of
    // other files are on their way: none of that may reach the disk, so the document there never
    // holds a step half done (a lock answered but still in flight, a file created but still in
    // flight, a commit answered but still in flight), which no real crash could leave.
    [PostgresFact]
    public async Task A_crash_among_files_moving_at_once_stops_them_all_and_replays_to_one_version_each()
    {
        foreach (var (point, k, delay) in new[] { ("after-blob", 4, 0), ("after-lock", 3, 40), ("after-lock", 6, 40), ("after-create", 5, 40), ("after-commit-rpc", 4, 40) })
        {
            var round = $"{point} #{k}, {delay} ms a save";
            await using var t = await TeamAsync();
            if (delay > 0) await t.A.SyncAsync();
            var paths = Enumerable.Range(0, 12).Select(i => $"Robot 2027/Batch/Part-{i:D2}.SLDPRT").ToArray();
            foreach (var path in paths) t.A.Write(path, "bytes of " + path);
            t.A.State.Delay = TimeSpan.FromMilliseconds(delay);
            var hits = 0;
            var crashed = false;
            var serializedAtCrash = -1;
            var after = new ConcurrentQueue<string>();
            t.A.CrashPoint = p =>
            {
                if (crashed) { after.Enqueue(p); return; }
                if (p == point && ++hits == k)
                {
                    crashed = true;
                    serializedAtCrash = t.A.Engine.Serializations;
                    throw new SimulatedCrash(p);
                }
            };
            t.A.Restart();
            await Assert.ThrowsAsync<SimulatedCrash>(() => t.A.SyncAsync());
            Assert.True(after.IsEmpty, $"{round}: steps after the crash: {string.Join(", ", after)}"); // no unit went past another step
            Assert.True(serializedAtCrash == t.A.Engine.Serializations, $"{round}: the state was serialized {t.A.Engine.Serializations - serializedAtCrash} more times after the crash");
            var (saves, document) = (t.A.State.Saves, t.A.State.Load());
            await Task.Delay(500);
            Assert.Equal(saves, t.A.State.Saves);
            Assert.Equal(document, t.A.State.Load());
            Assert.True(after.IsEmpty, round);
            var halfDone = HalfDone(document);
            Assert.True(halfDone.Count == 0, $"{round}: steps half done on disk: {string.Join(", ", halfDone)}");

            t.A.CrashPoint = null;
            t.A.State.Delay = TimeSpan.Zero;
            t.A.Restart();
            await t.A.SyncTimesAsync(2);
            foreach (var path in paths) Assert.Equal(1, await t.Versions(await t.FileId(Path.GetFileName(path))));
            Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_side_versions s join armory_files f on f.id=s.file_id where f.project_id=@p", ("p", t.Project)));
            Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", t.Project)));
            Assert.All(paths, p => Assert.True(t.A.Disk.IsReadOnly(p)));
            Assert.Empty(t.A.Disk.OpenWriteViolations);
        }
    }

    // New files' records in a saved document that hold an answer with their write still in
    // flight: only a save made in the middle of a step (after its answer, before its record was
    // cleared) can hold one.
    private static List<string> HalfDone(byte[]? document)
    {
        List<string> found = [];
        if (document is null) return found;
        foreach (var (path, node) in JsonNode.Parse(document)!["files"]!.AsObject())
        {
            if (node?["inflight"] is not JsonObject flight) continue;
            var kind = flight["kind"]?.GetValue<string>();
            if (kind == "lock" && ((node["attempt"]?.GetValue<int>() ?? 0) > 0 || node["holder"] is JsonObject)) found.Add($"{path}: lock answered");
            if (kind == "create" && node["fileId"] is JsonValue) found.Add($"{path}: created");
            if (kind == "commit" && node["baseHash"]?.GetValue<string>() is { } hash && hash == flight["hash"]?.GetValue<string>()) found.Add($"{path}: committed");
        }
        return found;
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

    // Names the server holds for one (it compares lower(normalize(name, NFC))) are one unit here,
    // whatever .NET's own casing says (it keeps the capital sharp s and the dotted capital I
    // apart from their small letters): the first in path order gets the name and the other is one
    // "shares a name" item, however fast the server answers.
    [PostgresFact]
    public async Task Names_the_server_holds_for_one_are_sent_one_after_the_other()
    {
        (string First, string Second)[] pairs = [("Robot 2027/A/\u1E9Eolt.SLDPRT", "Robot 2027/B/\u00DFolt.SLDPRT"), ("Robot 2027/C/\u0130nsert.SLDPRT", "Robot 2027/D/insert.SLDPRT")];
        foreach (var rpc in new[] { 0, 30 })
        {
            await using var t = await TeamAsync(latency: new LatencyProfile(TimeSpan.Zero, 0, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(rpc)));
            await t.A.SyncAsync();
            foreach (var (first, second) in pairs)
            {
                t.A.Write(first, "first " + first);
                t.A.Write(second, "second " + second);
            }
            await t.A.SyncAsync();
            var live = await t.World.QueryAsync("select folder || '/' || name from armory_files where project_id=@p and current_version_id is not null and deleted_at is null",
                r => "Robot 2027/" + r.GetString(0), ("p", t.Project));
            Assert.Equal(pairs.Select(p => p.First).Order(StringComparer.Ordinal), live.Order(StringComparer.Ordinal));
            var shared = t.A.Card(NoticeKinds.NameShared)!;
            Assert.Equal(pairs.Select(p => p.Second).Order(StringComparer.Ordinal), shared.Items.Select(i => i.Path).Order(StringComparer.Ordinal));
        }
    }

    // Moving reads as one line for as long as the move lasts: the window's rename of a folder
    // (the team's answer included, here a slow one) is "Moving 3 files to Robot 2027 › Drivetrain
    // › Gears" in the activity messages, and the team's rename made on the other computer is one
    // operation with its own count and its one target, not the files moved so far.
    [PostgresFact]
    public async Task Moving_files_reads_as_one_line_while_the_move_lasts()
    {
        await using var t = await TeamAsync();
        string[] files = ["Robot 2027/Drivetrain/Gearbox/Housing.SLDPRT", "Robot 2027/Drivetrain/Gearbox/Gear-14T.SLDPRT", "Robot 2027/Drivetrain/Gearbox/Gear-60T.SLDPRT"];
        foreach (var path in files) t.A.Write(path, "bytes of " + path);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var seen = new ConcurrentQueue<ActivityView>();
        t.A.Activities += seen.Enqueue;
        t.A.Network.RpcDelay = path => path.EndsWith("/armory_rename_folder", StringComparison.Ordinal) ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        Assert.True((await t.A.Engine.RenameFolderAsync(t.Project, "Drivetrain/Gearbox", "Gears")).Ok);
        const string Line = "Moving 3 files to Robot 2027 › Drivetrain › Gears";
        Assert.Contains(seen, a => a.Move is { FilesTotal: 3, FilesDone: 0, Line: Line } && a.Line == Line);
        Assert.Null(t.A.Engine.View.Activity.Move);

        // The other computer moves its folder in one step; right after the move, before its
        // records follow, the panel says what this operation moves.
        ActivityView? during = null;
        t.B.CrashPoint = point => { if (point == "after-team-folder-move") during = t.B.Engine.ActivityNow; };
        t.B.Restart();
        await t.B.SyncAsync();
        Assert.Equal(Line, during?.Move?.Line);
        Assert.Equal(3, during!.Move!.FilesTotal);
        Assert.All(files, p => Assert.NotNull(t.B.Read(p.Replace("Gearbox", "Gears", StringComparison.Ordinal))));
        Assert.Null(t.B.Engine.View.Activity.Move);
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
