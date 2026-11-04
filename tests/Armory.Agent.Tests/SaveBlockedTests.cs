using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.Core;
using Armory.EndToEnd.Tests;
using Armory.Platform.Windows;
using Armory.SolidWorks;
using Xunit.Abstractions;

namespace Armory.Agent.Tests;

public sealed class SaveBlockedTests(ITestOutputHelper output)
{
    private const string Part = "Class/Bracket.SLDPRT";

    [Theory]
    [InlineData("free", "have not checked out this file")]
    [InlineData("held", "Ana Reyes has this file")]
    [InlineData("other", "Ana Reyes has this file")]
    [InlineData("nonmember", "project you are not on")]
    [InlineData("othercomputer", "another computer")]
    [InlineData("archived", "does not let you change")]
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
    public async Task Cached_ownership_does_not_accuse_a_file_whose_bit_was_cleared_or_whose_path_is_now_empty()
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, "free", link);
        var disk = (PortableVaultFileSystem)app.Files;
        disk.ClearReadOnly(Part);
        link.Send(new LinkSaving(42, app.Engine.FullPathOf(Part), Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.DoesNotContain(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
        disk.ApplyLockAttribute(TempFolder.PathValue(Part), LockOwnership.Free);
        File.Delete(disk.Full(Part));
        link.Send(new LinkSaving(42, app.Engine.FullPathOf(Part), Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        Assert.DoesNotContain(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
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
    public async Task Changed_words_or_actions_refresh_the_card_without_a_second_notification_in_the_burst()
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, "free", link);
        link.Send(new LinkSaving(42, app.Engine.FullPathOf(Part), Guid.NewGuid(), LinkSaveMode.Current));
        await app.Engine.LinkSettledAsync();
        var view = app.Engine.View;
        var card = Assert.Single(view.Notices, n => n.Kind == "saveBlocked");
        var asks = new SaveBlockedAsks();
        var first = Assert.Single(asks.Update(view).Show);
        // Going online can enable checkout; ownership can change while the card is standing.
        var refreshed = card with { Detail = "The current protection reason changed.", Action = card.Action! with { Label = "Check out" } };
        view = view with { Notices = [refreshed] };
        Assert.Empty(asks.Update(view).Show);
        Assert.Single(view.Notices);
        var next = refreshed with { Key = "saveBlocked:" + Guid.NewGuid().ToString("N") };
        var second = Assert.Single(asks.Update(view with { Notices = [next] }).Show);
        Assert.Equal(first.Tag, second.Tag);
        Assert.Equal(next.Key, second.Card.Key);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("held")]
    [InlineData("nonmember")]
    public async Task Two_hundred_save_bursts_each_have_exactly_one_card_and_notification(string reason)
    {
        using var temp = new TempFolder(); var link = new RecordsLink();
        await using var app = new Fixture(temp.Root, reason, link);
        using var notices = new SaveNotices(app.Engine);
        string? previous = null;
        var tags = new HashSet<string>();
        for (var burst = 1; burst <= 200; burst++)
        {
            var id = Guid.NewGuid();
            var attempt = new LinkSaving(42, app.Engine.FullPathOf(Part), id, LinkSaveMode.Current);
            link.Send(attempt);
            var posted = await notices.NextAsync();
            tags.Add(posted.Tag);
            await app.Engine.LinkSettledAsync();
            var card = Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
            Assert.NotEqual(previous, card.Key);
            Assert.Equal(card.Key, posted.Card.Key);
            // Duplicate delivery, two link processes, and retries immediately before the
            // boundary all belong to this sliding burst, even beyond its original start + 3s.
            link.Send(attempt);
            for (var retry = 0; retry < 2; retry++)
            {
                app.Clock.Advance(SaveBlockedDetector.Burst - TimeSpan.FromTicks(1));
                link.Send(attempt with { ProcessId = 43, SaveId = Guid.NewGuid() });
                await app.Engine.LinkSettledAsync();
                Assert.Equal(card.Key, Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked").Key);
                Assert.Equal(burst, notices.Posted.Count);
            }
            previous = card.Key;
            app.Clock.Advance(SaveBlockedDetector.Burst + TimeSpan.FromTicks(burst % 2));
        }
        Assert.Equal(200, notices.Posted.Count);
        Assert.Single(tags);
        Assert.Equal("original fixture bytes", File.ReadAllText(temp.File(Part)));
        Assert.Equal(0, app.Network.Calls);
        Assert.Empty(app.Snapshots.All());
        Assert.Empty(link.Commands);
        output.WriteLine($"{reason}: 200 bursts, 800 save records, 200 notification plans, one standing card and one stable tag; exact 3s and 3s + 1 tick boundaries.");
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
        foreach (var (reason, words) in new[] { ("free", "have not checked out this file"), ("held", "Ana Reyes has this file"), ("nonmember", "project you are not on") })
            await WindowsSaveBurstsAsync(reason, words, 1);
    }

    [WindowsFact]
    public Task Windows_two_hundred_read_only_save_bursts_each_notify_once()
        => WindowsSaveBurstsAsync("free", "have not checked out this file", 200);

    private async Task WindowsSaveBurstsAsync(string reason, string words, int bursts)
    {
        using var temp = new TempFolder();
        var target = temp.File(Part); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "original fixture bytes");
        // Establish intent and protection BEFORE the adapter's watcher exists. A sleep
        // cannot drain queued attribute hints, and those hints are not refused writes.
        using (var policy = new ReadOnlyPolicy(new WindowsPaths(temp.Root)))
            policy.Apply(TempFolder.PathValue(Part), LockOwnership.OtherPerson);
        using var child = new ComSaveChild();
        await child.ReadyAsync();
        await using var link = new SolidWorksLink(new SolidWorksLinkOptions { VaultRoot = temp.Root, Candidates = () => [child.Pid], DiscoveryInterval = TimeSpan.FromMilliseconds(50) });
        await using var app = new Fixture(temp.Root, reason, link, windows: true);
        link.Start();
        await WaitForViewAsync(app.Engine, view => view.SolidWorks?.State == SolidWorksStates.Attached);
        child.Send("open \"" + target + "\"");
        await child.UntilLine(s => s.StartsWith("event FileOpenPostNotify", StringComparison.Ordinal));
        // The app's callback queues discovery on the link's STA. Its return alone
        // does not mean the document's save-event sink has been advised yet.
        await child.UntilLine(s => s.StartsWith("advise 83a33d32", StringComparison.OrdinalIgnoreCase), includeHistory: true);
        await app.Engine.LinkSettledAsync();
        var disk = (WindowsVaultFileSystem)app.Files;
        Assert.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
        var hints = disk.HintCount;
        Assert.Equal(0, hints);
        using var notices = new SaveNotices(app.Engine);
        string? previous = null;
        var tags = new HashSet<string>();
        var latencies = new List<double>();
        for (var burst = 1; burst <= bursts; burst++)
        {
            var timer = Stopwatch.StartNew();
            child.Send("save \"" + target + "\"");
            var notice = await notices.NextAsync();
            var elapsed = timer.Elapsed;
            await RefusedAsync();
            Assert.Contains(words, notice.Card.Detail);
            Assert.True(elapsed < TimeSpan.FromSeconds(2));
            latencies.Add(elapsed.TotalMilliseconds);
            tags.Add(notice.Tag);
            var card = Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked");
            Assert.Equal(notice.Card.Key, card.Key);
            Assert.NotEqual(previous, card.Key);
            Assert.Equal(burst, notices.Posted.Count);
            // A real second COM save just before the boundary stays in the same burst.
            app.Clock.Advance(SaveBlockedDetector.Burst - TimeSpan.FromTicks(1));
            child.Send("save \"" + target + "\"");
            await RefusedAsync();
            Assert.Equal(card.Key, Assert.Single(app.Engine.View.Notices, n => n.Kind == "saveBlocked").Key);
            Assert.Equal(burst, notices.Posted.Count);
            Assert.Equal(2 * burst, child.SaveEvents);
            Assert.Equal("original fixture bytes", File.ReadAllText(target));
            Assert.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
            Assert.Equal(hints, disk.HintCount); // keep the original strict equality
            previous = card.Key;
            app.Clock.Advance(SaveBlockedDetector.Burst + TimeSpan.FromTicks(burst % 2));
        }
        Assert.Single(tags);
        Assert.Equal(bursts, notices.Posted.Count);
        Assert.Equal(0, app.Network.Calls);
        Assert.Empty(app.Snapshots.All());
        output.WriteLine($"Windows {reason}: {bursts} bursts, {2 * bursts} refused COM writes, {notices.Posted.Count} notifications, one standing card/tag, {disk.HintCount - hints} watcher hints; latency min {latencies.Min():F2} max {latencies.Max():F2} ms; original bytes/protection unchanged.");

        async Task RefusedAsync()
        {
            await child.UntilLine(s => s.StartsWith("error save", StringComparison.Ordinal) && s.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
            await app.Engine.LinkSettledAsync();
        }
    }

    private static async Task WaitForViewAsync(SyncEngine engine, Func<AgentView, bool> condition)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(AgentView view) { if (condition(view)) ready.TrySetResult(); }
        engine.ViewChanged += Observe;
        try { Observe(engine.View); await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { engine.ViewChanged -= Observe; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly RefuseNetwork Network = new();
        internal readonly MemorySnapshotStore Snapshots = new();
        internal readonly SessionManager Sessions;
        internal readonly SyncEngine Engine;
        internal readonly IVaultFileSystem Files;
        internal readonly SaveClock Clock = new();
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
            if (!windows)
            {
                var portable = (PortableVaultFileSystem)Files;
                var full = portable.Full(Part); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "original fixture bytes");
                portable.ApplyLockAttribute(TempFolder.PathValue(Part), reason == "mine" ? LockOwnership.ThisDevice : LockOwnership.Free);
            }
            Engine = new(new EngineOptions { VaultRoot = root }, new EngineDependencies { Files = Files, State = store, Journal = new MemoryJournalStore(), Snapshots = Snapshots,
                Sessions = Sessions, Api = new(new PostgrestClient(http, Sessions)), Blobs = new(http, http, new Uri("http://fixture.test"), Sessions), SolidWorks = link, Clock = Clock });
        }
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); (Files as IDisposable)?.Dispose(); http.Dispose(); }
    }

    private sealed class SaveClock : TimeProvider
    {
        private long ticks;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private sealed class SaveNotices : IDisposable
    {
        private readonly SyncEngine engine;
        private readonly SaveBlockedAsks asks = new();
        private readonly Channel<(string Tag, NoticeGroupView Card)> arrived = Channel.CreateUnbounded<(string, NoticeGroupView)>();
        internal readonly ConcurrentQueue<(string Tag, NoticeGroupView Card)> Posted = new();
        internal SaveNotices(SyncEngine engine) { this.engine = engine; engine.ViewChanged += Observe; Observe(engine.View); }
        private void Observe(AgentView view)
        {
            foreach (var notice in asks.Update(view).Show) { Posted.Enqueue(notice); arrived.Writer.TryWrite(notice); }
        }
        internal async Task<(string Tag, NoticeGroupView Card)> NextAsync()
            => await arrived.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public void Dispose() => engine.ViewChanged -= Observe;
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
        private readonly Channel<string> arrived = Channel.CreateUnbounded<string>();
        internal ComSaveChild()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Armory.FakeSolidWorks.dll"));
            start.ArgumentList.Add("--start-after"); start.ArgumentList.Add("0");
            process = Process.Start(start)!;
            void Receive(object sender, DataReceivedEventArgs e) { if (e.Data is { } line) { lines.Enqueue(line); arrived.Writer.TryWrite(line); } }
            process.OutputDataReceived += Receive;
            process.ErrorDataReceived += Receive;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        internal int Pid => process.Id;
        internal int SaveEvents => lines.Count(s => s.StartsWith("event FileSaveNotify", StringComparison.Ordinal));
        internal Task ReadyAsync() => UntilLine(s => s.StartsWith("ready ", StringComparison.Ordinal));
        internal void Send(string command) { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
        internal async Task UntilLine(Func<string, bool> match, bool includeHistory = false)
        {
            if (includeHistory && lines.Any(match)) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await foreach (var line in arrived.Reader.ReadAllAsync(timeout.Token)) if (match(line)) return; }
            catch (OperationCanceledException) { Assert.Fail("Timed out waiting for the COM fixture:\n" + string.Join("\n", lines)); }
        }
        public void Dispose() { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
    }
}
