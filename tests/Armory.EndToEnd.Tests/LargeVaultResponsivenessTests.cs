using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Telemetry;
using Armory.TestSupport;
using Xunit.Abstractions;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Requirement E (0.3.3, feedback N6: "no button or action in the armory should feel laggy or
// delayed"): every click answers at once and finishes quickly. One window action per test (every
// action Bridge.cs takes to the engine), measured on a mentor's computer holding a 1,500-file
// synced vault, on the school network (LatencyProfile.School), with the open-files question
// costing what it cost on Windows (10 seconds were it asked about every read-only file, a fifth of
// that a file for one checked out here: OpenAmongCost), while the loop runs as it does in the app.
// Each action must answer within 1 second plus the time its server calls and transfers took and
// the open-files questions it had to ask about its own files (added up, so it never undercounts
// them; each question is bounded by the 2 second budget), and an action that works on files says
// what it is doing in the window's running lines within half a second. The
// times are printed as a table, and written to the file ARMORY_RESPONSIVENESS_REPORT names
// (docs/agent/responsiveness-0.3.3.md is such a run). The class runs on its own (its collection
// is not parallel), so other tests' work never shows in its times.
[Collection(LargeVault.Collection)]
public sealed class LargeVaultResponsivenessTests(LargeVault vault, ITestOutputHelper output) : IClassFixture<LargeVault>
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WorkingShown = TimeSpan.FromMilliseconds(500);

    private Computer B => vault.B;
    private Computer A => vault.A;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < within, "Timed out waiting: " + what);
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < within, "Timed out waiting: " + what);
            await Task.Delay(50);
        }
    }

    // One click, timed from the moment it is made: its answer, its running line (when it has one),
    // and the server calls and transfers made meanwhile. Asserts the answer came within a second
    // plus that server time, and the running line within half a second.
    private async Task<T> Click<T>(string action, Func<Task<T>> click, string? line)
    {
        var shown = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = line is null ? 0 : B.Engine.ActivityNow.Log.Count(l => l.Line == line);
        var watch = Stopwatch.StartNew();
        void Seen(ActivityView activity)
        {
            if (line is not null && activity.Log.Count(l => l.Line == line) > before) shown.TrySetResult(watch.Elapsed);
        }
        var first = B.Flight.Recorded;
        B.Activities += Seen;
        T result;
        TimeSpan answered;
        try
        {
            result = await click();
            answered = watch.Elapsed;
            if (line is not null) await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        finally { B.Activities -= Seen; }
        var server = ServerTime(first);
        var questions = OpenFilesTime(first);
        var working = shown.Task.IsCompletedSuccessfully ? shown.Task.Result : (TimeSpan?)null;
        Record(action, answered, working, server, questions);
        output.WriteLine(action + ", " + Passes(first));
        Assert.True(answered < Answer + server + questions,
            $"{action} answered after {answered.TotalMilliseconds:F0} ms, with {server.TotalMilliseconds:F0} ms of server time and {questions.TotalMilliseconds:F0} ms of open-files questions");
        if (line is not null)
        {
            Assert.True(working is not null, $"{action} never showed \"{line}\"");
            Assert.True(working < WorkingShown, $"{action} showed \"{line}\" after {working!.Value.TotalMilliseconds:F0} ms");
        }
        return result;
    }

    // The server calls and transfers this computer made since the flight event first (their
    // times added up, the loop's included).
    private TimeSpan ServerTime(long first) => Sum(first, e => e.Kind is FlightKind.Rpc or FlightKind.Transfer);

    // The open-files questions the action itself had to ask about its own files (a folder's files
    // before it moves them, the files it lets go of): Windows' cost, each bounded by the engine's
    // 2 second budget, like a server round trip.
    private TimeSpan OpenFilesTime(long first) => Sum(first, e => e.Kind == FlightKind.OpenFiles);

    private TimeSpan Sum(long first, Func<FlightEvent, bool> which)
    {
        long ms = 0;
        foreach (var e in B.Flight.Snapshot())
            if (e.Sequence > first && which(e)) ms += e.Ms;
        return TimeSpan.FromMilliseconds(ms);
    }

    // What the engine did meanwhile, pass by pass (its phases in ms), and each open-files question:
    // where the time went when an answer is slow.
    private string Passes(long first)
    {
        var text = new StringBuilder();
        foreach (var e in B.Flight.Snapshot())
        {
            if (e.Sequence <= first) continue;
            switch (e.Kind)
            {
                case FlightKind.PassStart: text.Append(CultureInfo.InvariantCulture, $"\n  pass {e.Name}:"); break;
                case FlightKind.PassPhase: text.Append(CultureInfo.InvariantCulture, $" {e.Name} {e.Ms}"); break;
                case FlightKind.PassYield: text.Append(CultureInfo.InvariantCulture, $" (gave way after {e.Ms})"); break;
                case FlightKind.PassEnd: text.Append(CultureInfo.InvariantCulture, $", {e.Ms} in all"); break;
                case FlightKind.OpenFiles: text.Append(CultureInfo.InvariantCulture, $" [open files: {e.Count} in {e.Ms}]"); break;
            }
        }
        return "engine meanwhile:" + text;
    }

    private void Record(string action, TimeSpan answered, TimeSpan? working, TimeSpan server, TimeSpan questions)
    {
        vault.Results[action] = new(answered, working, server, questions);
        output.WriteLine(LargeVault.Table(vault.Results));
    }

    private async Task<Guid> Id(string path) => await vault.FileIdAsync(path);

    [PostgresFact]
    public async Task A_check_out_answers_at_once()
    {
        var path = LargeVault.PathOf(0);
        var answer = await Click("checkOut", () => B.CheckOutAsync(path), "Checking out Part-0000.SLDPRT");
        Assert.Equal("Checked out Part-0000.SLDPRT.", answer.Message);
        Assert.False(B.Disk.IsReadOnly(path));
    }

    [PostgresFact]
    public async Task A_check_out_and_open_answers_at_once()
    {
        var path = LargeVault.PathOf(1);
        var answer = await Click("checkOut and open", () => B.CheckOutAndOpenAsync(path), "Checking out Part-0001.SLDPRT");
        Assert.Equal("Checked out Part-0001.SLDPRT.", answer.Message);
        Assert.Contains(path, B.Disk.Launched);
    }

    [PostgresFact]
    public async Task A_check_in_answers_at_once()
    {
        var path = LargeVault.PathOf(2);
        Assert.True((await B.CheckOutAsync(path)).Ok);
        B.Save(path, "the mentor's fix");
        var answer = await Click("checkIn", () => B.CheckInAsync(path), "Checking in Part-0002.SLDPRT");
        Assert.Equal("Checked in Part-0002.SLDPRT.", answer.Message);
        Assert.True(B.Disk.IsReadOnly(path));
    }

    [PostgresFact]
    public async Task An_undo_answers_at_once()
    {
        var path = LargeVault.PathOf(3);
        Assert.True((await B.CheckOutAsync(path)).Ok);
        B.Save(path, "an idea that did not work");
        var answer = await Click("undoCheckOut", () => B.UndoCheckOutAsync(path), "Undoing the check out of Part-0003.SLDPRT");
        Assert.Equal("Undid the check out of Part-0003.SLDPRT. Your changes are kept as your own copy.", answer.Message);
        Assert.Equal("part 3 of the robot", B.Text(path));
    }

    [PostgresFact]
    public async Task A_force_check_in_answers_at_once()
    {
        var path = LargeVault.PathOf(4);
        Assert.True((await A.CheckOutAsync(path)).Ok);
        await WaitUntil(() => B.Row(path).Checkout.State == CheckoutStates.Other, TimeSpan.FromSeconds(30), "the mentor's computer to see Alex's check out");
        var id = await Id(path);
        var answer = await Click("takeBack", () => B.TakeBackAsync(id), "Force checking in Part-0004.SLDPRT");
        Assert.StartsWith("Force checked in Part-0004.SLDPRT from Alex Kim.", answer.Message);
    }

    [PostgresFact]
    public async Task Force_check_in_of_a_hundred_files_answers_at_once()
    {
        const string folder = LargeVault.Root + "/S01";
        Assert.True((await A.CheckOutAsync(folder)).Ok);
        var paths = Enumerable.Range(100, 100).Select(LargeVault.PathOf).ToList();
        await WaitUntil(() => paths.All(p => B.Row(p).Checkout.State == CheckoutStates.Other), TimeSpan.FromSeconds(30), "the mentor's computer to see Alex's check outs");
        List<Guid> ids = [];
        foreach (var path in paths) ids.Add(await Id(path));
        var answer = await Click("takeBackAll (100 files)", () => B.Engine.TakeBackAsync(ids), "Force checking in 100 files");
        Assert.StartsWith("Force checked in 100 files.", answer.Message);
    }

    [PostgresFact]
    public async Task Open_answers_at_once()
    {
        var path = LargeVault.PathOf(5);
        var answer = await Click("launchFile", () => B.Engine.LaunchAsync(path), "Opening Part-0005.SLDPRT");
        Assert.Equal("Opening Part-0005.SLDPRT.", answer.Message);
        Assert.Contains(path, B.Disk.Launched);
    }

    [PostgresFact]
    public async Task File_detail_answers_at_once()
    {
        var id = await Id(LargeVault.PathOf(6));
        var detail = await Click("openFile (File detail)", () => B.Engine.GetFileDetailAsync(id), line: null);
        Assert.NotNull(detail);
        Assert.Equal("Part-0006.SLDPRT", detail.Name);
        Assert.NotEmpty(detail.History);
    }

    [PostgresFact]
    public async Task A_file_rename_answers_at_once()
    {
        var answer = await Click("renameFile", () => B.Engine.RenameFileAsync(LargeVault.PathOf(7), "Part-0007 v2.SLDPRT"), "Renaming Part-0007.SLDPRT to Part-0007 v2.SLDPRT");
        Assert.Equal("Renamed Part-0007.SLDPRT to Part-0007 v2.SLDPRT.", answer.Message);
    }

    [PostgresFact]
    public async Task A_new_folder_answers_at_once()
    {
        var answer = await Click("createFolder", () => B.Engine.CreateFolderAsync(vault.Project, "Vault", "New parts"), "Making the folder New parts");
        Assert.Equal("Made the folder New parts in Robot 2027 › Vault.", answer.Message);
    }

    [PostgresFact]
    public async Task A_folder_rename_answers_at_once()
    {
        var answer = await Click("renameFolder (100 files)", () => B.Engine.RenameFolderAsync(vault.Project, "Vault/S02", "S02 renamed"), "Renaming S02 to S02 renamed");
        Assert.True(Directory.Exists(B.Disk.Full(LargeVault.Root + "/S02 renamed")));
        Assert.True(answer.Ok, answer.Message);
    }

    [PostgresFact]
    public async Task A_folder_delete_answers_at_once()
    {
        var answer = await Click("deleteFolder (100 files)", () => B.Engine.DeleteFolderAsync(vault.Project, "Vault/S03"), "Deleting the folder S03");
        Assert.True(answer.Ok, answer.Message);
    }

    [PostgresFact]
    public async Task Adding_files_answers_at_once()
    {
        var source = Path.Combine(vault.World.Temp, "added-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        List<string> files = [];
        for (var i = 0; i < 10; i++)
        {
            var file = Path.Combine(source, $"Bought-{i:D2}.STEP");
            File.WriteAllText(file, "a part bought in " + i);
            files.Add(file);
        }
        var answer = await Click("addFiles (10 files)", () => B.Engine.AddFilesAsync(vault.Project, "Vault", files), "Adding 10 files and folders");
        Assert.Equal("Copied 10 files into Robot 2027 › Vault.", answer.Message);
    }

    [PostgresFact]
    public async Task Dismissing_a_notice_answers_at_once()
    {
        var path = LargeVault.PathOf(8);
        B.Open(path);
        try
        {
            await WaitUntil(() => B.Engine.View.Prompt?.Path == path, TimeSpan.FromSeconds(30), "the check-out question");
            var key = B.Engine.View.Prompt!.Key;
            await Click("dismissNotice", async () =>
            {
                B.Engine.DismissNotice(key);
                await WaitUntil(() => B.Engine.View.Prompt?.Key != key, TimeSpan.FromSeconds(10), "the question to go");
                return true;
            }, line: null);
        }
        finally { B.Close(path); }
    }

    [PostgresFact]
    public async Task Pause_and_resume_answer_at_once()
    {
        await Click("pause", async () =>
        {
            B.Engine.Pause();
            await WaitUntil(() => B.Engine.View.Sync.State == SyncStates.Paused, TimeSpan.FromSeconds(10), "the window to say paused");
            return true;
        }, line: null);
        await Click("resume", async () =>
        {
            B.Engine.Resume();
            await WaitUntil(() => B.Engine.View.Sync.State != SyncStates.Paused, TimeSpan.FromSeconds(10), "the window to say it syncs again");
            return true;
        }, line: null);
    }

    // Send feedback keeps the engine's own snapshot (0.3.3: 5 of 15 notes had none, the engine busy).
    [PostgresFact]
    public async Task Send_feedback_keeps_a_snapshot_at_once()
    {
        var folder = Path.Combine(vault.World.Temp, "incidents-" + Guid.NewGuid().ToString("N"));
        var reporter = new IncidentReporter(new FlightRecorder(), new IncidentStore(folder), new IncidentSources
        {
            Header = () => new IncidentHeader("0.3.3", "test", "mentor laptop", Mentor),
            Snapshot = async ct => await B.Engine.DescribeAsync(ct),
            QuickSnapshot = () => SyncEngine.DescribeView(B.Engine.View),
        });
        var saved = await Click("sendFeedback (snapshot)", () => reporter.SaveNoteAsync("idea", "the buttons feel quick now"), line: null);
        var note = reporter.Store.Read(saved!);
        Assert.NotNull(note["snapshot"]!["engine"]);
        Assert.Null(note["snapshot"]!["engineBusy"]);
    }

    [PostgresFact]
    public async Task Check_in_all_answers_at_once()
    {
        const string folder = LargeVault.Root + "/S04";
        Assert.True((await B.CheckOutAsync(folder)).Ok);
        var paths = Enumerable.Range(400, 100).Select(LargeVault.PathOf).ToArray();
        foreach (var path in paths.Take(10)) B.Save(path, "fixed " + path);
        var answer = await Click("checkIn all (100 files)", () => B.CheckInAsync(paths), "Checking in 100 files");
        Assert.Equal("Checked in 100 files.", answer.Message);
    }

    [PostgresFact]
    public async Task Undo_all_answers_at_once()
    {
        const string folder = LargeVault.Root + "/S05";
        Assert.True((await B.CheckOutAsync(folder)).Ok);
        var paths = Enumerable.Range(500, 100).Select(LargeVault.PathOf).ToArray();
        var answer = await Click("undoCheckOut all (100 files)", () => B.UndoCheckOutAsync(paths), "Undoing 100 check outs");
        Assert.Equal("Undid 100 check outs.", answer.Message);
    }

    // A whole 500-file folder: one lock batch, its files read for none of them (unchanged since
    // the scan), and the open-files question asked at most twice.
    [PostgresFact]
    public async Task Checking_out_a_big_folder_answers_at_once()
    {
        var (reads, questions) = (B.Disk.OpenReads, B.Disk.OpenAmongCalls);
        var answer = await Click("checkOut of a folder (500 files)", () => B.CheckOutAsync(LargeVault.Root + "/Big"), "Checking out 500 files");
        Assert.Equal("Checked out 500 files.", answer.Message);
        Assert.Equal(0, B.Disk.OpenReads - reads);
        Assert.InRange(B.Disk.OpenAmongCalls - questions, 0, 4);
    }

    // Ten Check in keys pressed one after another on My files rows: answered together, by one
    // pass or two and one or two armory_release_locks calls, never one pass a click (0.3.1: 15
    // clicks were 15 passes, the last answered 8 minutes later).
    [PostgresFact]
    public async Task Ten_row_clicks_of_check_in_are_answered_together()
    {
        var paths = Enumerable.Range(600, 10).Select(LargeVault.PathOf).ToArray();
        Assert.True((await B.CheckOutAsync(paths)).Ok);
        foreach (var path in paths) B.Save(path, "row " + path);
        var releases = vault.World.Supabase.RpcCount("armory_release_locks") + vault.World.Supabase.RpcCount("armory_release_lock");
        var answers = await Click("checkIn, 10 row clicks", async () => await Task.WhenAll(paths.Select(p => B.CheckInAsync(p))), "Checking in Part-0609.SLDPRT");
        for (var i = 0; i < paths.Length; i++) Assert.Equal($"Checked in {Path.GetFileName(paths[i])}.", answers[i].Message);
        Assert.InRange(vault.World.Supabase.RpcCount("armory_release_locks") + vault.World.Supabase.RpcCount("armory_release_lock") - releases, 1, 2);
    }

    // File detail's Put back on this computer: a kept copy of the mentor's own, checked out to them.
    [PostgresFact]
    public async Task Putting_back_a_kept_copy_answers_at_once()
    {
        var path = LargeVault.PathOf(9);
        Assert.True((await B.CheckOutAsync(path)).Ok);
        B.Save(path, "a copy worth keeping");
        Assert.True((await B.UndoCheckOutAsync(path)).Ok);
        var id = await Id(path);
        var kept = (await B.Engine.GetFileDetailAsync(id))!.History.First(h => h.Kind == HistoryKinds.KeptCopy);
        var answer = await Click("putBackKeptCopy", () => B.Engine.PutBackKeptCopyAsync(id, Guid.Parse(kept.Id)), "Putting your copy of Part-0009.SLDPRT back");
        Assert.True(answer.Ok, answer.Message);
        Assert.Equal("a copy worth keeping", B.Text(path));
    }

    // The loop's own pass over the whole vault with nothing to do asks whether no file is open.
    [PostgresFact]
    public async Task A_quiet_pass_over_the_large_vault_asks_about_no_file()
    {
        await B.Engine.SyncOnceAsync();
        var asked = B.Disk.OpenAmongSizes.Sum();
        var watch = Stopwatch.StartNew();
        var first = B.Flight.Recorded;
        await B.Engine.SyncOnceAsync();
        watch.Stop();
        Record("a quiet pass (the loop's own)", watch.Elapsed, null, ServerTime(first), OpenFilesTime(first));
        Assert.Equal(0, B.Disk.OpenAmongSizes.Sum() - asked);
    }
}

// The large vault every test above shares (built once, at loopback speed), and the table of times.
public sealed class LargeVault : IAsyncLifetime
{
    internal const string Collection = "large vault, alone";
    internal const int Files = 1500;
    internal const string Root = "Robot 2027/Vault";
    internal readonly ConcurrentDictionary<string, Measured> Results = new(StringComparer.Ordinal);
    internal World World { get; private set; } = null!;
    internal Guid Project { get; private set; }
    // Alex's laptop, and the mentor's: every click is the mentor's.
    internal Computer A { get; private set; } = null!;
    internal Computer B { get; private set; } = null!;

    internal sealed record Measured(TimeSpan Answered, TimeSpan? Working, TimeSpan Server, TimeSpan OpenFiles);

    // Windows' open-files question, scaled to 10 seconds were it asked about all 1,500 read-only
    // files, and a fifth of that a file for writable ones (checked out here).
    internal static TimeSpan Cost(int readOnly, int writable) => TimeSpan.FromMilliseconds((10_000.0 * readOnly + 2_000.0 * writable) / Files);

    // 1,000 files in ten folders of 100 (S00 to S09) and 500 in Big, named Part-0000 to Part-1499.
    internal static string PathOf(int i) => i < 1000 ? $"{Root}/S{i / 100:D2}/Part-{i:D4}.SLDPRT" : $"{Root}/Big/Part-{i:D4}.SLDPRT";

    internal async Task<Guid> FileIdAsync(string path)
        => (await World.QueryAsync("select id from armory_files where project_id=@p and name=@n", r => r.GetGuid(0), ("p", Project), ("n", path[(path.LastIndexOf('/') + 1)..]))).Single();

    public async Task InitializeAsync()
    {
        if (!ArmoryTestDatabase.IsAvailable) return;
        World = await World.StartAsync();
        var mentor = await World.PersonAsync(Mentor, admin: true);
        Project = await mentor.Api.CreateProjectAsync("Robot 2027", 2027, Guid.NewGuid());
        await mentor.Api.AddMemberAsync(Project, Alex, MemberRole.Student, Guid.NewGuid());
        await ArmoryV3StandIn.ApplyCoreAsync(World.Database);
        await ArmoryV3StandIn.ApplyBreakLocksAsync(World.Database);
        A = await World.ComputerAsync("student A laptop", Alex);
        B = await World.ComputerAsync("mentor laptop", Mentor);
        for (var i = 0; i < Files; i++) A.Write(PathOf(i), $"part {i} of the robot");
        await A.SyncTimesAsync(2);
        await B.SyncAsync();
        Assert.Equal(Files, Directory.EnumerateFiles(B.Disk.Full(Root), "*", SearchOption.AllDirectories).Count());
        // Saved a while ago, as a student's files are (a hash taken just after a write is never
        // trusted again without reading the file).
        foreach (var file in Directory.EnumerateFiles(B.Disk.Full(Root), "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
        await B.SyncAsync();
        // What is measured: the school network, Windows' open-files cost, and the loop running.
        A.Network.Profile = LatencyProfile.School;
        B.Network.Profile = LatencyProfile.School;
        B.Disk.OpenAmongCost = Cost;
        B.Disk.ReuseHashes = true;
        A.Disk.OpenAmongCost = B.Disk.OpenAmongCost;
        var passes = B.Flight.Snapshot().Count(e => e.Kind == FlightKind.PassEnd);
        B.Engine.Start();
        var watch = Stopwatch.StartNew();
        while (B.Flight.Snapshot().Count(e => e.Kind == FlightKind.PassEnd) <= passes && watch.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
    }

    public async Task DisposeAsync()
    {
        if (World is null) return;
        try { Write(); }
        finally { await World.DisposeAsync(); }
    }

    internal static string Table(IReadOnlyDictionary<string, Measured> results)
    {
        static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture);
        var table = new StringBuilder();
        table.AppendLine("| Window action | Answered (ms) | Running line shown (ms) | Server calls and transfers meanwhile (ms) | Open-files questions (ms) | Allowed (ms) |");
        table.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (action, m) in results.OrderBy(r => r.Key, StringComparer.Ordinal))
            table.AppendLine($"| {action} | {Ms(m.Answered)} | {(m.Working is { } w ? Ms(w) : "")} | {Ms(m.Server)} | {Ms(m.OpenFiles)} | {Ms(TimeSpan.FromSeconds(1) + m.Server + m.OpenFiles)} |");
        return table.ToString();
    }

    // The table, into the file ARMORY_RESPONSIVENESS_REPORT names (a path from the repository's
    // root, or a full path), with how it was made.
    private void Write()
    {
        if (Environment.GetEnvironmentVariable("ARMORY_RESPONSIVENESS_REPORT") is not { Length: > 0 } target || Results.IsEmpty) return;
        if (!Path.IsPathFullyQualified(target))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Armory.sln"))) root = root.Parent;
            if (root is null) return;
            target = Path.Combine(root.FullName, target);
        }
        var document = new StringBuilder();
        document.AppendLine("# How quickly each window action answers (0.3.3)");
        document.AppendLine();
        document.AppendLine("Generated by `LargeVaultResponsivenessTests` (tests/Armory.EndToEnd.Tests), requirement E and");
        document.AppendLine("feedback N6: a mentor's computer holding a 1,500-file synced vault, the school network");
        document.AppendLine("(`LatencyProfile.School`: 60 ms a database call, 250 ms to sign a transfer, 60 ms and 4 MB/s");
        document.AppendLine("to file storage), the open-files question costing what it cost on Windows (10 seconds were it");
        document.AppendLine("asked about all 1,500 read-only files, a fifth of that a file for files checked out here), and");
        document.AppendLine("the sync loop running as it does in the app. Each action must answer within 1 second plus the");
        document.AppendLine("server calls and transfers made meanwhile and the open-files questions it asked about its own");
        document.AppendLine("files (their times added up; each question is bounded by the engine's 2 second budget), and say");
        document.AppendLine("what it is doing in the window's running lines within half a second. The window itself marks the");
        document.AppendLine("pressed key busy the moment it is clicked, before the host hears of it. The test file system");
        document.AppendLine("reuses a file's hash while its size and last-write time are unchanged, as the Windows scan does.");
        document.AppendLine();
        document.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Measured {DateTime.UtcNow:yyyy-MM-dd} on {Environment.OSVersion.Platform}, {Environment.ProcessorCount} processors, with the portable test file system."));
        document.AppendLine("To make it again, from the repository's root, with a throwaway PostgreSQL cluster:");
        document.AppendLine();
        document.AppendLine("```");
        document.AppendLine("ARMORY_TEST_POSTGRES=\"Host=127.0.0.1;Port=<port>;Username=postgres;Database=postgres\" \\");
        document.AppendLine("ARMORY_RESPONSIVENESS_REPORT=docs/agent/responsiveness-0.3.3.md \\");
        document.AppendLine("dotnet test tests/Armory.EndToEnd.Tests --filter FullyQualifiedName~LargeVaultResponsivenessTests");
        document.AppendLine("```");
        document.AppendLine();
        document.Append(Table(Results));
        File.WriteAllText(target, document.ToString());
    }
}

[CollectionDefinition(LargeVault.Collection, DisableParallelization = true)]
public sealed class LargeVaultCollection;
