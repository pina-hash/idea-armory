using Armory.Core;

namespace Armory.Core.Tests;

// Desk tracking (idea-app ledger 0521): the queue of what this computer tells the website about
// each file, the end-of-period nudge, and the site admin's automatic check in. One test per rule.
public sealed class DeskTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 22, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = Guid.Parse("8b906ba1-ad34-40fd-bf54-63b7db8f7e8c");
    private static readonly Guid B = Guid.Parse("1c2d3e4f-0000-4000-8000-000000000002");

    [Fact]
    public void Events_wait_in_order_and_carry_the_word_the_server_reads()
    {
        var q = new DeskEventQueue();
        Assert.True(q.Add(A, DeskEventKind.Opened, T0));
        Assert.True(q.Add(A, DeskEventKind.Saved, T0.AddMinutes(1)));
        Assert.True(q.Add(B, DeskEventKind.Closed, T0.AddMinutes(2)));
        Assert.True(q.Add(A, DeskEventKind.Nudged, T0.AddMinutes(3)));
        var batch = q.Take();
        Assert.Equal(["opened", "saved", "closed", "nudged"], batch.Select(e => e.KindWord));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void The_same_kind_for_the_same_file_within_twenty_seconds_is_one_event()
    {
        var q = new DeskEventQueue();
        Assert.True(q.Add(A, DeskEventKind.Saved, T0));
        Assert.False(q.Add(A, DeskEventKind.Saved, T0.AddSeconds(5)));
        Assert.True(q.Add(B, DeskEventKind.Saved, T0.AddSeconds(5)));
        Assert.True(q.Add(A, DeskEventKind.Opened, T0.AddSeconds(6)));
        Assert.True(q.Add(A, DeskEventKind.Saved, T0.AddSeconds(25)));
        Assert.Equal(4, q.Count);
        // An empty id is never sent.
        Assert.False(q.Add(Guid.Empty, DeskEventKind.Opened, T0));
    }

    [Fact]
    public void A_batch_is_at_most_two_hundred_and_a_failed_one_goes_back_in_front()
    {
        var q = new DeskEventQueue();
        for (var i = 0; i < 250; i++) q.Add(Guid.NewGuid(), DeskEventKind.Opened, T0.AddSeconds(i));
        var first = q.Take();
        Assert.Equal(DeskEventQueue.BatchLimit, first.Count);
        Assert.Equal(50, q.Count);
        q.PutBack(first);
        Assert.Equal(250, q.Count);
        Assert.Equal(first[0], q.Take(1)[0]);
    }

    [Fact]
    public void The_queue_keeps_the_newest_thousand()
    {
        var q = new DeskEventQueue();
        var ids = Enumerable.Range(0, 1100).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++) q.Add(ids[i], DeskEventKind.Opened, T0.AddSeconds(i));
        Assert.Equal(DeskEventQueue.Capacity, q.Count);
        Assert.Equal(ids[100], q.Take(1)[0].FileId);
        q.Clear();
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Open_is_the_later_of_open_and_close_and_a_nudge_is_not_a_touch()
    {
        var q = new DeskEventQueue();
        Assert.False(q.IsOpen(A));
        Assert.Null(q.LastTouched(A));
        q.Add(A, DeskEventKind.Opened, T0);
        Assert.True(q.IsOpen(A));
        q.Add(A, DeskEventKind.Closed, T0.AddMinutes(5));
        Assert.False(q.IsOpen(A));
        // A late, older open never reopens it.
        q.Add(A, DeskEventKind.Opened, T0.AddMinutes(1));
        Assert.False(q.IsOpen(A));
        Assert.Equal(T0.AddMinutes(5), q.LastTouched(A));
        q.Add(A, DeskEventKind.Nudged, T0.AddMinutes(30));
        Assert.Equal(T0.AddMinutes(5), q.LastTouched(A));
    }

    [Fact]
    public void A_bell_is_nudged_from_five_minutes_before_to_ten_after_once()
    {
        var bell = T0;
        IReadOnlyList<DateTimeOffset> ends = [bell.AddHours(-2), bell];
        Assert.Null(PeriodNudge.Due(ends, bell.AddMinutes(-6), null));
        Assert.Equal(bell, PeriodNudge.Due(ends, bell.AddMinutes(-5), null));
        Assert.Equal(bell, PeriodNudge.Due(ends, bell.AddMinutes(10), null));
        Assert.Null(PeriodNudge.Due(ends, bell.AddMinutes(11), null));
        Assert.Null(PeriodNudge.Due(ends, bell, lastNudged: bell));
        Assert.Equal(bell, PeriodNudge.Due(ends, bell, lastNudged: bell.AddHours(-2)));
        Assert.Null(PeriodNudge.Due([], bell, null));
    }

    [Fact]
    public void Automatic_check_in_is_off_unless_set_and_never_for_an_open_file()
    {
        var since = T0;
        Assert.False(AutoCheckIn.Due(null, since, null, isOpen: false, now: since.AddDays(2)));
        Assert.False(AutoCheckIn.Due(0, since, null, isOpen: false, now: since.AddDays(2)));
        Assert.False(AutoCheckIn.Due(30, since, null, isOpen: true, now: since.AddDays(2)));
        Assert.False(AutoCheckIn.Due(30, since, null, isOpen: false, now: since.AddMinutes(29)));
        Assert.True(AutoCheckIn.Due(30, since, null, isOpen: false, now: since.AddMinutes(30)));
        // Idle counts from the last open, save or close after the check out.
        Assert.False(AutoCheckIn.Due(30, since, since.AddMinutes(20), isOpen: false, now: since.AddMinutes(45)));
        Assert.True(AutoCheckIn.Due(30, since, since.AddMinutes(20), isOpen: false, now: since.AddMinutes(50)));
        // A touch from before the check out does not count.
        Assert.True(AutoCheckIn.Due(30, since, since.AddMinutes(-90), isOpen: false, now: since.AddMinutes(30)));
    }
}
