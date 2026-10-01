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

| Break | Test that turned red |
|---|---|
| Removed the lock-holder predicate from `armory_move_file` (`answer:=f.deleted_at is null`) | `MoveFileRequiresTheLockHolderAndWritesFileMoved`: the move with no lock held succeeded (`Assert.False` saw true). |
| Removed the last-mentor guard from `armory_remove_member` | `TheLastMentorCanNeverBeRemoved`: the sole mentor removed themself (`Assert.Throws` saw no exception). |
| Removed the last-mentor guard from `armory_add_member` (demotion path) | `TheLastMentorCanNeverBeRemoved`: the sole mentor demoted themself to `cad_lead`. |

Every other `AgentRpcTests` test stayed green during each break, so each test isolates its rule.
