using System.Diagnostics;
using System.Globalization;
using System.Text;
using Armory.TestSupport;
using Xunit.Abstractions;

namespace Armory.EndToEnd.Tests;

// Transfer throughput through the school network profile (LatencyHandler): Alex adds a batch of
// parts, then Maria's computer receives them. The default run is a small smoke run of the
// harness; ARMORY_THROUGHPUT=1 runs the full measurement reported in docs/agent/PROOF.md, and
// ARMORY_THROUGHPUT_OUT=<file> appends each result line to that file.
public sealed class ThroughputTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Transfers_through_a_school_network_profile()
    {
        var full = Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT") == "1";
        var small = full ? 120 : 12;
        var large = full ? 2 : 0;
        const int SmallBytes = 256 * 1024, LargeBytes = 32 * 1024 * 1024;
        await using var t = await ScenarioTests.TeamAsync(latency: LatencyProfile.School);
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

        var line = string.Create(CultureInfo.InvariantCulture,
            $"THROUGHPUT label={Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_LABEL") ?? "current"} files={paths.Count} bytes={total} " +
            $"upload_s={up.Elapsed.TotalSeconds:F1} upload_passes={upPasses} upload_files_per_s={paths.Count / up.Elapsed.TotalSeconds:F2} upload_MB_per_s={total / 1e6 / up.Elapsed.TotalSeconds:F2} " +
            $"download_s={down.Elapsed.TotalSeconds:F1} download_passes={downPasses} download_files_per_s={paths.Count / down.Elapsed.TotalSeconds:F2} download_MB_per_s={total / 1e6 / down.Elapsed.TotalSeconds:F2} " +
            $"a_rpc={t.A.Network.RpcRequests} a_site={t.A.Network.SiteRequests} a_storage={t.A.Network.StorageRequests} " +
            $"b_rpc={t.B.Network.RpcRequests} b_site={t.B.Network.SiteRequests} b_storage={t.B.Network.StorageRequests}");
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ARMORY_THROUGHPUT_OUT") is { Length: > 0 } file) File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
        Assert.All(paths, p => Assert.NotNull(t.B.Read(p)));
    }
}
