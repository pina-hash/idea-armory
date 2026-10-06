# Reconciliation

`Reconciler.Plan` is a pure function of `SyncInput`; it does not read a clock, generate
ids, or perform I/O. `PlanAll` sorts by canonical path and rejects duplicate path inputs.
Revision identity includes both version id and content hash, so an intervening version
with identical bytes is still observed. Null remote means absent; a revision with a null
hash means a retained tombstone.

Rules:

- Remote changes download only into a closed file. Open files receive a notification.
- Local changes upload with this device's lock, or acquire it before upload when free.
  Another device, even for the same student, requires a named side version.
- Concurrent changes preserve the local snapshot first, then download or notify.
- A revoked lock requires preservation even if the remote hash did not change.
- Remote tombstones move unchanged bytes to recovery; changed bytes first become a
  side version. A later plan may move them only with `PreservedLocalHash` evidence.
- A local deletion proposes a tombstone under a lock if the remote is unchanged. A
  newer remote downloads instead. Another holder's lock defers the local deletion.
- Offline plans contain journal intents and no server actions. The adapter captures
  every save with `SaveRecorder`, including saves between reconciliation calls.
- SolidWorks shared and side uploads fail closed for unreadable or too-new releases.
  A refusal never authorizes deleting the private snapshot.

## Check out modes

`SyncInput.Checkout` selects the mode. `Automatic` (the default) is v1 and is unchanged:
`CheckoutTests.Automatic_plans_match_the_v1_fingerprint` hashes every Automatic plan over a
small complete state space and compares it with the value recorded at `b18791d`.
`SyncInput.Request` (`None`, `CheckIn`, `Undo`) is ignored in Automatic.

`Explicit` is v2 (PDM style): only a check out takes the lock on a shared file, saves made
while checked out are kept as side versions, and the shared version advances only at check
in or when a file is added. Every `SaveSideVersion` in Explicit carries `SyncAction.Why`
(`Conflict`, `LockBroken`, `SavedWhileCheckedOut`, `ChangedWithoutCheckOut`, `UndoCheckOut`);
Automatic never sets it. "Live" means a remote revision that is not a tombstone.

- E1, local bytes changed (or the lock was broken):
  - a remote tombstone whose bytes were already kept (`PreservedLocalHash`): recovery, as
    Automatic;
  - the SolidWorks release gate, exactly as Automatic;
  - no remote: `AcquireLockThenUpload` (`Upload` when this device holds the lock), an add;
  - a remote tombstone with no base, or with that same tombstone as base: the same, a
    re-add that revives the removed name and its history;
  - a broken lock, a remote that moved since base, a tombstone over a live base, or a lock
    held by another person or by my other device: as Automatic (side version, then download
    or notify when the remote is live). Why is `LockBroken`, `ChangedWithoutCheckOut` when
    only the lock differs, else `Conflict`;
  - a free lock: side version (`ChangedWithoutCheckOut`), then the shared version is put back;
  - this device's lock: `None` keeps a side version (`SavedWhileCheckedOut`) and nothing
    else; `CheckIn` uploads; `Undo` keeps a side version (`UndoCheckOut`), then restores the
    shared version (download, or notify while open).
- E2, local file absent: as Automatic, except that a live file another person or my other
  device has checked out is put back (downloaded) instead of refused.
- E3, local bytes unchanged: as Automatic.
- Offline: as Automatic, but never an `AcquireLock` intent for a file with a live remote.

`CheckoutRules.IsReadOnlyOnDisk(ownership)` is the read-only rule: a file the server has is
read-only on disk unless this device holds its lock. Files the server does not have stay
writable; the adapter decides which files the rule covers.

`CheckoutTests` has one test per row above, the gate on every Explicit route in both modes,
properties over the whole state space (the shared file advances only at check in or add,
every kept copy names why, an open file is never replaced), and the read-only rule.
`CheckoutSimulationTests` runs Explicit under the seeded simulation (see
[simulation](simulation.md)).

Actions are ordered dependencies, not independent commands to launch in parallel. If
side-version upload fails, the following download must not execute. Adapters must
revalidate both local bytes/open state and the remote revision at execution, use server
compare-and-swap, and replan on races. See [integration](integration.md).

`ReconcilerTests` covers every requested branch, same-author other-device locks, revoked
locks, first sync, missing files, deletion conflicts, all three SolidWorks extensions,
deterministic batches, and an exhaustive small-state open/offline matrix. The seeded
simulation additionally exercises execution, crash boundaries, preservation, and convergence.
