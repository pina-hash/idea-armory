# Decisions for 0.4.0

Mr. Pina asked for each of these on 2026-10-21 (idea-app ledger 0423). Where one reverses a
0.3.3 decision, the old one is named.

## SolidWorks years: warn, never block (reverses 0.3.3 A3, first bullet)

"When a file was saved in a newer SolidWorks version than its project uses (e.g. three Blade
Team Demo files saved in 2026 in a 2025 project), warn once and offer 'this is on purpose', which
silences it for that file. Never block."

- Core: `SolidWorksVersionGate.Decide` allows a release known to be newer than the pin in Warn
  and Enforce, marked `NewerThanPin`. It still refuses a missing pin, and an unknown release
  under Enforce (that is not a newer year; it is a year nobody could read).
- Server: idea-app migration 0288 (`armory_check_release`) records a newer release instead of
  raising 22023. `server/sql/006_v040_newer_release.sql` is that migration byte for byte.
- Every computer of the project shows one "newerRelease" card per file (any version, checked by
  the server or not), with "This is on purpose" (one file) or "These are on purpose" (several),
  which silences those files there until they are saved in the project's year again.
- The SolidWorks link still saves down to the project's year where it can, and still asks before
  a save down that drops something. "Keep this file on this computer only" is the student's own
  choice and still keeps those bytes here (`FileState.KeptHereHash`), even after SolidWorks
  closes the file, until a save in the project's year.
- A document SolidWorks can't take back to the project's year is saved in this computer's year
  and goes up, warned; the link's card says what to change to keep it in the project's year.
- What this costs: a 2026 part reaches the lab's 2025 computers, which can open parts and
  assemblies to look but can't change them, and can't open 2026 drawings. The card on every
  computer says so and says who can fix it.
- Not changed: a save down SolidWorks did not confirm (B4, an unknown year after Save to Version)
  still stays here in both modes. It is an unread year, not a newer one; whether it should go up
  "release not checked" is a question for Mr. Pina.

## Updates (new)

Velopack, from this repository's releases; docs/agent/INSTALL.md "Updates" and the README.
