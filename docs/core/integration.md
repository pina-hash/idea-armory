# Adapter integration contract

The core is a library of decisions and durable-intent protocols, not a production sync
service. All rules operate on values, streams, or interfaces. No library code opens
real files, talks to a network, references SolidWorks, or starts a background process.

To preserve the tested guarantees, an agent/server adapter must:

1. Capture each saved byte sequence plus author, path, hash, and stable device-specific
   id durably before the sync layer acknowledges it. Store immutable snapshots until
   server preservation is confirmed. Recover orphan captures before journal replay.
2. Serialize journal writes. Flush the complete frame; tolerate a torn final append;
   retain committed entries and corruption evidence. Stable ids cannot be regenerated
   after a process restart. Replayed effects and their ids commit atomically on the
   server. Replay lock/deletion intents by reconciling current state, not by executing
   stale destructive commands.
3. Archive every intermediate save as a student-attributed retained version or side
   version, even if a newer local save superseded it. Apply the SolidWorks version gate
   to that archival route too. A refused newer CAD format remains a local private draft
   and a pending intent, not a silently successful upload.
4. Execute each `SyncPlan` in order. Await side-version durability before setting
   `PreservedLocalHash`; never set it merely because an upload was attempted. Bind it
   to immutable bytes, and clear it whenever local bytes change.
5. Before replacing or moving a file, coordinate with open application handles, rehash
   or verify the captured file identity, and revalidate the expected remote version.
   If anything changed since planning, discard the plan and reconcile again. Stage
   downloads and use an atomic replace under that coordination. Recovery is a retained
   copy/move, never a deletion of the only bytes.
6. Make lock acquisition and uploads server-authoritative. A shared write or tombstone
   checks the expected remote version and the current device's lock/fencing token in
   the same transaction. A broken lock cannot authorize a stale upload. Persist the
   previous holder's recovery obligation independently of any new lock holder.
7. Persist BASE only after the corresponding local/remote action is durably complete.
   Retrying after lost acknowledgements may create a side version but must not lose
   bytes. Remote tombstones retain all old versions and blobs forever.
8. Resolve `ISavedReleaseReader` with the phase 0 spike (done in 0.3.3:
   `SolidWorksSavedReleaseReader`, [version gate](solidworks-version-gate.md)), provide role permissions from
   the actual authorization system, and make global names and part allocation atomic
   on the server. Enforce COTS/release-state permissions at that boundary as later lanes
   implement them.

The simulation checks crash behavior under these contracts with fake durable snapshots,
journals, server transactions, and application-open state. Real filesystem power-loss
semantics, network races, lock fencing, and CAD behavior still require integration tests.
No bounded randomized simulation establishes correctness for all possible executions.
