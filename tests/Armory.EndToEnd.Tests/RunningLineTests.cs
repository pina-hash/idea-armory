using System.Collections.Concurrent;
using Armory.Agent.Engine.View;
using Armory.Client;
using Armory.TestSupport;
using Npgsql;
using static Armory.EndToEnd.Tests.ScenarioTests;

namespace Armory.EndToEnd.Tests;

// 0.3.3 (feedback N8: "when im performing a loading operation, such as checking out large amounts
// of files, i would like to see some indication of progress and some of what is happening in the
// background"): a check out, an undo and a Force check in of many files have a count of their own
// from the click to the answer, the window hears it while the work goes on (not once it is over),
// and the answer of an action of many files is a running line too.
public sealed class RunningLineTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Files Alex has checked out on his laptop, made straight in the database (no bytes: only the
    // check outs matter here), named Bulk/Part0001.SLDPRT and on.
    private static async Task<List<Guid>> CheckedOutAsync(Team t, int count)
    {
        await using var c = await t.World.Database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            with made as (
                insert into public.armory_files (project_id, folder, name)
                select @p, 'Bulk', 'Part' || lpad(i::text, 4, '0') || '.SLDPRT' from generate_series(1, @n) i returning id)
            insert into public.armory_locks (file_id, holder_email, holder_device_id) select id, @h, @d from made returning file_id
            """, c);
        command.Parameters.AddWithValue("p", t.Project);
        command.Parameters.AddWithValue("n", count);
        command.Parameters.AddWithValue("h", Alex);
        command.Parameters.AddWithValue("d", t.A.DeviceId);
        await using var reader = await command.ExecuteReaderAsync();
        List<Guid> ids = [];
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    // A mentor force checks in 1,200 files: three armory_break_locks calls of half a second each.
    // The window hears "Force checked in 500 of 1,200 files" and the count of its own while the
    // calls are still going (until 0.3.3 every line came at once, after the pass that followed).
    [PostgresFact]
    public async Task Force_check_in_running_lines_arrive_while_locks_break()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyBreakLocksAsync(t.World.Database);
        var mentor = await t.World.ComputerAsync("mentor laptop", Mentor);
        var ids = await CheckedOutAsync(t, 1200);
        await mentor.SyncAsync();
        mentor.Network.RpcDelay = rpc => rpc.EndsWith("/" + ArmoryApi.BreakLocksRpc, StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;
        var seen = new ConcurrentQueue<(int Calls, ActivityView Activity)>();
        mentor.Activities += activity => seen.Enqueue((t.World.Supabase.RpcCount(ArmoryApi.BreakLocksRpc), activity));
        var took = await mentor.Engine.TakeBackAsync(ids);
        Assert.Equal("Force checked in 1,200 files. Anything that wasn't checked in is kept as its holder's own copy.", took.Message);
        Assert.Equal(3, t.World.Supabase.RpcCount(ArmoryApi.BreakLocksRpc));
        Assert.Contains(seen, s => s.Calls < 3 && s.Activity.Log.Any(l => l.Line == "Force checked in 500 of 1,200 files"));
        Assert.Contains(seen, s => s.Calls < 3 && s.Activity.Line == "Force checking in 500 of 1,200 files" && s.Activity.Upload?.Line == s.Activity.Line);
        // The answer is a running line too, and the count is gone with the work.
        Assert.Contains(mentor.Engine.ActivityNow.Log, l => l.Line == took.Message);
        Assert.Null(mentor.Engine.ActivityNow.Upload);
    }

    // Alex checks out a folder of 1,400 files (armory_lock_files, 500 a call, slow here): from the
    // click the window counts them ("Checking out 500 of 1,400 files", in the download direction),
    // the running lines say how far the server got, and the answer is the last line.
    [PostgresFact]
    public async Task A_check_out_of_1400_files_shows_a_count()
    {
        await using var t = await TeamAsync();
        await ArmoryV3StandIn.ApplyCoreAsync(t.World.Database);
        const string Folder = "Robot 2027/Many";
        for (var i = 0; i < 1400; i++) t.A.Write($"{Folder}/Part-{i:D4}.SLDPRT", "part " + i);
        await t.A.SyncTimesAsync(2);
        t.A.Network.RpcDelay = rpc => rpc.EndsWith("/armory_lock_files", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
        var seen = new ConcurrentQueue<ActivityView>();
        t.A.Activities += seen.Enqueue;
        var answer = await t.A.CheckOutAsync(Folder);
        output.WriteLine("CHECK OUT LINES " + string.Join(" | ", seen.Select(a => $"{a.Line} [{a.Download?.Line}]").Distinct()));
        Assert.Equal("Checked out 1,400 files.", answer.Message);
        Assert.Contains(seen, a => a.Line == "Checking out 0 of 1,400 files");
        Assert.Contains(seen, a => a.Line == "Checking out 500 of 1,400 files" && a.Download?.Line == a.Line);
        Assert.Contains(seen, a => a.Log.Any(l => l.Line == "Checked out 500 of 1,400 files"));
        Assert.Contains(t.A.Engine.ActivityNow.Log, l => l.Line == "Checked out 1,400 files.");
        Assert.Null(t.A.Engine.ActivityNow.Download);
        Assert.Null(t.A.Engine.ActivityNow.Line);
    }
}
