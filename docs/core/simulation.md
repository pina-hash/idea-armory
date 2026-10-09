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
twice, corruption, and acknowledgment loss. Unit tests cover the CAD gate and naming
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
`CheckoutRules.NextCheckOutStep` and the fake server's lock machine: a copy that is missing
or behind is brought up to date first, and bytes saved without a check out are kept and
the shared version put back first, so the lock is only taken over the live shared
version), edits and saves (a save fails on a read-only file), a forced save after clearing
the read-only attribute (later saves before the next pass are forced too), check in (the
student may save first), undo check out (the student closes the file first), adding a new
file or re-adding a removed name (sometimes while it is open), plus everything the v1
simulation does: opens, closes, disconnections, reconnections, process crashes, torn
journal writes (carried to the next append, as in v1, and aimed at a writable file),
mentor lock breaks (Take back), server removals, local deletions, simultaneous saves and
crashes between sync actions (sometimes right after a save, so the server can keep it and
the answer be lost). Check outs and check in or undo requests survive crashes, as the
agent persists them. An add holds its lock only while it is open: a closed add is created,
committed and checked in in one pass; a deletion takes a lock only for itself. Every 16
steps and at the end the clients save what they can, close everything, go online and check
in every check out.

Each client keeps the read-only attribute as its agent last applied it, using
`CheckoutRules.IsReadOnlyOnDisk` for a file the server has (an add stays writable): every
online pass, on a check out, on a staged download before it replaces the file, and before a
check in or undo releases the lock; offline passes apply it again from the ownership last
known. So a holder whose check out was taken back keeps saving until its next pass, as on
a real computer.

Since 0.3.3 (feedback N4) the agent plans from what its scan read, not from the disk. A file
SolidWorks opens while it is writable is, three times in four, held for writing: the agent's
scan can't read it and keeps the hash it read last (`LocalFile.Unread`), every save goes to
disk unseen until it is closed, a check out of it can't hash it, it can't be deleted, and a
download or a move to recovery over it is refused (Replace reads the destination first, and
refuses one that changed since the scan). Right after SolidWorks closes a checked-out part,
another program sometimes grabs it until the client's next pass (not open, still unreadable).
A check in, an undo, an add's automatic check in and a deletion's own lock are let go through
`CheckoutRules.NextCheckInStep`: the scan's view must be clean, then the file is read (when it
can be) and the lock goes only when it is closed and its bytes are the shared version.

The oracle keeps every v1 invariant and adds:

- Every shared version with bytes is written under a lock its writer took by an explicit
  check out or by adding the file (the harness labels the lock by what the server had,
  never by the plan); a removal may also use a lock the deletion took for itself.
- The shared file advances only at check in, or as the first version of an add or a
  revival.
- Every save that is not forced is to a file the server does not have, or to one this
  device held the lock of when its agent last made it writable. The oracle reads this from
  the server's lock table when the attribute is applied, never through `CheckoutRules`, so a
  read-only rule that leaves a checked-in file writable fails here.
- A save made without a check out (forced, or after the student cleared the attribute)
  never becomes a shared version.
- A closed add never keeps its lock after a pass.
- Undo never replaces an open file or bytes the server does not keep as a side version.
- A lock is let go only over the shared version: at every release, the bytes on disk (not the
  scan's view of them) equal the server's latest version (0.3.3).
- The working copy is never reverted while the student holds it: a download never replaces
  bytes saved under this device's own check out unless they became a shared version, for an
  undo (kept first), or after a mentor took that check out back (0.3.3).
- An edit made while this device held the file's check out is never lost to a file made
  read-only under it: closing (or the drain) drops an unsaved edit only when a mentor took that
  check out back, because the lock is never let go while the file is open (0.3.3).
- No check out survives the drain, and every save is in server history after the
  client's next online sync, as in v1.

A run must also reach every route it models (check outs, check ins, an add's automatic
check in, undo restores, adds, revivals, forced saves, put backs, offline saves, releases,
saves after a Take back, torn journal writes, crashes in the middle of a sync, lost
acknowledgments, saves through a SolidWorks write hold, check ins that waited for the file to
close and check ins that waited to read it, and a kept copy for every `SideVersionReason`);
the counts are printed as
`EXPLICIT coverage`. A normal run here reached `check_outs=19019 check_ins=5796
add_check_ins=1370 undos=564 adds=11791 revivals=7615 forced_saves=12208 put_backs=698
offline_saves=5926 releases=46526 saves_after_take_back=219 torn_writes=15253
mid_sync_crashes=13115 lost_acknowledgments=220`; `blocked_adds` (an add whose name
another device locked and then crashed before its first version) is printed but too rare
to require. With the write hold (0.3.3) a run reached `check_outs=19094 check_ins=6113
add_check_ins=361 undos=502 adds=11743 revivals=7528 forced_saves=12160 put_backs=703
blocked_adds=8 offline_saves=5869 releases=46494 saves_after_take_back=217 torn_writes=15423
mid_sync_crashes=13094 lost_acknowledgments=267 held_saves=9518 waits_for_close=2151
read_agains=217` in 19.5 s (an add SolidWorks holds is seen only once it is closed, so fewer
adds are checked in after saves made while open).

The full Core test project (both simulations in parallel) passed: `Total tests: 226.
Passed: 226.`, with `EXPLICIT scenarios=10000 elapsed=21.368s` and `SIMULATION
scenarios=10000 elapsed=27.459s` while other builds shared the machine; the test process
took 28.5 seconds. The v1 `SIMULATION state_hashes` for seeds 0-99 were again identical to
those at `b18791d`.

## Deliberate faults

Run `./scripts/Test-DeliberateBreaks.ps1` from PowerShell to repeat the experiment. The
script changes one production rule, runs only the simulation named by the case's filter,
requires a failing seed, and restores the original source bytes in `finally`. It verifies
that each mutation anchor occurs exactly once and SHA-256 before and after restoration.
It does not count a compilation error as a caught mutation.

| Broken rule | Simulation | Seed | Observed failure | Last verified commit |
|---|---|---:|---|---|
| Download despite an open file | v1 | 0 | Open overwrite at step 9 | `b375f8a` |
| Omit side-version action before conflict download | v1 | 0 | Unsynced local overwrite without side-version acknowledgement at step 12 (the v1 message, quoted) | `b375f8a` |
| Emit Upload instead of AcquireLockThenUpload | v1 | 1 | Shared-write assertion found a different/missing lock holder | `b375f8a` |
| Parse an incomplete journal frame as committed | v1 | 0 | Truncated JSON reached the replay path instead of being dropped | `b375f8a` |
| Explicit: a change with nobody holding the file plans AcquireLockThenUpload | explicit | 1 | A shared version under a lock taken by Implicit, not a check out or an add, at step 41 | `b375f8a` |
| Explicit: a save while checked out (no request) plans Upload | explicit | 1 | The shared file advanced before check in at step 35 | `b375f8a` |
| Explicit: undo restores without keeping the changes | explicit | 24 | Unsynced local overwrite without side-version acknowledgment at step 44 | `b375f8a` |
| Explicit: the read-only rule leaves a checked-in file writable (only another person's or my other device's check out is read-only) | explicit | 0 | A save to a shared file this device had not checked out at step 1 | `b375f8a` |
| Explicit: the check out rule takes the lock over bytes saved without a check out | explicit | 23 | A save made without a check out became the shared version at step 47 | `b375f8a` |
| Explicit: the check in rule lets go over bytes it did not read (feedback N4) | explicit | 46 | A lock was let go over bytes that are not the shared version | 0.3.3 |
| Explicit: the check in rule lets go while the file is open | explicit | 12 | An edit made under this device's check out could not be saved: the lock was let go while the file was open | 0.3.3 |

All nine were caught. The two 0.3.3 cases were run the same way (one anchor each in
`src/Armory.Core/Checkout.cs`, the explicit simulation, the source restored byte-identical)
by a shell port of the script, as PowerShell is not installed here; both were caught. Before A2 the script's `lock-before-upload` anchor no longer
matched the reconciler and the recorded hashes were stale; both are fixed. PowerShell is
not installed in the A2 container, so the nine cases were run by an exact Python port of
the script (same anchors, filters, REPRO match and byte-identical restore check). The
reconciliation source was restored byte-identical with SHA-256
`C20804528BFDB781D4D9D4F9B37BC45B31A28098237C8A5F5BC0F79DB5FFE1DB`.
The journal source was restored byte-identical with SHA-256
`6CE492000E01321374B06A45AE6045E5D18275C0D307D7684C9884BA0562637C`.
The check out rules source was restored byte-identical with SHA-256
`30264177C685520FEA965958B593BFA414FD4A0769520376E6ED11F31DCA9C1B`.
Raw logs and JSON results are generated under ignored `artifacts/mutations/`.

Stryker.NET mutation score: not run because neither the global nor local tool was
installed. No global tool was installed. The targeted checks are not represented
as a general mutation score.

This is executable evidence over bounded schedules and explicit adapter contracts.
It does not prove unbounded distributed correctness or validate real disk durability,
actual CAD bytes, licensing, or production network behavior.
