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
