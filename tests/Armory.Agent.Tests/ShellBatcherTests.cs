namespace Armory.Agent.Tests;

// T3 (docs/agent/EXPLORER.md): how the forwarders of one right-click become one action. Time
// comes in with each call, so these run on any computer in no time.
public sealed class ShellBatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ShellBatch> batches = [];
    private readonly ShellBatcher batcher;

    public ShellBatcherTests() => batcher = new ShellBatcher(batches.Add);

    private static ShellRequest At(int milliseconds, ShellVerb verb, string path) =>
        new(verb, path, 0, T0.AddMilliseconds(milliseconds));

    private static string File(int i) => $@"C:\IDEA\Armory\Robot 2027\Part {i:D3}.SLDPRT";

    [Fact]
    public void Forty_arrivals_twenty_milliseconds_apart_are_one_batch_300_ms_after_the_last()
    {
        for (var i = 0; i < 40; i++) batcher.Add(At(i * 20, ShellVerb.CheckOut, File(i)));
        Assert.Empty(batches);
        Assert.Equal(T0.AddMilliseconds(39 * 20 + 300), batcher.NextDue);
        batcher.Tick(T0.AddMilliseconds(39 * 20 + 299));
        Assert.Empty(batches);
        batcher.Tick(T0.AddMilliseconds(39 * 20 + 300));
        var batch = Assert.Single(batches);
        Assert.Equal(ShellVerb.CheckOut, batch.Verb);
        Assert.Equal(Enumerable.Range(0, 40).Select(File), batch.Paths);
        Assert.Equal(T0, batch.FirstArrived);
        Assert.Null(batcher.NextDue);
    }

    [Fact]
    public void A_gap_of_400_ms_makes_two_batches()
    {
        batcher.Add(At(0, ShellVerb.CheckIn, File(1)));
        batcher.Add(At(100, ShellVerb.CheckIn, File(2)));
        // The next arrival comes after the first batch was due: it closes, the new one opens.
        batcher.Add(At(500, ShellVerb.CheckIn, File(3)));
        var first = Assert.Single(batches);
        Assert.Equal([File(1), File(2)], first.Paths);
        batcher.Tick(T0.AddMilliseconds(800));
        Assert.Equal(2, batches.Count);
        Assert.Equal([File(3)], batches[1].Paths);
    }

    [Fact]
    public void Two_verbs_interleaved_make_two_batches_that_never_mix()
    {
        for (var i = 0; i < 10; i++)
            batcher.Add(At(i * 10, i % 2 == 0 ? ShellVerb.CheckIn : ShellVerb.Undo, File(i)));
        batcher.Tick(T0.AddSeconds(1));
        Assert.Equal(2, batches.Count);
        Assert.Equal([File(0), File(2), File(4), File(6), File(8)], batches.Single(b => b.Verb == ShellVerb.CheckIn).Paths);
        Assert.Equal([File(1), File(3), File(5), File(7), File(9)], batches.Single(b => b.Verb == ShellVerb.Undo).Paths);
    }

    [Fact]
    public void The_hundredth_path_closes_a_batch_at_once()
    {
        for (var i = 0; i < 99; i++) batcher.Add(At(i, ShellVerb.ForceCheckIn, File(i)));
        Assert.Empty(batches);
        batcher.Add(At(99, ShellVerb.ForceCheckIn, File(99)));
        var batch = Assert.Single(batches);
        Assert.Equal(100, batch.Paths.Count);
        Assert.Equal(T0.AddMilliseconds(99), batch.Closed);
        batcher.Add(At(100, ShellVerb.ForceCheckIn, File(100)));
        batcher.Tick(T0.AddSeconds(1));
        Assert.Equal([File(100)], batches[1].Paths);
    }

    [Fact]
    public void A_batch_closes_five_seconds_after_its_first_path_however_busy()
    {
        for (var i = 0; i < 30; i++) batcher.Add(At(i * 200, ShellVerb.CheckOut, File(i)));
        // Arrivals at 0 to 4,800 ms are the first batch; the one at 5,000 starts the next.
        Assert.Equal(25, Assert.Single(batches).Paths.Count);
        Assert.Equal(T0.AddSeconds(5), batches[0].Closed);
        batcher.Tick(T0.AddMilliseconds(29 * 200 + 300));
        Assert.Equal(5, batches[1].Paths.Count);
        // With nothing else arriving, the cap is the due time.
        var other = new List<ShellBatch>();
        var steady = new ShellBatcher(other.Add);
        steady.Add(At(0, ShellVerb.CheckIn, File(1)));
        for (var i = 1; i < 30; i++) steady.Add(At(i * 250, ShellVerb.CheckIn, File(1)));
        Assert.Equal(T0.AddSeconds(5), Assert.Single(other).Closed);
    }

    [Fact]
    public void Check_out_and_open_and_show_go_at_once_and_leave_open_batches_alone()
    {
        batcher.Add(At(0, ShellVerb.CheckOut, File(1)));
        batcher.Add(At(10, ShellVerb.Show, File(2)));
        batcher.Add(At(20, ShellVerb.CheckOutAndOpen, File(3)));
        batcher.Add(At(30, ShellVerb.CheckOutAndOpen, File(3)));
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b.Paths));
        Assert.Equal([ShellVerb.Show, ShellVerb.CheckOutAndOpen, ShellVerb.CheckOutAndOpen], batches.Select(b => b.Verb));
        Assert.Equal(T0.AddMilliseconds(300), batcher.NextDue);
        batcher.Tick(T0.AddMilliseconds(300));
        Assert.Equal(ShellVerb.CheckOut, batches[3].Verb);
    }

    [Fact]
    public void A_path_twice_counts_once_ignoring_case_and_still_extends_the_wait()
    {
        batcher.Add(At(0, ShellVerb.Undo, File(1)));
        batcher.Add(At(200, ShellVerb.Undo, File(1).ToUpperInvariant()));
        batcher.Add(At(250, ShellVerb.Undo, File(2)));
        Assert.Equal(T0.AddMilliseconds(550), batcher.NextDue);
        batcher.Tick(T0.AddMilliseconds(550));
        Assert.Equal([File(1), File(2)], Assert.Single(batches).Paths);
    }

    [Fact]
    public void Flush_closes_every_open_batch()
    {
        batcher.Add(At(0, ShellVerb.CheckIn, File(1)));
        batcher.Add(At(5, ShellVerb.CheckOut, File(2)));
        batcher.Flush(T0.AddMilliseconds(10));
        Assert.Equal([ShellVerb.CheckIn, ShellVerb.CheckOut], batches.Select(b => b.Verb));
        Assert.Null(batcher.NextDue);
    }
}
