# IDEA Armory
Armory is the IDEA pathway's team file vault for FRC Team 5669 and class projects.
It keeps saved work versioned, locks edits, and reconciles offline changes.
This repository contains the pure .NET sync core, deterministic simulation, and Windows disk adapters.
The [product scope](https://github.com/pina-hash/idea-app/blob/main/docs/ARMORY.md) defines the website, Windows agent, and SolidWorks add-in.

Install the .NET 10 LTS SDK (10.0.401 or a later patch in its feature band), then run:

```powershell
dotnet restore --locked-mode
dotnet build -warnaserror
dotnet test
```

The normal run includes 10,000 deterministic multi-client scenarios and must finish the
simulation within two minutes. For one failing seed or the million-scenario stress run:

```powershell
$env:ARMORY_SEED = '42'
dotnet test --filter Seeded_scenarios
Remove-Item Env:ARMORY_SEED
$env:ARMORY_STRESS = '1'
dotnet test --filter Seeded_scenarios
Remove-Item Env:ARMORY_STRESS
```

On bash, use `ARMORY_SEED=42 dotnet test --filter Seeded_scenarios` or
`ARMORY_STRESS=1 dotnet test --filter Seeded_scenarios`.

Read [the integration contract](docs/core/integration.md) before implementing an adapter.
`Armory.Core` performs no network or real disk operations. `Armory.Platform.Windows`
provides durable journals and snapshots, staged replacement, open-file checks, NTFS
inventory, lock attributes, and Windows paths. Platform tests run against real disk and
child processes on Windows; Linux discovers and skips them cleanly. All projects build
on both CI runners. See the [Windows adapter contracts](docs/platform/audit.md), especially
the [replacement race limitation](docs/platform/safe-replace.md).

Run the local phase 0 probes on Windows with `dotnet run --project tools/Armory.Probe -- probes .`.
Reports go to `docs/spike/`. This inspects registry installation data, attempts a bounded
CAD inspection and a generated/copied-document open/close measurement, and measures
100 replacements of 5 MiB files under Defender's unchanged current settings. It may
connect to SolidWorks, but only closes documents it creates/opens for the probe.

Saved-release detection remains unvalidated; no production reader guesses a year.
Server transactions, real CAD coordination, and school-network measurements remain later
work. Simulation and disk tests provide bounded evidence, not proof against every OS,
hardware, or concurrent-writer failure.

Design and evidence: [audit](docs/core/audit.md), [simulation](docs/core/simulation.md).

## Server and storage lane

`server/sql/` contains the reviewed draft PostgreSQL contract for later numbering in idea-app, with its identity integration notes in `server/IDEA_APP_CONVENTIONS.md`. `Armory.Storage` implements credential-free, content-addressed S3 transfers. Server integration tests require `ARMORY_TEST_POSTGRES` to identify a throwaway cluster; CI supplies a PostgreSQL service and the fixture creates and drops only its own database.
