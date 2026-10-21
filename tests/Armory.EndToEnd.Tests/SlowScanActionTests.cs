using System.Diagnostics;
using Armory.Telemetry;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.4.1 (check ins of 10.8 to 17.8 s on IDEA-06, note 47e9bf5e): a click made while the loop is
// reading the disk waited for that scan to end, and then its own pass read the whole disk again.
// On a lab computer whose scan takes seconds (ScanDelay stands for it), that was two scans per
// click. The loop now gives way the moment its scan is taken in and the action goes on from that
// scan: one scan per click, and the check in still reads the file itself before it lets go.
public sealed class SlowScanActionTests
{
    private static readonly TimeSpan Scan = TimeSpan.FromSeconds(3);

    [PostgresFact]
    public async Task A_check_in_clicked_during_the_loops_scan_waits_for_that_scan_and_reads_the_disk_no_second_time()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        for (var i = 0; i < 20; i++) t.A.Write($"Robot 2027/Drivetrain/Part {i}.SLDPRT", "part " + i);
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        t.B.Save(Plate, "Maria's plate");
        var file = await t.FileId("Plate.SLDPRT");

        // The loop runs as in the app, on a computer whose every scan takes 3 seconds.
        t.B.Poll = TimeSpan.FromMilliseconds(100);
        t.B.Restart();
        await t.B.SyncAsync();
        t.B.Disk.ScanDelay = Scan;
        var scans = t.B.Disk.ScansStarted;
        t.B.Engine.Start();
        var wait = Stopwatch.StartNew();
        while (t.B.Disk.ScansStarted == scans && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(t.B.Disk.ScansStarted > scans, "the loop never started a scan");

        // Clicked just after the loop's scan began.
        var flightBefore = t.B.Flight.Snapshot().Length;
        var clock = Stopwatch.StartNew();
        var answer = await t.B.CheckInAsync(Plate);
        var answered = clock.Elapsed;
        Assert.True(answer.Ok, answer.Message);
        var during = t.B.Flight.Snapshot().Skip(flightBefore).ToList();
        Assert.Contains(during, e => e.Kind == FlightKind.Note && e.Name == "scanReused");
        // At most what was left of the loop's scan, plus the check in itself: never a second scan.
        Assert.True(answered < Scan + TimeSpan.FromSeconds(1.8), $"answered after {answered.TotalSeconds:F1} s");
        Assert.Equal(Hash("Maria's plate"), await t.CurrentHash(file));
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.True(t.B.Disk.IsReadOnly(Plate));
        Assert.Empty(t.B.Disk.OpenWriteViolations);
        Assert.Empty(t.B.Disk.UnpreservedOverwrites);
    }

    // The scan the action goes on from must be the moment's: a save made after it is never let go
    // of unread. The check in reads the file before it lets go, finds bytes the scan never saw, and
    // keeps the check out until a pass has shared them.
    [PostgresFact]
    public async Task A_save_made_after_the_reused_scan_is_shared_before_the_check_in_lets_go()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        Assert.True((await t.B.CheckOutAsync(Plate)).Ok);
        t.B.Save(Plate, "Maria's plate 1");
        var file = await t.FileId("Plate.SLDPRT");

        t.B.Poll = TimeSpan.FromMilliseconds(100);
        t.B.Restart();
        await t.B.SyncAsync();
        t.B.Disk.ScanDelay = Scan;
        // SolidWorks saves again right after the loop's scan, before the check in reads the file.
        t.B.Disk.AfterNextScan = () => t.B.Save(Plate, "Maria's plate 2");
        var scans = t.B.Disk.ScansStarted;
        t.B.Engine.Start();
        var wait = Stopwatch.StartNew();
        while (t.B.Disk.ScansStarted == scans && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        await t.B.CheckInAsync(Plate);
        // Whatever the click answered, the check in ends with the last save shared and nothing lost.
        wait.Restart();
        while (await t.LiveLocks(file) > 0 && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal(Hash("Maria's plate 2"), await t.CurrentHash(file));
        Assert.Empty(t.B.Disk.OpenWriteViolations);
        Assert.Empty(t.B.Disk.UnpreservedOverwrites);
    }
}
