namespace Armory.Core;

public sealed record LockHolder(string Person, string Device);
public abstract record FileLock;
public sealed record FreeLock : FileLock;
public sealed record HeldByMe(LockHolder Holder, DateTimeOffset Since, bool HasUnsyncedChanges) : FileLock;
public sealed record HeldByOther(LockHolder Holder, DateTimeOffset Since, bool TheirChangesReachedServer) : FileLock;
public sealed record BreakRequested(FileLock Previous, LockHolder RequestedBy) : FileLock;
public sealed record Broken(LockHolder PreviousHolder, bool MustPreserveOnReconnect) : FileLock;
public enum LockEvent { Acquire, Edit, Synced, Release, RequestBreak, CancelBreak, ConfirmBreak, Reconnect }
public sealed record LockActor(LockHolder Identity, bool CanBreakLocks);
public sealed record LockTransition(bool Succeeded, FileLock State, bool SaveSideVersion, string? Problem, LockHolder? RecoveryOwner = null);

public static class LockMachine
{
    public static LockTransition Apply(FileLock state, LockEvent action, LockActor actor, DateTimeOffset now)
    {
        LockTransition Accept(FileLock next, bool preserve = false) => new(true, next, preserve, null);
        LockTransition Reject(string reason) => new(false, state, false, reason);
        static LockHolder? Holder(FileLock s) => s switch
        {
            HeldByMe me => me.Holder, HeldByOther other => other.Holder, _ => null
        };
        if (state is FreeLock && action == LockEvent.Acquire) return Accept(new HeldByMe(actor.Identity, now, false));
        if (state is Broken revoked && action == LockEvent.Acquire)
            return new(true, new HeldByMe(actor.Identity, now, false), false, null, revoked.PreviousHolder);
        if (state is HeldByMe me && actor.Identity == me.Holder)
        {
            if (action == LockEvent.Edit) return Accept(me with { HasUnsyncedChanges = true });
            if (action == LockEvent.Synced) return Accept(me with { HasUnsyncedChanges = false });
            if (action == LockEvent.Release && !me.HasUnsyncedChanges) return Accept(new FreeLock());
        }
        if (state is HeldByOther other && actor.Identity == other.Holder)
        {
            if (action == LockEvent.Edit) return Accept(other with { TheirChangesReachedServer = false });
            if (action == LockEvent.Synced) return Accept(other with { TheirChangesReachedServer = true });
            if (action == LockEvent.Release && other.TheirChangesReachedServer) return Accept(new FreeLock());
        }
        if (Holder(state) is not null && action == LockEvent.RequestBreak && actor.CanBreakLocks)
            return Accept(new BreakRequested(state, actor.Identity));
        if (state is BreakRequested request && Holder(request.Previous) is { } previous)
        {
            if (action == LockEvent.CancelBreak && actor.CanBreakLocks) return Accept(request.Previous);
            // Preserve unconditionally: an offline holder can have saves the server has not seen.
            if (action == LockEvent.ConfirmBreak && actor.CanBreakLocks) return new(true, new Broken(previous, true), false, null, previous);
        }
        if (state is Broken broken && action == LockEvent.Reconnect && actor.Identity == broken.PreviousHolder)
            return Accept(new FreeLock(), broken.MustPreserveOnReconnect);
        return Reject($"{action} is not legal for {state.GetType().Name} with this actor and unsynced state.");
    }
}
