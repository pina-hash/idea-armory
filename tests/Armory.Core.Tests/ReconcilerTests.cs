using Armory.Core;

namespace Armory.Core.Tests;

public sealed class ReconcilerTests
{
    [Fact]
    public void SolidWorksUploadWithoutPinnedReleaseIsRefused()
    {
        Assert.True(VaultPath.TryCreate("Robot/Arm.SLDPRT", out var path, out _));
        var prior = new Revision("v1", "old", "Alex");
        var input = new SyncInput(path, prior, "new", prior, LockOwnership.ThisDevice, false, true,
            SavedRelease: new(2025), PinnedRelease: null);

        var action = Assert.Single(Reconciler.Plan(input).Actions);

        Assert.Equal(SyncActionKind.Refuse, action.Kind);
        Assert.Contains("no pinned SolidWorks release", action.Reason);
    }

    private static SyncActionKind[] Kinds(SyncInput input) => Reconciler.Plan(input).Actions.Select(a => a.Kind).ToArray();

    [Theory]
    [InlineData(false, SyncActionKind.Download)]
    [InlineData(true, SyncActionKind.NotifyNewerVersionWaiting)]
    public void New_remote_respects_open_file(bool open, SyncActionKind expected)
        => Assert.Equal([expected], Kinds(Fixtures.Input with { Remote = Fixtures.Newer, IsOpen = open }));

    [Theory]
    [InlineData(LockOwnership.Free, SyncActionKind.AcquireLockThenUpload)]
    [InlineData(LockOwnership.ThisDevice, SyncActionKind.Upload)]
    [InlineData(LockOwnership.MyOtherDevice, SyncActionKind.SaveSideVersion)]
    [InlineData(LockOwnership.OtherPerson, SyncActionKind.SaveSideVersion)]
    public void Local_edit_obeys_lock(LockOwnership ownership, SyncActionKind expected)
    {
        var actions = Kinds(Fixtures.Input with { LocalHash = "edit", Lock = ownership });
        Assert.Equal(expected, actions[0]);
        if (expected == SyncActionKind.SaveSideVersion)
            Assert.DoesNotContain(actions, a => a is SyncActionKind.Upload or SyncActionKind.AcquireLockThenUpload);
    }

    [Theory]
    [InlineData(false, SyncActionKind.Download)]
    [InlineData(true, SyncActionKind.NotifyNewerVersionWaiting)]
    public void Conflict_preserves_first_then_refreshes_or_waits(bool open, SyncActionKind last)
        => Assert.Equal([SyncActionKind.SaveSideVersion, last], Kinds(Fixtures.Input with { LocalHash = "edit", Remote = Fixtures.Newer, IsOpen = open }));

    [Theory]
    [InlineData("base", false, SyncActionKind.MoveLocalToRecovery)]
    [InlineData("base", true, SyncActionKind.NotifyNewerVersionWaiting)]
    [InlineData("edit", false, SyncActionKind.SaveSideVersion)]
    [InlineData("edit", true, SyncActionKind.SaveSideVersion)]
    public void Tombstones_preserve_bytes_and_open_handles(string local, bool open, SyncActionKind expected)
        => Assert.Equal([expected], Kinds(Fixtures.Input with { LocalHash = local, IsOpen = open, Remote = new("v2", null, "Maria") }));

    [Fact]
    public void Preserved_tombstone_conflict_can_converge_on_next_plan()
        => Assert.Equal([SyncActionKind.MoveLocalToRecovery], Kinds(Fixtures.Input with { LocalHash = "edit", PreservedLocalHash = "edit", Remote = new("v2", null, "Maria") }));

    [Theory]
    [InlineData(false, SyncActionKind.ProposeTombstone)]
    [InlineData(true, SyncActionKind.Download)]
    public void Local_deletion_proposes_tombstone_unless_remote_advanced(bool changed, SyncActionKind expected)
        => Assert.Equal([expected], Kinds(Fixtures.Input with { LocalHash = null, Remote = changed ? Fixtures.Newer : Fixtures.Base }));

    [Theory]
    [InlineData(LockOwnership.OtherPerson)][InlineData(LockOwnership.MyOtherDevice)]
    public void Deletion_cannot_bypass_other_device_lock(LockOwnership ownership)
        => Assert.Equal([SyncActionKind.Refuse], Kinds(Fixtures.Input with { LocalHash = null, Lock = ownership }));

    [Fact]
    public void Open_deleted_file_cannot_tombstone()
        => Assert.Equal([SyncActionKind.Refuse], Kinds(Fixtures.Input with { LocalHash = null, IsOpen = true }));

    [Fact]
    public void Broken_lock_preserves_even_when_server_content_did_not_change()
        => Assert.Equal(SyncActionKind.SaveSideVersion, Kinds(Fixtures.Input with { LockWasBroken = true, LocalHash = "edit" })[0]);

    [Fact]
    public void Offline_intents_are_ordered_and_have_no_server_actions()
    {
        var plan = Reconciler.Plan(Fixtures.Input with { IsOnline = false, LocalHash = "edit" });
        Assert.Empty(plan.Actions);
        Assert.Equal([IntentKind.AcquireLock, IntentKind.Upload], plan.Intents.Select(i => i.Kind));
        Assert.Equal("edit", plan.Intents[1].Hash);
        Assert.Equal(IntentKind.Tombstone, Assert.Single(Reconciler.Plan(Fixtures.Input with { IsOnline = false, LocalHash = null }).Intents).Kind);
        Assert.Empty(Reconciler.Plan(Fixtures.Input with { IsOnline = false }).Intents);
        Assert.Single(Reconciler.Plan(Fixtures.Input with { IsOnline = false, LocalHash = "edit", Lock = LockOwnership.ThisDevice }).Intents);
    }

    [Theory]
    [InlineData(".SLDPRT")][InlineData(".sldasm")][InlineData(".SldDrw")]
    public void Every_SolidWorks_upload_path_is_gated(string extension)
    {
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        foreach (var remote in new[] { Fixtures.Base, Fixtures.Newer, new Revision("dead", null, "Maria") })
        {
            var input = Fixtures.Input with { Path = Fixtures.Path("part" + extension), LocalHash = "edit", Lock = ownership, Remote = remote, SavedRelease = new(2026), PinnedRelease = new(2025) };
            var action = Assert.Single(Reconciler.Plan(input).Actions);
            Assert.Equal(SyncActionKind.Refuse, action.Kind);
            Assert.Contains("2026", action.Reason);
            Assert.Contains("2025", action.Reason);
            Assert.Equal(SyncActionKind.Refuse, Kinds(input with { SavedRelease = null })[0]);
            Assert.NotEqual(SyncActionKind.Refuse, Kinds(input with { SavedRelease = new(2025) })[0]);
        }
    }

    [Fact]
    public void Same_hash_new_version_is_still_a_remote_change()
        => Assert.Equal([SyncActionKind.Download], Kinds(Fixtures.Input with { Remote = new("v2", "base", "Maria") }));

    [Fact]
    public void Empty_initial_and_new_file_states_are_defined()
    {
        Assert.Equal([SyncActionKind.None], Kinds(Fixtures.Input));
        Assert.Equal([SyncActionKind.None], Kinds(Fixtures.Input with { Base = null, LocalHash = null, Remote = null }));
        Assert.Equal([SyncActionKind.AcquireLockThenUpload], Kinds(Fixtures.Input with { Base = null, Remote = null, LocalHash = "new" }));
        Assert.Equal([SyncActionKind.Download], Kinds(Fixtures.Input with { Base = null, LocalHash = null }));
        Assert.Equal([SyncActionKind.Refuse], Kinds(Fixtures.Input with { Path = default }));
    }

    [Fact]
    public void Plans_and_cross_path_order_are_deterministic()
    {
        var a = Fixtures.Input with { Path = Fixtures.Path("z/file.txt") };
        var b = Fixtures.Input with { Path = Fixtures.Path("a/file.txt") };
        var first = Reconciler.PlanAll([a, b]);
        var second = Reconciler.PlanAll([b, a]);
        Assert.Equal(first.Select(p => p.Expected.Path), second.Select(p => p.Expected.Path));
        Assert.Equal(first.SelectMany(p => p.Actions), second.SelectMany(p => p.Actions));
        Assert.Equal(b.Path, first[0].Expected.Path);
        Assert.Throws<ArgumentException>(() => Reconciler.PlanAll([a, a]));
    }

    [Fact]
    public void Exhaustive_small_state_space_never_emits_download_for_open_file()
    {
        Revision?[] revisions = [null, Fixtures.Base, Fixtures.Newer, new("v3", null, "Maria")];
        foreach (var baseline in revisions)
        foreach (var remote in revisions)
        foreach (var local in new string?[] { null, "base", "edit" })
        foreach (var ownership in Enum.GetValues<LockOwnership>())
        {
            var input = Fixtures.Input with { Base = baseline, Remote = remote, LocalHash = local, Lock = ownership, IsOpen = true };
            Assert.DoesNotContain(Reconciler.Plan(input).Actions, a => a.Kind is SyncActionKind.Download or SyncActionKind.MoveLocalToRecovery);
            Assert.Empty(Reconciler.Plan(input with { IsOnline = false }).Actions);
        }
    }

    // One plan's every observable part: each action with its reason, flags and why, and each intent.
    private static string Describe(SyncPlan plan)
        => string.Join(";", plan.Actions.Select(a => $"{a.Kind}|{a.Reason}|{a.ReleaseNotChecked}|{a.Why}")) + "#" +
           string.Join(";", plan.Intents.Select(i => $"{i.Kind}|{i.Path}|{i.Hash}"));

    // The property the engine's open-file question rests on (0.3.3, feedback N6): where OpenMatters
    // says no, the plan is the same whether the file is open or not, over the whole small state
    // space in both check out modes and with every request. The engine then asks the platform only
    // about the files where it says yes.
    [Fact]
    public void Open_state_never_changes_a_plan_where_open_matters_says_it_does_not()
    {
        var asked = 0;
        var skipped = 0;
        foreach (var shape in CheckoutTests.SmallStateSpace())
        foreach (var mode in Enum.GetValues<CheckoutMode>())
        foreach (var request in Enum.GetValues<CheckoutRequest>())
        {
            var input = shape with { Checkout = mode, Request = request };
            var closed = Describe(Reconciler.Plan(input with { IsOpen = false }));
            var open = Describe(Reconciler.Plan(input with { IsOpen = true }));
            var matters = Reconciler.OpenMatters(input);
            Assert.Equal(matters, Reconciler.OpenMatters(input with { IsOpen = !input.IsOpen }));
            if (matters) { asked++; continue; }
            skipped++;
            Assert.True(closed == open, $"{input}: {closed} when closed, {open} when open");
        }
        // Not trivially true: most of the space needs no question at all.
        Assert.True(skipped > asked, $"{skipped} inputs skipped the question and {asked} asked it");
    }

    // The cases a pass meets most: a synced file, a saved file checked out here, an offline pass and
    // a new file never need the question; a newer version, a removal, a missing file and bytes kept
    // before the shared version comes back do.
    [Fact]
    public void Open_matters_only_where_the_plan_would_wait_for_the_file_to_close()
    {
        var synced = Fixtures.Input with { Checkout = CheckoutMode.Explicit };
        Assert.False(Reconciler.OpenMatters(synced));
        Assert.False(Reconciler.OpenMatters(synced with { LocalHash = "edit", Lock = LockOwnership.ThisDevice }));
        Assert.False(Reconciler.OpenMatters(synced with { LocalHash = "edit", Lock = LockOwnership.ThisDevice, Request = CheckoutRequest.CheckIn }));
        Assert.False(Reconciler.OpenMatters(synced with { Remote = Fixtures.Newer, IsOnline = false }));
        Assert.False(Reconciler.OpenMatters(synced with { Base = null, Remote = null, LocalHash = "new" }));
        Assert.True(Reconciler.OpenMatters(synced with { Remote = Fixtures.Newer }));
        Assert.True(Reconciler.OpenMatters(synced with { Remote = new("v3", null, "Maria") }));
        Assert.True(Reconciler.OpenMatters(synced with { LocalHash = null }));
        Assert.True(Reconciler.OpenMatters(synced with { LocalHash = "edit" }));
        Assert.True(Reconciler.OpenMatters(synced with { LocalHash = "edit", Lock = LockOwnership.ThisDevice, Request = CheckoutRequest.Undo }));
    }
}
