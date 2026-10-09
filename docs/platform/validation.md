# Windows validation evidence

Audit baseline: 164 core tests passed before any C2 edits. Windows 11 Home build 26200,
NTFS C:, SDK 10.0.401. No core source or core test changes.

Final local gate: 191 tests passed, comprising the unchanged 164 core tests and 27
Windows tests. Build passed with zero warnings and zero errors. Exact test summaries:

```text
Passed! - Failed: 0, Passed: 27, Skipped: 0, Total: 27
Passed! - Failed: 0, Passed: 164, Skipped: 0, Total: 164
```

The core simulation completed 10,000 scenarios in 44.187 seconds. Platform tests took
33 seconds while the core tests ran concurrently. Final real-process stress results:

```text
KILL_TEST runs=200 torn_tails=5 dropped_bytes=20480 duplicates=0 complete_records=1337
CHANGE_TEST created=5000 edited=5000 renamed=5000 deleted=2500 actual_overflows=4 final_files=2501 matches_clean_scan=true
```

These counts are measured, not fixed expected values. Scheduling changes how many kills
land within a write and how many notification buffers overflow. Tests assert complete,
ordered, nonduplicated bytes and an exact final inventory, not a preferred metric.

An earlier focused stress run also passed: 200 kills, seven torn tails, 28,672 dropped
bytes, 665 complete records, zero duplicates, and four actual watcher overflows.

Phase 0 reports:

- [Installed SolidWorks](../spike/installed-solidworks.md): 2026 SP04.1, listed in both registry views.
- [Saved release](../spike/saved-release.md): zero existing permitted-scope samples; one blank part generated in SolidWorks; no validated standalone reader. (Superseded 2026-10-09: the chunk reader, 158 of 158 public files read.)
- [Lock file](../spike/solidworks-lock-file.md): one `~$` file appeared on open and disappeared after close.
- [Defender](../spike/antivirus-replace.md): real-time protection enabled; 100/100 replacements succeeded, no sharing violations/retries.

The kill tests use actual child-process termination, not simulated stores. The watcher
test requires a real overflow, not an injected error. Windows-only attributes skip these
tests on Linux while preserving the full 10,000-scenario core simulation there.
