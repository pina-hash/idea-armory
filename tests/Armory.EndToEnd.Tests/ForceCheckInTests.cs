using Armory.Agent.Engine;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using Npgsql;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// Force check in of many files in one call (0.3.3, idea-app 0234 "The v0.3.1 server contract"):
// armory_break_locks, at most 500 files a call in id order, each call's record saved before it
// is sent, the answers read file by file into one plain sentence, ONE pass after, and one
// armory_break_lock per file on a site without it. Against the stand-in that copies 0234's body.
public sealed class ForceCheckInTests
{
    private const string BreakLocks = ArmoryApi.BreakLocksRpc, BreakLock = "armory_break_lock";

    private static async Task<(Team T, Computer Mentor)> MentorTeamAsync(bool batch = true, LatencyProfile? latency = null)
    {
        var t = await TeamAsync(latency: latency);
        if (batch) await ArmoryV3StandIn.ApplyBreakLocksAsync(t.World.Database);
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        return (t, mentor);
    }

    // Files Alex has checked out on his laptop, made straight in the database (no bytes: only the
    // check outs matter here), named Bulk/Part0001.SLDPRT and on.
    private static async Task<List<Guid>> CheckedOutAsync(Team t, int count, int first = 1)
    {
        await using var c = await t.World.Database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            with made as (
                insert into public.armory_files (project_id, folder, name)
                select @p, 'Bulk', 'Part' || lpad(i::text, 4, '0') || '.SLDPRT' from generate_series(@first, @first + @n - 1) i returning id)
            insert into public.armory_locks (file_id, holder_email, holder_device_id) select id, @h, @d from made returning file_id
            """, c);
        command.Parameters.AddWithValue("p", t.Project);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("n", count);
        command.Parameters.AddWithValue("h", Alex);
        command.Parameters.AddWithValue("d", t.A.DeviceId);
        await using var reader = await command.ExecuteReaderAsync();
        List<Guid> ids = [];
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static Task<long> LiveLocks(Team t) => t.World.CountAsync("select count(*) from armory_locks where broken_at is null");
    private static Task<long> BrokenChanges(Team t) => t.World.CountAsync("select count(*) from armory_change_feed where kind = 'lock_broken'");
    private static int Rpc(Team t, string function) => t.World.Supabase.RpcCount(function);

    // What each armory_break_locks call was sent: its files and its operation id.
    private static List<(List<string> Files, string Operation)> Batches(Team t)
        => t.World.Supabase.RpcArguments.Where(a => a.Function == BreakLocks)
            .Select(a => (a.Arguments.GetProperty("p_files").EnumerateArray().Select(e => e.GetString()!).ToList(), a.Arguments.GetProperty("p_operation").GetString()!)).ToList();

    private static IReadOnlyList<object> PendingRecords(Computer c) => EngineState.Load(c.State).ForceCheckIns;

    // A trigger on the lock table that refuses breaking one file's lock with these words (P0001,
    // as armory_break_lock's own refusal is), or, with once, a 40P01 the first time only.
    private static async Task RefuseAsync(Team t, Guid file, string words, string code = "P0001", bool once = false)
    {
        var name = "test_refuse_" + file.ToString("N");
        await using var c = await t.World.Database.OpenAsync();
        await new NpgsqlCommand($"""
            create sequence public.{name}_n;
            create function public.{name}() returns trigger language plpgsql as $$
            begin
                if {(once ? $"nextval('public.{name}_n') = 1" : "true")} then raise exception '{words}' using errcode = '{code}'; end if;
                return new;
            end $$;
            create trigger {name} before update on public.armory_locks for each row when (new.file_id = '{file}') execute function public.{name}();
            """, c).ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task A_force_check_in_of_1200_files_goes_in_three_calls_of_at_most_500_in_id_order_and_one_pass()
    {
        var (t, mentor) = await MentorTeamAsync(latency: LatencyProfile.School);
        await using var _ = t;
        var ids = await CheckedOutAsync(t, 1200);
        await mentor.SyncAsync();
        Assert.Equal(1200, mentor.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files).Count(f => f.Checkout.Label.StartsWith("Checked out by Alex Kim", StringComparison.Ordinal)));
        t.World.Supabase.RecordRpcArguments = true;
        var (reads, requests) = (Rpc(t, "armory_list_changes"), mentor.Network.RpcRequests);
        var shuffled = ids.OrderBy(_ => Guid.NewGuid()).ToList();
        var took = await mentor.Engine.TakeBackAsync(shuffled);
        Assert.Equal((true, "Force checked in 1,200 files. Anything that wasn't checked in is kept as its holder's own copy."), (took.Ok, took.Message));
        Assert.Equal((3, 0), (Rpc(t, BreakLocks), Rpc(t, BreakLock)));
        var batches = Batches(t);
        Assert.Equal([500, 500, 200], batches.Select(b => b.Files.Count));
        // In id order within each call and from one call to the next (PostgreSQL's uuid order is its text's).
        var sent = batches.SelectMany(b => b.Files).ToList();
        Assert.Equal(ids.Select(i => i.ToString()).Order(StringComparer.Ordinal), sent);
        Assert.Equal(3, batches.Select(b => b.Operation).Distinct().Count());
        Assert.Equal(0, await LiveLocks(t));
        Assert.Equal(1200, await BrokenChanges(t));
        // ONE pass after (it reads the changes once, and its own refresh may read once more), and
        // three calls, not 1,200, at 60 ms each on the school network.
        Assert.InRange(Rpc(t, "armory_list_changes") - reads, 1, 2);
        Assert.InRange(mentor.Network.RpcRequests - requests, 3, 12);
        Assert.Empty(PendingRecords(mentor));
        Assert.All(mentor.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files), f => Assert.Equal("Available", f.Checkout.Label));
    }

    [PostgresFact]
    public async Task Without_armory_break_locks_it_goes_file_by_file_and_the_batch_is_asked_again_an_hour_later()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        t.World.Supabase.HideFunction(BreakLocks); // a site before 0234: 404 PGRST202
        var ids = await CheckedOutAsync(t, 20);
        await mentor.SyncAsync();
        var first = await mentor.Engine.TakeBackAsync(ids.Take(10).ToList());
        Assert.Equal("Force checked in 10 files. Anything that wasn't checked in is kept as its holder's own copy.", first.Message);
        Assert.Equal((1, 10), (Rpc(t, BreakLocks), Rpc(t, BreakLock)));
        Assert.Contains(mentor.Logged, l => l.StartsWith("batches: the site has no armory_break_locks", StringComparison.Ordinal));
        // Within the hour the batch is not asked for.
        Assert.Equal("Force checked in 5 files. Anything that wasn't checked in is kept as its holder's own copy.", (await mentor.Engine.TakeBackAsync(ids.Skip(10).Take(5).ToList())).Message);
        Assert.Equal((1, 15), (Rpc(t, BreakLocks), Rpc(t, BreakLock)));
        Assert.Empty(PendingRecords(mentor));
        // An hour later it is asked for again, and the site has it now.
        t.World.Supabase.ShowFunction(BreakLocks);
        mentor.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Force checked in 5 files. Anything that wasn't checked in is kept as its holder's own copy.", (await mentor.Engine.TakeBackAsync(ids.Skip(15).ToList())).Message);
        Assert.Equal((2, 15), (Rpc(t, BreakLocks), Rpc(t, BreakLock)));
        Assert.Equal(0, await LiveLocks(t));
        Assert.Equal(20, await BrokenChanges(t));
    }

    [PostgresFact]
    public async Task Broken_not_checked_out_any_more_and_refused_files_read_as_one_plain_sentence()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        var all = (await CheckedOutAsync(t, 10)).Order().ToList();
        var (ids, more) = (all.Take(8).ToList(), all.Skip(8).ToList());
        await mentor.SyncAsync();
        // Since the mentor's view: Alex checked two in, one is refused for the role, one for
        // another reason in the server's words.
        Assert.True(await t.A.Api.ReleaseLockAsync(ids[0], t.A.DeviceId, Guid.NewGuid()));
        Assert.True(await t.A.Api.ReleaseLockAsync(ids[1], t.A.DeviceId, Guid.NewGuid()));
        await RefuseAsync(t, ids[2], "only a mentor or cad_lead may break a lock");
        await RefuseAsync(t, ids[3], "this file is being audited");
        var took = await mentor.Engine.TakeBackAsync(ids);
        Assert.True(took.Ok);
        Assert.Equal("Force checked in 4 files. Anything that wasn't checked in is kept as its holder's own copy. 2 files weren't checked out any more." +
            " 1 file is in a project where only a mentor or CAD lead can force a check in. 1 file was refused: the server said this file is being audited.", took.Message);
        Assert.Equal(1, Rpc(t, BreakLocks));
        Assert.Equal(4, await LiveLocks(t)); // the two refused, and the two not asked about yet
        Assert.Contains(mentor.Logged, l => l.Contains($"take back: {ids[3]}: P0001 this file is being audited", StringComparison.Ordinal));

        // Everything refused: nothing was force checked in, said plainly, in the plural.
        foreach (var id in more) await RefuseAsync(t, id, "only a mentor or cad_lead may break a lock");
        var none = await mentor.Engine.TakeBackAsync([ids[2], .. more]);
        Assert.False(none.Ok);
        Assert.Equal("No files were force checked in. 3 files are in a project where only a mentor or CAD lead can force a check in.", none.Message);
    }

    [PostgresFact]
    public async Task A_deadlock_is_sent_again_and_a_file_the_batch_answers_busy_goes_again()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        mentor.Network.RpcDelay = path => path.EndsWith(BreakLocks, StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(80) : TimeSpan.Zero;
        var ids = (await CheckedOutAsync(t, 6)).Order().ToList();
        await mentor.SyncAsync();
        // The whole call rolled back by a deadlock: PostgrestClient sends the same body again.
        t.World.Supabase.FailRpc(BreakLocks, "40P01", "deadlock detected");
        t.World.Supabase.RecordRpcArguments = true;
        var took = await mentor.Engine.TakeBackAsync(ids.Take(3).ToList());
        Assert.Equal("Force checked in 3 files. Anything that wasn't checked in is kept as its holder's own copy.", took.Message);
        Assert.Equal(2, Rpc(t, BreakLocks));
        Assert.Single(Batches(t).Select(b => b.Operation).Distinct());
        // One file's savepoint rolled back by a deadlock inside the batch: that file goes again in
        // a call of its own (a new id), and the sentence counts it with the rest.
        await RefuseAsync(t, ids[4], "deadlock detected", "40P01", once: true);
        var again = await mentor.Engine.TakeBackAsync(ids.Skip(3).ToList());
        Assert.Equal("Force checked in 3 files. Anything that wasn't checked in is kept as its holder's own copy.", again.Message);
        Assert.Equal(4, Rpc(t, BreakLocks));
        var last = Batches(t)[^1];
        Assert.Equal([ids[4].ToString()], last.Files);
        Assert.Equal(0, await LiveLocks(t));
        Assert.Equal(6, await BrokenChanges(t));
    }

    // RefuseAsync's trigger on one file, taken away again.
    private static async Task ForgetRefusalAsync(Team t, Guid file)
    {
        var name = "test_refuse_" + file.ToString("N");
        await using var c = await t.World.Database.OpenAsync();
        await new NpgsqlCommand($"drop trigger {name} on public.armory_locks; drop function public.{name}(); drop sequence public.{name}_n;", c).ExecuteNonQueryAsync();
    }

    // A file still busy after every round of one ask, asked for again: each round of the second
    // ask is a new call the server really runs (its id is chained from the call that answered
    // busy), never a replay of the first ask's busy receipt. Before 0.3.3 rounds 1 to 3 were built
    // from the round number alone, so the second ask got one real try instead of four.
    [PostgresFact]
    public async Task A_second_ask_gives_a_busy_file_fresh_tries()
    {
        var (t, mentor) = await MentorTeamAsync(latency: LatencyProfile.School);
        await using var _ = t;
        var ids = (await CheckedOutAsync(t, 3)).Order().ToList();
        await mentor.SyncAsync();
        t.World.Supabase.RecordRpcArguments = true;
        await RefuseAsync(t, ids[1], "deadlock detected", "40P01");
        var first = await mentor.Engine.TakeBackAsync([ids[0], ids[1]]);
        Assert.Equal("Force checked in 1 file. Anything that wasn't checked in is kept as its holder's own copy. 1 file was busy on the server: try it again in a moment.",
            first.Message);
        var firstAsk = Batches(t);
        Assert.Equal([2, 1, 1, 1], firstAsk.Select(b => b.Files.Count)); // round 0, then three more for the busy file
        Assert.Equal(4, firstAsk.Select(b => b.Operation).Distinct().Count());
        Assert.Equal(2, await LiveLocks(t));

        // Busy once more, then free: the second ask's own rounds try it again for real.
        await ForgetRefusalAsync(t, ids[1]);
        await RefuseAsync(t, ids[1], "deadlock detected", "40P01", once: true);
        var second = await mentor.Engine.TakeBackAsync([ids[1], ids[2]]);
        Assert.Equal((true, "Force checked in 2 files. Anything that wasn't checked in is kept as its holder's own copy."), (second.Ok, second.Message));
        var secondAsk = Batches(t).Skip(firstAsk.Count).ToList();
        Assert.Equal([$"{ids[1]},{ids[2]}", ids[1].ToString()], secondAsk.Select(b => string.Join(",", b.Files)));
        Assert.Empty(secondAsk.Select(b => b.Operation).Intersect(firstAsk.Select(b => b.Operation)));
        Assert.Equal(0, await LiveLocks(t));
        Assert.Equal(3, await BrokenChanges(t));
        Assert.Empty(PendingRecords(mentor));
    }

    [PostgresFact]
    public async Task A_lost_answer_is_answered_again_from_the_receipt_with_the_same_operation_id()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        var ids = await CheckedOutAsync(t, 10);
        await mentor.SyncAsync();
        t.World.Supabase.RecordRpcArguments = true;
        // The call lands, and its answer is lost on the way back.
        t.World.Supabase.DropRpcAcknowledgement(BreakLocks, 1);
        var lost = await mentor.Engine.TakeBackAsync(ids);
        Assert.False(lost.Ok);
        Assert.Equal("Force checked in 0 of 10 files. You went offline: try again once this computer is back online to finish the rest.", lost.Message);
        Assert.Equal(0, await LiveLocks(t));
        Assert.Single(PendingRecords(mentor)); // kept until its answer is known
        // Asked again from the same view: the same check outs, so the same id, and the server
        // answers what it answered the first time, writing nothing.
        var replay = await mentor.Engine.TakeBackAsync(ids);
        Assert.Equal((true, "Force checked in 10 files. Anything that wasn't checked in is kept as its holder's own copy."), (replay.Ok, replay.Message));
        var batches = Batches(t);
        Assert.Equal(2, batches.Count);
        Assert.Equal(batches[0].Operation, batches[1].Operation);
        Assert.Equal(10, await BrokenChanges(t));
        Assert.Empty(PendingRecords(mentor));
    }

    [PostgresFact]
    public async Task A_stop_before_the_call_is_finished_by_the_next_pass_and_a_stop_after_it_never_ends_a_new_check_out()
    {
        // Before: the record is on disk, the call was never sent; the next start sends it with the
        // same id, because every file still has the check out it named.
        {
            var (t, mentor) = await MentorTeamAsync();
            await using var _ = t;
            var ids = await CheckedOutAsync(t, 5);
            await mentor.SyncAsync();
            mentor.CrashPoint = p => { if (p == "before-break-batch") throw new SimulatedCrash(p); };
            mentor.Restart();
            await mentor.SyncAsync();
            await Assert.ThrowsAsync<SimulatedCrash>(() => mentor.Engine.TakeBackAsync(ids));
            Assert.Equal(0, Rpc(t, BreakLocks));
            Assert.Single(PendingRecords(mentor));
            mentor.CrashPoint = null;
            mentor.Restart();
            await mentor.SyncAsync();
            Assert.Equal(1, Rpc(t, BreakLocks));
            Assert.Equal(0, await LiveLocks(t));
            Assert.Equal(5, await BrokenChanges(t));
            Assert.Empty(PendingRecords(mentor));
            Assert.Contains(mentor.Logged, l => l.Contains("finished a force check in a stop interrupted: 5 of 5 files", StringComparison.Ordinal));
            Assert.All(mentor.Engine.View.Projects.Single().Folders.SelectMany(f => f.Files), f => Assert.Equal("Available", f.Checkout.Label));
        }
        // After: the call landed and the stop came before its answer was applied. Alex checks one
        // out again before the next start: the record names check outs that changed, so it is
        // dropped unsent, and Alex's new check out stays his.
        {
            var (t, mentor) = await MentorTeamAsync();
            await using var _ = t;
            var ids = await CheckedOutAsync(t, 5);
            await mentor.SyncAsync();
            mentor.CrashPoint = p => { if (p == "after-break-batch") throw new SimulatedCrash(p); };
            mentor.Restart();
            await mentor.SyncAsync();
            await Assert.ThrowsAsync<SimulatedCrash>(() => mentor.Engine.TakeBackAsync(ids));
            Assert.Equal(1, Rpc(t, BreakLocks));
            Assert.Equal(0, await LiveLocks(t));
            Assert.True(await t.A.Api.AcquireLockAsync(ids[0], t.A.DeviceId, Guid.NewGuid()));
            mentor.CrashPoint = null;
            mentor.Restart();
            await mentor.SyncAsync();
            Assert.Equal(1, Rpc(t, BreakLocks));
            Assert.Equal(1, await LiveLocks(t));
            Assert.Equal(Alex, await t.Holder(ids[0]));
            Assert.Equal(5, await BrokenChanges(t));
            Assert.Empty(PendingRecords(mentor));
            Assert.Contains(mentor.Logged, l => l.Contains("is not sent again: its check outs changed since", StringComparison.Ordinal));
        }
    }

    // The holder's computer keeps what they had not checked in as their own copy, as for one file
    // and as before 0.3.3, and every file comes back read-only with the shared version.
    [PostgresFact]
    public async Task The_holders_unsaved_work_is_kept_as_their_own_copy()
    {
        var (t, mentor) = await MentorTeamAsync();
        await using var _ = t;
        const int many = 12;
        for (var i = 0; i < many; i++) t.A.Write($"Robot 2027/Gears/Gear{i:00}.SLDPRT", "gear " + i);
        await t.A.SyncAsync();
        Assert.True((await t.A.CheckOutAsync("Robot 2027/Gears")).Ok);
        t.A.Save("Robot 2027/Gears/Gear03.SLDPRT", "Alex unfinished 3");
        t.A.Save("Robot 2027/Gears/Gear09.SLDPRT", "Alex unfinished 9");
        await mentor.SyncAsync();
        List<Guid> ids = [];
        for (var i = 0; i < many; i++) ids.Add(await t.FileId($"Gear{i:00}.SLDPRT"));
        var reads = Rpc(t, "armory_list_changes");
        var took = await mentor.Engine.TakeBackAsync(ids);
        Assert.Equal((true, $"Force checked in {many} files. Anything that wasn't checked in is kept as its holder's own copy."), (took.Ok, took.Message));
        Assert.Equal((1, 0), (Rpc(t, BreakLocks), Rpc(t, BreakLock)));
        Assert.InRange(Rpc(t, "armory_list_changes") - reads, 1, 2);

        await t.A.SyncAsync();
        foreach (var i in new[] { 3, 9 })
        {
            Assert.Contains(Alex + "|lock broken", await t.SideAuthors(ids[i]));
            Assert.Equal("gear " + i, t.A.Text($"Robot 2027/Gears/Gear{i:00}.SLDPRT"));
        }
        for (var i = 0; i < many; i++) Assert.True(t.A.Disk.IsReadOnly($"Robot 2027/Gears/Gear{i:00}.SLDPRT"));
        Assert.Empty(t.A.Engine.View.MyFiles);
        Assert.Contains(t.A.Engine.View.Notices, n => n.Kind == NoticeKinds.TakenBack);
        foreach (var c in new[] { t.A, mentor })
        {
            Assert.Empty(c.Disk.OpenWriteViolations);
            Assert.Empty(c.Disk.UnpreservedOverwrites);
        }
    }
}
