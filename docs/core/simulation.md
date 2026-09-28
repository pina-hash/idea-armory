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

## Deliberate faults

Run `./scripts/Test-DeliberateBreaks.ps1` from PowerShell to repeat the experiment. The
script changes one production rule, runs only the simulation, requires a failing seed,
and restores the original source bytes in `finally`. It verifies SHA-256 before and
after restoration. It does not count a compilation error as a caught mutation.

| Broken rule | Seed | Observed failure |
|---|---:|---|
| Download despite an open file | 0 | Open overwrite at step 9 |
| Omit side-version action before conflict download | 0 | Unsynced local overwrite without side-version acknowledgement at step 12 |
| Emit Upload instead of AcquireLockThenUpload | 1 | Shared-write assertion found a different/missing lock holder |
| Parse an incomplete journal frame as committed | 0 | Truncated JSON reached the replay path instead of being dropped |

All four were caught. The reconciliation source was restored byte-identical with SHA-256
`B30FA154719044279F0CA3E506314476A2D16D7C83F5B9C553E6ACE8FD8BF613`.
The journal source was restored byte-identical with SHA-256
`02D236758BBEA45D2674B1B015D1D2B9EE7DEBBD92511BE6BFC11B913AB3ADF9`.
Raw logs and JSON results are generated under ignored `artifacts/mutations/`.

Stryker.NET mutation score: not run because neither the global nor local tool was
installed. No global tool was installed. The four targeted checks are not represented
as a general mutation score.

This is executable evidence over bounded schedules and explicit adapter contracts.
It does not prove unbounded distributed correctness or validate real disk durability,
actual CAD bytes, licensing, or production network behavior.
