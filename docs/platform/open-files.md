# Open-file detection

`OpenFileDetector` registers a file with Windows Restart Manager, enumerates holding
process ids/names, and always performs an exclusive-open probe as a fallback/check.
Inability to establish exclusive access is treated as open. Diagnostics retain API
failure information instead of assuming that a failed Restart Manager query means free.
The API is queried only; no process is shut down or restarted.

**Many files at once, off the caller's thread (0.3.3).** `OpenAmong(files, budget)` answers
each file as `Inspect` does: the exclusive-open probe on each (cheap), then Restart Manager for
the rest, one session per batch of 500, a batch with a holder split in halves until each held
file is found. `OpenAmongAsync(files, budget, fresh, ct)` asks the same question on worker
threads, so the agent's engine thread only awaits it (0.3.2 held that thread for the whole
10 second budget every pass on a 1,467-file vault). Restart Manager runs one question at a
time: while an earlier one is still running past its budget, the probe alone answers and the
result says it timed out, so queries never pile up and slow each other down. A query goes on
until it ends (at most 60 seconds, or until the token is canceled, checked between sessions),
and a file it cleared stays cleared for 3 seconds while its NTFS id and last-write time are the
same, unless the question is fresh: a lock is let go only on a fresh answer. The result names
the programs Restart Manager found holding files; `WindowsVaultFileSystem` logs them, and a
question that ran past its budget, at most once per 10 minutes, saying how many such lines it
held back. The engine's budget is 2 seconds (`SyncEngine.OpenBudget`), and it asks only about
the files whose plan depends on being open (`Reconciler.OpenMatters`,
docs/agent/ENGINE.md, "Every click at once").

The real-disk test opens a destination with no sharing in another process, checks the
reported holder id/name, verifies replacement is refused, and confirms unchanged bytes
after releasing the handle. A scan test also confirms unreadability cannot invent a
deletion. Detection is a point-in-time observation, not a lifetime handle lease.
`ReplaceAndLockTests.The_open_files_question_keeps_its_budget_and_runs_one_query_at_a_time`
asks about 1,500 read-only files with files held open in a child process and holds the
question to its budget and to one Restart Manager query at a time.

`VaultIgnore` excludes every `~$*` segment, desktop.ini, Thumbs.db, and `.armory`, using
case-insensitive names. Tests cover the rule and a real directory inventory containing
those files. The [SolidWorks measurement](../spike/solidworks-lock-file.md) observed the
lock file appear/disappear for a generated part in 2026 SP04.1. Detection never depends
on that convention alone.

Reference: [Restart Manager resource registration](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmregisterresources).
