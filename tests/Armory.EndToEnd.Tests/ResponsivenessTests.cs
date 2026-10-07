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
}
