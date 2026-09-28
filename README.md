# IDEA Armory
Armory is the IDEA pathway's team file vault for FRC Team 5669 and class projects.
It keeps saved work versioned, locks edits, and reconciles offline changes.
This repository currently contains the pure .NET sync core and deterministic simulation.
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
The library performs no network or real disk operations. Durable storage, atomic server
transactions, file-handle coordination, and saved-release detection require real adapters
and the phase 0 spike. Simulation verifies these contracts with fakes; it is not a proof
against every possible OS, hardware, or server failure.

Design and evidence: [audit](docs/core/audit.md), [simulation](docs/core/simulation.md).
