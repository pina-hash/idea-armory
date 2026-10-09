using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.3.3 (feedback N3 and N2, docs/agent/ENGINE.md "The transfer queue" and "Activity"). Mr.
// Pina's 1,429-file download stopped and started: every 8 second slice waited for its files in
// flight, then scanned, read the server and planned before the next file started (no download
// ran for 41% of it), one straggling file held five idle lanes for 77 seconds, the window said
// "Checking for changes." between slices, and the running lines said "Sync finished" after every
// slice. Now a loop pass carries its files in flight on to the passes after it, the window says
// what is downloading all along, and the running lines say it finished once, at the end. Each
// computer runs its loop the way the app does.
public sealed class ContinuousTransferTests(ITestOutputHelper output)
{
    private static int Here(Computer c, string folder)
        => Directory.Exists(c.Disk.Full(folder)) ? Directory.EnumerateFiles(c.Disk.Full(folder), "*", SearchOption.AllDirectories).Count() : 0;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, "Timed out waiting: " + what);
            await Task.Delay(25);
        }
    }

    private static bool Downloading(ActivityView? activity)
        => activity is not null && (activity.Download is not null || activity.Line?.StartsWith("Downloading", StringComparison.Ordinal) == true);

    // When each download from file storage started and ended on one computer.
    private sealed class Timeline
    {
        private readonly List<(long At, int Change)> events = [];
        public void Watch(HttpRequestMessage request, bool starting)
        {
            if (request.Method != HttpMethod.Get) return;
            lock (events) events.Add((Stopwatch.GetTimestamp(), starting ? 1 : -1));
        }

        // The first start, the last end, and the longest stretch between them with no download running.
        public (long First, long Last, TimeSpan LongestIdle, int Gaps) Idle()
        {
            List<(long At, int Change)> all;
            lock (events) all = [.. events.OrderBy(e => e.At).ThenByDescending(e => e.Change)];
            var running = 0;
            long idleSince = 0, longest = 0;
            var gaps = 0;
            foreach (var (at, change) in all)
            {
                if (running == 0 && idleSince != 0)
                {
                    longest = Math.Max(longest, at - idleSince);
                    if (at - idleSince > Stopwatch.Frequency / 10) gaps++;
                }
                running += change;
                if (running == 0) idleSince = at;
            }
            return (all[0].At, all[^1].At, TimeSpan.FromSeconds((double)longest / Stopwatch.Frequency), gaps);
        }
    }

    // The running lines as they arrive (each ActivityView carries the last 40): the lines new in
    // each one, in order, with how many files of a folder were on disk when each first showed.
    private sealed class RunningLines(Computer c, string folder)
    {
        private readonly List<(string Line, int Here)> lines = [];
        private IReadOnlyList<ActivityLineView> last = [];
        public void Watch(ActivityView activity)
        {
            lock (lines)
            {
                var now = activity.Log;
                // The newest lines of the last view that the start of this one repeats.
                var overlap = Math.Min(last.Count, now.Count);
                while (overlap > 0 && !last.Skip(last.Count - overlap).SequenceEqual(now.Take(overlap))) overlap--;
                foreach (var line in now.Skip(overlap)) lines.Add((line.Line, line.Line.StartsWith("Finished", StringComparison.Ordinal) ? Here(c, folder) : -1));
                last = now;
            }
        }
        public List<(string Line, int Here)> All { get { lock (lines) return [.. lines]; } }
    }

    // 0.3.3 (feedback N3): B downloads 1,500 files with one second slices, the open-files question
    // costing what it did on Windows (15 ms a file asked about) and each read of the server taking
    // a second (0.2 to 0.9 s on DESKTOP-QH30N35, plus the scan). Downloads never stop between
    // slices: after the first one starts, there is never a whole second with none running, and
    // every view and activity message until the last one ends says files are downloading (never
    // "Checking for changes." or "saved" in between).
    [PostgresFact]
    public async Task A_big_download_never_pauses_between_slices()
    {
        await using var t = await TeamAsync();
        const int Files = 1500;
        const string Folder = "Robot 2027/Big";
        for (var i = 0; i < Files; i++) t.A.Write($"{Folder}/Part-{i:D4}.SLDPRT", $"big part {i}");
        var upload = Stopwatch.StartNew();
        await t.A.SyncTimesAsync(2);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"UPLOAD of {Files} files took {upload.Elapsed.TotalSeconds:F1} s"));

        t.B.PassSlice = TimeSpan.FromSeconds(1);
        t.B.Restart();
        t.B.Disk.ReuseHashes = true;
        t.B.Disk.OpenAmongCost = (readOnly, writable) => TimeSpan.FromMilliseconds(15 * (readOnly + writable));
        t.B.Network.StorageDelay = request => request.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero;
        t.B.Network.RpcDelay = rpc => rpc.EndsWith("/armory_my_projects", StringComparison.Ordinal) ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        var timeline = new Timeline();
        t.B.Network.StorageWatch = timeline.Watch;
        var seen = new ConcurrentQueue<(long At, string What, bool Downloading)>();
        t.B.Views += view => seen.Enqueue((Stopwatch.GetTimestamp(), $"view {view.Sync.State}: {view.Sync.Line}", view.Sync.State == SyncStates.Syncing && Downloading(view.Activity)));
        t.B.Activities += activity => seen.Enqueue((Stopwatch.GetTimestamp(), $"activity: {activity.Line}", Downloading(activity)));
        var whole = Stopwatch.StartNew();
        t.B.Engine.Start();
        await WaitUntil(() => Here(t.B, Folder) == Files, TimeSpan.FromMinutes(3), "B's 1,500 downloads");
        whole.Stop();

        var (first, last, idle, gaps) = timeline.Idle();
        var passes = t.B.Flight.Snapshot().Count(e => e.Kind == Armory.Telemetry.FlightKind.PassStart);
        var carried = t.B.Flight.Snapshot().Where(e => e.Kind == Armory.Telemetry.FlightKind.PassYield).Sum(e => e.Count2);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BIG DOWNLOAD files={Files} whole_s={whole.Elapsed.TotalSeconds:F1} longest_idle_ms={idle.TotalMilliseconds:F0} gaps_over_100ms={gaps} passes={passes} carried={carried}"));
        Assert.True(idle < TimeSpan.FromSeconds(1), $"No download ran for {idle.TotalMilliseconds:F0} ms between the first and the last");
        var between = seen.Where(s => s.At > first && s.At < last).ToList();
        Assert.NotEmpty(between);
        var wrong = between.Where(s => !s.Downloading).Select(s => s.What).ToList();
        Assert.True(wrong.Count == 0, "Not saying it was downloading: " + string.Join(" | ", wrong.Distinct().Take(5)));
        Assert.Empty(t.B.Disk.OpenWriteViolations);
    }

    // 0.3.3 (incident 7a6c7d95: one 31.5 MB download took 83 seconds and held its pass, five
    // lanes idle): one file's download stalls. The other 199 all arrive long before the stall is
    // noticed, and the stalled one is asked for again and arrives too.
    [PostgresFact]
    public async Task A_stalled_download_does_not_hold_the_others()
    {
        await using var t = await TeamAsync();
        const int Files = 200;
        const string Folder = "Robot 2027/Bulk";
        for (var i = 0; i < Files; i++) t.A.Write($"{Folder}/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);

        t.B.PassSlice = TimeSpan.FromSeconds(1);
        t.B.StallAfter = TimeSpan.FromSeconds(10);
        t.B.Restart();
        var stalled = Hash("bulk part 5");
        var stalls = 1;
        t.B.Network.StorageDelay = request => request.Method != HttpMethod.Get ? TimeSpan.Zero
            : request.RequestUri!.AbsoluteUri.Contains(stalled, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref stalls, 0) == 1 ? TimeSpan.FromHours(1)
            : TimeSpan.FromMilliseconds(100);
        var watch = Stopwatch.StartNew();
        t.B.Engine.Start();
        const string Stalled = Folder + "/Part-005.SLDPRT";
        await WaitUntil(() => Here(t.B, Folder) >= Files - 1, TimeSpan.FromSeconds(60), "the other 199 downloads");
        var others = watch.Elapsed;
        var stalledHereThen = File.Exists(t.B.Disk.Full(Stalled));
        await WaitUntil(() => Here(t.B, Folder) == Files, TimeSpan.FromSeconds(60), "the stalled download, asked for again");
        var all = watch.Elapsed;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"STALLED DOWNLOAD others_s={others.TotalSeconds:F1} all_s={all.TotalSeconds:F1}"));
        Assert.False(stalledHereThen, "the stalled file arrived before the others");
        Assert.True(others < TimeSpan.FromSeconds(10), $"the other 199 took {others.TotalSeconds:F1} s: they waited for the stalled one");
        Assert.True(all >= TimeSpan.FromSeconds(10), "the stalled download was never stalled");
        Assert.Equal("bulk part 5", t.B.Text(Stalled));
        Assert.Contains(t.B.Flight.Snapshot(), e => e.Kind == Armory.Telemetry.FlightKind.Transfer && e.Detail == "stalled");
    }

    // 0.3.3 (feedback N2: "it says 'sync finished' in the action log even though there are still
    // hundreds of files to go"): a download of 200 files over many one second slices says how far
    // it got while it goes on, and that it finished exactly once, after the last file is here.
    // No running line says "sync".
    [PostgresFact]
    public async Task A_long_download_says_it_finished_once_at_the_end()
    {
        await using var t = await TeamAsync();
        const int Files = 200;
        const string Folder = "Robot 2027/Bulk";
        for (var i = 0; i < Files; i++) t.A.Write($"{Folder}/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);

        t.B.PassSlice = TimeSpan.FromSeconds(1);
        t.B.Restart();
        t.B.Network.StorageDelay = request => request.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        var lines = new RunningLines(t.B, Folder);
        t.B.Activities += lines.Watch;
        t.B.Engine.Start();
        await WaitUntil(() => Here(t.B, Folder) == Files, TimeSpan.FromSeconds(90), "B's downloads");
        await WaitUntil(() => lines.All.Any(l => l.Line.StartsWith("Finished", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "the line that says it finished");
        await Task.Delay(1000); // nothing more is said about it
        var all = lines.All;
        output.WriteLine("RUNNING LINES " + string.Join(" | ", all.Select(l => l.Line).Where(l => !l.StartsWith("Downloaded Part", StringComparison.Ordinal))));
        var finished = Assert.Single(all, l => l.Line.StartsWith("Finished", StringComparison.Ordinal));
        Assert.Equal("Finished: 200 files downloaded.", finished.Line);
        Assert.Equal(Files, finished.Here);
        Assert.DoesNotContain(all, l => Regex.IsMatch(l.Line, @"\bsync", RegexOptions.IgnoreCase));
        Assert.All(all.Where(l => l.Line.StartsWith("Downloaded ", StringComparison.Ordinal) && l.Line.Contains(" of ", StringComparison.Ordinal)),
            l => Assert.Matches(@"^Downloaded \d+ of 200 files$", l.Line));
        Assert.Equal(SyncStates.Synced, t.B.Engine.View.Sync.State);
    }

    // 0.3.3 (feedback N2): paused in the middle of a long download, the running lines say how far
    // it got once the files in flight have landed, and nothing more downloads; resumed, the rest
    // comes, and the line at the end counts the rest.
    [PostgresFact]
    public async Task Pausing_a_long_download_says_how_far_it_got()
    {
        await using var t = await TeamAsync();
        const int Files = 200;
        const string Folder = "Robot 2027/Bulk";
        for (var i = 0; i < Files; i++) t.A.Write($"{Folder}/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);
        t.B.PassSlice = TimeSpan.FromSeconds(1);
        t.B.Restart();
        t.B.Network.StorageDelay = request => request.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        var lines = new RunningLines(t.B, Folder);
        t.B.Activities += lines.Watch;
        t.B.Engine.Start();
        await WaitUntil(() => Here(t.B, Folder) >= 30, TimeSpan.FromSeconds(60), "B's first downloads");
        t.B.Engine.Pause();
        await WaitUntil(() => lines.All.Any(l => l.Line.StartsWith("Paused", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "the line that says how far it got");
        var paused = Assert.Single(lines.All, l => l.Line.StartsWith("Paused", StringComparison.Ordinal)).Line;
        var match = Regex.Match(paused, @"^Paused: (?<n>\d+) files downloaded so far\.$");
        Assert.True(match.Success, paused);
        var sofar = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        await Task.Delay(1500);
        Assert.Equal(sofar, Here(t.B, Folder));
        Assert.Equal(SyncStates.Paused, t.B.Engine.View.Sync.State);
        Assert.DoesNotContain(lines.All, l => l.Line.StartsWith("Finished", StringComparison.Ordinal));

        t.B.Engine.Resume();
        await WaitUntil(() => Here(t.B, Folder) == Files, TimeSpan.FromSeconds(90), "the rest of B's downloads");
        await WaitUntil(() => lines.All.Any(l => l.Line.StartsWith("Finished", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "the line that says it finished");
        Assert.Equal($"Finished: {Files - sofar} files downloaded.", Assert.Single(lines.All, l => l.Line.StartsWith("Finished", StringComparison.Ordinal)).Line);
    }

    // 0.3.3 (feedback N3 f): a file Armory downloaded is known by its hash, so the next scan does
    // not read it again.
    [PostgresFact]
    public async Task A_downloaded_file_is_not_read_again_by_the_next_scan()
    {
        await using var t = await TeamAsync();
        for (var i = 0; i < 50; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D3}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);
        t.B.Disk.ReuseHashes = true;
        await t.B.SyncAsync();
        Assert.Equal(50, Here(t.B, "Robot 2027/Bulk"));
        var hashes = t.B.Disk.ScanHashes;
        await t.B.SyncAsync();
        Assert.Equal(0, t.B.Disk.ScanHashes - hashes);
        // A file changed since is read again.
        t.B.Disk.ClearReadOnly("Robot 2027/Bulk/Part-007.SLDPRT");
        t.B.Write("Robot 2027/Bulk/Part-007.SLDPRT", "changed without a check out");
        File.SetLastWriteTimeUtc(t.B.Disk.Full("Robot 2027/Bulk/Part-007.SLDPRT"), DateTime.UtcNow.AddMinutes(1));
        hashes = t.B.Disk.ScanHashes;
        await t.B.SyncAsync();
        Assert.True(t.B.Disk.ScanHashes - hashes >= 1);
    }

    // 0.3.3 (X-kept-save-every-pass, feedback N1's snapshot "Uploading 0 of 1 file" with nothing
    // moving): a checked-out file whose save is already kept moves nothing on the passes after.
    [PostgresFact]
    public async Task A_save_already_kept_is_never_moving_again()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "v2, kept while checked out");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        Assert.Equal(1, await t.Sides(file));
        var uploads = new ConcurrentQueue<ActivityView>();
        t.A.Activities += activity => { if (activity.Upload is not null) uploads.Enqueue(activity); };
        int logged;
        lock (t.A.Logged) logged = t.A.Logged.Count;
        await t.A.SyncTimesAsync(3);
        List<string> since;
        lock (t.A.Logged) since = t.A.Logged.Skip(logged).ToList();
        Assert.DoesNotContain(since, l => l.StartsWith("pass: moving", StringComparison.Ordinal));
        Assert.Empty(uploads);
        Assert.Null(t.A.Engine.View.Activity.Upload);
        Assert.Equal(1, await t.Sides(file));
    }
}
