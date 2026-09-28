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

Actions are ordered dependencies, not independent commands to launch in parallel. If
side-version upload fails, the following download must not execute. Adapters must
revalidate both local bytes/open state and the remote revision at execution, use server
compare-and-swap, and replan on races. See [integration](integration.md).

`ReconcilerTests` covers every requested branch, same-author other-device locks, revoked
locks, first sync, missing files, deletion conflicts, all three SolidWorks extensions,
deterministic batches, and an exhaustive small-state open/offline matrix. The seeded
simulation additionally exercises execution, crash boundaries, preservation, and convergence.
