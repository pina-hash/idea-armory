using System.Net;
using Armory.Agent.Engine.View;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// v0.2.1, the field report: a mentor added a part and checked it in; a student checked it out
// before it had downloaded to his computer, while he was also reorganizing folders; then the
// button was stuck and nobody could check it in. The rule now: every lock this computer holds
// is its check out, shown as "Checked out by you" with a working Check in and Undo, wherever
// the file is and whether it is here or not, and a check out that fails leaves no lock.
public sealed class StuckCheckOutTests
{
    private const string Other = "Robot 2027/Frame/Other.SLDPRT", Bracket = "Robot 2027/Frame/Bracket.SLDPRT";
    private const string Moved = "Robot 2027/Chassis/Bracket.SLDPRT";

    private static FileRowView RowOf(Computer c, Guid file)
        => c.Engine.View.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files).Single(r => r.FileId == file.ToString());

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, "Timed out waiting: " + what);
            await Task.Delay(50);
        }
    }

    [PostgresFact]
    public async Task Checked_out_before_it_downloaded_while_its_folder_moved()
    {
        await using var t = await TeamAsync();
        // The team's folder, on both computers; then the mentor's new part and 40 more files.
        t.A.Write(Other, "other");
        await t.A.SyncAsync();
        await t.B.SyncAsync();
        t.A.Write(Bracket, "bracket v1");
        for (var i = 0; i < 40; i++) t.A.Write($"Robot 2027/Frame/New-{i:D2}.SLDPRT", $"new part {i}");
        await t.A.SyncTimesAsync(2);
        var file = await t.FileId("Bracket.SLDPRT");

        // Maria's computer is downloading them slowly; the part's own download is refused for now.
        var refused = true;
        t.B.Network.StorageFault = r => refused && r.Method == HttpMethod.Get && r.RequestUri!.AbsoluteUri.Contains(Hash("bracket v1"), StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        t.B.Network.StorageDelay = r => r.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
        t.B.Engine.Start();
        await WaitUntil(() => Directory.EnumerateFiles(t.B.Disk.Full("Robot 2027/Frame")).Count() >= 4 &&
            t.B.Engine.View.Projects.SelectMany(p => p.Folders).SelectMany(f => f.Files).Any(r => r.FileId == file.ToString()), TimeSpan.FromSeconds(30), "the downloads to start");
        var rowPath = RowOf(t.B, file).Path;
        Assert.Equal(Bracket, rowPath);

        // She reorganizes: the folder is renamed in Explorer while files are still on their way,
        // and she checks the part out from the row she sees. It isn't here yet and can't come yet:
        // the check out says so and leaves no lock behind.
        t.B.RenameFolder("Robot 2027/Frame", "Robot 2027/Chassis");
        var first = await t.B.CheckOutAsync(rowPath);
        Assert.False(first.Ok, first.Message);
        Assert.Equal(0, await t.LiveLocks(file));

        // Its download comes through: checking out the row as it is now takes the lock.
        refused = false;
        await WaitUntil(() => RowOf(t.B, file).Path == Moved, TimeSpan.FromSeconds(30), "the row to follow the folder");
        var second = await t.B.CheckOutAsync(Moved);
        Assert.True(second.Ok, second.Message);
        Assert.Equal(Maria, await t.Holder(file));
        await WaitUntil(() => Directory.EnumerateFiles(t.B.Disk.Full("Robot 2027/Chassis")).Count() == 42, TimeSpan.FromSeconds(60), "every file to arrive");
        await t.B.Engine.StopAsync();
        await t.B.SyncTimesAsync(2);

        // Nothing went back to the old folder: not on her disk, and not for the team.
        Assert.False(Directory.Exists(t.B.Disk.Full("Robot 2027/Frame")), "the old folder was made again on Maria's computer");
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and folder='Frame' and deleted_at is null", ("p", t.Project)));
        Assert.Equal(42, await t.World.CountAsync("select count(*) from armory_files where project_id=@p and folder='Chassis' and deleted_at is null", ("p", t.Project)));
        Assert.Equal(0, await t.World.CountAsync("select count(*) from armory_locks l join armory_files f on f.id=l.file_id where f.project_id=@p and l.broken_at is null and f.id<>@f",
            ("p", t.Project), ("f", file)));

        // Her row, her file list and her disk all say it is hers; the mentor's computer says Maria has it.
        Assert.Equal(CheckoutStates.Mine, RowOf(t.B, file).Checkout.State);
        Assert.Equal(Moved, Assert.Single(t.B.Engine.View.MyFiles).Path);
        Assert.False(t.B.Disk.IsReadOnly(Moved));
        await t.A.SyncTimesAsync(2);
        Assert.Equal(CheckoutStates.Other, RowOf(t.A, file).Checkout.State);
        Assert.Equal(Moved, RowOf(t.A, file).Path);

        // Check in works from the row, and the file is free for everyone again.
        t.B.Save(Moved, "bracket v2");
        Assert.Equal("Checked in Bracket.SLDPRT.", (await t.B.CheckInAsync(Moved)).Message);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Equal(Hash("bracket v2"), await t.CurrentHash(file));
        Assert.True(t.B.Disk.IsReadOnly(Moved));
        Assert.Empty(t.B.Disk.OpenWriteViolations);
        Assert.Empty(t.B.Disk.UnpreservedOverwrites);
    }

    // A lock this computer holds with no record of it here (taken by a call whose answer this
    // computer never kept), on a file that has not downloaded, in a folder renamed on this disk:
    // shown as checked out by you, listed in My files, and Undo simply lets it go.
    [PostgresFact]
    public async Task A_lock_with_no_record_here_is_still_checked_out_by_you()
    {
        await using var t = await TeamAsync();
        t.A.Write(Other, "other");
        t.A.Write(Bracket, "bracket v1");
        await t.A.SyncAsync();
        var file = await t.FileId("Bracket.SLDPRT");
        // Maria's computer has the folder but not the part (its download is refused), and
        // the server holds the part's lock for her computer.
        t.B.Network.StorageFault = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsoluteUri.Contains(Hash("bracket v1"), StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        await t.B.SyncAsync();
        t.B.RenameFolder("Robot 2027/Frame", "Robot 2027/Chassis");
        Assert.True(await t.B.Api.AcquireLockAsync(file, t.B.DeviceId, Guid.NewGuid()));
        await t.B.SyncTimesAsync(2);

        var row = RowOf(t.B, file);
        Assert.Equal(Moved, row.Path);
        Assert.Equal(CheckoutStates.Mine, row.Checkout.State);
        Assert.Equal("Checked out by you", row.Checkout.Label);
        Assert.Equal(Moved, Assert.Single(t.B.Engine.View.MyFiles).Path);
        Assert.Null(t.B.Read(Moved));
        // Undo of a file that never came down: the lock goes, nothing else changes.
        Assert.Equal("Undid the check out of Bracket.SLDPRT.", (await t.B.UndoCheckOutAsync(Moved)).Message);
        Assert.Equal(0, await t.LiveLocks(file));
        Assert.Empty(t.B.Engine.View.MyFiles);
        Assert.Equal(1, await t.Versions(file));
        Assert.Equal(0, await t.Sides(file));
    }
}
