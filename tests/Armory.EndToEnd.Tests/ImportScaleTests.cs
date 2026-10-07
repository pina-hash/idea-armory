using System.Diagnostics;
using System.Globalization;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// The brief's 5,000-file import (section 4). Alex unzips one folder of 5,000 small files in
// nested folders: mostly SolidWorks parts (no saved-release reader, so v1 showed a "SolidWorks
// year not checked" notice for every one) and some text files, about 2% of them with the names of
// parts Maria already added to the project. Synced until a pass sends nothing, Alex sees at most
// 3 notice cards (exactly one import summary and one card for the shared names), every other
// file is in Armory with exactly one version, no check out is left, and every file in Armory is
// read-only while the 100 that share a name stay writable. The SCAN line reports the scan times
// of the portable test file system, which reads and hashes every file on every scan (the Windows
// adapter hashes again only what changed), and the pass times.
public sealed class ImportScaleTests(ITestOutputHelper output)
{
    private const int Imported = 5_000, Existing = 100;

    [PostgresFact]
    public async Task A_5000_file_import_is_quiet_and_complete()
    {
        await using var t = await TeamAsync();
        // Maria added the project's parts earlier (her own import summary, which she closed).
        for (var i = 0; i < Existing; i++) t.B.Write($"Robot 2027/Parts/Part-{i:D4}.SLDPRT", $"the project's own part {i}");
        await t.B.SyncAsync();
        await t.B.Engine.DismissNoticeAsync(t.B.Card(NoticeKinds.Import)!.Key);
        await t.A.SyncAsync();
        Assert.Empty(t.A.Engine.View.Notices);

        // The unzipped folder: 10 assemblies of 25 subfolders of 20 files; every 50th file has the
        // name of one of Maria's parts, every 10th is a text file.
        List<string> unzipped = [], shared = [];
        for (var i = 0; i < Imported; i++)
        {
            var folder = $"Robot 2027/Unzipped/Assembly {i / 500:D2}/Sub {i / 20 % 25:D2}";
            var name = i % 50 == 0 ? $"Part-{i / 50:D4}.SLDPRT" : i % 10 == 5 ? $"Notes-{i:D4}.txt" : $"Import-{i:D4}.SLDPRT";
            var path = $"{folder}/{name}";
            t.A.Write(path, $"unzipped file {i}: {new string('x', i % 200)}");
            (i % 50 == 0 ? shared : unzipped).Add(path);
        }
        Assert.Equal(Imported / 50, shared.Count);

        var scansBefore = t.A.Disk.ScanTimes.Count;
        var watch = Stopwatch.StartNew();
        var passes = 0;
        TimeSpan firstPass = default;
        while (true)
        {
            Assert.True(++passes <= 10, "Alex's computer was still sending after 10 passes.");
            var (rpc, storage) = (t.A.Network.RpcRequests, t.A.Network.StorageRequests);
            var reads = Reads(t);
            await t.A.SyncAsync();
            if (passes == 1) firstPass = watch.Elapsed;
            var writes = t.A.Network.RpcRequests - rpc - (Reads(t) - reads) + t.A.Network.StorageRequests - storage;
            if (writes == 0) break;
        }
        var untilIdle = watch.Elapsed;
        var firstScan = t.A.Disk.ScanTimes[scansBefore];
        // One more pass with nothing new: the scan of an unchanged folder.
        await t.A.SyncAsync();
        var rescan = t.A.Disk.ScanTimes[^1];

        var cards = t.A.Engine.View.Notices;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"SCAN files={Imported} scan_ms={firstScan.TotalMilliseconds:F0} rescan_ms={rescan.TotalMilliseconds:F0} first_pass_s={firstPass.TotalSeconds:F1} until_idle_s={untilIdle.TotalSeconds:F1} cards={cards.Count} passes={passes} state_saves={t.A.State.Saves}"));
        Assert.InRange(cards.Count, 1, 3);
        var import = Assert.Single(cards, c => c.Kind == NoticeKinds.Import);
        Assert.Equal($"Added {Imported - shared.Count:N0} of {Imported:N0} files to Robot 2027 › Unzipped", import.Title);
        var names = Assert.Single(cards, c => c.Kind == NoticeKinds.NameShared);
        Assert.Equal(shared.Count, names.Count);
        Assert.Equal($"{shared.Count} files share a name with other files in this project", names.Title);

        // Every other file is in Armory, with exactly one version, and nothing is checked out.
        var versions = await t.World.QueryAsync("select f.folder || '/' || f.name, (select count(*) from armory_versions v where v.file_id=f.id) from armory_files f where f.project_id=@p and f.folder like 'Unzipped/%' and f.deleted_at is null",
            r => (Path: "Robot 2027/" + r.GetString(0), Count: r.GetInt64(1)), ("p", t.Project));
        Assert.Equal(unzipped.Count, versions.Count);
        Assert.All(versions, v => Assert.Equal(1, v.Count));
        Assert.Equal(unzipped.Order(StringComparer.Ordinal), versions.Select(v => v.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Existing, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and folder='Parts'", ("p", t.Project)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null", ("p", t.Project)));
        // The read-only rule: every file in Armory is read-only, the 100 that share a name stay
        // writable (the server does not have them).
        Assert.All(unzipped, p => Assert.True(t.A.Disk.IsReadOnly(p), p));
        Assert.All(shared, p => Assert.False(t.A.Disk.IsReadOnly(p), p));
        Assert.Empty(t.A.Disk.OpenWriteViolations);
        Assert.Empty(t.A.Disk.UnpreservedOverwrites);
        // Saves before server writes are shared: far fewer saves of the state document than writes.
        var written = await t.World.CountAsync("select count(*) from armory_change_feed where project_id=@p", ("p", t.Project));
        Assert.True(t.A.State.Saves < written, $"{t.A.State.Saves} saves of the state document for {written} server writes");
    }

    // Calls that only read the server.
    private static long Reads(Team t)
        => t.World.Supabase.RpcCount("armory_my_projects") + t.World.Supabase.RpcCount("armory_list_changes") + t.World.Supabase.RpcCount("armory_project_files");
}
