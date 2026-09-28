namespace Armory.Core;

public sealed record Revision(string Id, string? Hash, string Author)
{
    public bool IsTombstone => Hash is null;
}
public enum LockOwnership { Free, ThisDevice, MyOtherDevice, OtherPerson }
public enum SyncActionKind { None, Download, Upload, AcquireLockThenUpload, SaveSideVersion, MoveLocalToRecovery, NotifyNewerVersionWaiting, Refuse, ProposeTombstone }
public enum IntentKind { AcquireLock, Upload, Tombstone }
public sealed record PendingIntent(IntentKind Kind, VaultPath Path, string? Hash);
public sealed record SyncInput(VaultPath Path, Revision? Base, string? LocalHash, Revision? Remote,
    LockOwnership Lock, bool IsOpen, bool IsOnline, bool LockWasBroken = false,
    SolidWorksRelease? SavedRelease = null, SolidWorksRelease? PinnedRelease = null, string? PreservedLocalHash = null);
public sealed record SyncAction(SyncActionKind Kind, string? Reason = null);
public sealed record SyncPlan(SyncInput Expected, IReadOnlyList<SyncAction> Actions, IReadOnlyList<PendingIntent> Intents);

public static class Reconciler
{
    // Plans are conditional: execute in order, await durability, and replan on any changed input.
    // In particular, SaveSideVersion must commit before a following Download is allowed.
    public static SyncPlan Plan(SyncInput input)
    {
        SyncPlan Actions(params SyncAction[] actions) => new(input, actions, []);
        SyncAction Action(SyncActionKind kind, string? reason = null) => new(kind, reason);
        if (!input.Path.IsValid) return Actions(Action(SyncActionKind.Refuse, "Invalid server path; a lead must fix it."));
        var localChanged = input.LocalHash != input.Base?.Hash;
        var remoteChanged = !SameRevision(input.Base, input.Remote);
        if (!input.IsOnline)
        {
            List<PendingIntent> intents = [];
            if (localChanged)
            {
                if (input.LocalHash is null) intents.Add(new(IntentKind.Tombstone, input.Path, null));
                else
                {
                    if (input.Lock != LockOwnership.ThisDevice) intents.Add(new(IntentKind.AcquireLock, input.Path, input.LocalHash));
                    intents.Add(new(IntentKind.Upload, input.Path, input.LocalHash));
                }
            }
            return new(input, [], intents);
        }

        SyncAction Refresh() => Action(input.IsOpen ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.Download,
            input.IsOpen ? "Close or reload the open file to receive the newer version." : null);
        SyncAction Recover() => Action(input.IsOpen ? SyncActionKind.NotifyNewerVersionWaiting : SyncActionKind.MoveLocalToRecovery);
        if (input.LocalHash is not null && (localChanged || input.LockWasBroken))
        {
            if (input.Remote?.IsTombstone == true && input.PreservedLocalHash == input.LocalHash)
                return Actions(Recover());
            if (IsSolidWorks(input.Path))
            {
                if (input.PinnedRelease is null)
                    return Actions(Action(SyncActionKind.Refuse, "The vault has no pinned SolidWorks release; upload is unsafe."));
                var problem = SolidWorksVersionGate.UploadProblem(input.SavedRelease, input.PinnedRelease.Value);
                if (problem is not null) return Actions(Action(SyncActionKind.Refuse, problem));
            }
            var mustPreserve = input.LockWasBroken || remoteChanged || input.Remote?.IsTombstone == true ||
                input.Lock is LockOwnership.MyOtherDevice or LockOwnership.OtherPerson;
            if (mustPreserve)
            {
                var preserve = Action(SyncActionKind.SaveSideVersion, "Keep local bytes as this student's named side version.");
                // A tombstone never authorizes removing changed local bytes. After side-version
                // acknowledgement, a fresh plan may move the now-preserved local copy to recovery.
                if (input.Remote is null || input.Remote.IsTombstone) return Actions(preserve);
                return Actions(preserve, Refresh()); // MUTATION: conflict preservation
            }
            return Actions(Action(input.Lock == LockOwnership.ThisDevice ? SyncActionKind.Upload : SyncActionKind.AcquireLockThenUpload));
        }
        if (input.LocalHash is null)
        {
            if (input.Remote is null || input.Remote.IsTombstone) return Actions(Action(SyncActionKind.None));
            if (remoteChanged || input.Base is null) return Actions(Refresh());
            if (input.IsOpen) return Actions(Action(SyncActionKind.Refuse, "An open file cannot propose deletion."));
            if (input.Lock is LockOwnership.MyOtherDevice or LockOwnership.OtherPerson)
                return Actions(Action(SyncActionKind.Refuse, "Another device holds the lock; retain the deletion intent."));
            return Actions(Action(SyncActionKind.ProposeTombstone, "Acquire or verify this device's lock, then append a tombstone conditionally."));
        }
        if (input.Remote?.IsTombstone == true) return Actions(Recover());
        if (remoteChanged && input.Remote is not null) return Actions(Refresh());
        return Actions(Action(SyncActionKind.None));
    }

    public static IReadOnlyList<SyncPlan> PlanAll(IEnumerable<SyncInput> inputs)
    {
        var ordered = inputs.OrderBy(i => i.Path).ToArray();
        if (ordered.Select(i => i.Path).Distinct().Count() != ordered.Length)
            throw new ArgumentException("A batch must contain each canonical vault path once.", nameof(inputs));
        return ordered.Select(Plan).ToArray();
    }

    public static bool SameRevision(Revision? left, Revision? right)
        => left?.Id == right?.Id && left?.Hash == right?.Hash;
    public static bool IsSolidWorks(VaultPath path)
        => new[] { ".sldprt", ".sldasm", ".slddrw" }.Any(e => path.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
