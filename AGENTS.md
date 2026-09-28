# Armory standing rules

- American spelling everywhere.
- Never use em dashes.
- Solo sessions push straight to `main` once, at the end.
- Every rule in `Armory.Core` has a test.
- The simulation invariants may be strengthened but never weakened.
- Keep the core independent of network, real disk, UI, and SolidWorks assemblies.
- Windows platform tests use real disk and child processes; skip them on non-Windows hosts.
- Preserve originals in probes. CAD searches stay within Documents and the vault, capped at 50 files.
- Never change Defender settings or commit customer/team CAD files.
- Keep replacement's documented concurrent-writer limitation explicit until application coordination closes it.
