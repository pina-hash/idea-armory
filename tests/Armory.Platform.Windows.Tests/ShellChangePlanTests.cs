using Armory.Core;

namespace Armory.Platform.Windows.Tests;

public sealed class ShellChangePlanTests
{
    private const string Root = @"C:\IDEA\Armory";

    private static BadgeEntry E(string path, BadgeState state) => new(path, state);

    [Fact]
    public void The_same_badges_need_no_notice()
    {
        BadgeEntry[] badges = [E("Robot 2027", BadgeState.Mine), E("Robot 2027/A.SLDPRT", BadgeState.Mine)];
        var plan = ShellChangePlan.For(Root, badges, [E(@"ROBOT 2027\a.sldprt", BadgeState.Mine), E("robot 2027", BadgeState.Mine)]);
        Assert.Equal(ShellChangeKind.Nothing, plan.Kind);
        Assert.Empty(plan.Paths);
        Assert.Same(ShellChangePlan.Nothing, ShellChangePlan.For(Root, [], [E("x", BadgeState.None)]));
    }

    [Fact]
    public void A_few_changes_name_each_item_added_removed_or_changed()
    {
        var plan = ShellChangePlan.For(Root + @"\",
            [E("Robot 2027/A.SLDPRT", BadgeState.Synced), E("Robot 2027/B.SLDPRT", BadgeState.Locked), E("Robot 2027/C.SLDPRT", BadgeState.Mine)],
            [E("Robot 2027/A.SLDPRT", BadgeState.Mine), E("Robot 2027/C.SLDPRT", BadgeState.Mine), E("Robot 2027/D.SLDPRT", BadgeState.Synced), E("Robot 2027", BadgeState.Mine)]);
        Assert.Equal(ShellChangeKind.Items, plan.Kind);
        Assert.Equal(new[]
        {
            @"C:\IDEA\Armory\Robot 2027",
            @"C:\IDEA\Armory\Robot 2027\A.SLDPRT",
            @"C:\IDEA\Armory\Robot 2027\B.SLDPRT",
            @"C:\IDEA\Armory\Robot 2027\D.SLDPRT",
        }, plan.Paths);
    }

    [Fact]
    public void Up_to_256_changes_are_items_and_more_are_their_folders()
    {
        var at = Enumerable.Range(0, 256).Select(i => E($"P{i % 4}/Part {i}.SLDPRT", BadgeState.Synced)).ToArray();
        Assert.Equal(ShellChangeKind.Items, ShellChangePlan.For(Root, [], at).Kind);
        Assert.Equal(256, ShellChangePlan.For(Root, [], at).Paths.Count);
        var over = Enumerable.Range(0, 257).Select(i => E($"P{i % 4}/Sub/Part {i}.SLDPRT", BadgeState.Synced)).Append(E("Top.SLDPRT", BadgeState.Mine)).ToArray();
        var plan = ShellChangePlan.For(Root, [], over);
        Assert.Equal(ShellChangeKind.Folders, plan.Kind);
        // Each folder once (case ignored), and a top-level file's folder is the vault root itself.
        Assert.Equal(new[] { Root, @"C:\IDEA\Armory\P0\Sub", @"C:\IDEA\Armory\P1\Sub", @"C:\IDEA\Armory\P2\Sub", @"C:\IDEA\Armory\P3\Sub" }, plan.Paths);
    }

    [Fact]
    public void More_than_256_folders_refresh_the_vault_root_once()
    {
        var many = Enumerable.Range(0, 600).Select(i => E($"Folder {i}/Part.SLDPRT", BadgeState.Locked)).ToArray();
        var plan = ShellChangePlan.For(@"C:/IDEA/Armory/", many, []);
        Assert.Equal(ShellChangeKind.Root, plan.Kind);
        Assert.Equal(new[] { Root }, plan.Paths);
    }

    [Fact]
    public void Two_entries_for_one_path_count_as_the_strongest()
    {
        var plan = ShellChangePlan.For(Root, [E("a.prt", BadgeState.Locked), E("A.PRT", BadgeState.Attention)], [E("a.prt", BadgeState.Attention)]);
        Assert.Equal(ShellChangeKind.Nothing, plan.Kind);
    }
}
