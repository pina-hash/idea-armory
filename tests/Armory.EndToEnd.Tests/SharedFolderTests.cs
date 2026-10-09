using System.Security.Cryptography;
using System.Text;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// What a computer shared by several students asks of the engine (docs/agent/PROFILES.md, 0.3.3
// part F): a stop after which nothing of an engine writes again, even with a download or a check
// in under way (the next student's engine starts on the same folder); what the folder's student
// still has waiting, in the take-over's own words; whose a folder is, without an engine; an
// engine for the next student that looks and writes nothing until the folder is handed over; and
// the last student's check outs read-only while they are away.
public sealed class SharedFolderTests
{
    // Everything an engine could write, as it is now: every file in the folder (its bytes and its
    // read-only bit), the state document, the journal, the kept copies, and the read-only calls.
    private sealed record Picture(string Disk, string State, int StateSaves, string Journal, int Snapshots, int AttributeBatches);

    private static Picture Take(Computer c)
    {
        var disk = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(c.Disk.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(c.Disk.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            var inArmory = relative.StartsWith(".armory/", StringComparison.Ordinal);
            disk.Append(relative).Append('=').Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))
                .Append(!inArmory && c.Disk.IsReadOnly(relative) ? " read-only" : "").Append('\n');
        }
        var state = c.State.Load() ?? [];
        return new Picture(disk.ToString(), Convert.ToHexStringLower(SHA256.HashData(state)), c.State.Saves,
            Convert.ToHexStringLower(SHA256.HashData(c.Journal.ReadAll())), c.Snapshots.Enumerate().Count, c.Disk.AttributeBatches);
    }

    private static int Files(Computer c, string folder)
        => Directory.Exists(c.Disk.Full(folder)) ? Directory.EnumerateFiles(c.Disk.Full(folder)).Count() : 0;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, "waited " + within.TotalSeconds + " s for " + what);
            await Task.Delay(50);
        }
    }

    // Another student's sign-in on this computer, in a store of its own (as a profile has).
    private static async Task<SessionManager> SignInAsync(Team t, Computer c, string email)
    {
        var sessions = new SessionManager(c.Http, new InMemorySecretStore());
        using var browser = new FakeBrowser();
        Task<FakeBrowserResult>? approval = null;
        var flow = new ConnectFlow(c.Http, t.World.Site.BaseUri, new Launcher(uri => approval = browser.SignInAndApproveAsync(uri, email)), sessions);
        await flow.ConnectAsync(c.Name);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await approval!).Status);
        return sessions;
    }

    // An engine for that student over this computer's folder and stores, never started (a probe).
    private static SyncEngine EngineFor(Team t, Computer c, SessionManager sessions) => new(new EngineOptions { VaultRoot = World.Root }, new EngineDependencies
    {
        Files = c.Disk, Journal = c.Journal, Snapshots = c.Snapshots, State = c.State, Sessions = sessions,
        Api = new ArmoryApi(new PostgrestClient(c.Http, sessions)), Blobs = new BlobClient(c.Http, c.Http, t.World.Site.BaseUri, sessions), Clock = c.Clock,
    });

    private sealed class Launcher(Action<Uri> open) : IBrowserLauncher { public void Open(Uri uri) => open(uri); }

    private static void NoViolations(Computer c)
    {
        Assert.Empty(c.Disk.OpenWriteViolations);
        Assert.Empty(c.Disk.UnpreservedOverwrites);
    }

    [PostgresFact]
    public async Task Stop_during_a_download_pass_writes_nothing_after_it_returns()
    {
        await using var t = await TeamAsync();
        for (var i = 0; i < 80; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT", $"bulk part {i}");
        await t.A.SyncTimesAsync(2);
        // Maria's computer downloads them slowly, several at once, in its loop.
        t.B.Network.StorageDelay = r => r.Method == HttpMethod.Get ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;
        t.B.Engine.Start();
        await WaitUntil(() => Files(t.B, "Robot 2027/Bulk") >= 12, TimeSpan.FromSeconds(60), "a dozen downloads");
        await t.B.Engine.StopForGoodAsync();
        var stopped = Take(t.B);
        var here = Files(t.B, "Robot 2027/Bulk");
        Assert.InRange(here, 12, 79);

        // Storage answers at once now, and the engine is woken and asked again: nothing moves.
        t.B.Network.StorageDelay = null;
        t.B.Engine.Wake();
        await Task.Delay(1500);
        Assert.Equal(stopped, Take(t.B));
        Assert.True(t.B.Engine.IsStopping);
        await Assert.ThrowsAsync<EngineStoppedException>(() => t.B.Engine.SyncOnceAsync());
        await Assert.ThrowsAsync<EngineStoppedException>(() => t.B.CheckOutAsync(Plate));
        await Assert.ThrowsAsync<EngineStoppedException>(() => t.B.Engine.TakeOverFolderAsync());
        await Assert.ThrowsAsync<EngineStoppedException>(() => t.B.Engine.WaitingAsync());
        await t.B.Engine.StopForGoodAsync(); // a second stop waits for the first and does nothing more
        await t.B.Engine.DisposeAsync();
        Assert.Equal(stopped, Take(t.B));

        // The next engine over the same folder carries on where it stopped: every file arrives once.
        t.B.Restart();
        await t.B.SyncTimesAsync(2);
        Assert.Equal(80, Files(t.B, "Robot 2027/Bulk"));
        for (var i = 0; i < 80; i++) Assert.Equal($"bulk part {i}", t.B.Text($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT"));
        NoViolations(t.B);
    }

    [PostgresFact]
    public async Task Stop_during_a_check_in_action_finishes_or_stops_it_before_returning()
    {
        await using var t = await TeamAsync();
        for (var i = 0; i < 24; i++) t.A.Write($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT", $"v1 {i}");
        await t.A.SyncTimesAsync(2);
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Bulk")).Ok);
        for (var i = 0; i < 24; i++) t.A.Save($"Robot 2027/Bulk/Part-{i:D2}.SLDPRT", $"v2 {i}");
        var before = t.A.Network.StorageRequests;
        t.A.Network.StorageDelay = r => r.Method == HttpMethod.Put ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
        var checkIn = t.A.CheckInAsync("Robot 2027/Bulk");
        await WaitUntil(() => t.A.Network.StorageRequests > before + 3, TimeSpan.FromSeconds(30), "the check in's uploads to start");
        await t.A.Engine.StopForGoodAsync();
        // The action was over when the stop returned (its answer reaches the caller a moment
        // later, through the engine thread): done, or stopped with the engine.
        Assert.True(await Task.WhenAny(checkIn, Task.Delay(TimeSpan.FromSeconds(5))) == checkIn, "the check in was still running after the stop returned");
        string? said;
        try { said = (await checkIn).Message; }
        catch (OperationCanceledException) { said = null; }
        var stopped = Take(t.A);

        t.A.Network.StorageDelay = null;
        t.A.Engine.Wake();
        await Task.Delay(1500);
        Assert.Equal(stopped, Take(t.A));
        await Assert.ThrowsAsync<EngineStoppedException>(() => t.A.CheckInAsync("Robot 2027/Bulk"));
        Assert.Equal(stopped, Take(t.A));

        // The check in was durable: Alex's next engine finishes it, every save his.
        t.A.Restart();
        await t.A.SyncTimesAsync(3);
        for (var i = 0; i < 24; i++)
        {
            var path = $"Robot 2027/Bulk/Part-{i:D2}.SLDPRT";
            var file = await t.FileId($"Part-{i:D2}.SLDPRT");
            Assert.Equal(Hash($"v2 {i}"), await t.CurrentHash(file));
            Assert.Equal(0, await t.LiveLocks(file));
            Assert.True(t.A.Disk.IsReadOnly(path), path + " is still writable after its check in" + (said is null ? "" : " (" + said + ")"));
        }
        NoViolations(t.A);
    }

    [PostgresFact]
    public async Task Waiting_names_what_the_take_over_refuses_with_and_the_folder_names_its_owner()
    {
        await using var t = await TeamAsync();
        Assert.Null(SyncEngine.OwnerOf(new MemoryStateStore()));
        var unreadable = new MemoryStateStore();
        unreadable.Save("not a state document"u8.ToArray());
        Assert.Null(SyncEngine.OwnerOf(unreadable));
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.Equal(Alex, SyncEngine.OwnerOf(t.A.State));
        var nothing = await t.A.Engine.WaitingAsync();
        Assert.Equal((Alex, false, ""), (nothing.Owner, nothing.Any, nothing.Words));

        // A check out and a new file nobody has added yet.
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Write("Robot 2027/Drivetrain/Sketch.SLDPRT", "Alex's sketch");
        var waiting = await t.A.Engine.WaitingAsync();
        Assert.Equal((Alex, 1, 0, 0, 1, 0), (waiting.Owner, waiting.CheckedOut, waiting.Unsent, waiting.Changed, waiting.Added, waiting.FolderChanges));
        Assert.Equal(["1 file checked out", "1 new file not in Armory yet"], waiting.Parts);
        Assert.Equal("1 file checked out and 1 new file not in Armory yet", waiting.Words);

        // Maria signs in here: the take-over refuses with the same words, and the window names
        // the folder's owner and what waits (never parsed from a sentence: X-owner-name).
        await t.A.ConnectAsync(Maria);
        await t.A.SyncAsync();
        Assert.Equal(Connections.VaultOwnedByOther, t.A.Engine.View.Connection);
        var owner = t.A.Engine.View.FolderOwner!;
        Assert.Equal((Alex, "Alex Kim"), (owner.Email, owner.Name));
        Assert.Equal(waiting.Parts, owner.Waiting);
        var refused = await t.A.Engine.TakeOverFolderAsync();
        Assert.Equal($"Alex Kim still has {waiting.Words} in this folder. Alex can sign in to Armory here to finish them, or you can use a folder of your own.", refused.Message);
        Assert.Equal(Alex, SyncEngine.OwnerOf(t.A.State));
        // A folder that is the student's own names nobody.
        await t.A.ConnectAsync(Alex);
        await t.A.SyncAsync();
        Assert.Null(t.A.Engine.View.FolderOwner);
    }

    [PostgresFact]
    public async Task A_stopped_engine_for_the_next_student_looks_and_writes_nothing_until_the_take_over()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex, not checked in");
        await t.A.Engine.StopForGoodAsync();
        var maria = await SignInAsync(t, t.A, Maria);
        var before = Take(t.A);

        // Maria's engine on Alex's folder, never started: it sees Alex's work and leaves it be.
        await using (var probe = EngineFor(t, t.A, maria))
        {
            var waiting = await probe.WaitingAsync();
            Assert.Equal((Alex, "1 file checked out"), (waiting.Owner, waiting.Words));
            Assert.False((await probe.TakeOverFolderAsync()).Ok);
        }
        Assert.Equal(before, Take(t.A));
        Assert.Equal("Alex, not checked in", t.A.Text(Plate));

        // Alex comes back and checks in; a fresh look finds nothing of his, and the folder is Maria's.
        t.A.Restart();
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        await t.A.Engine.StopForGoodAsync();
        var probe2 = EngineFor(t, t.A, maria);
        Assert.False((await probe2.WaitingAsync()).Any);
        Assert.True((await probe2.TakeOverFolderAsync()).Ok);
        Assert.Null(SyncEngine.OwnerOf(t.A.State));
        var first = await probe2.SyncOnceAsync();
        Assert.Equal((0, 0), (first.Downloaded, first.Uploaded));
        Assert.Equal(Maria, SyncEngine.OwnerOf(t.A.State));
        await probe2.DisposeAsync();
        NoViolations(t.A);
    }

    [PostgresFact]
    public async Task Sealed_check_outs_are_read_only_until_their_student_runs_again()
    {
        await using var t = await TeamAsync();
        t.A.Write(Plate, "v1");
        t.A.Write("Robot 2027/Drivetrain/Gear.SLDPRT", "gear v1");
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync(Plate)).Ok);
        t.A.Save(Plate, "Alex, not checked in");
        Assert.False(t.A.Disk.IsReadOnly(Plate));

        // The next student moves to a folder of their own: Alex's check out is sealed.
        Assert.Equal(1, await t.A.Engine.SealCheckOutsAsync());
        await t.A.Engine.StopForGoodAsync();
        Assert.True(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Drivetrain/Gear.SLDPRT"));
        Assert.Throws<IOException>(() => t.A.Save(Plate, "someone else's save"));
        Assert.Equal("Alex, not checked in", t.A.Text(Plate));

        // Alex is back: his check out is writable again, and his work is his.
        t.A.Restart();
        await t.A.SyncAsync();
        Assert.False(t.A.Disk.IsReadOnly(Plate));
        Assert.True(t.A.Disk.IsReadOnly("Robot 2027/Drivetrain/Gear.SLDPRT"));
        t.A.Save(Plate, "Alex v2");
        Assert.Equal("Checked in Plate.SLDPRT.", (await t.A.CheckInAsync(Plate)).Message);
        Assert.Equal(Hash("Alex v2"), await t.CurrentHash(await t.FileId("Plate.SLDPRT")));
        NoViolations(t.A);
    }
}
