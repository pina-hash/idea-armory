using Armory.Core;

namespace Armory.Core.Tests;

// C5: which question a document SolidWorks opened gets (OpenAsk), once per document per
// SolidWorks session (OpenAskOnce), and which opens are one question (OpenBursts).
public sealed class OpenAskTests
{
    [Theory]
    [InlineData(LockOwnership.Free, true, OpenAskKind.CheckOut)]
    [InlineData(LockOwnership.Free, false, OpenAskKind.CheckOut)]          // its bit cleared by hand: still not checked out
    [InlineData(LockOwnership.ThisDevice, true, OpenAskKind.Reopen)]
    [InlineData(LockOwnership.ThisDevice, false, OpenAskKind.None)]
    [InlineData(LockOwnership.OtherPerson, true, OpenAskKind.HeldByOther)]
    [InlineData(LockOwnership.OtherPerson, false, OpenAskKind.HeldByOther)]
    [InlineData(LockOwnership.MyOtherDevice, true, OpenAskKind.HeldOnMyOtherComputer)]
    [InlineData(LockOwnership.MyOtherDevice, false, OpenAskKind.HeldOnMyOtherComputer)]
    public void A_top_level_tracked_document_is_asked_by_who_has_it(LockOwnership ownership, bool readOnly, OpenAskKind expected)
        => Assert.Equal(expected, OpenAsk.Decide(ownership, readOnly, topLevel: true, tracked: true));

    [Fact]
    public void A_reference_or_a_file_the_team_does_not_have_is_never_asked()
    {
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var readOnly in new[] { false, true })
        {
            Assert.Equal(OpenAskKind.None, OpenAsk.Decide(ownership, readOnly, topLevel: false, tracked: true));
            Assert.Equal(OpenAskKind.None, OpenAsk.Decide(ownership, readOnly, topLevel: true, tracked: false));
            Assert.Equal(OpenAskKind.None, OpenAsk.Decide(ownership, readOnly, topLevel: false, tracked: false));
        }
    }

    [Fact]
    public void A_document_is_asked_once_per_SolidWorks_session()
    {
        var once = new OpenAskOnce();
        Assert.True(once.TryAsk(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 4120));
        Assert.False(once.TryAsk(@"c:\idea\armory\robot\plate.sldprt", 4120)); // closed and opened again: same session
        Assert.True(once.WasAsked(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 4120));
        Assert.True(once.TryAsk(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 7788)); // another SolidWorks
        Assert.True(once.TryAsk(@"C:\IDEA\Armory\Robot\Arm.SLDASM", 4120));
        once.SessionEnded(4120);                                             // that SolidWorks closed
        Assert.False(once.WasAsked(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 4120));
        Assert.True(once.WasAsked(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 7788));
        Assert.True(once.TryAsk(@"C:\IDEA\Armory\Robot\Plate.SLDPRT", 4120));
        Assert.Equal(2, once.Count);
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
    private static (string, DateTimeOffset) At(string path, double seconds) => (path, T0.AddSeconds(seconds));

    [Fact]
    public void Markers_within_three_seconds_of_the_previous_one_are_one_burst()
    {
        var bursts = OpenBursts.MarkerBursts([At("a", 0), At("b", 2.9), At("c", 5.8), At("d", 8.9), At("e", 9.0)]);
        Assert.Equal(2, bursts.Count);
        Assert.Equal(["a", "b", "c"], bursts[0].Paths);                    // 2.9 s apart: together
        Assert.Equal(["d", "e"], bursts[1].Paths);                         // 3.1 s after c: a new burst
        Assert.Equal((T0, T0.AddSeconds(5.8)), (bursts[0].First, bursts[0].Last));
        Assert.Single(OpenBursts.MarkerBursts([At("a", 0), At("b", 3.0)]));  // exactly 3 s is within
    }

    [Fact]
    public void A_burst_lasts_at_most_sixty_seconds()
    {
        // An assembly whose parts keep loading every two seconds for two and a half minutes.
        var opens = Enumerable.Range(0, 75).Select(i => At($"part-{i:D2}", i * 2)).ToList();
        var bursts = OpenBursts.MarkerBursts(opens);
        Assert.Equal([31, 31, 13], bursts.Select(b => b.Paths.Count));
        Assert.All(bursts, b => Assert.True(b.Last - b.First <= OpenBursts.MarkerMaxSpan));
        Assert.Equal(75, bursts.Sum(b => b.Paths.Count));
    }

    [Fact]
    public void A_burst_of_one_file_is_an_open_of_that_file()
    {
        var bursts = OpenBursts.MarkerBursts([At("Plate.SLDPRT", 0), At("Arm.SLDASM", 30)]);
        Assert.Equal([["Plate.SLDPRT"], ["Arm.SLDASM"]], bursts.Select(b => b.Paths));
        Assert.Empty(OpenBursts.MarkerBursts([]));
    }

    [Fact]
    public void Link_opens_within_one_and_a_half_seconds_of_the_first_are_one_group()
    {
        // Measured from the group's first open, not the previous one.
        var groups = OpenBursts.LinkGroups([At("a", 0), At("b", 1.0), At("c", 1.5), At("d", 2.4), At("e", 2.8)]);
        Assert.Equal([["a", "b", "c"], ["d", "e"]], groups.Select(g => g.Paths));
    }

    [Fact]
    public void Each_path_is_in_one_burst_at_its_earliest_open_in_time_order()
    {
        var bursts = OpenBursts.MarkerBursts([At("b", 1), At("A", 1), At("a", 20), At("c", 0)]);
        Assert.Equal([["c", "A", "b"]], bursts.Select(b => b.Paths));     // ties by path; "a" again is the same file
    }
}
