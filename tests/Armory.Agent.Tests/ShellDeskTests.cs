using System.Collections.Concurrent;
using Armory.Agent.Engine.View;

namespace Armory.Agent.Tests;

// Views and doubles for the shell desk's tests: a vault at C:\IDEA\Armory with the files a test
// names, a host that records each engine call, and a window and tray that record what they show.
internal static class ShellViews
{
    internal const string Root = @"C:\IDEA\Armory";
    internal static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    internal static readonly CheckoutView Available = new(CheckoutStates.Available, "Available", null, null, null, null);
    internal static readonly CheckoutView Mine = new(CheckoutStates.Mine, "Checked out by you", "Alex Kim", "alex.kim@students.test", "LAB-PC-01", "2026-10-09T11:00:00Z");

    internal static CheckoutView HeldBy(string name, string device = "LAB-PC-07") =>
        new(CheckoutStates.Other, $"Checked out by {name} on {device}", name, name.Replace(' ', '.').ToLowerInvariant() + "@students.test", device, "2026-10-09T11:00:00Z");

    internal static CheckoutView OnMyOther(string device = "LAPTOP-9") =>
        new(CheckoutStates.MyOtherComputer, $"Checked out by you on {device}", "Alex Kim", "alex.kim@students.test", device, "2026-10-09T11:00:00Z");

    internal static FileRowView Row(string path, CheckoutView? checkout = null, bool inArmory = true) =>
        new(inArmory ? Guid.NewGuid().ToString() : null, path[(path.LastIndexOf('/') + 1)..], path, FileStatuses.Synced, checkout ?? Available, false, false, null, null, null, false);

    // A signed-in view of these files, grouped into projects by their first folder.
    internal static AgentView View(IEnumerable<FileRowView> rows, string connection = Connections.SignedIn, bool canTakeBack = true)
    {
        var all = rows.ToArray();
        var projects = all.GroupBy(r => r.Path.Split('/')[0]).Select(p => new ProjectView(Guid.NewGuid().ToString(), p.Key, false, canTakeBack ? "mentor" : "student", canTakeBack,
            p.GroupBy(r => FolderIn(r.Path)).Select(f => new FolderView(f.Key, f.Key.Length == 0 ? p.Key : f.Key[(f.Key.LastIndexOf('/') + 1)..], f.Count(), f.ToArray())).ToArray(),
            2025, 0)).ToArray();
        var mine = all.Where(r => r.Checkout.State == CheckoutStates.Mine).Select(r => new MyFileView(r.FileId, r.Path, r.Name, r.Path.Split('/')[0], r.Status, null, r.Checkout)).ToArray();
        return new AgentView(connection, new ConnectView("idle", null), connection == Connections.SignedOut ? null : new AccountView("alex.kim@students.test", "LAB-PC-01"),
            new SyncView(SyncStates.Synced, "Everything is saved to Armory.", null, 0), new ActivityView(null, null, null, null, null, [], []), Root, [], null, mine, projects,
            new SettingsView(Root, true, "system"), "idea");
    }

    internal static AgentView View(params FileRowView[] rows) => View(rows, Connections.SignedIn);

    private static string FolderIn(string path)
    {
        var parts = path.Split('/');
        return string.Join('/', parts[1..^1]);
    }

    internal static ShellBatch Batch(ShellVerb verb, params string[] paths) => new(verb, paths, At, At);

    internal static void WaitUntil(Func<bool> done, int seconds = 10)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(10);
        Assert.True(done(), "Timed out.");
    }
}

internal sealed class FakeShellHost(AgentView view) : IShellHost
{
    internal readonly TaskCompletionSource StartedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ConcurrentQueue<string> Calls = new();
    internal ActionResult Result { get; set; } = new(true, "Done.");
    internal AgentView CurrentView { get; set; } = view;

    internal FakeShellHost Ready()
    {
        StartedSource.TrySetResult();
        return this;
    }

    public Task Started => StartedSource.Task;
    public string VaultRoot => ShellViews.Root;
    public AgentView View => CurrentView;

    private Task<ActionResult> Call(string what, IEnumerable<string> paths)
    {
        Calls.Enqueue(what + ": " + string.Join(" | ", paths));
        return Task.FromResult(Result);
    }

    public Task<ActionResult> CheckOutAsync(IReadOnlyList<string> paths, bool open) => Call(open ? "check out and open" : "check out", paths);
    public Task<ActionResult> CheckInAsync(IReadOnlyList<string> paths) => Call("check in", paths);
    public Task<ActionResult> UndoCheckOutAsync(IReadOnlyList<string> paths) => Call("undo", paths);
    public Task<ActionResult> TakeBackAsync(IReadOnlyList<Guid> fileIds) => Call("force check in", fileIds.Select(i => i.ToString()));
    public Task<ActionResult> CheckOutAndReopenAsync(IReadOnlyList<string> paths) => Call("check out and reopen", paths);
    public Task<ActionResult> AnswerSaveDownAsync(string path, bool keepLocal) => Call(keepLocal ? "keep local" : "save in the pinned year", [path]);
    public bool PickerShowing { get; set; }
}

internal sealed class FakeSurface : IShellSurface
{
    internal readonly ConcurrentQueue<string> Shown = new();
    internal readonly ConcurrentQueue<ShellQuestion> Questions = new();
    internal readonly ConcurrentQueue<ActionResult> Answers = new();
    internal bool Confirm { get; set; } = true;

    public void OpenWindow() => Shown.Enqueue("window");
    public void Reveal(string vaultPath) => Shown.Enqueue("reveal " + vaultPath);
    public Task<bool> ConfirmAsync(ShellQuestion question)
    {
        Questions.Enqueue(question);
        return Task.FromResult(Confirm);
    }
    public void Answer(ActionResult result) => Answers.Enqueue(result);
}

// docs/agent/EXPLORER.md 1.2: what each right-click item and each notification link does, the
// questions asked first, and the one sentence that answers.
public sealed class ShellDeskTests
{
    private const string Plate = "Robot 2027/Drivetrain/Plate.SLDPRT";
    private const string Gear = "Robot 2027/Drivetrain/Gear.SLDPRT";
    private const string Hub = "Robot 2027/Drivetrain/Hub/Hub.SLDPRT";
    private static string Full(string path) => ShellViews.Root + "\\" + path.Replace('/', '\\');

    private static async Task<(FakeShellHost Host, FakeSurface Surface, ShellDesk Desk)> Run(AgentView view, ShellBatch batch, bool confirm = true, ShellDesk? desk = null)
    {
        var host = new FakeShellHost(view).Ready();
        var surface = new FakeSurface { Confirm = confirm };
        desk ??= new ShellDesk(_ => { });
        await desk.RunAsync(batch, host, surface);
        return (host, surface, desk);
    }

    [Fact]
    public async Task Each_item_goes_to_its_engine_action_with_vault_paths_and_its_sentence_is_shown()
    {
        var view = ShellViews.View(ShellViews.Row(Plate), ShellViews.Row(Gear, ShellViews.Mine));
        foreach (var (verb, call) in new[]
        {
            (ShellVerb.CheckOut, "check out: " + Plate + " | " + Gear),
            (ShellVerb.CheckIn, "check in: " + Plate + " | " + Gear),
            (ShellVerb.Undo, "undo: " + Plate + " | " + Gear),
        })
        {
            var (host, surface, _) = await Run(view, ShellViews.Batch(verb, Full(Plate), Full(Gear)));
            Assert.Equal([call], host.Calls);
            Assert.Equal("Done.", Assert.Single(surface.Answers).Message);
            Assert.Empty(surface.Questions);
        }
        var (opened, openedSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOutAndOpen, Full(Plate)));
        Assert.Equal(["check out and open: " + Plate], opened.Calls);
        Assert.Single(openedSurface.Answers);
    }

    [Fact]
    public async Task A_path_outside_the_Armory_folder_is_refused_with_one_sentence()
    {
        var view = ShellViews.View(ShellViews.Row(Plate));
        foreach (var outside in new[] { @"C:\Users\alex\Documents\Plate.SLDPRT", @"C:\IDEA\ArmoryOld\Plate.SLDPRT", Full("Robot 2027/~$Plate.SLDPRT"), Full("Robot 2027/.armory/state.json") })
        {
            var (host, surface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOut, Full(Plate), outside));
            Assert.Empty(host.Calls);
            Assert.Equal(new ActionResult(false, "That isn't in the Armory folder."), Assert.Single(surface.Answers));
        }
    }

    [Fact]
    public async Task Not_signed_in_opens_the_window_on_connect_and_says_connect_first()
    {
        var view = ShellViews.View([ShellViews.Row(Plate)], Connections.SignedOut);
        foreach (var verb in new[] { ShellVerb.CheckOut, ShellVerb.CheckIn, ShellVerb.Show, ShellVerb.ForceCheckIn })
        {
            var (host, surface, _) = await Run(view, ShellViews.Batch(verb, Full(Plate)));
            Assert.Empty(host.Calls);
            Assert.Equal(["window"], surface.Shown);
            Assert.Equal(new ActionResult(false, "Connect this computer first."), Assert.Single(surface.Answers));
        }
    }

    [Fact]
    public async Task Check_out_of_a_folder_asks_first_and_cancel_checks_out_nothing()
    {
        var view = ShellViews.View(ShellViews.Row(Plate), ShellViews.Row(Gear, ShellViews.HeldBy("Maria Lopez")), ShellViews.Row(Hub));
        var (refused, refusedSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOut, Full("Robot 2027/Drivetrain")), confirm: false);
        Assert.Empty(refused.Calls);
        Assert.Empty(refusedSurface.Answers);
        var question = Assert.Single(refusedSurface.Questions);
        Assert.Equal("Check out all", question.Title);
        Assert.Equal("Check out 2 files in Drivetrain and its folders? Nobody else can save them until you check them in. " +
            "1 other file is checked out by someone else, and stays with them.", question.Text);
        Assert.Equal("Check out 2 files", question.Ok);
        Assert.False(question.Warning);

        var (done, doneSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOut, Full("Robot 2027/Drivetrain")));
        Assert.Equal(["check out: Robot 2027/Drivetrain"], done.Calls);
        Assert.Single(doneSurface.Answers);
        // Check out and open on a folder opens nothing, and asks the same.
        var (folderOpen, folderOpenSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOutAndOpen, Full("Robot 2027/Drivetrain/Hub")));
        Assert.Equal(["check out: Robot 2027/Drivetrain/Hub"], folderOpen.Calls);
        Assert.Equal("Check out 1 file in Hub? Nobody else can save it until you check it in.", Assert.Single(folderOpenSurface.Questions).Text);
    }

    [Fact]
    public async Task Files_alone_and_a_folder_with_nothing_to_check_out_never_ask()
    {
        var view = ShellViews.View(ShellViews.Row(Plate), ShellViews.Row(Gear, ShellViews.HeldBy("Maria Lopez")));
        var (files, filesSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckOut, Full(Plate), Full(Gear)));
        Assert.Empty(filesSurface.Questions);
        Assert.Single(files.Calls);
        var held = ShellViews.View(ShellViews.Row(Gear, ShellViews.HeldBy("Maria Lopez")));
        var (folder, folderSurface, _) = await Run(held, ShellViews.Batch(ShellVerb.CheckOut, Full("Robot 2027/Drivetrain")));
        Assert.Empty(folderSurface.Questions);
        Assert.Equal(["check out: Robot 2027/Drivetrain"], folder.Calls);
    }

    [Fact]
    public async Task While_the_picker_shows_nothing_acts_and_the_window_opens_on_it()
    {
        // A shared computer (docs/agent/PROFILES.md, F7): a right-click or a notification's button
        // never acts in the name of whoever used Armory last.
        var view = ShellViews.View(ShellViews.Row(Plate), ShellViews.Row(Gear, ShellViews.Mine));
        foreach (var verb in new[] { ShellVerb.CheckOut, ShellVerb.CheckOutAndOpen, ShellVerb.CheckIn, ShellVerb.Undo, ShellVerb.ForceCheckIn, ShellVerb.Show, ShellVerb.Uri })
        {
            var host = new FakeShellHost(view).Ready();
            host.PickerShowing = true;
            var surface = new FakeSurface();
            await new ShellDesk(_ => { }).RunAsync(ShellViews.Batch(verb, verb == ShellVerb.Uri ? "idea-armory:act?t=AAAAAAAAAAAAAAAAAAAAAA&a=checkout" : Full(Plate)), host, surface);
            Assert.Empty(host.Calls);
            Assert.Empty(surface.Questions);
            Assert.Equal(["window"], surface.Shown);
            Assert.Equal(new ActionResult(false, "Pick who you are in Armory first, then try again."), Assert.Single(surface.Answers));
        }
    }

    [Fact]
    public async Task Show_in_armory_reveals_the_vault_path_and_the_armory_folder_is_home()
    {
        var view = ShellViews.View(ShellViews.Row(Plate));
        var (_, file, _) = await Run(view, ShellViews.Batch(ShellVerb.Show, Full(Plate)));
        Assert.Equal(["reveal " + Plate], file.Shown);
        var (_, root, _) = await Run(view, ShellViews.Batch(ShellVerb.Show, ShellViews.Root + "\\"));
        Assert.Equal(["reveal "], root.Shown);
        Assert.Empty(root.Answers);
    }

    [Fact]
    public async Task Check_in_on_the_armory_folder_itself_checks_in_every_file_checked_out_here()
    {
        var view = ShellViews.View(ShellViews.Row(Plate, ShellViews.Mine), ShellViews.Row(Gear), ShellViews.Row(Hub, ShellViews.Mine));
        var (host, _, _) = await Run(view, ShellViews.Batch(ShellVerb.CheckIn, ShellViews.Root));
        Assert.Equal(["check in: " + Plate + " | " + Hub], host.Calls);
        var (none, noneSurface, _) = await Run(ShellViews.View(ShellViews.Row(Gear)), ShellViews.Batch(ShellVerb.CheckIn, ShellViews.Root));
        Assert.Empty(none.Calls);
        Assert.Equal("Nothing there is checked out by you.", Assert.Single(noneSurface.Answers).Message);
        // Nothing else acts on the whole folder at once.
        var (whole, wholeSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.Undo, ShellViews.Root));
        Assert.Empty(whole.Calls);
        Assert.Equal("Pick files or folders inside a project for that.", Assert.Single(wholeSurface.Answers).Message);
    }

    [Fact]
    public async Task Force_check_in_takes_back_only_files_someone_else_has_and_asks_first_naming_them()
    {
        var maria = ShellViews.Row(Plate, ShellViews.HeldBy("Maria Lopez"));
        var other = ShellViews.Row(Hub, ShellViews.OnMyOther());
        var view = ShellViews.View(maria, ShellViews.Row(Gear), other, ShellViews.Row("Robot 2027/Arm/Arm.SLDPRT", ShellViews.HeldBy("Sam Lee")));
        var (refused, refusedSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.ForceCheckIn, Full("Robot 2027/Drivetrain")), confirm: false);
        Assert.Empty(refused.Calls);
        var question = Assert.Single(refusedSurface.Questions);
        Assert.Equal("Force check in all", question.Title);
        Assert.Equal("Force check in 2 files? Maria Lopez and Alex Kim have them checked out now. Any changes anyone hasn't checked in are kept as their own copy " +
            "in each file's history, so nothing is lost. Then anyone can check them out.", question.Text);
        Assert.Equal("Force check in 2 files", question.Ok);
        Assert.True(question.Warning);

        var (done, doneSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.ForceCheckIn, Full("Robot 2027/Drivetrain")));
        Assert.Equal(["force check in: " + maria.FileId + " | " + other.FileId], done.Calls);
        Assert.Single(doneSurface.Answers);

        var (one, oneSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.ForceCheckIn, Full(Plate)));
        Assert.Equal("Force check in Plate.SLDPRT? Maria Lopez has it checked out now. Any changes Maria hasn't checked in are kept as Maria's own copy " +
            "in the file's history, so nothing is lost. Then anyone can check it out.", Assert.Single(oneSurface.Questions).Text);
        Assert.Equal(["force check in: " + maria.FileId], one.Calls);
    }

    [Fact]
    public async Task Force_check_in_with_nothing_held_or_no_right_says_so_without_asking()
    {
        var (nothing, nothingSurface, _) = await Run(ShellViews.View(ShellViews.Row(Plate), ShellViews.Row(Gear, ShellViews.Mine)), ShellViews.Batch(ShellVerb.ForceCheckIn, Full("Robot 2027")));
        Assert.Empty(nothing.Calls);
        Assert.Empty(nothingSurface.Questions);
        Assert.Equal("None of those files is checked out by someone else now.", Assert.Single(nothingSurface.Answers).Message);
        var student = ShellViews.View([ShellViews.Row(Plate, ShellViews.HeldBy("Maria Lopez"))], canTakeBack: false);
        var (refused, refusedSurface, _) = await Run(student, ShellViews.Batch(ShellVerb.ForceCheckIn, Full(Plate)));
        Assert.Empty(refused.Calls);
        Assert.Equal("Only a mentor or CAD lead can force a check in.", Assert.Single(refusedSurface.Answers).Message);
    }

    [Fact]
    public async Task A_notification_link_checks_out_once_and_an_unknown_one_only_opens_the_window()
    {
        var desk = new ShellDesk(_ => { });
        var view = ShellViews.View(ShellViews.Row(Plate));
        var token = desk.Tokens.Issue(ProtocolLink.CheckOut, [Plate]);
        var link = ProtocolLink.Format(token, ProtocolLink.CheckOut);
        var (host, surface, _) = await Run(view, ShellViews.Batch(ShellVerb.Uri, link), desk: desk);
        Assert.Equal(["check out and reopen: " + Plate], host.Calls);
        Assert.Single(surface.Answers);
        // Used: the same link again, an unknown token, a malformed link and a show link only
        // open the window.
        var show = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.Show, [Plate]), ProtocolLink.Show);
        foreach (var again in new[] { link, ProtocolLink.Format("AAAAAAAAAAAAAAAAAAAAAA", ProtocolLink.CheckOut), "idea-armory:act?t=" + token + "&a=checkout&x=1", "idea-armory:", show })
        {
            var (none, noneSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.Uri, again), desk: desk);
            Assert.Empty(none.Calls);
            Assert.Equal(["window"], noneSurface.Shown);
            Assert.Empty(noneSurface.Answers);
        }
        // Signed out, a check out link opens the window on Connect.
        var signedOut = ShellViews.View([ShellViews.Row(Plate)], Connections.SignedOut);
        var fresh = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.CheckOut, [Plate]), ProtocolLink.CheckOut);
        var (outHost, outSurface, _) = await Run(signedOut, ShellViews.Batch(ShellVerb.Uri, fresh), desk: desk);
        Assert.Empty(outHost.Calls);
        Assert.Equal("Connect this computer first.", Assert.Single(outSurface.Answers).Message);
    }

    // The question before a save down: its two buttons answer for the one file it named, once;
    // a check out token never answers a save down, and nothing acts while the picker shows.
    [Fact]
    public async Task A_save_down_notification_answers_save_in_the_year_or_keep_here_once()
    {
        var desk = new ShellDesk(_ => { });
        var view = ShellViews.View(ShellViews.Row(Plate, ShellViews.Mine));
        var save = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.SaveIn, [Plate], "s1"), ProtocolLink.SaveIn);
        var (host, surface, _) = await Run(view, ShellViews.Batch(ShellVerb.Uri, save), desk: desk);
        Assert.Equal(["save in the pinned year: " + Plate], host.Calls);
        Assert.Single(surface.Answers);
        var keep = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.KeepLocal, [Plate], "s1"), ProtocolLink.KeepLocal);
        var (kept, _, _) = await Run(view, ShellViews.Batch(ShellVerb.Uri, keep), desk: desk);
        Assert.Equal(["keep local: " + Plate], kept.Calls);
        // Used, or named with another action than it was made for: only the window.
        var checkOutToken = desk.Tokens.Issue(ProtocolLink.CheckOut, [Plate]);
        foreach (var again in new[] { save, keep, ProtocolLink.Format(checkOutToken, ProtocolLink.KeepLocal) })
        {
            var (none, noneSurface, _) = await Run(view, ShellViews.Batch(ShellVerb.Uri, again), desk: desk);
            Assert.Empty(none.Calls);
            Assert.Equal(["window"], noneSurface.Shown);
        }
        var picking = new FakeShellHost(view) { PickerShowing = true }.Ready();
        var pickSurface = new FakeSurface();
        await desk.RunAsync(ShellViews.Batch(ShellVerb.Uri, ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.SaveIn, [Plate]), ProtocolLink.SaveIn)), picking, pickSurface);
        Assert.Empty(picking.Calls);
        Assert.Equal(ShellWords.PickYourselfFirst, Assert.Single(pickSurface.Answers).Message);
    }

    [Fact]
    public void Batches_wait_for_the_tray_and_the_host_start_and_none_runs_after_close()
    {
        var log = new ConcurrentQueue<string>();
        var desk = new ShellDesk(log.Enqueue);
        desk.Receive(ShellViews.Batch(ShellVerb.CheckIn, Full(Plate)));
        var host = new FakeShellHost(ShellViews.View(ShellViews.Row(Plate, ShellViews.Mine)));
        var surface = new FakeSurface();
        desk.Attach(host, surface);
        Thread.Sleep(200);
        Assert.Empty(host.Calls);
        host.Ready();
        ShellViews.WaitUntil(() => host.Calls.Count == 1);
        Assert.Contains("shell: checkin, 1 item", log);
        desk.Close();
        desk.Receive(ShellViews.Batch(ShellVerb.CheckIn, Full(Plate)));
        Thread.Sleep(200);
        Assert.Single(host.Calls);
    }

    [Fact]
    public void File_explorer_paths_become_vault_paths_and_nothing_else_does()
    {
        Assert.True(ShellPaths.TryVaultPath(@"C:\IDEA\Armory", @"C:\IDEA\Armory\Robot 2027\Plate.SLDPRT", out var path));
        Assert.Equal("Robot 2027/Plate.SLDPRT", path);
        Assert.True(ShellPaths.TryVaultPath(@"C:\IDEA\Armory\", @"c:\idea\armory\Robot 2027\Arm\", out path));
        Assert.Equal("Robot 2027/Arm", path);
        Assert.True(ShellPaths.TryVaultPath(@"C:\IDEA\Armory", @"C:\IDEA\Armory\", out path));
        Assert.Equal("", path);
        Assert.True(ShellPaths.TryVaultPath(@"C:\IDEA\Armory", "C:/IDEA/Armory/Robot 2027/Plate.SLDPRT", out path));
        Assert.Equal("Robot 2027/Plate.SLDPRT", path);
        foreach (var outside in new[] { @"C:\IDEA\ArmoryOld\Plate.SLDPRT", @"D:\IDEA\Armory\Plate.SLDPRT", @"C:\IDEA", "", @"C:\IDEA\Armory\Robot 2027\~$Plate.SLDPRT",
            @"C:\IDEA\Armory\.armory\journal.bin", @"C:\IDEA\Armory\Robot 2027\Thumbs.db", @"C:\IDEA\Armory\Robot 2027\..\..\Windows\notepad.exe" })
            Assert.False(ShellPaths.TryVaultPath(@"C:\IDEA\Armory", outside, out _), outside);
    }
}
