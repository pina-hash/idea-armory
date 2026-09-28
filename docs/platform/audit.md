# Lane C2 audit and adapter boundaries

Measured 2026-09-27 on Windows 11 Home, version 10.0.26200, build 26200. The system
drive C: is NTFS, fixed, and reported healthy. SDK: 10.0.401.

The initial `git pull --ff-only` reported already up to date. Branch `main` contained
`c7364c1`; the worktree was clean. Both Git author fields were populated.
`dotnet build -warnaserror` passed with zero warnings/errors. The audit test summary was:

```text
Passed! - Failed: 0, Passed: 164, Skipped: 0, Total: 164
```

Read AGENTS.md, README.md, every docs/core note, the public core source, and the current
[public scope](https://github.com/pina-hash/idea-app/blob/main/docs/ARMORY.md).
No idea-app files changed. No Armory.Core source or existing core tests changed.

Exact interface mapping:

| Core boundary | C2 implementation |
|---|---|
| `Armory.Core.IJournalStore` | `DurableJournalStore` |
| `Armory.Core.ISaveSnapshotStore` | `DurableSnapshotStore` |
| `Armory.Core.ISavedReleaseReader` | No production implementation: byte-level release detection was not validated |
| `SyncInput.IsOpen` | Supplied from `OpenFileDetector.Inspect(...).IsOpen` |
| `SyncInput.LocalHash` | Supplied from `LocalChangeDetector.Scan()` inventory |
| `Armory.Core.IIntentSink` | Server-side boundary; not implemented in this disk-only lane |

C1 contains no open-file interface or local-state-reader interface. Those are input
values in its pure engine, not differently named interfaces. New Windows services
compose those values without adding a platform reference to the core.

C1 already frames journal intents with SHA-256. C2 adds a CRC-32C transport envelope
without replacing that format or modifying the core. Invalid complete records are
quarantined and block replay; incomplete suffixes are removed and their size reported.

The Windows projects target `net10.0-windows`; the core stays `net10.0`. Tests use
`WindowsFact`/`WindowsTheory` so the existing Ubuntu workflow builds them and reports
clean skips. The same workflow executes them on Windows. No SolidWorks assembly is
referenced by the platform or core; optional probe automation uses late-bound COM.

Read the module guarantees and limitations before using these adapters. In particular,
MoveFileEx is atomic as a rename but not as a hash-comparison-plus-rename operation.
The measured limitation is recorded in [safe replacement](safe-replace.md). This is not
reported as an Armory.Core defect and has not been concealed by weakening its simulation.
