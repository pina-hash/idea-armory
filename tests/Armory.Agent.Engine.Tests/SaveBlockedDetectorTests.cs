using Armory.Agent.Engine;

namespace Armory.Agent.Engine.Tests;

public sealed class SaveBlockedDetectorTests
{
    [Theory]
    [InlineData((int)SaveBlockedReason.NotCheckedOut, true, "have not checked out this file")]
    [InlineData((int)SaveBlockedReason.NotCheckedOut, false, "have not checked out this file")]
    [InlineData((int)SaveBlockedReason.OtherStudent, false, "Ana Reyes has this file, so you can only look at it")]
    [InlineData((int)SaveBlockedReason.NotYourProject, false, "project you are not on")]
    [InlineData((int)SaveBlockedReason.OtherComputer, false, "another computer")]
    [InlineData((int)SaveBlockedReason.ProjectReadOnly, false, "does not let you change")]
    public void Each_reason_names_the_file_and_one_safe_action(int reason, bool checkout, string words)
    {
        var detector = new SaveBlockedDetector(TimeProvider.System);
        detector.ForStudent("alex@school.test");
        var protection = new SaveProtection((SaveBlockedReason)reason, checkout, "Ana Reyes");
        Assert.True(detector.Attempt("Class/Bracket.SLDPRT", protection));
        var card = Assert.Single(detector.Cards(_ => protection));
        Assert.Equal("SolidWorks can't save Bracket.SLDPRT", card.Title);
        Assert.Contains(words, card.Detail);
        Assert.Contains(checkout ? "save again in SolidWorks" : "use Save As", card.Detail);
        Assert.Equal(checkout ? "Check out" : "Save a copy in your folder", card.Action!.Label);
        Assert.Single(card.Items);
        Assert.Single(card.Action.Paths);
    }

    [Fact]
    public void Bursts_slide_per_file_case_insensitively_and_never_repeat_for_view_refreshes()
    {
        var time = new Clock(); var detector = new SaveBlockedDetector(time);
        var p = new SaveProtection(SaveBlockedReason.NotCheckedOut, true);
        detector.ForStudent("alex@school.test");
        Assert.True(detector.Attempt("Class/A.SLDPRT", p));
        var key = Assert.Single(detector.Cards(_ => p)).Key;
        for (var i = 0; i < 100; i++) { time.Advance(0.1); Assert.False(detector.Attempt("class/a.sldprt", p)); }
        Assert.Equal(key, Assert.Single(detector.Cards(_ => p)).Key);
        Assert.True(detector.Attempt("Class/B.SLDASM", p));
        Assert.Equal(2, detector.Cards(_ => p).Count);
        time.Advance(3);
        Assert.True(detector.Attempt("Class/A.SLDPRT", p));
        Assert.DoesNotContain(detector.Cards(_ => p), c => c.Key == key);
        Assert.Equal(2, detector.Cards(_ => p).Count);
    }

    [Fact]
    public void Unmanaged_paths_and_unknown_protection_never_raise_a_card()
    {
        var d = new SaveBlockedDetector(TimeProvider.System);
        var p = new SaveProtection(SaveBlockedReason.NotCheckedOut, true);
        Assert.False(d.Attempt("Class/A.SLDPRT", p));
        d.ForStudent("alex@school.test");
        Assert.False(d.Attempt("../A.SLDPRT", p));
        Assert.False(d.Attempt("Class/A.SLDPRT", null));
        Assert.Empty(d.Cards(_ => p));
    }

    [Fact]
    public void Ownership_changes_close_save_success_and_student_boundaries_remove_old_cards()
    {
        var d = new SaveBlockedDetector(TimeProvider.System);
        var p = new SaveProtection(SaveBlockedReason.NotCheckedOut, true);
        d.ForStudent("alex@school.test"); d.Attempt("Class/A.SLDPRT", p);
        Assert.Empty(d.Cards(_ => null)); // checkout completed
        d.Attempt("Class/A.SLDPRT", p); d.Forget("Class/A.SLDPRT");
        Assert.Empty(d.Cards(_ => p));
        d.Attempt("Class/A.SLDPRT", p); d.ForStudent("ana@school.test");
        Assert.Empty(d.Cards(_ => p));
        d.Attempt("Class/A.SLDPRT", p); d.ForStudent(null);
        Assert.Empty(d.Cards(_ => p));
    }

    [Fact]
    public void A_fresh_reason_replaces_words_within_the_same_burst_and_memory_is_bounded()
    {
        var d = new SaveBlockedDetector(TimeProvider.System);
        var p = new SaveProtection(SaveBlockedReason.NotCheckedOut, true);
        d.ForStudent("alex@school.test"); d.Attempt("Class/A.SLDPRT", p);
        var key = Assert.Single(d.Cards(_ => p)).Key;
        var other = new SaveProtection(SaveBlockedReason.OtherStudent, false, "Ana");
        Assert.True(d.Attempt("Class/A.SLDPRT", other));
        Assert.Equal(key, Assert.Single(d.Cards(_ => other)).Key);
        for (var i = 0; i < 500; i++) d.Attempt($"Class/Part{i}.SLDPRT", p);
        Assert.Equal(SaveBlockedDetector.Capacity, d.Cards(_ => p).Count);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }
}
