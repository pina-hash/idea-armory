using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using Xunit.Abstractions;

namespace Armory.EndToEnd.Tests;

// Transfer throughput through the school network profile (LatencyHandler): Alex adds a batch of
// parts, then Maria's computer receives them, both computers moving EngineOptions.TransferConcurrency
// files at once. Each run is its own world (its own storage, so no run finds its bytes already
// stored). The default run is short: a small batch at concurrency 1 and at the default, and the
// default must be at least twice as fast both ways; it also holds the activity panel to its
// words and to at most four messages a second. ARMORY_THROUGHPUT=1 runs the measurement in
// docs/agent/PROOF.md, ARMORY_THROUGHPUT_RUNS times (default 3) with the sweeps interleaved:
// "mixed" (120 parts of 256 KiB and two of 32 MiB, one computer each way) at concurrency 1, 2,
// 4, 6, 8 and 12; "small" (the 120 parts alone) at 1, 2, 4, 6, 8, 12 and 24; and "class" (six
// computers behind ONE school link: each adds 20 parts at the same moment, then each receives
// the other 100) at 1, 2, 4, 6, 8 and 12. ARMORY_THROUGHPUT_ONLY=mixed|small|class runs one
// sweep; ARMORY_THROUGHPUT_OUT=<file> appends each result line to that file.
public sealed class ThroughputTests(ITestOutputHelper output)
{
    private const int SmallBytes = 256 * 1024, LargeBytes = 32 * 1024 * 1024;
    private static readonly Regex TransferLine = new(@"^((Uploading|Downloading) [\d,]+ of [\d,]+ files?, [\d.]+ (bytes?|KB|MB|GB|TB) left|Checking in [\d,]+ of [\d,]+ files?)(, (about \d+ (min|sec)|less than a minute))?$", RegexOptions.CultureInvariant);

    [PostgresFact]
    public async Task Transfers_through_a_school_network_profile()
    {
        if (Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT") == "1")
        {
            var runs = int.TryParse(Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_RUNS"), out var n) && n > 0 ? n : 3;
            var only = Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_ONLY");
            for (var run = 1; run <= runs; run++)
            {
                if (only is null or "mixed")
                    foreach (var concurrency in new[] { 1, 2, 4, 6, 8, 12 }) await MeasureAsync(concurrency, small: 120, large: 2, $"mixed-c{concurrency}-r{run}");
                if (only is null or "small")
                    foreach (var concurrency in new[] { 1, 2, 4, 6, 8, 12, 24 }) await MeasureAsync(concurrency, small: 120, large: 0, $"small-c{concurrency}-r{run}");
                if (only is null or "class")
                    foreach (var concurrency in new[] { 1, 2, 4, 6, 8, 12 }) await MeasureClassAsync(concurrency, computers: 6, parts: 20, $"class6-c{concurrency}-r{run}");
            }
            return;
        }
        var one = await MeasureAsync(1, small: 16, large: 0, "smoke-c1");
        var many = await MeasureAsync(EngineOptions.DefaultTransferConcurrency, small: 16, large: 0, $"smoke-c{EngineOptions.DefaultTransferConcurrency}", watch: true);
        Assert.True(one.Upload >= 2 * many.Upload, $"upload at concurrency {EngineOptions.DefaultTransferConcurrency} took {many.Upload:F1} s, concurrency 1 {one.Upload:F1} s");
        Assert.True(one.Download >= 2 * many.Download, $"download at concurrency {EngineOptions.DefaultTransferConcurrency} took {many.Download:F1} s, concurrency 1 {one.Download:F1} s");
    }

    private async Task<(double Upload, double Download)> MeasureAsync(int concurrency, int small, int large, string label, bool watch = false)
    {
        await using var t = await ScenarioTests.TeamAsync(latency: LatencyProfile.School);
        foreach (var computer in new[] { t.A, t.B })
        {
            computer.TransferConcurrency = concurrency;
            computer.Restart();
        }
        var random = new Random(5669);
        var paths = new List<string>();
        for (var i = 0; i < small + large; i++)
        {
            var bytes = new byte[i < small ? SmallBytes : LargeBytes];
            random.NextBytes(bytes);
            var path = $"Robot 2027/Throughput/{(i < small ? "Part" : "Large")}-{i:D4}.SLDPRT";
            t.A.Write(path, bytes);
            paths.Add(path);
        }
        long total = (long)small * SmallBytes + (long)large * LargeBytes;

        var watched = watch ? new Watched(t.A.Engine, t.B.Engine) : null;
        var up = Stopwatch.StartNew();
        var upPasses = 0;
        while (await t.World.CountAsync("select count(*) from armory_versions v join armory_files f on f.id=v.file_id where f.project_id=@p", ("p", t.Project)) < paths.Count)
        {
            Assert.True(++upPasses <= 30, "Alex's computer did not finish sending in 30 passes.");
            await t.A.SyncAsync();
        }
        up.Stop();

        var down = Stopwatch.StartNew();
        var downPasses = 0;
        while (paths.Any(p => t.B.Read(p) is null))
        {
            Assert.True(++downPasses <= 30, "Maria's computer did not finish receiving in 30 passes.");
            await t.B.SyncAsync();
        }
        down.Stop();
        watched?.Stop();

        var line = string.Create(CultureInfo.InvariantCulture,
            $"THROUGHPUT label={label} files={paths.Count} bytes={total} " +
            $"upload_s={up.Elapsed.TotalSeconds:F1} upload_passes={upPasses} upload_files_per_s={paths.Count / up.Elapsed.TotalSeconds:F2} upload_MB_per_s={total / 1e6 / up.Elapsed.TotalSeconds:F2} " +
            $"download_s={down.Elapsed.TotalSeconds:F1} download_passes={downPasses} download_files_per_s={paths.Count / down.Elapsed.TotalSeconds:F2} download_MB_per_s={total / 1e6 / down.Elapsed.TotalSeconds:F2} " +
            $"a_rpc={t.A.Network.RpcRequests} a_site={t.A.Network.SiteRequests} a_storage={t.A.Network.StorageRequests} " +
            $"b_rpc={t.B.Network.RpcRequests} b_site={t.B.Network.SiteRequests} b_storage={t.B.Network.StorageRequests}");
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_OUT") is { Length: > 0 } file) File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
        Assert.All(paths, p => Assert.NotNull(t.B.Read(p)));
        watched?.Check(output, concurrency);
        return (up.Elapsed.TotalSeconds, down.Elapsed.TotalSeconds);
    }

    // A classroom behind one school connection: six computers share ONE link (25 MB/s for every
    // body, LatencyProfile.School otherwise), each moving `concurrency` files at once. Each adds
    // `parts` parts of 256 KiB at the same moment (a class-wide Pack and Go), then each receives
    // the parts of the other five. The class's time is until the last computer is done.
    private async Task MeasureClassAsync(int concurrency, int computers, int parts, string label)
    {
        await using var world = await World.StartAsync();
        world.Latency = LatencyProfile.School;
        world.SharedLink = new NetworkLink();
        var mentor = await world.PersonAsync(ScenarioTests.Mentor, admin: true);
        var project = await mentor.Api.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        var lab = new List<Computer>();
        for (var i = 0; i < computers; i++)
        {
            var email = $"student{i:D2}@students.test";
            await mentor.Api.AddMemberAsync(project, email, MemberRole.Student, Guid.NewGuid());
            var computer = await world.ComputerAsync($"lab PC {i:D2}", email);
            computer.TransferConcurrency = concurrency;
            computer.Restart();
            lab.Add(computer);
        }
        await Task.WhenAll(lab.Select(c => c.SyncAsync()));
        var random = new Random(5669);
        var paths = new List<string>[computers];
        for (var i = 0; i < computers; i++)
        {
            paths[i] = [];
            for (var j = 0; j < parts; j++)
            {
                var bytes = new byte[SmallBytes];
                random.NextBytes(bytes);
                var path = $"Robot 2027/Station {i:D2}/Part-{i:D2}-{j:D3}.SLDPRT";
                lab[i].Write(path, bytes);
                paths[i].Add(path);
            }
        }
        var all = paths.SelectMany(p => p).ToArray();

        var up = Stopwatch.StartNew();
        var upEach = await Task.WhenAll(lab.Select(async (c, i) =>
        {
            var watch = Stopwatch.StartNew();
            for (var pass = 1; await UnsentAsync(world, paths[i]) > 0; pass++)
            {
                Assert.True(pass <= 30, $"{c.Name} did not finish sending in 30 passes.");
                await c.SyncAsync();
            }
            return watch.Elapsed.TotalSeconds;
        }));
        up.Stop();
        var down = Stopwatch.StartNew();
        var downEach = await Task.WhenAll(lab.Select(async (c, i) =>
        {
            var watch = Stopwatch.StartNew();
            for (var pass = 1; all.Any(p => c.Read(p) is null); pass++)
            {
                Assert.True(pass <= 30, $"{c.Name} did not finish receiving in 30 passes.");
                await c.SyncAsync();
            }
            return watch.Elapsed.TotalSeconds;
        }));
        down.Stop();
        long upBytes = (long)all.Length * SmallBytes, downBytes = (long)all.Length * (computers - 1) * SmallBytes;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"CLASS label={label} computers={computers} concurrency={concurrency} parts_each={parts} " +
            $"upload_s={up.Elapsed.TotalSeconds:F1} upload_mean_s={upEach.Average():F1} upload_link_MB_per_s={upBytes / 1e6 / up.Elapsed.TotalSeconds:F2} " +
            $"download_s={down.Elapsed.TotalSeconds:F1} download_mean_s={downEach.Average():F1} download_link_MB_per_s={downBytes / 1e6 / down.Elapsed.TotalSeconds:F2}");
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_OUT") is { Length: > 0 } file) File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
        foreach (var c in lab) Assert.All(all, p => Assert.NotNull(c.Read(p)));
    }

    // Parts of these paths that have no version on the server yet.
    private static async Task<long> UnsentAsync(World world, List<string> paths)
    {
        var folder = paths[0].Split('/')[1];
        var sent = await world.CountAsync("select count(*) from armory_versions v join armory_files f on f.id=v.file_id where f.folder=@f", ("f", folder));
        return paths.Count - sent;
    }

    // What the window was told while the files moved: the activity messages (each with the time
    // it arrived) and the status lines of the views.
    private sealed class Watched
    {
        private const int ActiveShown = 8;
        private readonly List<(SyncEngine From, long At, ActivityView Activity)> activity = [];
        private readonly List<string> lines = [];
        private readonly SyncEngine[] engines;

        public Watched(params SyncEngine[] engines)
        {
            this.engines = engines;
            foreach (var engine in engines)
            {
                engine.ActivityChanged += view => { lock (activity) activity.Add((engine, Stopwatch.GetTimestamp(), view)); };
                engine.ViewChanged += OnView;
            }
        }

        private bool stopped;
        private void OnView(AgentView view) { lock (lines) if (!stopped) lines.Add(view.Sync.Line); }

        public void Stop()
        {
            lock (activity) lock (lines) stopped = true;
            foreach (var engine in engines) engine.ViewChanged -= OnView;
        }

        public void Check(ITestOutputHelper output, int concurrency)
        {
            (SyncEngine From, long At, ActivityView Activity)[] seen;
            lock (activity) seen = [.. activity];
            output.WriteLine($"ACTIVITY messages={seen.Length} lines: " + string.Join(" | ", seen.Select(s => s.Activity.Line).OfType<string>().Distinct().Take(8)));
            Assert.Contains(seen, s => s.Activity.Upload is not null);
            Assert.Contains(seen, s => s.Activity.Download is not null);
            foreach (var (_, _, view) in seen)
            {
                Assert.True(view.Active.Count <= ActiveShown, "more than 8 files listed as moving");
                foreach (var direction in new[] { view.Upload, view.Download }.OfType<DirectionView>())
                {
                    Assert.Matches(TransferLine, direction.Line);
                    Assert.InRange(direction.FilesDone, 0, direction.FilesTotal);
                    Assert.InRange(direction.BytesDone, 0, direction.BytesTotal);
                }
                if (view.Line is { } line) Assert.Matches(TransferLine, line);
            }
            // Several files at once, each with its own bar.
            if (concurrency > 1) Assert.Contains(seen, s => s.Activity.Active.Count > 1);
            // At most four a second from each computer.
            foreach (var from in seen.GroupBy(s => s.From))
            {
                var times = from.Select(s => s.At).ToArray();
                for (var i = 1; i < times.Length; i++)
                    Assert.True(Stopwatch.GetElapsedTime(times[i - 1], times[i]) >= TimeSpan.FromMilliseconds(240), "two activity messages less than 250 ms apart");
            }
            // The status line follows what is moving.
            string[] views;
            lock (lines) views = [.. lines];
            Assert.Contains(views, v => v.StartsWith("Uploading ", StringComparison.Ordinal));
            Assert.Contains(views, v => v.StartsWith("Downloading ", StringComparison.Ordinal));
        }
    }
}
