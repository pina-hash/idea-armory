# Simulation and server parity

Baseline reviewed at `08677b6fc4583160f445d2a26cb06625da3ed7ea`.

## Operation map before implementation

| Simulation `FakeServer` operation | Production RPC | Difference found before implementation |
|---|---|---|
| Read latest shared revision | authenticated reads of `armory_files.current_version_id` and `armory_versions` | No RPC. This is an RLS-protected read. |
| Read the current lock | authenticated read of `armory_locks` | No RPC. This is an RLS-protected read. The lock row identifies its holder by caller email and registered device UUID. |
| Acquire a free lock | `armory_acquire_lock` | The RPC validates the caller-owned device and locks by the email/device pair. |
| Release a held lock | `armory_release_lock` | Only the exact email/device holder pair can release. |
| Break a held lock and notify the former device | `armory_break_lock` plus the change feed | SQL preserves the former email/device pair and publishes both in the break change payload. |
| Add a shared version only when the expected parent is current and the caller holds the lock | `armory_commit_version` | The rules match. SQL turns a stale commit into a side version instead of rejecting before the call. |
| Add a conflict or broken-lock side version | `armory_save_side_version` | The rules match. |
| Add a tombstone only when the expected parent is current and the caller holds the lock | `armory_tombstone` | The rules match. The fake represents deletion as a hashless shared revision, while SQL stores an immutable tombstone and marks the file deleted. |
| Deduplicate a replayed operation by stable operation ID | write RPC receipts | All write RPCs accept an operation UUID and atomically return the stored receipt on replay. |
| Store and retrieve immutable bytes by SHA-256 | none | Intentionally remains an in-memory blob map because storage is outside this lane. |
| Enumerate all shared and side versions and prove none were purged | authenticated reads of `armory_versions`, `armory_side_versions`, and `armory_tombstones` | No RPC. These are RLS-protected reads. |

The device identity, break-notice, and operation-receipt gaps are closed. `PostgresSimulationServer` registers its devices and sends device and operation UUIDs through every production write RPC; it has no adapter-local lock or replay workaround. No simulation invariant is relaxed.

## Findings from server-backed runs

| Failing seed | Finding | Decision and correction |
|---|---|---|
| 0 | After a mentor broke a lock, `armory_acquire_lock` could never acquire that file again because the broken row continued to conflict with the insert. The fake correctly treated `Broken` as acquirable. | Product rule 3 says the first edit takes the lock, and rule 5 says the former holder's later work becomes a side version. SQL was wrong. Acquisition now atomically replaces a broken row, with a contract regression test. |

## Refactor and run evidence

The `ISimulationServer` extraction changed only the static type of the existing fake and
made `FakeServer` implement that contract. The normal 10,000-seed run passed in 83.422
seconds before and after the extraction. The final-state SHA-256 values for seeds 0
through 99 matched pairwise; the exact values are recorded in
`simulation-seed-hashes.txt`.

The four existing deliberate breaks remained caught without changing their seeds: open
file at seed 9, conflict side version at seed 0, lock before upload at seed 1, and torn
journal at seed 0. The normal PostgreSQL run executes 300 seeds and completed in 44
seconds. Set `ARMORY_SERVER_STRESS=1` for the 5,000-seed run, or set
`ARMORY_SERVER_SEED` to reproduce one printed seed.
