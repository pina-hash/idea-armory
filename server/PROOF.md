# Deliberate-break proof

Each mutation below was applied only to a temporary copy or working-tree edit, its named test was run against a fresh throwaway database, and the original SQL was restored byte-for-byte (verified by SHA-256).

| Break | Test that turned red |
|---|---|
| Changed the stale-parent comparison to accept the old parent | `SameParentCommitRaceAdvancesOnceAndPreservesOther` observed a second shared advance instead of a side version. |
| Removed the commit lock-holder predicate | `RpcHappyPathsAndRefusals` accepted a commit after release. |
| Removed `armory_versions_immutable` | `VersionRowsAreImmutable` allowed version byte metadata to change. |
| Changed `armory_projects_read` to `using (true)` | `RlsIsolatesProjectsAndAnonCannotUseRpcs` exposed the second project. |
| Restored `grant select on all tables in schema public` | `GrantsAreLimitedToRlsProtectedArmoryTables` found that the unrelated, explicitly revoked `coin_ledger` table became selectable. |
| Restored the production `armory.test_email` fallback | `ProductionSqlIgnoresTestIdentityAndEveryRpcRefusesWithoutAppIdentity` found that an RPC accepted the caller-set identity instead of refusing. |

The final three SQL files are the restored versions. The proof procedure hashes each before mutation and compares after restoration.

## Server-backed simulation mutations

The 300-seed PostgreSQL simulation was also run against two temporary mutations of
`002_armory_rpcs.sql`. Each failure included the reproducible seed, and the production
SQL was restored to the exact pre-mutation SHA-256 afterward.

| Break | Server-backed simulation result |
|---|---|
| Reversed the stale-parent comparison | Seed 0 failed because the RPC reported a side version where the observed current parent required a shared advance. |
| Removed the commit lock-holder predicate | Seed 0 failed because a client that did not acquire the lock was able to commit instead of receiving PostgreSQL's refusal. |

## S4 device and replay mutations

The S4 contract suite was run after each temporary mutation, followed by restoration of the exact saved SQL bytes:

| Break | Test that turned red |
|---|---|
| Changed lock ownership checks from `(holder_email, holder_device_id)` back to email alone | `SamePersonOnTwoDevicesCannotShareLockAndOtherDeviceCommitBecomesSideVersion` allowed the laptop to act under the school PC's lock. |
| Removed the operation receipt replay check | `SimultaneousReplayWritesExactlyOnceForOneThousandIterations` attempted the second immutable write and failed on the duplicate receipt instead of returning the first result. |
