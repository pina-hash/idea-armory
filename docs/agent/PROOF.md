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
- after a drain: every capture from either computer is in server history, byte for byte;
  every save is on the server; both computers' files equal the server's latest state
  (a deleted file is absent).

Result on 2026-10-01 at the step 4 commit: `E2E_SEEDS count=200 first=0 elapsed=116.0s failures=0`
(84.0 s on an idle machine).

## Deliberate break

`SyncEngine.IsOpenNow` (the engine's "never overwrite an open file" check, used for Core's
input and again before every write) was changed to return `false`. SHA-256 of
`src/Armory.Agent.Engine/SyncEngine.cs` before the break and after restoring it:
`4c041107945c859d5b053a27a2d946b29892195803e20ab52972bfdabfc25c78` (the engine as committed in
`c4b5573`; the break ran in a clean worktree of that commit).

```text
REPRO: ARMORY_E2E_SEED=0 dotnet test tests/Armory.EndToEnd.Tests --filter Seeded | open file overwritten at step 9 on B0: recovery Seed 0000/class/gear.SLDPRT
E2E_SEEDS count=200 first=0 elapsed=99.2s failures=75
```

75 of 200 seeds failed; the first printed seed is 0. The source was restored byte-identical
and the full run passed again.
