using Armory.Agent.Engine.View;
using Armory.Telemetry;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// What the engine tells the flight recorder (docs/agent/TELEMETRY.md), end to end: a pass with
// its phases, its server calls and transfers; a read-only bit someone cleared; a check out
// repaired; and the compact snapshot an incident carries.
public sealed class TelemetryTests
{
    private static List<FlightEvent> Since(FlightRecorder flight, long sequence) => flight.Snapshot().Where(e => e.Sequence > sequence).ToList();

    [PostgresFact]
    public async Task A_pass_is_recorded_with_its_phases_its_server_calls_and_its_transfers()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        await t.A.SyncAsync();
        var events = t.A.Flight.Snapshot();
        var passStart = events.First(e => e.Kind == FlightKind.PassStart);
        var passEnd = events.Last(e => e.Kind == FlightKind.PassEnd);
        Assert.Equal("whole", passStart.Name);
        Assert.True(passEnd.Ok);
        Assert.Equal(1, passEnd.Count2); // uploaded
        Assert.Equal(["scan", "server", "plan", "move", "finish"],
            events.Where(e => e.Kind == FlightKind.PassPhase && e.Sequence > passStart.Sequence && e.Sequence < passEnd.Sequence).Select(e => e.Name!));
        Assert.Contains(events, e => e.Kind == FlightKind.Rpc && e.Name == "armory_project_files" && e.Status == 200 && e.Ok);
        Assert.Contains(events, e => e.Kind == FlightKind.Rpc && e.Name!.StartsWith("armory_commit_version", StringComparison.Ordinal) && e.Ok);
        Assert.Contains(events, e => e.Kind == FlightKind.Rpc && e.Name == Armory.Client.BlobClient.UrlCall);
        var upload = Assert.Single(events, e => e.Kind == FlightKind.Transfer);
        Assert.Equal(("upload", (long)"plate v1 by Alex".Length, true), (upload.Name!, upload.Bytes, upload.Ok));
        // Nothing went wrong: none of it is a glitch.
        var detector = new GlitchDetector();
        Assert.All(events, e => Assert.Null(detector.Inspect(e)));
    }

    [PostgresFact]
    public async Task A_read_only_bit_someone_cleared_is_recorded_and_put_back()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        var before = t.A.Flight.Recorded;
        t.A.Disk.ClearReadOnly(Plate);
        await t.A.SyncAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        var broken = Assert.Single(Since(t.A.Flight, before), e => e.Kind == FlightKind.ReadOnlyBroken);
        Assert.Equal(Plate, broken.Target);
        Assert.Equal(GlitchKinds.ReadOnlyBroken, GlitchRules.ReadOnlyBroken(broken)?.Kind);
        // Put back, it stays put: the next pass records nothing more.
        before = t.A.Flight.Recorded;
        await t.A.SyncAsync();
        Assert.DoesNotContain(Since(t.A.Flight, before), e => e.Kind == FlightKind.ReadOnlyBroken);
    }

    // The readOnlyBroken incidents of IDEA-06 (0.3.0, 0.3.1): a file the scan can't read keeps
    // the read-only bit an earlier scan read (cleared, from when it was checked out), so every
    // pass took it for a bit someone cleared, though the bit was set. A file the scan could not
    // read is never judged by its old entry: no event, and nothing is applied to it, until a
    // pass can read it. (A bit someone really cleared is still recorded: the test above.)
    [PostgresFact]
    public async Task An_unreadable_file_never_reports_readOnlyBroken()
    {
        const string Bracket = "Robot 2027/Drivetrain/Bracket.SLDPRT";
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write(Bracket, "bracket");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate, Bracket)).Ok);
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        var before = t.A.Flight.Recorded;
        var batches = t.A.Disk.AttributeBatches;
        // Plate: open in SolidWorks; Bracket: held by another program the open-file check doesn't see.
        t.A.Disk.Hold(Plate);
        t.A.Disk.HoldUnreadable(Bracket);
        Assert.True((await t.A.CheckInAsync(Plate, Bracket)).Ok);
        await t.A.SyncTimesAsync(3);
        Assert.DoesNotContain(Since(t.A.Flight, before), e => e.Kind == FlightKind.ReadOnlyBroken);
        Assert.Equal(batches, t.A.Disk.AttributeBatches); // nothing applied over what wasn't read
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        // Readable again: checked in, read-only, and still no event.
        t.A.Disk.Unhold(Plate);
        t.A.Disk.Unhold(Bracket);
        await t.A.SyncTimesAsync(2);
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly(Bracket));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.DoesNotContain(Since(t.A.Flight, before), e => e.Kind == FlightKind.ReadOnlyBroken);
        // Each wait is noted once, when it starts.
        Assert.Equal([Bracket + ": unreadable", Plate + ": open"],
            Since(t.A.Flight, before).Where(e => e.Kind == FlightKind.Note && e.Name == "checkInWaits").Select(e => e.Detail!).Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task A_check_out_with_no_record_here_is_recorded_as_repaired()
    {
        await using var t = await TeamAsync();
        await t.B.SyncAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        await t.A.SyncAsync();
        var file = await t.FileId("Plate.SLDPRT");
        // The server holds the lock for Maria's computer, which has never heard of the file.
        Assert.True(await t.B.Api.AcquireLockAsync(file, t.B.DeviceId, Guid.NewGuid()));
        var before = t.B.Flight.Recorded;
        await t.B.SyncAsync();
        var repaired = Assert.Single(Since(t.B.Flight, before), e => e.Kind == FlightKind.RepairedCheckout);
        Assert.Equal(Plate, repaired.Target);
        Assert.Equal(CheckoutStates.Mine, t.B.Row(Plate).Checkout.State);
        Assert.Equal(GlitchKinds.RepairedCheckout, new GlitchDetector().Inspect(repaired)?.Kind);
    }

    [PostgresFact]
    public async Task A_file_that_fails_three_passes_in_a_row_is_a_repeated_failure_and_its_recovery_is_recorded()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        await t.A.SyncAsync();
        t.B.Network.StorageFault = r => r.Method == HttpMethod.Get ? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) : null;
        var detector = new GlitchDetector();
        var glitches = new List<Glitch>();
        for (var pass = 0; pass < 3; pass++)
        {
            var before = t.B.Flight.Recorded;
            await t.B.SyncAsync();
            foreach (var e in Since(t.B.Flight, before)) if (detector.Inspect(e) is { } glitch) glitches.Add(glitch);
        }
        var failures = t.B.Flight.Snapshot().Where(e => e.Kind == FlightKind.FileFailed).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, f => Assert.Equal(Plate, f.Target));
        Assert.Contains(t.B.Flight.Snapshot(), e => e.Kind == FlightKind.Transfer && e.Name == "download" && !e.Ok && e.Status == 0);
        var repeated = Assert.Single(glitches);
        Assert.Equal(GlitchKinds.RepeatedFailure, repeated.Kind);
        Assert.StartsWith(Plate + " failed 3 times in a row", repeated.Summary);
        t.B.Network.StorageFault = null;
        await t.B.SyncAsync();
        Assert.Equal("plate v1 by Alex", t.B.Text(Plate));
        Assert.Equal(Plate, Assert.Single(t.B.Flight.Snapshot(), e => e.Kind == FlightKind.FileRecovered).Target);
    }

    [PostgresFact]
    public async Task The_snapshot_counts_files_requests_and_check_outs_and_keeps_the_last_three_passes()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "plate v1 by Alex");
        t.A.Write("Robot 2027/Drivetrain/Gear.SLDPRT", "gear");
        await t.A.SyncTimesAsync(4);
        Assert.Equal("Checked out Plate.SLDPRT.", (await t.A.CheckOutAsync(Plate)).Message);
        var snapshot = await t.A.Engine.DescribeAsync();
        Assert.Equal(Connections.SignedIn, snapshot["connection"]!.GetValue<string>());
        Assert.Equal(2, snapshot["files"]!.GetValue<int>());
        Assert.Equal(2, snapshot["filesByStatus"]![FileStatuses.Synced]!.GetValue<int>());
        Assert.Equal(1, snapshot["checkedOutHere"]!.GetValue<int>());
        Assert.Equal(Plate, snapshot["checkedOutHerePaths"]![0]!.GetValue<string>());
        Assert.True(snapshot["engine"]!["online"]!.GetValue<bool>());
        Assert.Equal(0, snapshot["pendingRequests"]!["checkIn"]!.GetValue<int>());
        Assert.Equal(3, snapshot["lastPasses"]!.AsArray().Count);
        Assert.Equal("Robot 2027", snapshot["engine"]!["projects"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(t.A.Engine.View.Settings.VaultRoot, snapshot["settings"]!["vaultRoot"]!.GetValue<string>());
    }
}
