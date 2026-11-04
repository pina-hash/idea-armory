using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.EndToEnd.Tests;
using Armory.SolidWorks;
using Xunit.Abstractions;

namespace Armory.Agent.Tests;

public sealed class SaveBlockedTests(ITestOutputHelper output)
{
    private const string Part = "Class/Bracket.SLDPRT";

    [Theory]
    [InlineData("free", "have not checked it out")]
    [InlineData("held", "belongs to Ana Reyes")]
    [InlineData("other", "belongs to Ana Reyes")]
    [InlineData("nonmember", "project you are not on")]
    [InlineData("othercomputer", "another computer")]
    [InlineData("archived", "does not allow you to edit")]
    public async Task Real_engine_resolves_the_reason_from_its_state_without_network_or_a_pass(string reason, string words)
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, reason, link);
        var full = app.Engine.FullPathOf(Part);
        link.Send(new LinkOpened(42, full, SolidWorksDocTypes.Part, true, false, false, DateTimeOffset.UtcNow));
        await app.Engine.LinkSettledAsync();
        Assert.DoesNotContain(app.Engine.View.Notices, n => n.Kind == "saveBlocked"); // opening is not saving
        var timer = Stopwatch.StartNew();
        var asks = new SaveBlockedAsks();
        link.Send(new LinkSaving(42, full, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        var card = Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        var toast = Assert.Single(asks.Update(app.Engine.View).Show);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        output.WriteLine($"{reason}: save record to Home card + notification plan {timer.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Contains(words, card.Detail);
        Assert.Equal(card, toast.Card);
        Assert.Equal("Save a copy in your folder", card.Action!.Label); // offline start: no speculative checkout
        Assert.Equal("copy", (await app.Engine.SaveBlockedActionAsync(card.Key))!.Command);
        Assert.Empty(asks.Update(app.Engine.View).Show);
        for (var i = 0; i < 20; i++) link.Send(new LinkSaving(42, full, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.Equal(card.Key, Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked").Key);
        Assert.Empty(asks.Update(app.Engine.View).Show);
        Assert.Equal(0, app.Network.Calls);
        Assert.Empty(app.Snapshots.All());
        Assert.Empty(link.Commands);
        // A failed/canceled save keeps its explanation; a later successful save removes it.
        link.Send(new LinkSaveCanceled(42, full, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        link.Send(new LinkSaved(42, full, Guid.NewGuid(), 1, null));
        await app.Engine.LinkSettledAsync();
        Assert.DoesNotContain(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        Assert.Null(await app.Engine.SaveBlockedActionAsync(card.Key));
        Assert.Single(asks.Update(app.Engine.View).Withdraw);
    }

    [Theory]
    [InlineData("mine")]
    [InlineData("unknown")]
    [InlineData("unmanaged")]
    public async Task Writable_unknown_and_unmanaged_files_never_get_a_save_card(string reason)
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, reason, link);
        foreach (var path in new[] { app.Engine.FullPathOf(Part), app.Engine.FullPathOf("Class/~$Bracket.SLDPRT"), app.Engine.FullPathOf("Class/Bracket.swp"), "C:\\Elsewhere\\Bracket.SLDPRT" })
            link.Send(new LinkSaving(42, path, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.DoesNotContain(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        Assert.Equal(0, app.Network.Calls);
    }

    [Fact]
    public async Task Closed_documents_and_signed_out_students_leave_no_action_for_an_old_notification()
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, "held", link);
        var full = app.Engine.FullPathOf(Part);
        link.Send(new LinkSaving(42, full, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        var key = Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked").Key;
        link.Send(new LinkClosed(42, full));
        await app.Engine.LinkSettledAsync();
        Assert.Null(await app.Engine.SaveBlockedActionAsync(key));
        link.Send(new LinkSaving(42, full, Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        key = Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked").Key;
        app.Sessions.SignOut();
        Assert.Null(await app.Engine.SaveBlockedActionAsync(key));
    }

    [Fact]
    public async Task A_stalled_server_read_does_not_delay_the_save_explanation()
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, "free", link);
        app.Network.Stall = true;
        app.Engine.Start();
        await app.Network.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timer = Stopwatch.StartNew();
        link.Send(new LinkSaving(42, app.Engine.FullPathOf(Part), Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        output.WriteLine($"Stalled server read: save record to Home card {timer.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public async Task A_save_notification_routes_its_single_use_card_key_without_directly_checking_out()
    {
        var desk = new ShellDesk(_ => { });
        var host = new FakeShellHost(ShellViews.View()).Ready(); var surface = new FakeSurface();
        var key = "saveBlocked:" + Guid.NewGuid().ToString("N");
        var link = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.SaveBlocked, [key]), ProtocolLink.SaveBlocked);
        Assert.True(ProtocolLink.TryParse(link, out _));
        await desk.RunAsync(ShellViews.Batch(ShellVerb.Uri, link), host, surface);
        Assert.Equal(["blocked save: " + key], host.Calls);
        await desk.RunAsync(ShellViews.Batch(ShellVerb.Uri, link), host, surface);
        Assert.Single(host.Calls);
        var otherAction = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.SaveBlocked, [key]), ProtocolLink.CheckOut);
        await desk.RunAsync(ShellViews.Batch(ShellVerb.Uri, otherAction), host, surface);
        Assert.Single(host.Calls);
        host.PickerShowing = true;
        var picking = ProtocolLink.Format(desk.Tokens.Issue(ProtocolLink.SaveBlocked, [key]), ProtocolLink.SaveBlocked);
        await desk.RunAsync(ShellViews.Batch(ShellVerb.Uri, picking), host, surface);
        Assert.Single(host.Calls);
    }

    [Fact]
    public void Copy_folders_are_per_student_and_never_copy_or_modify_a_CAD_file()
    {
        using var temp = new TempFolder();
        var a = StudentCopyFolder.Open(temp.Root, "Alex@school.test");
        var b = StudentCopyFolder.Open(temp.Root, "ana@school.test");
        Assert.NotEqual(a, b);
        Assert.Equal(a, StudentCopyFolder.Open(temp.Root, " alex@school.test "));
        Assert.StartsWith(Path.Combine(temp.Root, "Armory copies") + Path.DirectorySeparatorChar, a);
        Assert.Empty(Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories));
        Assert.StartsWith("Student-", StudentCopyFolder.Name("../../elsewhere"));
        Assert.Throws<IOException>(() => StudentCopyFolder.Open("", "alex@school.test"));
    }

    // Windows only: real NTFS protection, a separate COM process, production SolidWorks link,
    // production engine, Home model and Windows notification plan. The child's existing save
    // fixture fires FileSaveNotify then AppendAllText; this is NOT a real SolidWorks trace.
    [WindowsFact]
    public async Task Windows_read_only_save_fixture_reaches_Home_and_notification_without_changing_the_original()
    {
        foreach (var (reason, words) in new[] { ("free", "have not checked it out"), ("held", "belongs to Ana Reyes"), ("nonmember", "project you are not on") })
        {
            using var temp = new TempFolder();
            var target = temp.File(Part); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "original fixture bytes");
            using var child = new ComSaveChild();
            await child.ReadyAsync();
            await using var link = new SolidWorksLink(new SolidWorksLinkOptions { VaultRoot = temp.Root, Candidates = () => [child.Pid], DiscoveryInterval = TimeSpan.FromMilliseconds(50) });
            await using var app = new Fixture(temp.Root, reason, link, windows: true);
            link.Start();
            await Until(() => app.Engine.View.SolidWorks?.State == SolidWorksStates.Attached);
            child.Send("open \"" + target + "\"");
            await child.UntilLine(s => s.StartsWith("advise 83a33d32", StringComparison.OrdinalIgnoreCase));
            await app.Engine.LinkSettledAsync();
            var disk = (WindowsVaultFileSystem)app.Files;
            disk.ApplyLockAttribute(TempFolder.PathValue(Part), LockOwnership.OtherPerson);
            Assert.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
            await Task.Delay(250); // drain the successful attribute-change hint before the refused write
            var hints = disk.HintCount;
            var timer = Stopwatch.StartNew();
            var posted = new TaskCompletionSource<(NoticeGroupView Card, string Tag)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asks = new SaveBlockedAsks();
            app.Engine.ViewChanged += view => { foreach (var (tag, card) in asks.Update(view).Show) posted.TrySetResult((card, tag)); };
            child.Send("save \"" + target + "\"");
            var notice = await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var elapsed = timer.Elapsed;
            await child.UntilLine(s => s.StartsWith("error save", StringComparison.Ordinal) && s.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
            Assert.Contains(words, notice.Card.Detail);
            Assert.True(elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal("original fixture bytes", File.ReadAllText(target));
            Assert.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
            await Task.Delay(250);
            Assert.Equal(hints, disk.HintCount); // denied writes do not produce a watcher save event
            Assert.Equal(0, app.Network.Calls);
            Assert.Empty(app.Snapshots.All());
            output.WriteLine($"Windows {reason}: child save request to Home + notification plan {elapsed.TotalMilliseconds:F2} ms; refused-write watcher hints {disk.HintCount - hints}; original bytes and protection unchanged.");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition()) { Assert.True(time.Elapsed < TimeSpan.FromSeconds(20), "Timed out waiting for the Windows COM fixture."); await Task.Delay(10); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly RefuseNetwork Network = new();
        internal readonly MemorySnapshotStore Snapshots = new();
        internal readonly SessionManager Sessions;
        internal readonly SyncEngine Engine;
        internal readonly IVaultFileSystem Files;
        private readonly HttpClient http;
        internal Fixture(string root, string reason, ISolidWorksLink link, bool windows = false)
        {
            http = new(Network);
            Sessions = new(http, new InMemorySecretStore());
            var id = Guid.NewGuid(); var project = Guid.NewGuid();
            Sessions.SignIn(new("http://fixture.test", "anon", "access", "refresh", DateTimeOffset.UtcNow.AddYears(1), "alex.kim@school.test", id, "IDEA-01"));
            var state = new EngineState { Email = Sessions.Current!.Email, DeviceId = id };
            state.Projects[project] = new ProjectState { Id = project, Name = "Class", Folder = "Class", Departed = reason == "nonmember", Usable = reason != "nonmember", Archived = reason == "archived" };
            var st = new FileState { Path = Part, ProjectId = project, FileId = Guid.NewGuid(), BaseId = "v1", BaseHash = "hash", AppliedOwnership = LockOwnership.Free };
            if (reason is "held" or "other") st.Holder = new("ana.reyes@school.test", Guid.NewGuid(), "IDEA-02", DateTimeOffset.UtcNow);
            if (reason == "othercomputer") st.Holder = new(state.Email, Guid.NewGuid(), "IDEA-02", DateTimeOffset.UtcNow);
            if (reason == "mine") st.AppliedOwnership = LockOwnership.ThisDevice;
            if (reason == "unknown") st.AppliedOwnership = null;
            if (reason == "held") { var held = new HeldWork(); held.Files[Part] = st; state.Held["ana.reyes@school.test"] = held; }
            else if (reason != "unmanaged") state.Files[Part] = st;
            var store = new MemoryStateStore(); store.Save(state.Serialize());
            Files = windows ? new WindowsVaultFileSystem(root) : new PortableVaultFileSystem(root);
            Engine = new(new EngineOptions { VaultRoot = root }, new EngineDependencies { Files = Files, State = store, Journal = new MemoryJournalStore(), Snapshots = Snapshots,
                Sessions = Sessions, Api = new(new PostgrestClient(http, Sessions)), Blobs = new(http, http, new Uri("http://fixture.test"), Sessions), SolidWorks = link });
        }
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); (Files as IDisposable)?.Dispose(); http.Dispose(); }
    }

    private sealed class RefuseNetwork : HttpMessageHandler
    {
        internal int Calls;
        internal bool Stall;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Entered.TrySetResult();
            if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new HttpRequestException("This test must not use the network.");
        }
    }

    private sealed class RecordsLink : ISolidWorksLink
    {
        private readonly Channel<LinkRecord> channel = Channel.CreateUnbounded<LinkRecord>();
        internal readonly List<string> Commands = [];
        public ChannelReader<LinkRecord> Records => channel.Reader;
        internal void Send(LinkRecord record) => channel.Writer.TryWrite(record);
        public Task<WritableOutcome> MakeWritableAsync(string path, CancellationToken ct) { Commands.Add(path); return Task.FromResult(WritableOutcome.Refused); }
        public Task<SaveDownOutcome> SaveInPinnedReleaseAsync(string path, CancellationToken ct) { Commands.Add(path); return Task.FromResult(SaveDownOutcome.ReadOnly); }
        public void SetPins(IReadOnlyList<ProjectPin> pins) { }
        public void KeepLocal(string path, bool keep) => Commands.Add(path);
        public void StatusText(string text) { }
        public void Replay() { }
    }

    private sealed class ComSaveChild : IDisposable
    {
        private readonly Process process;
        private readonly ConcurrentQueue<string> lines = new();
        internal ComSaveChild()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Armory.FakeSolidWorks.dll"));
            start.ArgumentList.Add("--start-after"); start.ArgumentList.Add("0");
            process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) lines.Enqueue(line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) lines.Enqueue(line); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        internal int Pid => process.Id;
        internal Task ReadyAsync() => UntilLine(s => s.StartsWith("ready ", StringComparison.Ordinal));
        internal void Send(string command) { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
        internal async Task UntilLine(Func<string, bool> match)
        {
            var time = Stopwatch.StartNew();
            while (!lines.Any(match)) { Assert.True(time.Elapsed < TimeSpan.FromSeconds(20), string.Join("\n", lines)); await Task.Delay(10); }
        }
        public void Dispose() { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
    }
}
