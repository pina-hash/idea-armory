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
| A Pack and Go of 60 files (14 names the project already has) is unzipped, the connection drops after 20 are in (files go several at once, so those already on their way may land too: at least 20 and fewer than 46 are in), and the inner folder is renamed: one `armory_rename_folder`, no `armory_move_file`, no `armory_tombstone`, one `folder_renamed` and no `file_moved`, the same file ids, the rest of the 46 added at the new place, the other computer moves its folder in place (no storage GET and no replace for the ones it had, nothing to recovery), one import card ("Added 46 of 60 files to Robot 2027 › Pack") and one card listing the 14, nothing else | `FolderScenarioTests.Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed` |
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

## Folders after the second E2 review (one test each, all guards)

Two adversarial reviewers (safety and product lenses) replayed the folder work and reported 24
findings, several of them the same problem seen through both lenses. All are fixed except one
noted below, each class with one test in `FolderSafetyTests` (named after the review's probe):

| Holds | Test |
|---|---|
| A stop right after the team's folder move here (P18): no removal, nothing to recovery or downloaded, nothing "shares a name" | `A_stop_right_after_the_team_folder_move_here_removes_nothing` |
| A stop right after the window's rename moved the folder here (P19): one change, no removal | `A_stop_right_after_the_window_renames_a_folder_here_removes_nothing` |
| A stop right after a project folder moved for a rename on the site (P17): no folder made again, no download, both saves of a checked-out file kept | `A_stop_right_after_a_project_folder_moves_here_downloads_nothing` |
| A stop right after a folder, and a project folder, went back: records kept, one notice each | `A_stop_right_after_a_folder_is_put_back_keeps_its_records` |
| A stop before or after `armory_rename_folder` and `armory_delete_folder` replays to one change (the named points `before-folder` and `after-folder`) | `A_stop_before_or_after_a_folder_call_replays_to_one_change` |
| A folder deleted over a teammate's newer version or new part (P6, P6b) is put back with her work and one notice naming her; deleted again, one call | `A_folder_deleted_over_newer_work_is_put_back_and_removes_nothing` |
| A project folder dragged into another project's folder (P20) goes back; nothing is added to the other project | `A_project_folder_dragged_into_another_project_is_put_back` |
| Two saves while a folder waits to go back (P4, P4b) both reach the server; My files says "changed" | `Saves_while_a_folder_waits_to_be_put_back_are_all_kept` |
| A take back while the project folder waits (P16) makes the file read-only at once | `A_file_taken_back_while_its_project_folder_waits_is_read_only` |
| Two folders swapped offline (P5): three renames through the temporary name, a save kept | `Folders_swapped_offline_go_through_their_temporary_name` |
| A folder and a folder inside it renamed together (P2): two renames, never undone | `A_folder_and_a_folder_inside_it_renamed_together_are_two_renames` |
| A renamed folder never takes over another file's record or unsent save (P15b) | `A_renamed_folder_never_takes_over_another_files_record` |
| Folders a student makes stay, in the app or Explorer, at the top or inside a known folder (P1, D17) | `Folders_a_student_makes_stay` |
| The window's Rename folder and Delete folder with their answer lost (P3) finish once, the whole folder together | `A_folder_rename_from_the_window_whose_answer_is_lost_finishes_once` |
| 20 shared names cost no `armory_create_file` on any pass; an idle pass is at most 3 calls | `Files_sharing_a_name_cost_no_server_call` |
| The unzipped folder deleted after the shared-name card: nothing waiting, nothing retried, the bytes kept locally | `Deleting_an_unzipped_folder_leaves_nothing_waiting` |
| Two folders put back in one pass: one card naming who, each item saying why | `Two_folders_put_back_in_one_pass_are_one_card_naming_who` |
| A folder of new files moved to another project is added there | `A_folder_of_new_files_moved_to_another_project_is_added_there` |
| An unzip seen over three passes (the last under 10 files) is one import | `An_unzip_seen_over_several_passes_is_one_import` |
| A rename raced by the team's rename (P14) follows the team's name with one notice, never two folders | `A_folder_renamed_here_and_by_the_team_at_once_follows_the_team` |
| A file removed while open says so (not "a newer version is waiting"; row `notInArmory`) | `A_file_removed_while_it_is_open_here_says_so` |

Against the engine before these fixes (`809a236`) 20 of the 21 fail; the one that passes is
the before/after-folder replay, which the review found already held (only its guard was
missing). Each fix was then broken on its own in the fixed engine (one edit, built, the 21
run, restored byte for byte), on 2026-10-07:

| Break | Red |
|---|---|
| no durable record before a folder move | the project-move and put-back stop tests |
| no durable record and no "records follow a file already at its team path" | all four stop-after-move tests |
| no check for newer work before `armory_delete_folder` | `A_folder_deleted_over_newer_work_...` |
| a student's folder not kept by TidyFolders | `Folders_a_student_makes_stay` |
| no save kept while a folder waits | `Saves_while_a_folder_waits_...` |
| the read-only rule not applied where an away folder's file is | `A_file_taken_back_while_..._is_read_only` |
| a rename allowed onto another file's record | `A_renamed_folder_never_takes_over_...` |
| no fresh read between folder calls | the swap, the nested rename and the before/after-folder replay |
| pending renames rewritten by later moves | the swap |
| a project folder in another project's folder followed (and not found by its bytes) | `A_project_folder_dragged_...` |
| no name check before `armory_create_file` | the shared-name cost test and the deleted-unzip test |
| a vanished name-refused file kept waiting | `Deleting_an_unzipped_folder_...` |
| a folder of new files put back | `A_folder_of_new_files_moved_...` |
| no joining of an unzip's later passes | `An_unzip_seen_over_several_passes_...` |
| a rename raced by the team not followed | `A_folder_renamed_here_and_by_the_team_...` |
| the window's rename dropped when its answer is lost | `A_folder_rename_from_the_window_...` |
| a removal while open said as "newer version waiting" | `A_file_removed_while_it_is_open_...` |
| several put-backs titled without names | `Two_folders_put_back_...` |

Two of these are guarded twice on purpose: the durable move record and the rule that a
record follows a file already at its team path each save the team and window stop cases
alone, and a project folder in another project's folder is found by its bytes when the
platform's move is missing; each pair broken together turns its tests red.

Not done: folder rename and delete steps in `SeededRunTests` (its oracle compares fixed
paths; following moved folders would be a new oracle). The named crash points of folder work
are reached by the stop tests above instead.

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

Stage E3 adds the 5,000-file import test (at most 3 cards), which this break also turns red
(see "Stage E3" below). After the three restores the working tree matched the commit (`git status` clean) and
the full suite passed again.

### Rerun after the second E2 review (2026-10-07)

The three breaks were run again against the engine with the review's fixes (the anchors and
the one-line edits unchanged), each restored byte for byte. Each file's SHA-256 before the
break and after the restore, then broken:

- (a) `SyncEngine.Actions.cs` `ef92471fe2235a4cabe47aaaf63e0734700085413361631d427e60279dd3da12`,
  broken `edacf7619187541378a7ff064a91fe82156ea2273a3da33af713051c4cf899a7`. Red: the same 3 of 3
  (`E2E_SEEDS count=200 first=0 elapsed=14.7s failures=200`, first `ARMORY_E2E_SEED=0`).
- (b) `SyncEngine.Folders.cs` `449ddb8b2d7a3b1658d3ff60d9a3bbb8c07d058afb88d4a052145f2c4536972b`,
  broken `12e281cc19eaeda36bd4016b89c8b19dbfe06a288e9a81c121afbb3651d93e2f`. Red: the same 3 of
  the 13 folder scenarios, each at `RpcCount("armory_rename_folder")` (expected 1, actual 0).
- (c) `SyncEngine.View.cs` `ab59569089eead771372bb275cade7fd546167b00d9b42fdac25adff10077417`,
  broken `1c15f4c042de7ced7d5ccbfdc3c9abf175a7aa4b4818c79d2d3516b6cbdfafdc`. Red: the Pack and
  Go scenario, 15 cards instead of 2.


## Stage E3: transfers made fast and visible (2026-10-07)

First measured on `1dcbcd4`, then measured again after the E3 review on `bc60434` (the
review's fixes: the check outs and releases at the end of a pass go `TransferConcurrency` at
a time, the activity panel is honest about time left, check ins and moves, and the claim that
6 is "the knee" is withdrawn). Every number in this section is from `bc60434` unless it says
otherwise (the commit after it changes only documents and a comment). Measured on this
container's 4 CPUs with nothing else running: the container had just started, no other
agent's processes were on it, and the load average, logged every 15 seconds through the
40-minute sweep, stayed between 0.01 and 2.27 (mean 0.31; the peaks are the sweep's own test
host starting and its six-computer classroom). The throughput runs wait on the profile's
simulated delays, not on the CPU.

### Throughput

`ThroughputTests.Transfers_through_a_school_network_profile` through the school network
profile (`LatencyProfile.School`: 60 ms to file storage at 4 MB/s per connection and 25 MB/s
for the whole link, 250 ms for a blob URL, 60 ms per server call). Each run is its own world
(nothing is stored already). Three runs of every point, interleaved (run 1 of every point,
then run 2, then run 3):
`ARMORY_THROUGHPUT=1 ARMORY_THROUGHPUT_RUNS=3 ARMORY_TEST_POSTGRES=... dotnet test tests/Armory.EndToEnd.Tests --filter ThroughputTests`.
Each cell is the median of the three runs, the lowest and highest in brackets; the speedups
are of the medians.

Before (v1, `b18791d`, one file at a time, the same profile and batch): upload 122 files,
98.6 MB in 100.0 s (1.22 files/s, 0.99 MB/s); download 65.4 s (1.86 files/s, 1.51 MB/s).

**Mixed batch, one computer each way.** Alex adds 120 parts of 256 KiB and two of 32 MiB
(122 files, 98.6 MB), then Maria's computer receives them, both moving
`TransferConcurrency` files at once.

| Files at once | Upload | Faster than 1 | Download | Faster than 1 |
|---|---|---|---|---|
| 1 | 97.6 s (97.4 to 100.0) | 1.0x | 64.0 s (64.0 to 65.3) | 1.0x |
| 2 | 49.0 s (48.9 to 49.2) | 2.0x | 32.2 s (32.1 to 32.6) | 2.0x |
| 4 | 26.6 s (26.0 to 26.7) | 3.7x | 17.5 s (17.5 to 17.8) | 3.7x |
| 6 | 18.9 s (18.4 to 19.0) | 5.2x | 12.9 s (12.8 to 12.9) | 5.0x |
| 8 | 15.1 s (14.5 to 15.1) | 6.5x | 10.2 s (10.2 to 10.3) | 6.3x |
| 12 | 11.2 s (10.7 to 11.8) | 8.7x | 9.0 s (9.0 to 9.1) | 7.1x |

At 1 the harness reproduces the v1 numbers, so it measures what it measured then. Against the
first E3 record (`1dcbcd4`, one run each: uploads 34.0 s at 4, 26.3 s at 6, 24.9 s at 8 and
19.2 s at 12) the uploads are faster at every point above 1, because the 122 releases at the
end of the pass no longer go one after the other (at 60 ms each they were about 7 s of every
upload: 26.3 s became 18.9 s at 6). The downloads moved by at most 1.3 s. What still flattens
this batch past 6 is not the network: each 32 MiB file takes about 8.4 s at the 4 MB/s one
connection gets, whatever the concurrency (a file cannot be split: see "Multipart upload and
ranged downloads" below), and the shared 25 MB/s link is never full (10.9 MB/s down and
8.8 MB/s up at 12 at once).

**Small files only, one computer each way.** The same 120 parts without the two large ones
(31.5 MB).

| Files at once | Upload | Faster than 1 | Download | Faster than 1 |
|---|---|---|---|---|
| 1 | 79.1 s (78.7 to 79.2) | 1.0x | 46.4 s (46.4 to 46.9) | 1.0x |
| 2 | 39.6 s (39.5 to 39.7) | 2.0x | 23.3 s (23.2 to 23.3) | 2.0x |
| 4 | 20.1 s (20.0 to 20.1) | 3.9x | 11.7 s (11.7 to 11.8) | 4.0x |
| 6 | 13.7 s (13.6 to 13.7) | 5.8x | 8.0 s (7.9 to 8.1) | 5.8x |
| 8 | 10.6 s (10.3 to 11.2) | 7.5x | 6.0 s (6.0 to 6.2) | 7.7x |
| 12 | 7.1 s (7.1 to 7.2) | 11.1x | 4.1 s (4.1 to 4.1) | 11.3x |
| 24 | 4.1 s (4.0 to 4.2) | 19.3x | 2.3 s (2.3 to 2.4) | 20.2x |

Alone on the link, small files have no knee up to 24: the time nearly halves with every
doubling, both ways. The review measured the uploads with the releases still one after the
other at 21.5 s at 6, 14.9 s at 12 and 12.1 s at 24; that floor is gone.

**A classroom behind one school link.** Six computers (`MeasureClassAsync`) share ONE link
(`NetworkLink`: 25 MB/s for every storage body of every computer; the per-connection rate,
the round trips and the server calls as before). Each adds 20 parts of 256 KiB at the same
moment (120 files, 31.5 MB in all, a class-wide Pack and Go), then each receives the other
five computers' 100 parts at the same moment (157.3 MB through the link, which takes 6.3 s
at 25 MB/s). The time is until the last of the six is done; the link column is what went
through the link per second, down and up.

| Files at once (each computer) | Class upload | Faster than 1 | Class download | Faster than 1 | Link, down / up |
|---|---|---|---|---|---|
| 1 | 13.5 s (13.5 to 13.6) | 1.0x | 38.7 s (38.7 to 38.8) | 1.0x | 4.1 / 2.3 MB/s |
| 2 | 7.0 s (7.0 to 7.0) | 1.9x | 19.6 s (19.5 to 20.3) | 2.0x | 8.0 / 4.5 MB/s |
| 4 | 3.9 s (3.9 to 3.9) | 3.5x | 10.0 s (10.0 to 10.1) | 3.9x | 15.7 / 8.1 MB/s |
| 6 | 3.2 s (3.2 to 3.3) | 4.2x | 7.1 s (7.0 to 7.1) | 5.5x | 22.3 / 9.8 MB/s |
| 8 | 2.7 s (2.7 to 2.8) | 5.0x | 6.9 s (6.9 to 7.0) | 5.6x | 22.8 / 11.5 MB/s |
| 12 | 2.4 s (2.4 to 2.4) | 5.6x | 6.9 s (6.9 to 6.9) | 5.6x | 22.8 / 13.1 MB/s |

**The default stays 6, and that is a judgment call, not a knee in the data.** The first E3
record called 6 "the knee" of the mixed sweep; the review showed that the flattening there came
from the two large files and from the releases sent one after the other, not from the
network, and that the classroom reason given for stopping at 6 had never been measured.
Measured now: one computer alone keeps getting faster up to 24 at once (small files), so for
one computer there is no knee. Six computers behind one 25 MB/s link fill it at 6 at once
each (22.3 MB/s), and past 6 the class's download gains nothing (7.1 s at 6, 6.9 s at 8 and
at 12, against 6.3 s for the bytes alone), while its upload, which waits on four server calls
and a blob URL for every file rather than on the link, still gains a little (3.2 s at 6,
2.4 s at 12). The number that fills a link depends on how many computers share it and how fast
it is (here six computers at 6 each, 36 small-file connections, nearly fill 25 MB/s; twelve
computers would at about 3 each, while one computer alone reaches only 13.5 MB/s at 24), and
neither is known for a given school. 6 is chosen because it
is where this six-computer class stops gaining on the download, it gives one computer alone
5 to 6 times the speed of one at a time, it keeps a classroom's server calls at a few dozen at
once, and every file in flight fits the activity panel's 8 rows. A school with a faster link or
fewer computers would do better with more; the setting is `EngineOptions.TransferConcurrency`.

The default run of the same test (a guard, about 25 s) moves 16 parts at 1 and at the default
and requires the default to be at least twice as fast both ways; it also holds every activity
message to the window's words ("Checking in 0 of 16 files" among them), at most 8 files
listed, more than one file moving at once, at most one message every 250 ms from each
computer, and status lines that start with "Uploading" and "Downloading":

```text
THROUGHPUT label=smoke-c1 files=16 bytes=4194304 upload_s=11.3 upload_passes=1 upload_files_per_s=1.42 upload_MB_per_s=0.37 download_s=6.4 download_passes=1 download_files_per_s=2.51 download_MB_per_s=0.66 a_rpc=70 a_site=17 a_storage=16 b_rpc=3 b_site=17 b_storage=16
THROUGHPUT label=smoke-c6 files=16 bytes=4194304 upload_s=2.4 upload_passes=1 upload_files_per_s=6.65 upload_MB_per_s=1.74 download_s=1.4 download_passes=1 download_files_per_s=11.69 download_MB_per_s=3.06 a_rpc=70 a_site=17 a_storage=16 b_rpc=3 b_site=17 b_storage=16
ACTIVITY messages=10 lines: Uploading 0 of 16 files, 4 MB left | Uploading 6 of 16 files, 2.5 MB left | Uploading 12 of 16 files, 1 MB left | Uploading 12 of 16 files, 0 bytes left | Checking in 0 of 16 files | Downloading 0 of 16 files, 4 MB left | Downloading 6 of 16 files, 2.5 MB left | Downloading 12 of 16 files, 1 MB left
```

("Uploading 12 of 16 files, 0 bytes left" is true: the last bytes are in file storage and
those files wait for their server calls.)

### Multipart upload and ranged downloads

Multipart upload is not built: the frozen contract signs one PUT URL with a signed content
length (D11), so a file goes up in one request, and the brief's "multipart upload for large
files" is a false claim under it. A 32 MiB file therefore takes about 8.4 s at the 4 MB/s one
connection gets, which is what bounds the mixed sweep above. Ranged parallel downloads
(several `Range` GETs of one signed URL, which S3 and R2 accept; the audit listed them as
optional) were considered and not built: `BlobClient` checks a download's SHA-256 while it
streams, and split ranges would have to be written to the staging file in pieces and hashed
after; the large-file download would gain at most the number of ranges, and only for files of
tens of MB, while the uploads of the same files cannot gain at all.

### Activity

What the review measured on `1dcbcd4`, and what each became:

- **Time left** read about twice the truth when it first appeared (600 files at a steady 10
  a second: "about 2 min" at 3 s for 57 s left; on the school profile, 32 s shown for 17.2 s
  left). The averages started from the first sample, taken before any file had finished
  (zero), and climbed over 5 s. They now count from the direction's first file and are
  divided by the weight they have gathered; the same steady run is within a quarter of the
  truth from 3 s on (`StateAndActivityTests.Time_left_is_close_to_the_truth_from_three_seconds_on`,
  which also holds the speed to 7.5 to 12.5 MB/s for a true 10).
- **After the last upload** a 200-file import spent 15.1 s of its 36.9 s releasing locks one
  at a time while the window said "Checking for changes.". The releases (and asked-for check
  outs) now go `TransferConcurrency` at a time, and while they do, the upload direction and
  the status line say "Checking in 412 of 4,900 files"; the 5,000-file import asserts that
  line appears, and its first pass went from 77.8 s to 39.2 s (below).
- **Moving** never reached the window (each file's move opened and closed the lane in one
  step on the engine thread). A move is now one operation from its start to its end, with
  its own count and its one target: "Moving 3 files to Robot 2027 › Drivetrain › Gears" is in
  the activity messages while the window's rename waits for the server, and on the other
  computer while the team's rename is made
  (`ConcurrencyTests.Moving_files_reads_as_one_line_while_the_move_lasts`).
- **My files during an import** listed every file being added (4,900 rows at once in the
  5,000-file import, each "Checked out by you" with a false "You added it while it was open"
  note), because an add's lock was counted as a check out. A lock taken only for an add of a
  closed file, or only for a move or a removal, is no longer listed.

### The 5,000-file import

`ImportScaleTests.A_5000_file_import_is_quiet_and_complete` (a guard): Maria's 100 parts are in
the project; Alex unzips 5,000 small files (10 assemblies of 25 subfolders of 20 files, every
10th a text file, every 50th with the name of one of Maria's parts) and syncs until a pass
sends nothing. It holds at most 3 cards (exactly one import summary, "Added 4,900 of 5,000
files to Robot 2027 › Unzipped", and one card for the 100 shared names), every other file in
Armory with exactly one version, no check out left, the 4,900 read-only and the 100 writable,
no write to an open file, no unpreserved overwrite, and fewer saves of the state document than
server writes. Since the review it also watches the whole import, not only the view at the
end (`QuietWatch`): every view and every activity message (81 views and 86 messages built
while files were moving in the run below) must have at most 3 cards, only of those two kinds,
nothing in My files, no waiting line (the run stays online) and at most 8 files listed as
moving, and the check ins after the uploads must say "Checking in". Run alone (the run before
it, without its output shown, took the same 45 s):

```text
SCAN files=5000 scan_ms=105 rescan_ms=63 first_pass_s=39.2 until_idle_s=39.9 cards=2 passes=2 state_saves=10883 views=84 mid_pass_views=81 activity_messages=86
ACTIVITY lines: Checking in # of # files | Checking in # of # files, less than a minute | Uploading # of # files, # KB left | Uploading # of # files, # KB left, about # sec | Uploading # of # files, # KB left, less than a minute
```

`scan_ms` and `rescan_ms` are the portable test file system's (`PortableVaultFileSystem`),
which lists, reads and hashes all 5,000 files on every scan; the Windows adapter
(`LocalChangeDetector`) hashes again only what changed. On Windows,
`LocalStateTests.Five_thousand_files_with_real_watcher_overflow_match_clean_scan` (5,000 files
made under a stalled watcher, all edited and renamed, 2,500 deleted, compared with a fresh
scan) takes about 12 s on windows-latest, as reported to this stage; its own timing could not
be read from here (the proxy refuses the CI log and artifact downloads), and the whole Platform
tests step of run 37578843714 took 44 s.

The first pass is the import: 4,900 new files through the fake server, PostgreSQL and the fake
storage on this machine, 6 at once, then their check ins, 6 at once. It took 77.8 s on
`1dcbcd4`, when the 4,900 releases went one after the other, and 39.2 s now. In the parallel
end-to-end suite it shares the CPUs with the seeded run (63.4 s there).

### Suite time

| End-to-end suite, alone | Tests | Time |
|---|---|---|
| Before E3 (`4c76cf8`) | 85 | 3 min 6 s |
| After E3 (`1dcbcd4`) | 92 | 4 min 0 s |
| After the E3 review (`bc60434`) | 94 | 2 min 40 s |

The seeded run is the suite's longest test: alone, 163.1 s before E3 and 156.8 s after
(`E2E_SEEDS count=200 first=0 failures=0` both); inside the parallel suite 236.5 s after E3
and 126.1 s after the review (`E2E_SEEDS count=200 first=0 elapsed=126.1s failures=0`), now
that the import beside it is shorter. The whole solution's tests took 4 min 6 s on the commit that records these numbers (the
engine of `bc60434`; every project passed: 626 passed, 0 failed, 68 skipped, all of them the
Windows-only tests of `AgentProcessTests`, `WindowsVaultFileSystemTests`,
`DpapiSecretStoreTests`, `DurableJournalTests`, `LocalStateTests` and `ReplaceAndLockTests`);
`tools/agent-ui/check-ui.mjs` passed on the same tree.

### New guards

| Test | Holds |
|---|---|
| `StateAndActivityTests.The_state_document_written_in_pieces_is_the_whole_document` | the pieces a save writes are the reflection serializer's document, after every kind of change |
| `StateAndActivityTests.Every_field_of_a_file_record_marks_it_changed` | every `FileState` property and list marks its record changed (by reflection) |
| `StateAndActivityTests.Ids_come_from_saved_blocks_and_never_repeat_after_a_restart` | an id block is saved before its first id; a restart never repeats one; a block whose save fails hands out no id (review) |
| `StateAndActivityTests.The_activity_panel_says_what_moves_in_the_window_s_words` | the activity lines, " › " in folder paths, the waiting line, "Checking in", one move operation at a time (review) |
| `StateAndActivityTests.Time_left_is_close_to_the_truth_from_three_seconds_on` | a steady run's time left within a quarter of the truth from 3 s on (review) |
| `StateAndActivityTests.Names_the_server_holds_for_one_share_a_unit_key` | the unit key folds case, accents, compatibility forms, the sharp s and the dotted I at least as much as the server (review) |
| `ClientTests.A_storage_refusal_or_timeout_fails_one_transfer_and_a_dead_connection_is_offline` | storage refusals, timeouts and cut downloads are `StorageTransferException`; a connection that fails is offline |
| `ConcurrencyTests.The_engine_works_on_its_own_thread_never_the_callers` | every step and view on the "Armory engine" thread |
| `ConcurrencyTests.File_detail_answers_while_a_pass_moves_files` | File detail in under 2 s while a pass waits 3 s a request on storage |
| `ConcurrencyTests.A_crash_among_files_moving_at_once_stops_them_all_and_replays_to_one_version_each` | a crash among 12 uploads (five crashes, four of them with a 40 ms disk): no step, no serialization of the state and no half-done record on disk after it; the next engine makes one version each (review) |
| `ConcurrencyTests.A_storage_refusal_fails_that_one_file_and_the_rest_go_on` | a refused upload and a refused download are one file's item; the pass stays online |
| `ConcurrencyTests.The_server_is_read_at_most_twice_a_pass_and_not_again_when_nothing_moved` | two reads of the server at most a pass; a quiet pass reads no project files |
| `ConcurrencyTests.A_slow_disk_makes_fewer_larger_saves` | with 50 ms saves, at most one save for every two server writes |
| `ConcurrencyTests.Names_the_server_holds_for_one_are_sent_one_after_the_other` | "ẞolt" and "ßolt", "İnsert" and "insert" in different folders: the first in path order gets the name at 0 and 30 ms server latency (review) |
| `ConcurrencyTests.Moving_files_reads_as_one_line_while_the_move_lasts` | the Moving line while the window's rename waits for the server and while the other computer moves the folder (review) |
| `ThroughputTests.Transfers_through_a_school_network_profile` | the default at least twice as fast as one at a time, both ways; the activity messages |
| `ImportScaleTests.A_5000_file_import_is_quiet_and_complete` | the 5,000-file import above, every view and message of it |
| `FolderScenarioTests.Pack_and_Go_with_duplicate_names_then_the_inner_folder_is_renamed` (E2 guard, extended) | every view and message of the Pack and Go quiet too, offline stretch included (review) |

### The review's guards against the bugs they guard

Each one-line edit below was built and run with `ARMORY_TEST_POSTGRES` set, then restored
byte for byte (`git checkout`), on `bc60434`; SHA-256 before the edit and after the restore,
then broken:

- **A save after the crash.** `SyncEngine.Persistence.cs`: `StopSaving` set `failing = false`
  instead of `true` (so, as on `1dcbcd4`, a group commit waiting at the crash serializes after
  it). `c0ec708d60330b59139cdaa333e737a2c71648b1f1da602cebcca8142ca1b152`, broken
  `47aa49b3fa0e7d8cfc8aa385e0fba0013813084bac9fe67ef24f4e3f875d1028`. The crash guard failed
  at its first crash: "after-blob #4, 0 ms a save: the state was serialized 1 more times after
  the crash".
- **Names keyed by .NET's casing.** `SyncEngine.cs`: `NameKey` returned
  `name.ToUpperInvariant()`. `3b0b22c32eefc67c115151fac3cbb6b257f0e61fff1be6d2f721dfaa0df66cbb`,
  broken `b646b13f91621e64585a927e67716fa4cdc24a27a9cb50b7aca18767ec1d607e`. The name guard
  failed: "Robot 2027/D/insert.SLDPRT" got the name that belongs to "Robot 2027/C/İnsert.SLDPRT",
  the first in path order.
- **Time left not corrected for its start.** `ActivityTracker.cs`: both rates read the bare
  average (`BytesAverage`, `FilesAverage`) instead of dividing it by its weight.
  `c670581645754dadca8b8cbf18aa11ee4b785abeae1bd0628e4ca65f27707f10`, broken
  `f779adae701b1c1404ed7dffe9f8de4f1fb7166fa1aaa7302a67dd0143a17893`. The time-left guard
  failed at once: "at 3.00 s: 127 s left, truly 57.0 s", the review's "about 2 min" for 57 s.

### Breaks against the quiet guards

Three one-line breaks of `src/Armory.Agent.Engine/SyncEngine.View.cs`, each built and run
against the 5,000-file import and the Pack and Go scenario with `ARMORY_TEST_POSTGRES` set,
then restored byte for byte (`git checkout`). SHA-256 before each break and after each restore
`e9ccab2c502a7f3c1901ba7f7d689a988f12d7e0e7ccb3d4011240e71a1c21b8` (`bc60434`). Red 2 of 2
each:

- **(c) One notice per file**, as before (`var key = kind;` became
  `var key = kind + ":" + item.Path;` at `// MUTATION: notices are grouped by kind`), broken
  `7ec566521a58a8b0f76d285e5ba323d876108700e9bda7f0780ae24449066838`. The Pack and Go scenario
  saw 15 cards instead of 2; the 5,000-file import failed at its first view with cards in it:
  "view 3: 101 cards (nameShared: 1 file shares a name with another file in this project, ...)".
- **(M2) v1's My files rule** (a "waiting to send" row for every file refused or not sent yet,
  copied from `b18791d`), broken `447e3a432e7d6dd580faf5625475731e0bd3d6b6e5ccde69d00d9617b7895ebe`.
  The review found that only the Pack and Go guard caught it, at its final view. Now the import
  fails at its second view: "view 2: 5000 rows in My files, the first Robot 2027/Unzipped/Assembly
  00/Sub 00/Import-0001.SLDPRT "Available" note "Waiting to send"", and the Pack and Go
  scenario at its final My files check.
- **(M3) One "Waiting to send" card per file not sent yet**, broken
  `14244ce264f60ce70798187875ffc73837a79a7fc6d78b596375679e4d144c94`. The review found that both
  guards stayed green (nothing waits once the run is idle; the import's last view had 2 cards in
  this run too). Now both fail on their mid-pass views: the import at "view 2: 5001 cards
  (cantSend: Waiting to send, ...)", the Pack and Go at "view 2: 40 cards".

After the three restores the working tree matched the commit (`git status` showed only a
document being edited).

### Breaks (a) and (b) after the E3 review

The two other v2 breaks, run again against the engine after the review (the anchors and the
one-line edits as in "Deliberate breaks for v2"), each restored byte for byte; SHA-256 before
the break and after the restore, then broken:

- (a) `SyncEngine.Actions.cs` `d574681224e082dee4c2b3ec7630e282a61b72c9587521518d3edeeafa147071`,
  broken `980ecb5b3cfdc00a31e3982df6fb730076911653ce3c7fc202ab5ad9c200d99a`. Red: the same 3 of 3
  (`E2E_SEEDS count=200 first=0 elapsed=11.6s failures=200`, first `ARMORY_E2E_SEED=0`: "A0: Seed
  0000/robot/plate.txt is writable but this computer has not checked it out (step 0)").
- (b) `SyncEngine.Folders.cs` `80e0acc47f1446e6e9554714a953fb47b1e178572390eb7cb22a41dc86eefe44`,
  broken `a916a0e74cf93bae0287b3c41557633d193b3a8aa8a75116a67ce0f9604fcf95`. Red: the same 3 of
  the 13 folder scenarios, each at `RpcCount("armory_rename_folder")` (expected 1).

After both restores `git status` showed only a document being edited.
