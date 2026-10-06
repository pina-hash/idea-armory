# Deliberate-break proof

Each mutation below was applied only to a temporary copy or working-tree edit, its named test was run against a fresh throwaway database, and the original SQL was restored byte-for-byte (verified by SHA-256).

| Break | Test that turned red | Last verified commit |
|---|---|---|
| Changed the stale-parent comparison to accept the old parent | `SameParentCommitRaceAdvancesOnceAndPreservesOther` observed a second shared advance instead of a side version. | `b5094f3` |
| Removed the commit lock-holder predicate | `RpcHappyPathsAndRefusals` accepted a commit after release. | `b5094f3` |
| Removed `armory_versions_immutable` | `VersionRowsAreImmutable` allowed version byte metadata to change. | `b5094f3` |
| Changed `armory_projects_read` to `using (true)` | `RlsIsolatesProjectsAndAnonCannotUseRpcs` exposed the second project. | `b5094f3` |
| Restored `grant select on all tables in schema public` | `GrantsAreLimitedToRlsProtectedArmoryTables` found that the unrelated, explicitly revoked `coin_ledger` table became selectable. | `b5094f3` |
| Restored the production `armory.test_email` fallback | `ProductionSqlIgnoresTestIdentityAndEveryRpcRefusesWithoutAppIdentity` found that an RPC accepted the caller-set identity instead of refusing. | `b5094f3` |

The final three SQL files are the restored versions. The proof procedure hashes each before mutation and compares after restoration.

## Server-backed simulation mutations

The 300-seed PostgreSQL simulation was also run against two temporary mutations of
`002_armory_rpcs.sql`. Each failure included the reproducible seed, and the production
SQL was restored to the exact pre-mutation SHA-256 afterward.

| Break | Server-backed simulation result | Last verified commit |
|---|---|---|
| Reversed the stale-parent comparison | `Seeded_scenarios_preserve_every_save_and_converge_against_postgres` failed at seed 0 because the RPC reported a side version where the observed current parent required a shared advance. | `b5094f3` |
| Removed the commit lock-holder predicate | `Seeded_scenarios_preserve_every_save_and_converge_against_postgres` failed at seed 0 because a client that did not acquire the lock was able to commit instead of receiving PostgreSQL's refusal. | `b5094f3` |

## S4 device and replay mutations

The S4 contract suite was run after each temporary mutation, followed by restoration of the exact saved SQL bytes:

| Break | Test that turned red | Last verified commit |
|---|---|---|
| Changed lock ownership checks from `(holder_email, holder_device_id)` back to email alone | `SamePersonOnTwoDevicesCannotShareLockAndOtherDeviceCommitBecomesSideVersion` allowed the laptop to act under the school PC's lock. | `b5094f3` |
| Removed the operation receipt replay check | `SimultaneousReplayWritesExactlyOnceForOneThousandIterations` attempted the second immutable write and failed on the duplicate receipt instead of returning the first result. | `b5094f3` |

## Lane A agent RPC mutations (2026-10-01)

Each break edited `server/sql/004_armory_agent.sql` in the working tree, rebuilt, ran
`AgentRpcTests` against a fresh throwaway database, and restored the saved copy. SHA-256
before and after every restoration: `adfc31bbce432fc8293305320019d7abe90b246b0f5940446becb9d068e54024`.
The engine hardening then changed 004 (a commit to a removed file is kept as a side
version), so all three breaks were run again on the final file with the same results:
SHA-256 before and after `f25ae4aca74a832f61520f4ee599d72e563ab4bfccd3cb3f6602b9570892bf2d`,
12 of 13 `AgentRpcTests` green during each break and 13 of 13 after restoring.

| Break | Test that turned red |
|---|---|
| Removed the lock-holder predicate from `armory_move_file` (`answer:=f.deleted_at is null`) | `MoveFileRequiresTheLockHolderAndWritesFileMoved`: the move with no lock held succeeded (`Assert.False` saw true). |
| Removed the last-mentor guard from `armory_remove_member` | `TheLastMentorCanNeverBeRemoved`: the sole mentor removed themself (`Assert.Throws` saw no exception). |
| Removed the last-mentor guard from `armory_add_member` (demotion path) | `TheLastMentorCanNeverBeRemoved`: the sole mentor demoted themself to `cad_lead`. |

Every other `AgentRpcTests` test stayed green during each break, so each test isolates its rule.

## Contract v2 mutations (`005_v2.sql`, idea-app 0232, 2026-10-06)

Each break edited `server/sql/005_v2.sql` in the working tree with an exact-match script (the
edit is refused unless the old text occurs the stated number of times), rebuilt
`tests/Armory.Server.Tests` (which copies the SQL beside the tests), ran all 15 `V2RpcTests`
against fresh throwaway databases on the PostgreSQL 16 cluster at 127.0.0.1:55432, then copied
the saved original back and rebuilt. `sha256sum server/sql/005_v2.sql` before every break and
after every restore:
`4a9993a204936c9dca5f46c1176b0c378c941f0a008b2fbace31659ee0e2dd18`, and `git status` showed the
file clean after each restore. In every break exactly one of the 15 tests turned red; the other
14 stayed green. The SQL being broken is idea-app's 0232 copied verbatim, so these prove the
tests against the exact production bodies.

| Break | Exact edit | Test that turned red, and its message |
|---|---|---|
| (a) Dropped FOR UPDATE on the subtree's rows, the counterpart of the design's `armory_hold_folder` | In `armory_folder_files`, `order by folder, lower(name), id` followed by `for update;` became `order by folder, lower(name), id;` | `ACheckOutNeverLandsInsideAFolderRenameOrDelete`: "Race 0: a check-out landed inside a folder rename." (the free-running race, V2RpcTests.cs line 527; red in all 4 recorded runs, always at race 0) |
| (a) also dropping the project row locks | The edit above, and in `armory_rename_folder` and `armory_delete_folder` `perform 1 from public.armory_projects where id = p_project for update;` became `... where id = p_project;` | `ACheckOutNeverLandsInsideAFolderRenameOrDelete`: "The folder operation finished while another person's check-out was still open." (the deterministic interleaving, line 465) |
| (a) dropping only the project row locks | The two `armory_projects ... for update` edits alone | `ACheckOutNeverLandsInsideAFolderRenameOrDelete`: "The folder operation finished while another person's check-out was still open." (the check out taken again after a take back, which updates the lock row and so takes no lock on the file row; the check out goes first there, and the folder-first order is not held even unbroken, `docs/server/contract.md` open point 2) |
| (b) Dropped the device clause from "someone else" | In `armory_refuse_checked_out`, `where f.id = any(p_files) and (l.holder_email <> e or l.holder_device_id <> p_device)` became `where f.id = any(p_files) and l.holder_email <> e` | `FolderRenameIsRefusedWhileSomeoneElseOrMyOtherComputerHasAFileCheckedOut`: "Assert.Throws() Failure: No exception was thrown / Expected: typeof(Npgsql.PostgresException)" at line 358, the rename from the lab PC while the same student's laptop holds Part13.SLDPRT |
| (c) Kept the tombstone row on revival | In `armory_create_file`, deleted the line `delete from public.armory_tombstones where file_id = dead.id;` | `CreatingARemovedNameRevivesTheSameFileWithItsHistory`: "Assert.Equal() Failure: Values differ / Expected: 0 / Actual: 1" at line 230, the tombstone count after the revival |

Line numbers are those of `5e0851d`, where V2RpcTests gave each test its own people and the
revival test gained the 0.1.0 commit check. Breaks (b) and (c) were run again on that file the
same way (SHA-256 above before and after): the same single test turned red with the same message,
14 of 15 green each time.

Found while proving, reported to lane W rather than changed (005 must stay 0232's bytes):
`armory_rename_folder` and `armory_delete_folder` lock the project row before the files, while
`armory_acquire_lock` (and every per-file write) locks the file row first and the project row
second, through the change feed's foreign key. A true race between a folder operation and a
check out in that folder therefore deadlocks (SQLSTATE 40P01, one side rolled back, never a wrong
result): 6 to 37 of the 60 free-running races in six runs of the test above. A deterministic
reproduction with a 2-second sleep trigger after the lock insert, in a scratch database, aborted
the check out with "deadlock detected" while the rename moved its file. A variant that serializes
folder operations with `pg_advisory_xact_lock(hashtextextended('armory_folders:' ||
p_project::text, 0))` instead of the project row lock and adds `perform 1 from
public.armory_locks where file_id = any(ids) order by file_id for update;` after
`armory_folder_files` passed all 15 tests with 0 deadlocks in 60 races (run the same way, then
restored to the SHA-256 above). The client resends 40P01 and 40001 meanwhile
(`docs/agent/CLIENT.md`).
