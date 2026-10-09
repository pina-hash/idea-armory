using System.Diagnostics;
using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// v0.2.1 (docs/agent/ENGINE.md, "The loop"): a click never waits behind a whole pass. While a
// window action waits, the loop's pass starts no new file; the action's own pass moves only its
// files; and a long transfer stops starting files every PassSlice so the team's changes are read
// again during it. Each computer here runs its loop the way the app does.
public sealed class ResponsivenessTests(ITestOutputHelper output)
{
    private const int Bulk = 200;
    private const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";

    private static int Here(Computer c, string folder)
        => Directory.Exists(c.Disk.Full(folder)) ? Directory.EnumerateFiles(c.Disk.Full(folder), "*", SearchOption.AllDirectories).Count() : 0;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, "Timed out waiting: " + what);
            await Task.Delay(50);
        }
    }

    // B is downloading 200 files (a quarter second each from file storage, six at a time: about
    // 9 seconds) when Maria checks out another file. The check out answers in well under that,
    // with the lock taken while most of the downloads are still to come; the downloads then finish.
    [PostgresFact]
    public async Task A_check_out_answers_while_hundreds_of_files_download()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        for (var i = 0; i < Bulk; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Plate.SLDPRT");

        t.B.Network.StorageDelay = request => request.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
        var whole = Stopwatch.StartNew();
        t.B.Engine.Start();
        await WaitUntil(() => Here(t.B, "Robot 2027/Bulk") >= 6, TimeSpan.FromSeconds(30), "B's downloads to start");

        var click = Stopwatch.StartNew();
        var answer = await t.B.CheckOutAsync(Plate);
        click.Stop();
        var arrivedAtAnswer = Here(t.B, "Robot 2027/Bulk");
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("Checked out Plate.SLDPRT.", answer.Message);
        Assert.Equal(Maria, await t.Holder(file));
        Assert.False(t.B.Disk.IsReadOnly(Plate));

        await WaitUntil(() => Here(t.B, "Robot 2027/Bulk") == Bulk, TimeSpan.FromSeconds(90), "B's downloads to finish");
        whole.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHECK OUT DURING DOWNLOAD answer_s={click.Elapsed.TotalSeconds:F2} arrived_at_answer={arrivedAtAnswer} whole_download_s={whole.Elapsed.TotalSeconds:F1}"));
        Assert.True(arrivedAtAnswer < Bulk / 2, $"{arrivedAtAnswer} of {Bulk} files had arrived when the check out answered");
        Assert.True(click.Elapsed < TimeSpan.FromSeconds(3), $"the check out took {click.Elapsed.TotalSeconds:F1} s");
        Assert.True(click.Elapsed * 3 < whole.Elapsed, $"the check out took {click.Elapsed.TotalSeconds:F1} s of a {whole.Elapsed.TotalSeconds:F1} s download");
        Assert.Empty(t.B.Disk.OpenWriteViolations);
    }

    // B is downloading 200 files for about 20 seconds when Alex checks a file out on A. B's row
    // says Alex has it long before B's download ends: the loop's pass stops starting files after
    // PassSlice (8 seconds) and the next one reads the server again at once.
    [PostgresFact]
    public async Task Someone_elses_check_out_shows_during_a_long_download()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        for (var i = 0; i < Bulk; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);

        t.B.Network.StorageDelay = request => request.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
        var whole = Stopwatch.StartNew();
        t.B.Engine.Start();
        await WaitUntil(() => Here(t.B, "Robot 2027/Bulk") >= 6, TimeSpan.FromSeconds(30), "B's downloads to start");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        var checkedOut = whole.Elapsed;

        await WaitUntil(() => t.B.Row(Plate).Checkout.State == CheckoutStates.Other, TimeSpan.FromSeconds(60), "B to show Alex's check out");
        var shown = whole.Elapsed;
        var arrivedWhenShown = Here(t.B, "Robot 2027/Bulk");
        Assert.Equal("Checked out by Alex Kim on student A laptop", t.B.Row(Plate).Checkout.Label);
        await WaitUntil(() => Here(t.B, "Robot 2027/Bulk") == Bulk, TimeSpan.FromSeconds(120), "B's downloads to finish");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHECK OUT SEEN DURING DOWNLOAD checked_out_s={checkedOut.TotalSeconds:F1} shown_s={shown.TotalSeconds:F1} arrived_when_shown={arrivedWhenShown} whole_download_s={whole.Elapsed.TotalSeconds:F1}"));
        Assert.True(arrivedWhenShown < Bulk, "B showed the check out only after its download finished");
        Assert.True(shown - checkedOut < TimeSpan.FromSeconds(12), $"B showed it {(shown - checkedOut).TotalSeconds:F1} s after it happened");
    }

    // Alex checks in one file while his computer uploads 200 new ones (a quarter second each to
    // file storage): the check in answers promptly, its version shared, while most of the
    // uploads are still to come.
    [PostgresFact]
    public async Task A_check_in_answers_while_hundreds_of_files_upload()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2");
        for (var i = 0; i < Bulk; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D3}.SLDPRT", $"bulk part {i}");

        t.A.Network.StorageDelay = request => request.Method == HttpMethod.Put ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
        var whole = Stopwatch.StartNew();
        t.A.Engine.Start();
        Task<long> Uploaded() => t.World.CountAsync("select count(*) from armory_files where project_id=@p and folder='Bulk' and current_version_id is not null", ("p", t.Project));
        await WaitUntil(() => Uploaded().GetAwaiter().GetResult() >= 6, TimeSpan.FromSeconds(30), "A's uploads to start");

        var click = Stopwatch.StartNew();
        var answer = await t.A.CheckInAsync(Plate);
        click.Stop();
        var uploadedAtAnswer = await Uploaded();
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("Checked in Plate.SLDPRT.", answer.Message);
        Assert.Equal(Hash("v2"), await t.CurrentHash(file));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.True(t.A.Disk.IsReadOnly(Plate));

        await WaitUntil(() => Uploaded().GetAwaiter().GetResult() == Bulk, TimeSpan.FromSeconds(90), "A's uploads to finish");
        whole.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHECK IN DURING UPLOAD answer_s={click.Elapsed.TotalSeconds:F2} uploaded_at_answer={uploadedAtAnswer} whole_upload_s={whole.Elapsed.TotalSeconds:F1}"));
        Assert.True(uploadedAtAnswer < Bulk / 2, $"{uploadedAtAnswer} of {Bulk} files were up when the check in answered");
        Assert.True(click.Elapsed < TimeSpan.FromSeconds(3), $"the check in took {click.Elapsed.TotalSeconds:F1} s");
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }

    private static int Releases(Team t) => t.World.Supabase.RpcCount("armory_release_locks") + t.World.Supabase.RpcCount("armory_release_lock");
    private static int Passes(Computer c, string kind) => c.Flight.Snapshot().Count(e => e.Kind == Armory.Telemetry.FlightKind.PassStart && e.Name == kind);

    // 0.3.3 (feedback N6; 0.3.1's 15 row clicks were 15 passes, the last answered 8 minutes after
    // it was pressed): ten Check in keys pressed while a pass holds the gate are each recorded at
    // once and answered together, with one armory_release_locks call and at most one action pass.
    [PostgresFact]
    public async Task Ten_check_in_clicks_waiting_for_a_pass_are_one_pass_and_one_release_call()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        var paths = Enumerable.Range(0, 10).Select(i => $"Robot 2027/Rows/Part-{i}.SLDPRT").ToArray();
        foreach (var path in paths) t.A.Write(path, "v1 " + path);
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(paths)).Ok);
        foreach (var path in paths) t.A.Save(path, "v2 " + path);
        // A pass holds the gate: its first read of the server is slow.
        var slow = 1;
        t.A.Network.RpcDelay = rpc => rpc.EndsWith("/armory_my_projects", StringComparison.Ordinal) && Interlocked.Exchange(ref slow, 0) == 1 ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        var holding = t.A.SyncAsync();
        await WaitUntil(() => Volatile.Read(ref slow) == 0, TimeSpan.FromSeconds(10), "the pass to read the server");
        var (releases, passes) = (Releases(t), Passes(t.A, "action"));
        var answers = await Task.WhenAll(paths.Select(path => t.A.CheckInAsync(path)));
        await holding;
        for (var i = 0; i < paths.Length; i++) Assert.Equal($"Checked in Part-{i}.SLDPRT.", answers[i].Message);
        Assert.Equal(1, Releases(t) - releases);
        Assert.InRange(Passes(t.A, "action") - passes, 0, 1);
        foreach (var path in paths) Assert.Equal(Hash("v2 " + path), await t.CurrentHash(await t.FileId(Path.GetFileName(path))));
        Assert.Empty(t.A.Disk.OpenWriteViolations);
    }

    // 0.3.3 (X-view-open-per-file): files added while open (checked in by themselves once closed)
    // are never asked about one by one, by a view or by a quiet pass (IDEA-06 had 55 of them, one
    // Restart Manager session each in every view): a pass asks once for all of them and the views
    // read its answer.
    [PostgresFact]
    public async Task Views_and_a_quiet_pass_never_ask_whether_a_file_is_open_one_by_one()
    {
        await using var t = await TeamAsync();
        var paths = Enumerable.Range(0, 20).Select(i => $"Robot 2027/Open/Part-{i:D2}.SLDPRT").ToArray();
        foreach (var path in paths)
        {
            t.A.Write(path, "new " + path);
            t.A.Open(path);
        }
        await t.A.SyncTimesAsync(2);
        Assert.Equal(20, t.A.Engine.View.MyFiles.Count(f => f.Note?.StartsWith("You added it while it was open", StringComparison.Ordinal) == true));
        var single = t.A.Disk.IsOpenCalls;
        var questions = t.A.Disk.OpenAmongSizes.Count;
        for (var i = 0; i < 10; i++) t.A.Engine.ApplySettings(new SettingsView(World.Root, i % 2 == 0, "system"), i % 2 == 0 ? "idea" : "dark");
        await t.A.SyncAsync();
        Assert.Equal(single, t.A.Disk.IsOpenCalls);
        Assert.InRange(t.A.Disk.OpenAmongSizes.Count - questions, 1, 3);
        Assert.Equal(20, t.A.Engine.View.MyFiles.Count(f => f.Note?.StartsWith("You added it while it was open", StringComparison.Ordinal) == true));
        foreach (var path in paths) t.A.Close(path);
        await t.A.SyncTimesAsync(2);
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.All(paths, path => Assert.True(t.A.Disk.IsReadOnly(path)));
    }

    // 0.3.3 (the crash reports of 0.2.1 to 0.3.1: Windows ended the session while a quit waited
    // behind the open-files question): stopping while the loop's question is out returns at once.
    [PostgresFact]
    public async Task Stopping_while_the_open_files_question_is_slow_returns_at_once()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        t.A.Open(Plate);
        t.A.Disk.OpenAmongCost = (_, _) => TimeSpan.FromSeconds(6);
        var asked = t.A.Disk.OpenAmongCalls;
        t.A.Engine.Start();
        await WaitUntil(() => t.A.Disk.OpenAmongCalls > asked, TimeSpan.FromSeconds(30), "the loop to ask whether the part is open");
        var stop = Stopwatch.StartNew();
        await t.A.Engine.StopAsync();
        stop.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"STOP DURING A SLOW OPEN-FILES QUESTION stop_ms={stop.Elapsed.TotalMilliseconds:F0}"));
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(1), $"stopping took {stop.Elapsed.TotalMilliseconds:F0} ms");
    }

    // 0.3.3 (IDEA-06: SolidWorks closed unexpectedly and left 224 ~$ markers, asked about every
    // pass): once a marker is stale it is not asked about again until it changes, and nothing
    // deletes it.
    [PostgresFact]
    public async Task Stale_markers_are_not_asked_about_again_until_they_change()
    {
        await using var t = await TeamAsync();
        var paths = Enumerable.Range(0, 200).Select(i => $"Robot 2027/Crashed/Part-{i:D3}.SLDPRT").ToArray();
        foreach (var path in paths) t.A.Write(path, "part " + path);
        await t.A.SyncAsync();
        foreach (var path in paths)
        {
            t.A.Open(path);
            t.A.Disk.CrashApp(path);
        }
        await t.A.SyncAsync();
        t.A.Clock.Advance(TimeSpan.FromMinutes(11));
        await t.A.SyncAsync();
        Assert.Equal(200, t.A.Card(NoticeKinds.CantRead)!.Count);
        var asked = t.A.Disk.OpenAmongSizes.Sum();
        await t.A.SyncTimesAsync(2);
        Assert.Equal(0, t.A.Disk.OpenAmongSizes.Sum() - asked);
        Assert.Equal(200, t.A.Card(NoticeKinds.CantRead)!.Count);
        Assert.All(paths, path => Assert.True(File.Exists(t.A.Disk.Full(Path.GetDirectoryName(path)!.Replace('\\', '/') + "/~$" + Path.GetFileName(path)))));
        // SolidWorks opens one again: its marker is written again, and it is asked about.
        t.A.Disk.Open(paths[7]);
        File.SetLastWriteTimeUtc(t.A.Disk.Full("Robot 2027/Crashed/~$Part-007.SLDPRT"), DateTime.UtcNow.AddSeconds(5));
        await t.A.SyncAsync();
        Assert.Equal(2, t.A.Disk.OpenAmongSizes.Sum() - asked);
        Assert.Equal(paths[7], t.A.Engine.View.Prompt?.Path);
        Assert.Equal(199, t.A.Card(NoticeKinds.CantRead)!.Count);
    }

    // 0.3.3 (0.3.1's folder check outs read and hashed all 1,424 files again and wrote the read-only
    // manifest once per file): a check out of 200 unchanged files reads none of them, and their bits
    // change in one batch per lock call; their check in sets them in one batch too, before the
    // locks go (it reads each one, as it must before letting its lock go).
    [PostgresFact]
    public async Task A_folder_check_out_reads_no_unchanged_file_and_sets_bits_in_batches()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        const string Folder = "Robot 2027/Many";
        var paths = Enumerable.Range(0, 200).Select(i => $"{Folder}/Part-{i:D3}.SLDPRT").ToArray();
        foreach (var path in paths)
        {
            t.A.Write(path, "part " + path);
            File.SetLastWriteTimeUtc(t.A.Disk.Full(path), DateTime.UtcNow.AddMinutes(-5));
        }
        await t.A.SyncTimesAsync(2);
        var (reads, single, batches) = (t.A.Disk.OpenReads, t.A.Disk.SingleAttributeCalls, t.A.Disk.AttributeBatches);
        Assert.Equal("Checked out 200 files.", (await t.A.CheckOutAsync(Folder)).Message);
        Assert.Equal(0, t.A.Disk.OpenReads - reads);
        Assert.Equal(0, t.A.Disk.SingleAttributeCalls - single);
        Assert.InRange(t.A.Disk.AttributeBatches - batches, 1, 3);
        Assert.All(paths, path => Assert.False(t.A.Disk.IsReadOnly(path)));
        (single, batches) = (t.A.Disk.SingleAttributeCalls, t.A.Disk.AttributeBatches);
        Assert.Equal("Checked in 200 files.", (await t.A.CheckInAsync(Folder)).Message);
        Assert.Equal(0, t.A.Disk.SingleAttributeCalls - single);
        Assert.InRange(t.A.Disk.AttributeBatches - batches, 1, 3);
        Assert.All(paths, path => Assert.True(t.A.Disk.IsReadOnly(path)));
    }

    // 0.3.3 (0.3.1: Open waited up to 31 seconds behind a plan; 0.3.2: File detail 10.6 seconds):
    // while the loop's open-files question is out (slow here, as on Windows), Open, File detail and
    // Pause answer at once, and a check out ends the question and answers quickly.
    [PostgresFact]
    public async Task Open_detail_pause_and_check_out_answer_while_the_open_files_question_is_slow()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        t.B.Open(Bracket);
        t.B.Disk.OpenAmongCost = (_, _) => TimeSpan.FromSeconds(6);
        var asked = t.B.Disk.OpenAmongCalls;
        t.B.Engine.Start();
        await WaitUntil(() => t.B.Disk.OpenAmongCalls > asked, TimeSpan.FromSeconds(30), "the loop to ask whether the bracket is open");
        var watch = Stopwatch.StartNew();
        Assert.Equal("Opening Plate.SLDPRT.", (await t.B.Engine.LaunchAsync(Plate)).Message);
        var open = watch.Elapsed;
        watch.Restart();
        Assert.Equal("Plate.SLDPRT", (await t.B.Engine.GetFileDetailAsync(file))!.Name);
        var detail = watch.Elapsed;
        watch.Restart();
        t.B.Engine.Pause();
        await WaitUntil(() => t.B.Engine.View.Sync.State == SyncStates.Paused, TimeSpan.FromSeconds(10), "the window to say paused");
        var pause = watch.Elapsed;
        t.B.Engine.Resume();
        await WaitUntil(() => t.B.Disk.OpenAmongCalls > asked + 1, TimeSpan.FromSeconds(30), "the loop to ask again");
        watch.Restart();
        var checkOut = await t.B.CheckOutAsync(Plate);
        var click = watch.Elapsed;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"WHILE A 6 S OPEN-FILES QUESTION IS OUT open_ms={open.TotalMilliseconds:F0} detail_ms={detail.TotalMilliseconds:F0} pause_ms={pause.TotalMilliseconds:F0} check_out_ms={click.TotalMilliseconds:F0}"));
        Assert.Equal("Checked out Plate.SLDPRT.", checkOut.Message);
        Assert.True(open < TimeSpan.FromMilliseconds(500), $"Open took {open.TotalMilliseconds:F0} ms");
        Assert.True(detail < TimeSpan.FromMilliseconds(500), $"File detail took {detail.TotalMilliseconds:F0} ms");
        Assert.True(pause < TimeSpan.FromMilliseconds(500), $"Pause took {pause.TotalMilliseconds:F0} ms");
        Assert.True(click < TimeSpan.FromSeconds(2), $"the check out took {click.TotalMilliseconds:F0} ms");
    }
}
