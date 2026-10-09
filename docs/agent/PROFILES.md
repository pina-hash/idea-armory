# Several students on one computer (0.3.3)

A lab computer is often one Windows sign-in that several students take turns at. Before 0.3.3
Armory had one sign-in per Windows user, so the next student either worked as the last one or
signed out and in through the browser every time. In 0.3.3 a computer can be **shared by several
students**: each student has a profile on it with their own Armory sign-in and a 4-digit PIN, and
the window opens on a picker ("Who is using Armory?") much like a browser's profile picker.

Code: `src/Armory.Core/SharedComputer.cs` and `PinHash.cs` (the rules, each with a test),
`src/Armory.Agent/Profiles.cs` (the store and each profile's clients),
`src/Armory.Agent/AgentHost.Profiles.cs` (the picker, switching, add and remove, the setting),
`src/Armory.Agent.Engine/SyncEngine.Stopping.cs` and `SyncEngine.Accounts.cs` (the stop guarantee,
what waits in a folder, sealing), the picker screen in `wwwroot/app.js`, and the bridge messages in
docs/agent/BRIDGE.md ("Several students on one computer").

## The decisions, as built

| # | Decision |
|---|---|
| F1 | A fourth setting, `"sharedComputer"` in `settings.json`, **off by default** and written only while it is on. Off: no `profiles\` folder is ever made or read, the host builds exactly the objects it built in 0.3.2 (over `secrets\`), the window and tray are unchanged, and every picker message is answered "This computer isn't set up for several students." without changing anything. `SharedComputerHostTests.Single_user_mode_is_unchanged` holds this. |
| F2 | One student at a time. Each profile has its own `SessionManager`, `ArmoryApi`, `BlobClient`, `ConnectFlow`, `TeamHeartbeat` and `FeedbackSender` (`ProfileClients`) over the host's three shared `HttpClient`s; the host swaps one pointer (`clients`), never the session inside a `SessionManager`, so a call that started as one student can never finish as another, and each refresh token has exactly one owner. Switching never needs the browser. |
| F3 | The store: `%LOCALAPPDATA%\IDEA Armory\profiles\profiles.json` (the index: ids, addresses, names, when each was added and last used, each student's folder and what they had waiting; never a token or PIN material) and `profiles\<id>\secrets\` (DPAPI: `armory-session`, `armory-pin`). Ids are 128 random bits, never derived from the address. An index from a newer Armory is never rewritten: the picker says to update Armory and nobody runs. |
| F4 | The PIN: exactly 4 ASCII digits; four of one digit and straight runs up or down are refused ("Pick a PIN that's harder to guess than 1234."). PBKDF2-HMAC-SHA256, 600,000 iterations, a 16-byte random salt per PIN, a 32-byte hash, compared in fixed time, kept in the profile's DPAPI store with the wrong-try count. Five free wrong tries, then waits of 30 s doubling to 15 min, **never a permanent lockout**; the wait survives a restart. "Forgot your PIN?" is the browser sign-in as that same account (another account changes nothing), then a new PIN; the wrong tries are forgotten. A student with no PIN while PINs are on signs in through the browser once before they can be picked. |
| F5 | **Settings > Shared computer > "Ask for a PIN when switching students"**, on by default, per computer. Only a mentor's own profile in use can change it; "mentor" is what the server says for that profile (`armory_my_projects`: a `mentor` role in any project), asked when they change it and right after each switch, never read from a folder's state (which may still hold the last student's roles). Who and when are kept in `profiles.json` and shown ("Turned off by Mr. Pina on Oct 9."). Off: picking a name switches at once; PIN records are kept, so turning PINs back on restores them. |
| F6 | The picker shows when the window is shown from hidden (closed with X, never shown, or started in the background; then the taskbar, Start, the tray, a desktop shortcut or a second launch all show it on the picker), on the first show or activation of a new local day (a timer checks while the window shows), when Windows is locked, and on Switch student. Minimizing and restoring the same day does not. X still leaves Armory running as the student in use. |
| F7 | While the picker shows, Armory keeps working for the student in use (passes, uploads, the heartbeat), and the view the page gets carries none of their account, files, notices, check-out question or file names. On a shared computer the tray's first item says "Using Armory: Jordan Reyes", Switch student replaces Sign out, Home's account card offers Switch student in place of Sign out and Switch account, and Settings shows who is using Armory now and every student on the computer. `AgentHost.PickerShowing` tells other parts (Explorer's items, the SolidWorks link) that nobody is picked yet. |
| F8 | A switch stops the last student's engine **for good** before the next student's starts: `SyncEngine.StopForGoodAsync` cancels the pass or window action under way, takes the engine's pass gate and never gives it back, and returns only when nothing of that engine runs; from then on nothing of it writes to the folder, `state.json`, the journal or the snapshots (tests compare them byte for byte). If it has not stopped after 60 s, its runtime is parked (kept open, its folder claim held, never reused) and the switch is refused; after 15 s the picker says "Still finishing Alex's last file...". Picking the student already in use on the same folder restarts nothing. Two profiles never write one folder at once: one switch at a time (the host's lifecycle gate); the next runtime starts only after the guarantee above; a folder is run by one runtime per process (`VaultRuntime` refuses a second claim) and one process (the stores' `FileShare.None` lock files); and an engine for another account does nothing in a folder bound to someone else. |
| F9 | One shared folder per computer (`settings.vaultRoot`, normally `C:\IDEA\Armory`), handed from student to student by the 0.3.2 rule (`TakeOverFolderAsync`: only when nothing of its owner's waits there: no check out, no save not sent, no change Armory hasn't kept, no new file, no folder change). When the last student's work waits, their tile says so ("Alex has 2 files checked out here") and the next student chooses **Wait for Alex** (nothing changes) or **Use `C:\IDEA\Armory-jordan`**, a folder of their own. Waiting work stays its student's until they come back and finish it. A student in a folder of their own is moved back to the shared folder at their next switch once nothing of theirs waits in their own folder and the shared one can be handed over; Home says so in one sentence. |
| F10 | When the next student goes to a folder of their own because the last one has files checked out in the shared folder, those files are made read-only there (`SyncEngine.SealCheckOutsAsync`, after the last student's engine has stopped for good) so SolidWorks opens them read-only for whoever sits down; the last student's own engine makes them writable again when they are back. |
| F11 | Add a student: the browser sign-in once, into a new profile, while the student in use keeps working; the step tells them to click **Not you? Use another account** when the browser page shows someone else; "Open the browser again" stops that sign-in and starts a new one; the same address again renews that student's profile (and is how a forgotten PIN is changed), never a second profile; Cancel keeps nothing. Remove (Settings, after a question): forgets that student's sign-in and PIN on this computer, ending the sign-in on the server when it can (`POST /auth/v1/logout?scope=local`), and **never deletes a file**; their waiting work stays theirs until they add themselves again. A student may remove themselves; a mentor in use may remove anyone; with PINs off anyone may remove anyone. |
| F12 | Turning shared mode on **moves** the signed-in student's `armory-session` into their new profile (a rename, read back before it counts; never a copy: two holders of one rotating refresh token end the session), with the PIN they choose in the same question. Turning it off keeps only the student in use (their sign-in moves back to `secrets\`, settings point at their folder) and forgets everyone else's sign-in and PIN; only a mentor may turn it off while other students use the computer with PINs on. A crash in the middle is finished at the next start from what is on disk (`migrating` in the index); the sign-in is never in both places. |
| F13 | Saved feedback notes and incident reports are sent only while the student who wrote them is in use (`IncidentUploader.WriterInUse`), so a student's words never go out under another student's sign-in. Each profile has its own heartbeat, beating only while that student is in use; the last student's says goodbye on a switch. |
| F14 | Names come from the sign-in address ("alex.kim@..." is "Alex Kim"); the picture is the student's initials in a disc whose color (one of 8) comes from the address, the same every time. Profile photos are not shown in 0.3.3. |

## What the PIN does and does not protect

The PIN keeps one student from acting as another through Armory by accident or on purpose: the
window, the tray and the check-out balloons. It is **not a security boundary**. Every student shares
the Windows sign-in, so anyone at the keyboard can open the Armory folder in File Explorer, and
anything that runs as that Windows user can ask DPAPI to unprotect the profile stores (DPAPI keeps
Windows users apart, not people at one sign-in). Someone who copies a PIN record off the computer
needs at most 10,000 tries, minutes on a fast computer; the waits after wrong tries are the guard
at the keyboard. SolidWorks saves made into a file checked out to the student in use count as that
student's work, exactly as on a computer one student uses.

## A folder of one's own: the same-path trade-off

Armory's rule is the same path on every computer (`C:\IDEA\Armory\...`), so the paths SolidWorks
saves in an assembly are right everywhere. A folder of one's own (`C:\IDEA\Armory-jordan`) breaks
that for the time it is used:

- An assembly opened from `C:\IDEA\Armory-jordan` usually finds its parts there, because SolidWorks
  looks in the open document's own folder before the saved path.
- A part that is not in Jordan's folder at the same place is found at the saved path,
  `C:\IDEA\Armory\...`: it opens from the last student's copy, read-only, without a word.
- A part already open in SolidWorks with the same name wins over both, and SolidWorks is shared by
  the Windows sign-in, so a part the last student left open can be the one Jordan's assembly uses.
- If the computer's SolidWorks lists `C:\IDEA\Armory` under File Locations > Referenced Documents,
  that folder wins over Jordan's.
- An assembly Jordan saves from his folder stores `C:\IDEA\Armory-jordan\...` paths. Other computers
  still open it (they find the parts in their own `C:\IDEA\Armory`), and the paths are put right the
  next time it is saved from `C:\IDEA\Armory`.

That is why the folder of one's own is offered only when the shared one is busy, never by default,
and why Armory moves the student back as soon as nothing of theirs waits there. The picker says
the trade-off in one sentence on the step that offers it.

The own folder's name is the shared folder plus `-` and the address's first name, lowercased,
accents folded, letters and digits only (`student` when nothing is left), then `-2`, `-3` while that
name is taken, and never over the 120-character folder limit.

## Turning it on, using it, turning it off

1. On the computer, the student signed in now opens **Settings > Shared computer** and turns on
   "This computer is shared by several students", choosing their PIN in the question that asks.
   Nothing else changes for them: same folder, same sign-in.
2. Every other student picks **Add a student** once: the browser opens on the Armory connect page;
   if it shows the last student, they click **Not you? Use another account**, sign in with their own
   school Google account, come back and choose their PIN.
3. From then on the window opens on the picker. One click on a name and four digits switch to that
   student; no browser.
4. A mentor (with their own profile in use) can turn the PINs off for that computer, and remove any
   student. Turning shared mode off keeps only the student in use.

## Owed to the lab

These cannot be proven on Linux CI and are checked on a lab computer:

1. What SolidWorks does with a document it has open writable when sealing makes its file
   read-only (F11; expected: its next save is refused, which protects the owner's work).
2. Whether the lab's SolidWorks lists `C:\IDEA\Armory` under Referenced Documents (it changes which
   copy an own-folder assembly loads).
3. How long a PIN check takes on the oldest lab PC (expected under one second).
4. "Not you? Use another account" on the connect page with the browser still signed in as the last
   student, end to end.
5. DPAPI and the real stores on Windows (`A_second_runtime_from_the_real_stores_is_refused` runs only
   there).

## Not in 0.3.3

- Profile photos (initials and a color stand in; the requirement is "picture or initials").
- Names from the team list (`armory_team_status`): names come from the address.
- The server's limit waits (`SubmitLimiter`) are kept per computer, not per student.
- A team-wide PIN switch on the website (it would need a new server field).
- Explorer's Armory items and the SolidWorks link refusing to act while the picker shows: the host
  exposes `PickerShowing`; wiring it belongs to those parts.
