using Armory.Core;

namespace Armory.Core.Tests;

public sealed class LockTests
{
    private static readonly LockHolder Owner = new("Alex", "laptop");
    private static readonly LockActor Actor = new(Owner, true);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public static IEnumerable<object[]> TransitionCases()
    {
        for (var state = 0; state < 7; state++)
        foreach (var action in Enum.GetValues<LockEvent>())
            yield return [state, action];
    }
    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void Every_state_event_pair_has_explicit_legality(int stateIndex, LockEvent action)
    {
        FileLock[] states = [new FreeLock(), new HeldByMe(Owner, Now, false), new HeldByMe(Owner, Now, true),
            new HeldByOther(Owner, Now, true), new HeldByOther(Owner, Now, false),
            new BreakRequested(new HeldByMe(Owner, Now, true), Owner), new Broken(Owner, true)];
        LockEvent[][] legal = [ [LockEvent.Acquire], [LockEvent.Edit, LockEvent.Synced, LockEvent.Release, LockEvent.RequestBreak],
            [LockEvent.Edit, LockEvent.Synced, LockEvent.RequestBreak], [LockEvent.Edit, LockEvent.Synced, LockEvent.Release, LockEvent.RequestBreak],
            [LockEvent.Edit, LockEvent.Synced, LockEvent.RequestBreak], [LockEvent.CancelBreak, LockEvent.ConfirmBreak], [LockEvent.Acquire, LockEvent.Reconnect] ];
        var result = LockMachine.Apply(states[stateIndex], action, Actor, Now);
        Assert.Equal(legal[stateIndex].Contains(action), result.Succeeded);
        if (!result.Succeeded) { Assert.Same(states[stateIndex], result.State); Assert.NotNull(result.Problem); }
    }

    [Fact]
    public void Lifecycle_tracks_dirty_status_and_refuses_dirty_release()
    {
        var acquired = LockMachine.Apply(new FreeLock(), LockEvent.Acquire, Actor, Now);
        var held = Assert.IsType<HeldByMe>(acquired.State);
        Assert.Equal(Now, held.Since);
        var dirty = LockMachine.Apply(held, LockEvent.Edit, Actor, Now).State;
        Assert.True(Assert.IsType<HeldByMe>(dirty).HasUnsyncedChanges);
        Assert.False(LockMachine.Apply(dirty, LockEvent.Release, Actor, Now).Succeeded);
        var clean = LockMachine.Apply(dirty, LockEvent.Synced, Actor, Now).State;
        Assert.IsType<FreeLock>(LockMachine.Apply(clean, LockEvent.Release, Actor, Now).State);
    }

    [Fact]
    public void Authority_is_supplied_and_other_devices_cannot_impersonate_holder()
    {
        var held = new HeldByMe(Owner, Now, true);
        var unauthorized = new LockActor(new("Maria", "desktop"), false);
        foreach (var action in Enum.GetValues<LockEvent>())
            Assert.False(LockMachine.Apply(held, action, unauthorized, Now).Succeeded);
        Assert.False(LockMachine.Apply(held, LockEvent.Synced, new(new("Alex", "desktop"), false), Now).Succeeded);
        var pending = LockMachine.Apply(held, LockEvent.RequestBreak, unauthorized with { CanBreakLocks = true }, Now).State;
        Assert.False(LockMachine.Apply(pending, LockEvent.ConfirmBreak, unauthorized, Now).Succeeded);
        Assert.False(LockMachine.Apply(pending, LockEvent.CancelBreak, unauthorized, Now).Succeeded);
        Assert.Equal(held, LockMachine.Apply(pending, LockEvent.CancelBreak, Actor, Now).State);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Broken_lock_preserves_on_reconnect_even_if_server_believed_holder_clean(bool dirty)
    {
        var held = new HeldByMe(Owner, Now, dirty);
        var pending = LockMachine.Apply(held, LockEvent.RequestBreak, Actor, Now).State;
        var broken = LockMachine.Apply(pending, LockEvent.ConfirmBreak, Actor, Now).State;
        Assert.False(LockMachine.Apply(broken, LockEvent.Reconnect, new(new("Alex", "other"), true), Now).Succeeded);
        var reconnected = LockMachine.Apply(broken, LockEvent.Reconnect, Actor, Now);
        Assert.True(reconnected.SaveSideVersion);
        Assert.IsType<FreeLock>(reconnected.State);
    }

    [Fact]
    public void Another_holder_can_acquire_revoked_lock_without_erasing_recovery_obligation()
    {
        var next = new LockActor(new("Maria", "desktop"), false);
        var result = LockMachine.Apply(new Broken(Owner, true), LockEvent.Acquire, next, Now);
        Assert.True(result.Succeeded);
        Assert.Equal(next.Identity, Assert.IsType<HeldByMe>(result.State).Holder);
        Assert.Equal(Owner, result.RecoveryOwner);
    }
}
