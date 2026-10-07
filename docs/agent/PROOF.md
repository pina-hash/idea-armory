# Agent end-to-end proof

`tests/Armory.EndToEnd.Tests` runs two real `SyncEngine`s ("student A laptop" and "student B
lab PC") on Linux, each with its own temp vault folder, durable stores, device and identity,
through:

- a real PostgreSQL database with `server/sql/001-005` (005 is idea-app's live migration 0232 with a grant-parity section) plus the test identity and `is_admin`
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

## v2 scenarios: check out, folders, projects and imports (one test each, all guards)

| Scenario | Test |
|---|---|
| Two students try to edit the same part: the second can't save and sees who has it | `ScenarioTests.Two_students_try_to_edit_the_same_part` |
| A Pack and Go of 60 files (14 names the project already has) is unzipped, the connection drops after 20 are in, and the inner folder is renamed: one `armory_rename_folder`, no `armory_move_file`, no `armory_tombstone`, one `folder_renamed` and no `file_moved`, the same file ids, the 26 that waited added at the new place, the other computer moves its folder in place (no storage GET and no replace for the 20 it had, nothing to recovery), one import card ("Added 46 of 60 files to Robot 2027 › Pack") and one card listing the 14, nothing else | `FolderScenarioTests.Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed` |
| A folder deleted on disk: one scan does nothing, then one `armory_delete_folder` (no `armory_tombstone`), one `folder_deleted`; the other computer moves its copies to recovery and its empty folder goes | `FolderScenarioTests.A_folder_deleted_on_disk_tombstones_its_files_in_one_call` |
| The same while Maria has a file in it checked out: refused, the folder is downloaded again, exactly one notice naming Maria Lopez | `FolderScenarioTests.A_folder_deleted_on_disk_while_Maria_has_a_file_checked_out_is_put_back` |
| A folder renamed on disk while Maria has a file in it checked out: one refused call, moved back, one notice naming her | `FolderScenarioTests.A_folder_renamed_on_disk_while_Maria_has_a_file_checked_out_is_put_back` |
| A removed name added again: the same id, v1 kept, one `file_revived`, the new bytes current on top of v1 | `FolderScenarioTests.A_removed_name_is_revived_with_its_history` |
| The project renamed on the site while Maria has a file open: she waits with one notice and untouched bytes; then the folder is renamed in place (no download, no removal, no second folder) and her check out goes on | `FolderScenarioTests.A_project_renamed_on_the_site_while_a_file_is_open` |
| A project folder renamed in Explorer is put back ("Project names are changed on ideabosco.com."), and waits, never made again beside it, while a file inside is open | `FolderScenarioTests.An_Explorer_rename_of_a_project_folder_is_put_back` |
| A project folder removed in Explorer is made again and downloaded, never a removal | `FolderScenarioTests.A_project_folder_removed_in_Explorer_is_put_back` |
| An archived project stops syncing, keeps its folder and bits, shows no notice; a check out there can still be checked in | `FolderScenarioTests.An_archived_project_stops_syncing_and_keeps_its_folder` |
| No view or File detail field carries a season | `FolderScenarioTests.No_view_field_carries_a_season` |
| The window's New folder, Add files (files and a whole folder, never over a file), Rename folder and Delete folder, one call each | `FolderScenarioTests.Folders_made_renamed_deleted_and_filled_in_the_app` |
| Without directory identity a folder rename is still one folder move | `FolderScenarioTests.A_folder_renamed_without_directory_identity_is_still_one_folder_move` |
| The team renames a folder while this computer has a file in it open: the rest moves now, the open one once it closes | `FolderScenarioTests.A_folder_renamed_by_the_team_waits_for_an_open_file` |

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

## Deliberate breaks for v2 (brief section 8)

Each break is one line, marked by its anchor comment, replaced in the source, built and run
against the end-to-end tests with `ARMORY_TEST_POSTGRES` set (a skipped test would not count),
then restored byte for byte. The SHA-256 of each file is the committed engine's (`a26c154`):
the same before the break and after the restore. Run on 2026-10-07.

**(a) A checked-in file left writable.** `src/Armory.Agent.Engine/SyncEngine.Actions.cs`,
anchor `// MUTATION: a checked-in file left writable` (the read-only rule's ownership,
`DesiredOwnership`): `? LockOwnership.Free : ownership;` became
`? LockOwnership.Free : ownership == LockOwnership.Free ? LockOwnership.ThisDevice : ownership;`,
so a file nobody has checked out (a file just checked in among them) is made writable.
SHA-256 `9bf1f3bb62ea84bd2700ec785df11fdcf3c5f68d22da33334538dfd735c18c70` before and after,
`1973dc78e217a149a2c9d2be62fb17a8a9a433c5ce94a1178e77d7acb201c27f` broken. Red: 3 of 3.

```text
Failed ScenarioTests.Two_students_try_to_edit_the_same_part          Assert.True(t.A.Disk.IsReadOnly(Plate)) after A's check in
Failed HardeningTests.Files_someone_else_holds_are_read_only_until_they_release   Expected: Free, Actual: ThisDevice
Failed SeededRunTests.Seeded_engines_preserve_every_save_and_converge_end_to_end
REPRO: ARMORY_E2E_SEED=0 dotnet test tests/Armory.EndToEnd.Tests --filter Seeded | A0: Seed 0000/robot/plate.txt is writable but this computer has not checked it out (step 0)
E2E_SEEDS count=200 first=0 elapsed=15.2s failures=200
```

**(b) A directory rename handled as per-file moves.** `src/Armory.Agent.Engine/SyncEngine.Folders.cs`,
anchor `// MUTATION: a directory rename is one folder move` (the branch that turns a
`FolderMove` into one `armory_rename_folder`): `StartFolderRename(source, from, to);` became
`return;`, so the folder move is dropped and the per-file evidence (`DetectLocalMoves`) moves
each file. In the Pack and Go scenario that sent 20 `armory_move_file` calls (20
`file_moved` changes) and no `armory_rename_folder`. SHA-256
`15de5120b612d1eba27ce32a792b6b330fb702d342fd19e5dc148afa2b014302` before and after,
`a73d4e42c4376795613c3362c7eda8b29a2d9384472dcb5815b44d7d333f15f7` broken. Red: 3 of the 13
folder scenarios, each at `RpcCount("armory_rename_folder")` (expected 1, actual 0):

```text
Failed FolderScenarioTests.Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed
Failed FolderScenarioTests.A_folder_renamed_on_disk_while_Maria_has_a_file_checked_out_is_put_back
Failed FolderScenarioTests.A_folder_renamed_without_directory_identity_is_still_one_folder_move
```

**(c) One notice per file.** `src/Armory.Agent.Engine/SyncEngine.View.cs`, anchor
`// MUTATION: notices are grouped by kind` (the key notices are grouped under):
`var key = kind;` became `var key = kind + ":" + item.Path;`. SHA-256
`858632a43ebdd909bcac0bf690a2f9d8d030bc8cdd6315d07468f3b0d3b9afa0` before and after,
`ddf623ee155b104fae12eae66fef4a4e1cf5a9378565e581e8e56797e0f9d3df` broken. Red: the Pack and
Go scenario, where the uploading student saw 15 cards (the import summary and one card per
file sharing a name) instead of 2:

```text
Failed FolderScenarioTests.Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed   Expected: 2, Actual: 15
```

Stage E3 adds the 5,000-file import test (at most 3 cards), which this break must also turn
red. After the three restores the working tree matched the commit (`git status` clean) and
the full suite passed again.
