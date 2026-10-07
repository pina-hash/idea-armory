using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armory.Agent.Engine.View;
using Armory.Core;

namespace Armory.Agent.Engine.Tests;

// Stage E3's pieces that need no server: the state document written in pieces (only what
// changed serialized again), ids handed out from saved blocks, the activity panel's words and
// numbers, and the engine thread.
public sealed class StateAndActivityTests
{
    private static readonly Guid Project = Guid.Parse("28906722-28e6-4337-8f24-d9c602f66c35");

    private static EngineState Sample()
    {
        var state = new EngineState { Email = "alex.kim@students.test", DeviceId = Guid.NewGuid() };
        state.Projects[Project] = new ProjectState { Id = Project, Name = "Robot 2027", Folder = "Robot 2027", Role = "student" };
        for (var i = 0; i < 300; i++)
        {
            var path = $"Robot 2027/Gearbox/Part-{i:D3}.SLDPRT";
            var st = new FileState { Path = path, ProjectId = Project, FileId = Guid.NewGuid(), BaseId = Guid.NewGuid().ToString(), BaseHash = new string('a', 64) };
            if (i % 7 == 0) st.Inflight = new Inflight("commit", Guid.NewGuid(), "e" + i, Project, st.FileId, Hash: new string('b', 64), Bytes: 12);
            if (i % 11 == 0) st.Sides.Add(new SideRecord(Guid.NewGuid(), new string('c', 64), "saved while checked out", DateTimeOffset.UnixEpoch));
            if (i % 13 == 0) st.Holder = new KnownLock("maria.lopez@students.test", Guid.NewGuid(), "LAB-PC-07", DateTimeOffset.UnixEpoch);
            st.Entries.Add($"{state.DeviceId}:save:{i}");
            state.Files[path] = st;
            state.Completed.Add($"{state.DeviceId}:done:{i}");
        }
        state.KnownFolders.Add("Robot 2027/Gearbox");
        state.Imports.Add(new ImportRecord(Guid.NewGuid(), Project, "Robot 2027/Gearbox", state.Files.Keys.Take(50).ToList(), DateTimeOffset.UnixEpoch));
        state.Dismissed["import"] = ["import:1"];
        return state;
    }

    // The pieces make the reflection serializer's document (the file records in another order).
    private static void SameDocument(EngineState state)
    {
        var pieces = JsonNode.Parse(state.Serialize());
        var whole = JsonNode.Parse(state.SerializeWhole());
        Assert.True(JsonNode.DeepEquals(pieces, whole), "the state document written in pieces differs from the whole document");
    }

    [Fact]
    public void The_state_document_written_in_pieces_is_the_whole_document()
    {
        var state = Sample();
        SameDocument(state);
        // Nothing changed: nothing to write.
        Assert.Null(state.SerializeParts(whole: false));
        // A field, a list, a record added, replaced, renamed and removed, an id, an import replaced.
        var files = state.Files.Values.ToArray();
        files[3].Attempt++;
        Assert.NotNull(state.SerializeParts(whole: false));
        SameDocument(state);
        files[40].Entries.RemoveAt(0);
        files[41].Inflight = null;
        state.Files["Robot 2027/New.SLDPRT"] = new FileState { Path = "Robot 2027/New.SLDPRT", ProjectId = Project };
        SameDocument(state);
        var moved = files[42];
        state.Files.Remove(moved.Path);
        moved.Path = "Robot 2027/Gears/Part-042.SLDPRT";
        state.Files[moved.Path] = moved;
        state.Files["Robot 2027/Gearbox/Part-010.SLDPRT"] = new FileState { Path = "Robot 2027/Gearbox/Part-010.SLDPRT", ProjectId = Project };
        state.Files.Remove("Robot 2027/Gearbox/Part-011.SLDPRT");
        state.Completed.Add("one more");
        state.Imports[0] = state.Imports[0] with { Paths = [.. state.Imports[0].Paths, "Robot 2027/New.SLDPRT"] };
        Assert.NotNull(state.SerializeParts(whole: false));
        SameDocument(state);
        Assert.Null(state.SerializeParts(whole: false));
        // And it loads back as it was.
        var store = new Store();
        store.Save(state.Serialize());
        var loaded = EngineState.Load(store);
        Assert.Equal(state.Files.Count, loaded.Files.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(loaded.SerializeWhole()), JsonNode.Parse(state.SerializeWhole())));
        // A record found by its FileId (after a load too), and not once it left.
        Assert.Same(loaded.Files[files[5].Path], loaded.FirstWithFileId(files[5].FileId!.Value));
        Assert.Same(state.Files[files[6].Path], state.FirstWithFileId(files[6].FileId!.Value));
        Assert.Same(moved, state.FirstWithFileId(moved.FileId!.Value));
        Assert.Null(state.FirstWithFileId(moved.FileId!.Value, except: moved));
        var gone = files[11].FileId!.Value;
        Assert.Null(state.FirstWithFileId(gone));
    }

    // A field added to FileState and forgotten by its change tracking would be saved late (or
    // never, until another field of the same record changed): every public property is set here.
    [Fact]
    public void Every_field_of_a_file_record_marks_it_changed()
    {
        var properties = typeof(FileState).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanWrite).ToArray();
        Assert.True(properties.Length >= 30);
        foreach (var property in properties)
        {
            var state = new EngineState();
            var st = new FileState { Path = "Robot 2027/Plate.SLDPRT" };
            state.Files[st.Path] = st;
            var before = state.Serialize();
            var version = st.Version;
            property.SetValue(st, Different(property.PropertyType, property.GetValue(st)));
            Assert.True(st.Version > version, $"{property.Name} does not mark its record changed");
            Assert.NotNull(state.SerializeParts(whole: false));
            Assert.False(before.AsSpan().SequenceEqual(state.Serialize()), property.Name);
            SameDocument(state);
        }
        // The lists' own changes.
        var lists = new EngineState();
        var file = new FileState { Path = "Robot 2027/Plate.SLDPRT" };
        lists.Files[file.Path] = file;
        foreach (var change in new Action[]
        {
            () => file.Entries.Add("e1"), () => file.Entries.Remove("e1"), () => file.Drafts.Add("d1"), () => file.Drafts.Clear(),
            () => file.Sides.Add(new SideRecord(Guid.NewGuid(), "h", "r", DateTimeOffset.UnixEpoch)), () => file.Sides.RemoveRange(0, 1),
            () => file.Entries.Add("e2"), () => file.Entries.RemoveAt(0),
        })
        {
            var version = file.Version;
            lists.Serialize();
            change();
            Assert.True(file.Version > version);
            Assert.NotNull(lists.SerializeParts(whole: false));
            SameDocument(lists);
        }
    }

    private static object? Different(Type type, object? current)
    {
        if (type == typeof(string)) return (string?)current + "x";
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(Guid?)) return Guid.NewGuid();
        if (type == typeof(int)) return (int)current! + 1;
        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(LockOwnership?)) return current is LockOwnership.ThisDevice ? LockOwnership.Free : LockOwnership.ThisDevice;
        if (type == typeof(CheckoutRequest)) return (CheckoutRequest)current! == CheckoutRequest.CheckIn ? CheckoutRequest.Undo : CheckoutRequest.CheckIn;
        if (type == typeof(KnownLock)) return new KnownLock("a@b.c", Guid.NewGuid(), "PC", DateTimeOffset.UnixEpoch);
        if (type == typeof(Inflight)) return new Inflight("lock", Guid.NewGuid());
        if (type == typeof(ChangeList<string>)) return new ChangeList<string> { "x" };
        if (type == typeof(ChangeList<SideRecord>)) return new ChangeList<SideRecord> { new(Guid.NewGuid(), "h", "r", DateTimeOffset.UnixEpoch) };
        throw new InvalidOperationException($"No different value for {type}: add one here.");
    }

    [Fact]
    public void Ids_come_from_saved_blocks_and_never_repeat_after_a_restart()
    {
        var state = new EngineState { DeviceId = Guid.NewGuid() };
        Assert.True(state.IdsRunOut);
        Assert.Throws<InvalidOperationException>(() => state.NextId("save"));
        state.ReserveIds();
        var store = new Store();
        store.Save(state.Serialize()); // the block is saved before its first id is used
        var issued = Enumerable.Range(0, 10).Select(_ => state.NextId("save")).ToList();
        Assert.Equal($"{state.DeviceId}:save:1", issued[0]);
        Assert.Equal(10, issued.Distinct().Count());
        // A crash now: the next start hands out ids after the whole saved block.
        var restarted = EngineState.Load(store);
        Assert.True(restarted.IdsRunOut);
        restarted.ReserveIds();
        var next = restarted.NextId("save");
        Assert.DoesNotContain(next, issued);
        Assert.Equal($"{state.DeviceId}:save:{EngineState.IdBlock + 1}", next);
        // A block lasts for IdBlock ids.
        var block = new EngineState { DeviceId = Guid.NewGuid() };
        block.ReserveIds();
        for (var i = 0; i < EngineState.IdBlock; i++) block.NextId("x");
        Assert.True(block.IdsRunOut);

        // A block whose save fails (the state file locked, the disk full) is given back: no id
        // comes from it, so none is handed out again after a restart. The engine reserves this
        // way (SyncEngine.NextId).
        var locked = new EngineState { DeviceId = Guid.NewGuid() };
        var disk = new Store();
        disk.Save(locked.Serialize());
        var failures = 2;
        void Save()
        {
            if (failures-- > 0) throw new IOException("The state file is locked.");
            disk.Save(locked.Serialize());
        }
        Assert.Throws<IOException>(() => locked.ReserveIds(Save));
        Assert.True(locked.IdsRunOut);
        Assert.Throws<InvalidOperationException>(() => locked.NextId("save"));
        Assert.Throws<IOException>(() => locked.ReserveIds(Save));
        Assert.Throws<InvalidOperationException>(() => locked.NextId("save"));
        Assert.Equal(0, EngineState.Load(disk).Sequence);
        locked.ReserveIds(Save); // the third save goes through
        var first = locked.NextId("save");
        Assert.Equal($"{locked.DeviceId}:save:1", first);
        var reloaded = EngineState.Load(disk);
        Assert.Equal(EngineState.IdBlock, reloaded.Sequence);
        reloaded.ReserveIds();
        Assert.NotEqual(first, reloaded.NextId("save"));
    }

    // The engine's units chain files whose names the server holds for one: its rule is
    // lower(normalize(name, NFC)) in PostgreSQL, and the key is at least as coarse.
    [Theory]
    [InlineData("Plate.SLDPRT", "PLATE.sldprt")]
    [InlineData("\u1E9Eolt.SLDPRT", "\u00DFolt.SLDPRT")] // capital and small sharp s
    [InlineData("\u0130nsert.SLDPRT", "insert.SLDPRT")] // dotted capital I and i
    [InlineData("Caf\u00E9.SLDPRT", "Cafe\u0301.SLDPRT")] // composed and decomposed
    [InlineData("\u212Aey.SLDPRT", "key.SLDPRT")] // the Kelvin sign and k
    public void Names_the_server_holds_for_one_share_a_unit_key(string one, string other)
        => Assert.Equal(SyncEngine.NameKey(one), SyncEngine.NameKey(other));

    [Fact]
    public void Different_names_keep_different_unit_keys()
        => Assert.NotEqual(SyncEngine.NameKey("Plate-1.SLDPRT"), SyncEngine.NameKey("Plate-2.SLDPRT"));

    [Fact]
    public void Completed_ids_keep_the_order_they_came_in_and_round_trip()
    {
        var ids = new IdSet { "c", "a", "b", "a" };
        Assert.Equal(["c", "a", "b"], ids);
        Assert.Equal(3, ids.Count);
        Assert.Contains("b", ids);
        var state = new EngineState { Completed = ids };
        var store = new Store();
        store.Save(state.Serialize());
        Assert.Equal(["c", "a", "b"], EngineState.Load(store).Completed);
        Assert.True(ids.Remove("a"));
        Assert.Equal(["c", "b"], ids);
        SameDocument(state);
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
        public void Advance(double seconds) => Now += (long)(seconds * 1000);
    }

    [Fact]
    public void The_activity_panel_says_what_moves_in_the_window_s_words()
    {
        var clock = new ManualClock();
        var activity = new ActivityTracker(clock);
        Assert.Null(activity.Snapshot().Line);
        const long Mb = 1024 * 1024;
        for (var i = 0; i < 9; i++) activity.Expect(Directions.Upload, $"Robot 2027/Part-{i}.SLDPRT", 6 * Mb);
        for (var i = 0; i < 1280; i++) activity.Expect(Directions.Download, $"Robot 2027/Gear-{i}.SLDPRT", 2 * Mb);
        var now = activity.Snapshot();
        Assert.Equal("Uploading 0 of 9 files, 54 MB left", now.Upload!.Line);
        Assert.Equal("Downloading 0 of 1,280 files, 2.5 GB left", now.Download!.Line);
        Assert.Equal(now.Download.Line, now.Line); // the direction with the most files left
        Assert.Null(now.Upload.SecondsLeft);

        // Three files go up over four seconds; the one still moving reports its bytes.
        for (var i = 0; i < 3; i++)
        {
            var transfer = activity.Start(Directions.Upload, $"Robot 2027/Part-{i}.SLDPRT", 6 * Mb);
            clock.Advance(0.5);
            transfer.Report(3 * Mb);
            activity.Snapshot();
            clock.Advance(0.5);
            transfer.Report(6 * Mb);
            activity.Finish(transfer);
            activity.Snapshot();
        }
        var moving = activity.Start(Directions.Upload, "Robot 2027/Part-3.SLDPRT", 6 * Mb);
        moving.Report(Mb);
        clock.Advance(1);
        now = activity.Snapshot();
        Assert.Equal(3, now.Upload!.FilesDone);
        Assert.Equal(19 * Mb, now.Upload.BytesDone);
        Assert.Equal(54 * Mb, now.Upload.BytesTotal);
        Assert.True(now.Upload.BytesPerSecond > 0);
        Assert.NotNull(now.Upload.SecondsLeft);
        Assert.Matches(@"^Uploading 3 of 9 files, 35 MB left, (about \d+ sec|less than a minute|about \d+ min)$", now.Upload.Line);
        var active = Assert.Single(now.Active);
        Assert.Equal(("Part-3.SLDPRT", Directions.Upload, Mb, 6 * Mb), (active.Name, active.Direction, active.BytesDone, active.BytesTotal));

        // A refused file leaves the totals; a planned file that did not need to move too.
        activity.Fail(moving);
        activity.Drop("Robot 2027/Part-8.SLDPRT");
        now = activity.Snapshot();
        Assert.Equal(7, now.Upload!.FilesTotal);
        Assert.Empty(now.Active);

        // At most eight files are listed while more move.
        for (var i = 0; i < 12; i++) activity.Start(Directions.Download, $"Robot 2027/Gear-{i}.SLDPRT", 2 * Mb);
        Assert.Equal(ActivityTracker.ActiveShown, activity.Snapshot().Active.Count);

        // A folder moving: its path with the window's separator; done, it is not shown. Each
        // move operation has its own count and target, never the files moved so far.
        activity.Moving(120, "Robot 2027/Gearbox");
        Assert.Equal("Moving 120 files to Robot 2027 › Gearbox", activity.Snapshot().Move!.Line);
        activity.Moved();
        Assert.Null(activity.Snapshot().Move);
        activity.Moving(3, "Robot 2027/Gears");
        Assert.Equal("Moving 3 files to Robot 2027 › Gears", activity.Snapshot().Move!.Line);
        activity.Moved();
        Assert.Null(activity.Snapshot().Move);

        // After the uploads, the locks a check in or an add lets go of count in the upload direction.
        var checkIns = new ActivityTracker(clock);
        for (var i = 0; i < 4900; i++) checkIns.Expect(ActivityTracker.CheckIn, $"Robot 2027/Part-{i}.SLDPRT", 0);
        for (var i = 0; i < 412; i++) checkIns.Done(ActivityTracker.CheckIn, $"Robot 2027/Part-{i}.SLDPRT");
        var checking = checkIns.Snapshot();
        Assert.Equal("Checking in 412 of 4,900 files", checking.Upload!.Line);
        Assert.Equal(checking.Upload.Line, checking.Line);
        Assert.Equal((412, 4900), (checking.Upload.FilesDone, checking.Upload.FilesTotal));

        // Waiting is carried with every snapshot; the end of a pass clears what moved.
        activity.SetWaiting(new WaitingView(3, "3 files are waiting to upload. They upload when this computer is back online."));
        activity.Reset();
        now = activity.Snapshot();
        Assert.Null(now.Upload);
        Assert.Null(now.Download);
        Assert.Null(now.Line);
        Assert.Equal(3, now.Waiting!.Count);
    }

    [Fact]
    public void Time_left_waits_for_three_seconds_and_two_files()
    {
        var clock = new ManualClock();
        var activity = new ActivityTracker(clock);
        for (var i = 0; i < 10; i++) activity.Expect(Directions.Download, $"f{i}", 1000);
        var one = activity.Start(Directions.Download, "f0", 1000);
        clock.Advance(4);
        activity.Finish(one);
        Assert.Null(activity.Snapshot().Download!.SecondsLeft); // one file done
        var two = activity.Start(Directions.Download, "f1", 1000);
        activity.Finish(two);
        clock.Advance(1);
        Assert.NotNull(activity.Snapshot().Download!.SecondsLeft);
        var early = new ActivityTracker(clock);
        early.Expect(Directions.Download, "a", 10);
        early.Expect(Directions.Download, "b", 10);
        early.Expect(Directions.Download, "c", 10);
        early.Finish(early.Start(Directions.Download, "a", 10));
        early.Finish(early.Start(Directions.Download, "b", 10));
        clock.Advance(1);
        Assert.Null(early.Snapshot().Download!.SecondsLeft); // two files, but one second
    }

    // Time left is close to the truth as soon as it is shown. 600 files of 1 MB go up 6 at a
    // time, each in 0.6 s (a steady 10 files and 10 MB a second), after 5 s of planning; the
    // panel is read every 250 ms. From 3 s after the first file started until the last 2 s, the
    // time left is within a quarter of the truth (and a second, for rounding up), and the speed
    // is right.
    [Fact]
    public void Time_left_is_close_to_the_truth_from_three_seconds_on()
    {
        const long Mb = 1024 * 1024;
        const int Files = 600, AtOnce = 6;
        const double Each = 0.6, Step = 0.05;
        var clock = new ManualClock();
        var activity = new ActivityTracker(clock);
        for (var i = 0; i < Files; i++) activity.Expect(Directions.Upload, $"f{i}", Mb);
        clock.Advance(5); // the pass plans and starts uploading 5 s after it learned of the files
        var start = clock.Now;
        var moving = new List<(ActivityTracker.Transfer Transfer, double Started)>();
        var next = 0;
        var checkedTimes = 0;
        for (var tick = 0; ; tick++)
        {
            var t = tick * Step;
            foreach (var done in moving.Where(m => t - m.Started >= Each - 1e-9).ToList())
            {
                done.Transfer.Report(Mb);
                activity.Finish(done.Transfer);
                moving.Remove(done);
            }
            while (moving.Count < AtOnce && next < Files) moving.Add((activity.Start(Directions.Upload, $"f{next++}", Mb), t));
            if (moving.Count == 0) break;
            foreach (var (transfer, started) in moving) transfer.Report((long)(Mb * Math.Min(1, (t - started) / Each)));
            if (tick % 5 == 0)
            {
                var view = activity.Snapshot().Upload!;
                var truth = Files / 10.0 - t;
                if (t >= 3 && truth >= 2)
                {
                    Assert.NotNull(view.SecondsLeft);
                    Assert.True(Math.Abs(view.SecondsLeft!.Value - truth) <= 0.25 * truth + 1, $"at {t:F2} s: {view.SecondsLeft} s left, truly {truth:F1} s");
                    Assert.InRange(view.BytesPerSecond, (long)(7.5 * Mb), (long)(12.5 * Mb));
                    checkedTimes++;
                }
            }
            clock.Now = start + (long)Math.Round((tick + 1) * Step * 1000);
        }
        Assert.True(checkedTimes > 200, $"checked {checkedTimes} times");
    }

    // The same sizes and times as the window's own (app.js bytes() and timeLeft()).
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(48L * 1024 * 1024, "48 MB")]
    [InlineData(2254857830L, "2.1 GB")]
    public void Sizes_read_like_the_window(long bytes, string words) => Assert.Equal(words, ActivityTracker.Bytes(bytes));

    [Theory]
    [InlineData(5, "less than a minute")]
    [InlineData(20, "about 20 sec")]
    [InlineData(22, "about 20 sec")]
    [InlineData(23, "about 25 sec")]
    [InlineData(55, "about 1 min")]
    [InlineData(89, "about 1 min")]
    [InlineData(180, "about 3 min")]
    [InlineData(200, "about 3 min")]
    public void Times_left_read_like_the_window(int seconds, string words) => Assert.Equal(words, ActivityTracker.TimeLeft(seconds));

    [Fact]
    public async Task Work_runs_on_the_engine_thread_which_ends_when_idle_and_starts_again()
    {
        var engine = new EngineThread(null, TimeSpan.FromMilliseconds(100));
        var caller = Environment.CurrentManagedThreadId;
        var seen = await engine.InvokeAsync(async () =>
        {
            var before = (Thread.CurrentThread.Name, engine.IsCurrent);
            await Task.Delay(10); // an await inside comes back to the engine thread
            return (before, after: (Thread.CurrentThread.Name, engine.IsCurrent), Environment.CurrentManagedThreadId);
        });
        Assert.Equal(("Armory engine", true), seen.before);
        Assert.Equal(("Armory engine", true), seen.after);
        Assert.NotEqual(caller, seen.CurrentManagedThreadId);
        Assert.False(engine.IsCurrent);
        // A failure comes back to the caller as it was thrown.
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.InvokeAsync<bool>(() => throw new InvalidDataException("x")));
        // Idle: the thread ends; the next work starts another.
        var started = engine.Started;
        await Task.Delay(400);
        Assert.True(await engine.InvokeAsync(() => Task.FromResult(engine.IsCurrent)));
        Assert.Equal(started + 1, engine.Started);
        var done = new ManualResetEventSlim();
        engine.Enqueue(done.Set);
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
    }

    // v0.2.1, the field crash: a view timer that fired a fraction of a millisecond early asked
    // Task.Delay for under a millisecond, which completes at once, and the timer and PublishSoon
    // called each other until the stack overflowed. The wait is now always at least 1 ms, in
    // whole milliseconds (and the timer is a loop, never a call back into PublishSoon).
    [Fact]
    public async Task The_view_timer_always_really_waits()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), SyncEngine.WaitBeforeNextView(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(490), SyncEngine.WaitBeforeNextView(TimeSpan.FromMilliseconds(10.5)));
        Assert.Equal(TimeSpan.FromMilliseconds(1), SyncEngine.WaitBeforeNextView(TimeSpan.FromMilliseconds(499.95)));
        Assert.Equal(TimeSpan.FromMilliseconds(1), SyncEngine.WaitBeforeNextView(TimeSpan.FromMilliseconds(499.2)));
        Assert.Equal(TimeSpan.FromMilliseconds(1), SyncEngine.WaitBeforeNextView(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(TimeSpan.FromMilliseconds(1), SyncEngine.WaitBeforeNextView(TimeSpan.FromSeconds(3)));
        // What made it recursive: a delay under a millisecond is already complete.
        Assert.True(Task.Delay(TimeSpan.FromMilliseconds(0.4)).IsCompleted);
        var waited = Task.Delay(SyncEngine.WaitBeforeNextView(TimeSpan.FromMilliseconds(499.6)));
        Assert.False(waited.IsCompleted);
        await waited;
    }

    private sealed class Store : IEngineStateStore
    {
        private byte[]? bytes;
        public byte[]? Load() => bytes;
        public void Save(byte[] state) => bytes = state;
    }
}
