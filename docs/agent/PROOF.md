# Agent end-to-end proof

`tests/Armory.EndToEnd.Tests` runs two real `SyncEngine`s ("student A laptop" and "student B
lab PC") on Linux, each with its own temp vault folder, durable stores, device and identity,
through:

- a real PostgreSQL database with `server/sql/001-004` plus the test identity and `is_admin`
  stubs (`tests/Armory.Server.Tests/sql/`);
- the fake ideabosco.com (`tests/Armory.TestSupport/FakeIdeaBosco.cs`), which implements
  contract sections 2 and 3, and the fake Supabase (PostgREST over that database, and token
  refresh);
- the fake S3 from `Armory.Storage.Tests` (`tests/Armory.Storage.Tests/FakeS3.cs`).

Each computer connects through the real loopback connect flow (`ConnectFlow`) with a fake
browser. The platform interfaces are faked only where Linux cannot provide them:
`PortableVaultFileSystem` uses a real temp folder and the platform layer's own ignore list
(`Armory.Platform.Windows.VaultIgnore`); "open in SolidWorks" is a set plus a real `~$`
marker file; read-only attributes are recorded. That double deliberately does not refuse
writes to an open file, so the engine's own check is what the proof exercises; any write
to an open file is recorded as a violation.

## Scenarios (one test each)

| | Scenario | Test |
|---|---|---|
| a | A creates a part, and B receives it | `A_creates_a_part_and_B_receives_it` |
| b | A edits while B has the file open; B is told and B's bytes are untouched | `A_edits_while_B_has_it_open_and_B_is_told_without_losing_bytes` |
| c | Both edit offline, then reconnect: one advance, one named side version, nothing lost | `Offline_edits_by_both_advance_once_and_keep_the_other_as_a_side_version` |
| d | A mentor breaks A's lock while A is offline and edits; A's later bytes become A's side version | `A_mentor_breaks_an_offline_lock_and_the_later_bytes_become_a_side_version` |
| e | A crash between every pair of upload steps (11 points) and a lost acknowledgement replay to exactly one version | `A_crash_between_every_upload_step_replays_to_exactly_one_version` |
| f | The same person on two devices: the second device's work becomes a side version | `The_same_person_on_a_second_device_cannot_commit_over_the_first` |
| g | A rename through `armory_move_file` arrives on B as a move | `A_rename_arrives_on_B_as_a_move` |
| h | A 2026-release SolidWorks file is refused, naming both releases (fake release reader) | `A_2026_SolidWorks_file_is_refused_naming_both_releases` |

## Hardening after an adversarial review

A four-lens review (data safety, crash replay, Core fidelity, proof strength) of the engine
and this proof confirmed 36 findings (6 refuted). Every confirmed engine finding is fixed,
and `HardeningTests` adds one test per class of problem:

| Test | Holds |
|---|---|
| `A_crash_during_a_second_version_replays_to_exactly_one_more_version` | 9 crash points on a second version: exactly one more version, no side version, no lock left |
| `A_broken_lock_without_a_mentor_edit_still_preserves_and_then_moves_on` | a break with no edit still yields the student's side version |
| `Renames_and_downloads_wait_for_an_open_file` | a rename or download never touches an open file, including one opened between plan and write |
| `Files_someone_else_holds_are_read_only_until_they_release` | read-only follows the lock; reopening takes the lock again |
| `A_stale_SolidWorks_marker_stops_holding_the_lock` | a `~$` file left by a crash stops counting as open after 10 minutes |
| `An_Explorer_rename_is_a_move_and_a_refused_one_is_put_back` | an Explorer rename is a server move, never a team-wide delete |
| `A_deletion_needs_two_scans_and_a_removed_name_is_not_reused` | one missed scan never deletes; a removed name is not silently reused |
| `A_refused_draft_never_reaches_the_server_and_never_holds_the_lock` | a gate-refused draft writes nothing and does not block teammates; enforce refuses unknown |
| `Reconnecting_keeps_the_old_device_locks_and_work` | a reconnect (new device id) keeps the old id's lock and commits as a shared version |
| `A_refused_file_never_stops_other_files_from_syncing` | a refused file (too large, or after removal from a project) never stalls other files |

The server gained `ACommitToARemovedFileIsKeptAsASideVersion`: a commit to a removed file
becomes a side version instead of advancing it.

## Seeded run

`SeededRunTests.Seeded_engines_preserve_every_save_and_converge_end_to_end` runs 200 seeds
(seeds 0 to 199; `ARMORY_E2E_SEED=<n>` reproduces one). Each seed is its own project and
two fresh computers, and runs 40 steps chosen by the same xorshift schedule as the Core
simulation: saves, simultaneous saves, opens and closes (with `~$` markers), going offline
and online, agent crashes at a random point inside a pass, mentor lock breaks, mentor
deletions, local deletions, and syncs. After every step it checks, and after every 16 steps
and at the end it drains (everyone online, files closed, four rounds of syncing) and checks:

- no file was written while open;
- server history (versions and side versions) is an immutable, growing prefix and every
  version's bytes are in storage;
- every saved byte sequence is on the server or still in its computer's snapshot store;
- every shared advance was made by the device holding the file's lock (from the change feed);
- nothing on disk is replaced or moved to recovery unless those bytes are already in server
  history;
- after every online pass, a file the other student holds is read-only and one this computer
  holds is not;
- across the 200 seeds, crashes landed at 13 named points inside uploads, side versions,
  downloads and lock changes (the test fails if any is never reached);
- after a drain: every capture from either computer is in server history, byte for byte;
  every save is on the server; both computers' files equal the server's latest state
  (a deleted file is absent).

Result on 2026-10-01 after hardening: `E2E_SEEDS count=200 first=0 elapsed=111.8s failures=0`, with
crashes at 20 distinct points (`after-blob, after-capture, after-commit, after-commit-rpc,
after-download, after-lock, after-release, after-side, after-tombstone, before-AcquireLockThenUpload,
before-Download, before-MoveLocalToRecovery, before-None, before-ProposeTombstone,
before-SaveSideVersion, before-commit, before-lock, before-release, before-replace, before-side`).

The end-to-end suite and the server simulation (`ServerSimulationTests`, five-minute budget)
share one PostgreSQL cluster from separate test processes. On a four-core CI runner, a full
overlap made the simulation 3.8 times slower (321 s against 84 to 98 s), and it failed its
budget twice. Every end-to-end world now holds a cluster-wide advisory lock shared
(`Armory.TestSupport.HeavyRunLock`), and the simulation takes it exclusively before its
clock starts, so the two never run at once. The budget and the 300 scenarios are unchanged.

## Deliberate break

`SyncEngine.IsOpenNow` (the engine's "never overwrite an open file" check, used for Core's
input and again before every write) was changed to return `false`. SHA-256 of
`src/Armory.Agent.Engine/SyncEngine.cs` before the break and after restoring it:
`c901b17a906bf2c9d778703297e76cba9903e0d0f08729dd793879b2caf9f627` (the hardened engine).

```text
REPRO: ARMORY_E2E_SEED=1 dotnet test tests/Armory.EndToEnd.Tests --filter Seeded | open file overwritten at step 10 on A1: replace Seed 0001/robot/plate.txt
E2E_SEEDS count=200 first=0 elapsed=67.5s failures=81
```

80 of 200 seeds failed; the first printed seed is 1. The source was restored byte-identical
and the full run passed again. The same break against the first engine (`c4b5573`, SHA-256
`4c041107945c859d5b053a27a2d946b29892195803e20ab52972bfdabfc25c78`) failed 75 of 200 seeds,
first `ARMORY_E2E_SEED=0`.
