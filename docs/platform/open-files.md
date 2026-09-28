# Open-file detection

`OpenFileDetector` registers a file with Windows Restart Manager, enumerates holding
process ids/names, and always performs an exclusive-open probe as a fallback/check.
Inability to establish exclusive access is treated as open. Diagnostics retain API
failure information instead of assuming that a failed Restart Manager query means free.
The API is queried only; no process is shut down or restarted.

The real-disk test opens a destination with no sharing in another process, checks the
reported holder id/name, verifies replacement is refused, and confirms unchanged bytes
after releasing the handle. A scan test also confirms unreadability cannot invent a
deletion. Detection is a point-in-time observation, not a lifetime handle lease.

`VaultIgnore` excludes every `~$*` segment, desktop.ini, Thumbs.db, and `.armory`, using
case-insensitive names. Tests cover the rule and a real directory inventory containing
those files. The [SolidWorks measurement](../spike/solidworks-lock-file.md) observed the
lock file appear/disappear for a generated part in 2026 SP04.1. Detection never depends
on that convention alone.

Reference: [Restart Manager resource registration](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmregisterresources).
