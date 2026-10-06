# Deterministic simulation

`SimulationTests.Seeded_scenarios_preserve_every_save_and_converge` runs 10,000 schedules
in the normal test run. Set `ARMORY_STRESS=1` for 1,000,000, or `ARMORY_SEED=<integer>`
to reproduce one schedule. A failing run prints a one-line `REPRO` command with its seed.
The xorshift32 schedule algorithm is specified in source and independent of runtime
changes to `System.Random`.

Each scenario uses two or three clients (including the same student on two devices),
three paths, independent fake disks, open-file sets, save snapshots, journals, online
flags, and logical clocks. Forty-eight random events include edits, saves, opens,
closes, disconnections, reconnections, process crashes, torn journal writes, lock breaks,
server tombstones, local deletions, simultaneous edits, and crashes between sync actions.
Every 16 steps and at the end, the clients go online and idle and must converge.

The fake server retains immutable bytes, main/side version history, tombstones, locks,
break recovery notices, and replay ids. The executor calls the real reconciler, lock
machine, journal, and save recorder. It archives every save and then reconciles the
current file. It has no mutation switches and does not alter a core decision to make
an invariant pass.

After each step, and each client sync during idle draining, the oracle checks that:

- Every saved byte sequence remains locally recoverable or on the server. Once its
  client has completed an online sync, it is retrievable from retained server history.
- Disk replacement/recovery never runs while open or over unpreserved local changes.
- Every shared advance records the issuing device as the actual lock holder.
- All clients match every server latest hash/tombstone after online idle draining.
- The previous server history remains an identical prefix and all referenced blobs exist.

`JournalTests` complements random scheduling by testing every byte cut of a frame, replay
twice, corruption, and acknowledgement loss. Unit tests cover the CAD gate and naming
rules independently of the generic byte-file simulation.

The final local gate passed all 164 tests: `Test Run Successful. Total tests: 164.
Passed: 164.` The 10,000-scenario simulation reported `elapsed=40.247s first_seed=0`;
the full test process took 40.9405 seconds. Normal execution asserts a two-minute
simulation budget. The million-scenario option is implemented but was not run during
this lane. `dotnet build -warnaserror` completed with zero warnings and zero errors.

Since lane A2 (2026-10-06) the journal keeps decoded entries in memory (see
[offline journal](offline-journal.md)), so a replay no longer decodes the whole journal
again. The printed `SIMULATION state_hashes` for seeds 0-99 are identical before (at
`b18791d`) and after every A2 change; the 10,000 scenarios took 67.3 s alone before and
21.8 s alone after on the same 4-CPU machine.

## Explicit check out simulation

`CheckoutSimulationTests.Seeded_explicit_checkout_scenarios_preserve_every_save_and_converge`
(guarded) runs the same scale and the same oracle with `CheckoutMode.Explicit`. It runs
10,000 schedules normally, `ARMORY_STRESS=1` for 1,000,000 and `ARMORY_SEED=<integer>` for
one; a failure prints `REPRO: ARMORY_SEED=<seed> dotnet test --filter
Seeded_explicit_checkout_scenarios`. It asserts its own two-minute budget.

Each scenario has two or three clients and five paths: three shared from the start and
two that start on no computer and no server. Forty-eight random events (weighted toward
files the client has checked out) include check out and "check out and open" (through
the fake server's lock machine; a copy that is behind is brought up to date first, and a
copy with changes nobody checked out is never checked out over), edits and saves (a save
fails on a read-only file, using `CheckoutRules.IsReadOnlyOnDisk`), a forced save after
clearing the read-only attribute, check in (the student may save first), undo check out
(the student closes the file first), adding a new file or re-adding a removed name
(sometimes while it is open), plus everything the v1 simulation does: opens, closes,
disconnections, reconnections, process crashes, torn journal writes, mentor lock breaks,
server removals, local deletions, simultaneous saves and crashes between sync actions.
Check outs and check in or undo requests survive crashes, as the agent persists them. An
add holds its lock until it is closed and is then checked in automatically; a deletion
takes a lock only for itself. Every 16 steps and at the end the clients save what they
can, close everything, go online and check in every check out.

The oracle keeps every v1 invariant and adds:

- Every shared version with bytes is written under a lock its writer took by an explicit
  check out or by adding the file (the harness labels the lock by what the server had,
  never by the plan); a removal may also use a lock the deletion took for itself.
- The shared file advances only at check in, or as the first version of an add or a
  revival.
- A save made without a check out never becomes a shared version.
- Undo never replaces an open file or bytes the server does not keep as a side version.
- No check out survives the drain, and every save is in server history after the
  client's next online sync, as in v1.

A run must also reach every route it models (check outs, check ins, an add's automatic
check in, undo restores, adds, revivals, forced saves, put backs, offline saves, releases
and a kept copy for every `SideVersionReason`); the counts are printed as
`EXPLICIT coverage`. A normal run here reached `check_outs=16788 check_ins=3948
add_check_ins=1629 undos=436 adds=7649 revivals=4293 forced_saves=11044 put_backs=728
offline_saves=3984`; `blocked_adds` (an add whose name another device locked and then
crashed before its first version) is printed but too rare to require.

The full Core test project (both simulations in parallel) passed: `Total tests: 221.
Passed: 221.`, with `EXPLICIT scenarios=10000 elapsed=32.781s` and `SIMULATION
scenarios=10000 elapsed=46.259s` while other builds shared the machine; the test process
took 50.0 seconds.

## Deliberate faults

Run `./scripts/Test-DeliberateBreaks.ps1` from PowerShell to repeat the experiment. The
script changes one production rule, runs only the simulation named by the case's filter,
requires a failing seed, and restores the original source bytes in `finally`. It verifies
that each mutation anchor occurs exactly once and SHA-256 before and after restoration.
It does not count a compilation error as a caught mutation.

| Broken rule | Simulation | Seed | Observed failure | Last verified commit |
|---|---|---:|---|---|
| Download despite an open file | v1 | 0 | Open overwrite at step 9 | `574f9a9` |
| Omit side-version action before conflict download | v1 | 0 | Unsynced local overwrite without side-version acknowledgement at step 12 | `574f9a9` |
| Emit Upload instead of AcquireLockThenUpload | v1 | 1 | Shared-write assertion found a different/missing lock holder | `574f9a9` |
| Parse an incomplete journal frame as committed | v1 | 0 | Truncated JSON reached the replay path instead of being dropped | `574f9a9` |
| Explicit: a change with nobody holding the file plans AcquireLockThenUpload | explicit | 0 | A shared version under a lock taken by Implicit, not a check out or an add, at step 47 | `574f9a9` |
| Explicit: a save while checked out (no request) plans Upload | explicit | 1 | The shared file advanced before check in at step 35 | `574f9a9` |
| Explicit: undo restores without keeping the changes | explicit | 3 | Unsynced local overwrite without side-version acknowledgement at step 39 | `574f9a9` |

All seven were caught. Before A2 the script's `lock-before-upload` anchor no longer
matched the reconciler and the recorded hashes were stale; both are fixed. PowerShell is
not installed in the A2 container, so the seven cases were run by an exact Python port of
the script (same anchors, filters, REPRO match and byte-identical restore check). The
reconciliation source was restored byte-identical with SHA-256
`156B1FFF98E080E2EE98DF7BA5E2F68AE430957FB180BD2DA8F18BFC19E83EBB`.
The journal source was restored byte-identical with SHA-256
`7B23E638C67885B20EBC719532F335CD3574296AF002C52C658AB6CFCBBDD2D1`.
Raw logs and JSON results are generated under ignored `artifacts/mutations/`.

Stryker.NET mutation score: not run because neither the global nor local tool was
installed. No global tool was installed. The targeted checks are not represented
as a general mutation score.

This is executable evidence over bounded schedules and explicit adapter contracts.
It does not prove unbounded distributed correctness or validate real disk durability,
actual CAD bytes, licensing, or production network behavior.
