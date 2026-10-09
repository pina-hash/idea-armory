using Armory.Core;

namespace Armory.Core.Tests;

public sealed class BadgeRulesTests
{
    private static BadgeFacts File(BadgeFileStatus status, BadgeCheckout checkout, bool inArmory = true, bool cantSend = false, bool cantRead = false) =>
        new("Robot 2027/Arm/Plate.SLDPRT", status, checkout, inArmory, cantSend, cantRead);

    public static IEnumerable<object[]> EveryStatusAndCheckout() =>
        from status in Enum.GetValues<BadgeFileStatus>()
        from checkout in Enum.GetValues<BadgeCheckout>()
        from inArmory in new[] { true, false }
        select new object[] { status, checkout, inArmory };

    // The owner's table, one row per combination: Attention for can't be uploaded, can't be read,
    // changed without a check out and kept copies; Mine for checked out here (changed or not)
    // and new files on their way in; Locked for someone else or my other computer; Synced for
    // up to date and checked out by nobody; nothing for a file that is not on this computer.
    [Theory]
    [MemberData(nameof(EveryStatusAndCheckout))]
    public void Every_status_and_checkout_has_exactly_the_documented_badge(BadgeFileStatus status, BadgeCheckout checkout, bool inArmory)
    {
        var expected =
            status == BadgeFileStatus.NotOnThisComputer ? BadgeState.None
            : status == BadgeFileStatus.KeptCopy ? BadgeState.Attention
            : status == BadgeFileStatus.Changed && checkout != BadgeCheckout.Mine ? BadgeState.Attention
            : checkout == BadgeCheckout.Mine ? BadgeState.Mine
            : !inArmory && status is BadgeFileStatus.Waiting or BadgeFileStatus.Uploading ? BadgeState.Mine
            : checkout is BadgeCheckout.Other or BadgeCheckout.MyOtherComputer ? BadgeState.Locked
            : inArmory && status == BadgeFileStatus.Synced ? BadgeState.Synced
            : BadgeState.None;
        Assert.Equal(expected, BadgeRules.For(File(status, checkout, inArmory)));
    }

    [Fact]
    public void Each_of_the_five_states_the_owner_listed_has_its_badge()
    {
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.Synced, BadgeCheckout.Mine)));
        Assert.Equal(BadgeState.Locked, BadgeRules.For(File(BadgeFileStatus.Synced, BadgeCheckout.Other)));
        Assert.Equal(BadgeState.Locked, BadgeRules.For(File(BadgeFileStatus.Synced, BadgeCheckout.MyOtherComputer)));
        // Changed and not checked in shares Mine: a change you can make is on a file you have.
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.Changed, BadgeCheckout.Mine)));
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.Uploading, BadgeCheckout.Mine)));
        Assert.Equal(BadgeState.Synced, BadgeRules.For(File(BadgeFileStatus.Synced, BadgeCheckout.Available)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.Waiting, BadgeCheckout.Available, inArmory: false, cantSend: true)));
    }

    [Fact]
    public void Attention_is_stronger_than_every_other_fact()
    {
        foreach (var checkout in Enum.GetValues<BadgeCheckout>())
        foreach (var status in Enum.GetValues<BadgeFileStatus>().Where(s => s != BadgeFileStatus.NotOnThisComputer))
        {
            Assert.Equal(BadgeState.Attention, BadgeRules.For(File(status, checkout, cantSend: true)));
            Assert.Equal(BadgeState.Attention, BadgeRules.For(File(status, checkout, cantRead: true)));
        }
        // A change made without a check out, here or on my other computer's file.
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.Changed, BadgeCheckout.Available)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.Changed, BadgeCheckout.Other)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.Changed, BadgeCheckout.MyOtherComputer)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.KeptCopy, BadgeCheckout.Mine)));
    }

    [Fact]
    public void A_new_file_not_in_armory_yet_is_mine_and_a_refused_one_needs_attention()
    {
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.Waiting, BadgeCheckout.Available, inArmory: false)));
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.Uploading, BadgeCheckout.Available, inArmory: false)));
        Assert.Equal(BadgeState.None, BadgeRules.For(File(BadgeFileStatus.NotInArmory, BadgeCheckout.Available, inArmory: false)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.NotInArmory, BadgeCheckout.Available, inArmory: false, cantSend: true)));
        // Uploading a save of a file nobody has checked out: not up to date yet, no badge.
        Assert.Equal(BadgeState.None, BadgeRules.For(File(BadgeFileStatus.Uploading, BadgeCheckout.Available)));
        Assert.Equal(BadgeState.None, BadgeRules.For(File(BadgeFileStatus.Downloading, BadgeCheckout.Available)));
        Assert.Equal(BadgeState.None, BadgeRules.For(File(BadgeFileStatus.NewerWaiting, BadgeCheckout.Available)));
    }

    // Feedback N4: a check in asked for while the file is open waits for it to close. It is
    // still checked out by you on this computer until then, changed or not.
    [Fact]
    public void A_check_in_waiting_for_its_file_to_close_stays_mine()
    {
        Assert.Equal(BadgeState.Mine, BadgeRules.For(File(BadgeFileStatus.CheckingInWhenClosed, BadgeCheckout.Mine)));
        Assert.Equal(BadgeState.Attention, BadgeRules.For(File(BadgeFileStatus.CheckingInWhenClosed, BadgeCheckout.Mine, cantRead: true)));
        Assert.Equal(BadgeState.Mine, BadgeRules.For(BadgeFacts.FromNames("Robot 2027/Arm/Plate.SLDPRT", "checkingInWhenClosed", "mine", true, false, false)));
    }

    [Fact]
    public void A_file_that_is_not_on_this_computer_never_has_a_badge()
    {
        foreach (var checkout in Enum.GetValues<BadgeCheckout>())
            Assert.Equal(BadgeState.None, BadgeRules.For(File(BadgeFileStatus.NotOnThisComputer, checkout, cantSend: true, cantRead: true)));
    }

    [Fact]
    public void The_strength_order_is_attention_mine_locked_synced()
    {
        Assert.True(BadgeState.Attention > BadgeState.Mine);
        Assert.True(BadgeState.Mine > BadgeState.Locked);
        Assert.True(BadgeState.Locked > BadgeState.Synced);
        Assert.True(BadgeState.Synced > BadgeState.None);
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<BadgeState>().Select(s => (int)s));
    }

    [Fact]
    public void Attention_and_mine_climb_to_every_folder_and_locked_and_synced_do_not()
    {
        var entries = BadgeRules.WithFolders([
            new("Robot 2027/Arm/Wrist/Plate.SLDPRT", BadgeState.Mine),
            new("Robot 2027/Arm/Elbow.SLDPRT", BadgeState.Locked),
            new("Robot 2027/Drive/Gear.SLDPRT", BadgeState.Synced),
            new("Robot 2027/Drive/Broken.SLDASM", BadgeState.Attention),
            new("Class 2026/Bracket.SLDPRT", BadgeState.Locked),
            new("Class 2026/Lesson 1/Cube.SLDPRT", BadgeState.Synced),
            new("Robot 2027/Notes.txt", BadgeState.None),
        ]);
        Assert.Equal(new BadgeEntry[]
        {
            new("Class 2026/Bracket.SLDPRT", BadgeState.Locked),
            new("Class 2026/Lesson 1/Cube.SLDPRT", BadgeState.Synced),
            new("Robot 2027", BadgeState.Attention),
            new("Robot 2027/Arm", BadgeState.Mine),
            new("Robot 2027/Arm/Elbow.SLDPRT", BadgeState.Locked),
            new("Robot 2027/Arm/Wrist", BadgeState.Mine),
            new("Robot 2027/Arm/Wrist/Plate.SLDPRT", BadgeState.Mine),
            new("Robot 2027/Drive", BadgeState.Attention),
            new("Robot 2027/Drive/Broken.SLDASM", BadgeState.Attention),
            new("Robot 2027/Drive/Gear.SLDPRT", BadgeState.Synced),
        }, entries);
    }

    [Fact]
    public void The_vault_root_never_has_a_badge_and_paths_merge_ignoring_case_and_separators()
    {
        var entries = BadgeRules.WithFolders([
            new(@"\Top.SLDPRT\", BadgeState.Attention),
            new(@"Robot 2027\Arm\Plate.SLDPRT", BadgeState.Mine),
            new("robot 2027/arm/plate.sldprt", BadgeState.Attention),
            new("ROBOT 2027/ARM/Other.SLDPRT", BadgeState.Locked),
            new("", BadgeState.Attention),
            new("/", BadgeState.Mine),
        ]);
        Assert.DoesNotContain(entries, e => e.Path.Length == 0);
        Assert.Equal(BadgeState.Attention, Assert.Single(entries, e => e.Path == "Top.SLDPRT").State);
        Assert.Equal(BadgeState.Attention, Assert.Single(entries, e => e.Path.Equals("Robot 2027/Arm/Plate.SLDPRT", StringComparison.OrdinalIgnoreCase)).State);
        Assert.Equal(BadgeState.Attention, Assert.Single(entries, e => e.Path.Equals("Robot 2027", StringComparison.OrdinalIgnoreCase)).State);
        Assert.Equal(BadgeState.Attention, Assert.Single(entries, e => e.Path.Equals("Robot 2027/Arm", StringComparison.OrdinalIgnoreCase)).State);
        Assert.All(entries, e => Assert.DoesNotContain('\\', e.Path));
    }

    [Fact]
    public void Entries_applies_the_rules_then_the_folders()
    {
        var entries = BadgeRules.Entries([
            new("Robot 2027/Arm/Plate.SLDPRT", BadgeFileStatus.Changed, BadgeCheckout.Mine, true, false, false),
            new("Robot 2027/Gear.SLDPRT", BadgeFileStatus.Synced, BadgeCheckout.Available, true, false, false),
            new("Robot 2027/Gone.SLDPRT", BadgeFileStatus.NotOnThisComputer, BadgeCheckout.Other, true, false, false),
        ]);
        Assert.Equal(new BadgeEntry[]
        {
            new("Robot 2027", BadgeState.Mine),
            new("Robot 2027/Arm", BadgeState.Mine),
            new("Robot 2027/Arm/Plate.SLDPRT", BadgeState.Mine),
            new("Robot 2027/Gear.SLDPRT", BadgeState.Synced),
        }, entries);
    }

    [Fact]
    public void The_engine_names_map_one_to_one_and_unknown_names_throw()
    {
        string[] statuses = ["synced", "changed", "uploading", "downloading", "waiting", "newerWaiting", "keptCopy", "notInArmory", "notOnThisComputer", "checkingInWhenClosed"];
        Assert.Equal(Enum.GetValues<BadgeFileStatus>(), statuses.Select(BadgeFacts.StatusOf));
        string[] checkouts = ["available", "mine", "other", "myOtherComputer"];
        Assert.Equal(Enum.GetValues<BadgeCheckout>(), checkouts.Select(BadgeFacts.CheckoutOf));
        Assert.Throws<ArgumentException>(() => BadgeFacts.StatusOf("Synced"));
        Assert.Throws<ArgumentException>(() => BadgeFacts.StatusOf("0"));
        Assert.Throws<ArgumentException>(() => BadgeFacts.CheckoutOf("someoneElse"));
        var facts = BadgeFacts.FromNames("a/b.SLDPRT", "keptCopy", "myOtherComputer", true, false, true);
        Assert.Equal(new BadgeFacts("a/b.SLDPRT", BadgeFileStatus.KeptCopy, BadgeCheckout.MyOtherComputer, true, false, true), facts);
    }
}
