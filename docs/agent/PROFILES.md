# Several people on one computer (0.3.3, 0.5.0)

A lab computer is one Windows sign-in, named IDEA, that many students take turns at. Mr. Pina
(2026-10-25 and 10-26, ledger 0437): setting up a new person and switching "has to be very
thoughtless and automatic", two people on one project work out of the same folder ("I don't want
to be downloading the same project multiple times on one computer"), and the update must carry
every existing lab computer forward. 0.5.0 builds that. The 0.3.3 design it replaces is below,
with each decision 0.5.0 changed marked.

## 0.5.0 as built

Code: `src/Armory.Agent.Engine/SyncEngine.People.cs` (the hand-over and held work),
`EngineState.People` and `EngineState.Held` (schema 3), `src/Armory.Agent.Engine/FolderFoldRunner.cs`
and `src/Armory.Core/FolderFold.cs` (folding folders of one's own back in),
`src/Armory.Core/SharedComputer.cs` (when the picker shows, when a computer becomes shared, PINs after
the update), `src/Armory.Agent/AgentHost.Profiles.cs` and `AgentHost.Fold.cs` (switching, becoming
shared, the fold at start), and Home's "On this computer" cards in `wwwroot/app.js`.

| # | Decision |
|---|---|
| G1 | **No setup step.** There is no "this computer is shared" switch. The first time someone other than the person signed in (or, signed out, the person the folder was last used by) signs in through Connect or Switch account, the computer becomes shared by itself (`SharedComputer.BecomesShared`, `AgentHost.BecomeSharedAsync`): the first person's sign-in moves into a profile of their own (a rename, never a copy), the new person gets a profile with the sign-in that just finished, and Armory switches to them. The same person signing in again only renews their sign-in. |
| G2 | **No PIN by default.** A computer shared by 0.5.0 asks for none; a mentor in use may turn PINs on for that computer (Settings > Shared computer > Ask for a PIN when switching), and then everyone chooses one at their next pick. |
| G3 | **One click or none.** The window opens on the faces of the people who use the computer, most recent first, when it is shown from hidden, on the first open of a day and after Windows is locked, only when two or more people use it; Switch (Home, the tray) always shows it, since that is where someone new adds themselves. One click on a face switches. |
| G4 | **One folder per project per computer.** Everyone works in the computer's Armory folder (`settings.vaultRoot`, normally `C:\IDEA\Armory`); there is no folder of one's own any more, in the picker or anywhere. A switch starts the next person's engine on that folder, and its first pass hands the folder over by itself (`SyncEngine.HandOver`). Everything that is nobody's waiting work is the team's version and stays as it is: nothing downloads again. |
| G5 | **Waiting work stays with its person, in the same folder.** What the last person left waiting (a file checked out, a check in or undo asked, a save not sent, a write in flight, a file changed or new that Armory hasn't kept, a folder change not sent) moves out of the records into `EngineState.Held` under their address, exactly as it was, with each file's bytes' hash. A held file is never captured, planned, downloaded over, moved or sent while someone else is in use; it is kept read-only on disk, so SolidWorks opens it read-only for the next person; the window says whose it is ("Alex's work waits here: 2 files") and refuses Check out, Rename, Delete folder and Put back on it in one sentence. A held file someone renames in File Explorer is still known by its bytes and stays held. A held file's folder renamed here follows (`RekeyHeld`). |
| G6 | **Sent only under its owner's sign-in.** When its person is in use again, their held work goes back into the records as it was (`ReturnHeld`) and their own engine finishes it. A journaled save carries its author; an engine attaches and sends only its own person's (`SavedBy`). Before a switch the last person's engine runs one more pass, at most 15 seconds, so whatever can go now goes under their sign-in (`FlushLockedAsync`); the rest waits for them. |
| G7 | **Open files stay open.** A switch changes who Armory acts for, never what is on disk: no document is closed or moved. A file SolidWorks has open that becomes held only gets its read-only bit; its next save is refused, which protects its owner's work. |
| G8 | **Devices follow their person.** `EngineState.People` keeps every device id each person's sign-ins registered here, so a check out taken under any of them is theirs again when they are back (including one taken in a folder of their own before the update). |
| G9 | **A second Windows account (Fast User Switching).** Kept safe at today's level: each Windows account still runs its own Armory with its own sign-ins. While another account's Armory holds the folder, this one says so in plain words (never "choose another folder") and starts there by itself when the folder is free (every 30 seconds, `AgentHost.RetryStartSoon`); the folder's state document is shared, so work one account left waiting is held for its person when the other runs. Not built: one running engine shared by both accounts. |

### Carrying existing computers forward

Every start of 0.5.0 does this before any engine runs, with no question to anyone:

- **One person in `C:\IDEA\Armory` (0.3.2 to 0.4.x, the common case).** The state document (schema 2) loads as schema 3 with that person and every device id it was synced under in `People`. Nothing else changes; nothing is downloaded.
- **Shared-computer profiles turned on (0.3.3 to 0.4.x), PINs on, several people.** `profiles.json` version 1 is read and saved at once as version 2 (`ProfileStore.Load`). PINs stay on only where a mentor's own change turned them on (`SharedComputer.PinsKeptAfterUpdate`: `pinsRequired` with `pinsChangedBy` set). Under 0.3.3 to 0.4.x PINs were on by default the moment shared mode was turned on, and only a mentor in use could change the setting, which the index recorded with who did it; so `pinsChangedBy` is the one thing in the tree that tells a mentor's deliberate choice from the default. A student-chosen PIN under the old default never starts blocking switches; every PIN record is kept, so a mentor turning PINs on brings them back.
- **Folders of one's own (`C:\IDEA\Armory-<name>`, `-2`, `-3`), with or without waiting work.** Each one a profile names, and each `Armory-*` folder beside the shared one that holds Armory's records, is folded in (`FolderFoldRunner`, the rules in `Armory.Core.FolderFold`): a file the shared folder has with the same bytes is removed from the own folder; one it lacks moves in; an older or newer team version nobody works on goes aside into the shared folder's `.armory\folded\<folder>\own\`; the owner's waiting work over a team copy nobody works on comes in, and that team copy goes aside into `...\team\`; where both copies are somebody's work, or the file is open in SolidWorks, both stay where they are and the summary says so. Nothing is deleted unless the same bytes are in the shared folder, and nothing is overwritten. The owner's records for their waiting files come in held for them, saved before any file moves, with their devices; what pointed into the own folder's journal (saves not sent) is left behind, because the file on disk is its latest save and their own engine keeps it again under their sign-in. A folder with nothing left in it has its own `.armory` (its journal and kept saves, every save made there) moved into `.armory\folded\<folder>\armory`, and goes. Every profile then points at the shared folder. A computer whose shared mode was turned off while its student worked in a folder of their own (settings pointing at `C:\IDEA\Armory-alex`) is folded into `C:\IDEA\Armory` and goes on there.
- **A folder bound to one person and one device (Phase A).** On the first pass of anyone else, the hand-over (G4, G5) moves it to the computer-bound shape: the person in use's records, and everyone else's waiting work held per person.
- **The one screen.** The fold's outcome is one sentence on Home, under "On this computer", for a week ("Moved 3 projects into C:\IDEA\Armory, kept 2 files waiting for Maria. Each person's work goes to Armory under their own sign-in when they pick their name."), kept in `.armory\folded\note.json`; every action is a line in agent.log (`fold: ...`).
- **A power cut in the middle.** Every step is decided again from what the disk and the two state documents hold at that moment, so the next start finishes the fold and nothing is done twice; a file whose record was already held for its owner is still theirs. Tested at every step (`FoldRunnerTests`, and on NTFS in `FoldWindowsTests`).

### Going back to 0.4.x after 0.5.0 ran

An older Armory never acts on a folder that holds someone's work for them. Held work exists only on a computer used by two or more people, and such a computer has `profiles.json` version 2 and `sharedComputer` on: Armory 0.3.3 to 0.4.x reads any version above 1 as set up by a newer Armory and runs nobody ("This computer's students were set up by a newer Armory. Update Armory to use them."), so nothing is sent as anyone, and no file is touched. Installing 0.5.0 again picks up where it was. A computer one person used keeps a schema 3 state document that an older Armory reads as before (it ignores `people` and `held`, which are empty there). Folded own folders are not recreated by an older Armory; their records stay in `.armory\folded`.

## Owed to the lab before 0.5.0 ships

Run on a real lab computer: one person leaves a file checked out and a second opens the same assembly (expected: the part opens read-only and their save is refused; nothing of the first person's changes); two people on one project with nothing checked out (expected: no download starts on the switch); a switch while SolidWorks has a part open (expected: the part stays open; if it was the last person's check out it becomes read-only); a computer with an existing `Armory-<name>` folder updating in place (expected: the one-sentence note, the folder gone or named as kept); Fast User Switching between two Windows accounts (expected: the second says the first one's Armory holds the folder, and starts by itself once that one quits).

## 0.3.3 (the design 0.5.0 replaced)

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

## The 0.3.3 decisions, as built

| # | Decision |
|---|---|
| F1 | **Superseded by G1 (0.5.0): no switch; a computer becomes shared by itself.** A fourth setting, `"sharedComputer"` in `settings.json`, **off by default** and written only while it is on. Off: no `profiles\` folder is ever made or read, the host builds exactly the objects it built in 0.3.2 (over `secrets\`), the window and tray are unchanged, and every picker message is answered "This computer isn't set up for several students." without changing anything. `SharedComputerHostTests.Single_user_mode_is_unchanged` holds this. |
| F2 | One student at a time. Each profile has its own `SessionManager`, `ArmoryApi`, `BlobClient`, `ConnectFlow`, `TeamHeartbeat` and `FeedbackSender` (`ProfileClients`) over the host's three shared `HttpClient`s; the host swaps one pointer (`clients`), never the session inside a `SessionManager`, so a call that started as one student can never finish as another, and each refresh token has exactly one owner. Switching never needs the browser. |
| F3 | The store: `%LOCALAPPDATA%\IDEA Armory\profiles\profiles.json` (the index: ids, addresses, names, when each was added and last used, each student's folder and what they had waiting; never a token or PIN material) and `profiles\<id>\secrets\` (DPAPI: `armory-session`, `armory-pin`). Ids are 128 random bits, never derived from the address. An index from a newer Armory is never rewritten: the picker says to update Armory and nobody runs. |
| F4 | **0.5.0: PINs are off unless a mentor turns them on (G2).** The PIN: exactly 4 ASCII digits; four of one digit and straight runs up or down are refused ("Pick a PIN that's harder to guess than 1234."). PBKDF2-HMAC-SHA256, 600,000 iterations, a 16-byte random salt per PIN, a 32-byte hash, compared in fixed time, kept in the profile's DPAPI store with the wrong-try count. Five free wrong tries, then waits of 30 s doubling to 15 min, **never a permanent lockout**; the wait survives a restart. "Forgot your PIN?" is the browser sign-in as that same account (another account changes nothing), then a new PIN; the wrong tries are forgotten. A student with no PIN while PINs are on signs in through the browser once before they can be picked. |
| F5 | **0.5.0: off by default (G2).** **Settings > Shared computer > "Ask for a PIN when switching students"**, on by default, per computer. Only a mentor's own profile in use can change it; "mentor" is what the server says for that profile (`armory_my_projects`: a `mentor` role in any project), asked when they change it and right after each switch, never read from a folder's state (which may still hold the last student's roles). Who and when are kept in `profiles.json` and shown ("Turned off by Mr. Pina on Oct 9."). Off: picking a name switches at once; PIN records are kept, so turning PINs back on restores them. |
| F6 | The picker shows when the window is shown from hidden (closed with X, never shown, or started in the background; then the taskbar, Start, the tray, a desktop shortcut or a second launch all show it on the picker), on the first show or activation of a new local day (a timer checks while the window shows), when Windows is locked, and on Switch student. Minimizing and restoring the same day does not. X still leaves Armory running as the student in use. |
| F7 | While the picker shows, Armory keeps working for the student in use (passes, uploads, the heartbeat), and the view the page gets carries none of their account, files, notices, check-out question or file names. On a shared computer the tray's first item says "Using Armory: Jordan Reyes", Switch student replaces Sign out, Home's account card offers Switch student in place of Sign out and Switch account, and Settings shows who is using Armory now and every student on the computer. `AgentHost.PickerShowing` tells other parts (Explorer's items, the SolidWorks link) that nobody is picked yet. |
| F8 | A switch stops the last student's engine **for good** before the next student's starts: `SyncEngine.StopForGoodAsync` cancels the pass or window action under way, takes the engine's pass gate and never gives it back, and returns only when nothing of that engine runs; from then on nothing of it writes to the folder, `state.json`, the journal or the snapshots (tests compare them byte for byte). If it has not stopped after 60 s, its runtime is parked (kept open, its folder claim held, never reused) and the switch is refused; after 15 s the picker says "Still finishing Alex's last file...". Picking the student already in use on the same folder restarts nothing. Two profiles never write one folder at once: one switch at a time (the host's lifecycle gate); the next runtime starts only after the guarantee above; a folder is run by one runtime per process (`VaultRuntime` refuses a second claim) and one process (the stores' `FileShare.None` lock files); and an engine for another account does nothing in a folder bound to someone else. |
| F9 | **Superseded by G4 and G5 (0.5.0): no folder of one's own; waiting work is held for its person in the one folder.** One shared folder per computer (`settings.vaultRoot`, normally `C:\IDEA\Armory`), handed from student to student by the 0.3.2 rule (`TakeOverFolderAsync`: only when nothing of its owner's waits there: no check out, no save not sent, no change Armory hasn't kept, no new file, no folder change). When the last student's work waits, their tile says so ("Alex has 2 files checked out here") and the next student chooses **Wait for Alex** (nothing changes) or **Use `C:\IDEA\Armory-jordan`**, a folder of their own. Waiting work stays its student's until they come back and finish it. A student in a folder of their own is moved back to the shared folder at their next switch once nothing of theirs waits in their own folder and the shared one can be handed over; Home says so in one sentence. |
| F10 | **0.5.0: every held file is read-only for the person in use (G5).** When the next student goes to a folder of their own because the last one has files checked out in the shared folder, those files are made read-only there (`SyncEngine.SealCheckOutsAsync`, after the last student's engine has stopped for good) so SolidWorks opens them read-only for whoever sits down; the last student's own engine makes them writable again when they are back. |
| F11 | Add a student: the browser sign-in once, into a new profile, while the student in use keeps working; the step tells them to click **Not you? Use another account** when the browser page shows someone else; "Open the browser again" stops that sign-in and starts a new one; the same address again renews that student's profile (and is how a forgotten PIN is changed), never a second profile; Cancel keeps nothing. Remove (Settings, after a question): forgets that student's sign-in and PIN on this computer, ending the sign-in on the server when it can (`POST /auth/v1/logout?scope=local`), and **never deletes a file**; their waiting work stays theirs until they add themselves again. A student may remove themselves; a mentor in use may remove anyone; with PINs off anyone may remove anyone. |
| F12 | **0.5.0: there is no switch to turn on or off; becoming shared (G1) moves the sign-in the same way.** Turning shared mode on **moves** the signed-in student's `armory-session` into their new profile (a rename, read back before it counts; never a copy: two holders of one rotating refresh token end the session), with the PIN they choose in the same question. Turning it off keeps only the student in use (their sign-in moves back to `secrets\`, settings point at their folder) and forgets everyone else's sign-in and PIN; only a mentor may turn it off while other students use the computer with PINs on. A crash in the middle is finished at the next start from what is on disk (`migrating` in the index); the sign-in is never in both places. |
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

## A folder of one's own: the same-path trade-off (0.3.3 to 0.4.x; 0.5.0 folds these folders back in)

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
- An assembly Jordan saves from that own folder stores `C:\IDEA\Armory-jordan\...` paths. Other computers
  still open it (they find the parts in their own `C:\IDEA\Armory`), and the paths are put right the
  next time it is saved from `C:\IDEA\Armory`.

That is why the folder of one's own is offered only when the shared one is busy, never by default,
and why Armory moves the student back as soon as nothing of theirs waits there. The picker says
the trade-off in one sentence on the step that offers it.

The own folder's name is the shared folder plus `-` and the address's first name, lowercased,
accents folded, letters and digits only (`student` when nothing is left), then `-2`, `-3` while that
name is taken, and never over the 120-character folder limit.

## Turning it on, using it, turning it off (0.3.3 to 0.4.x; 0.5.0 has no switch)

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

## Owed to the lab (0.3.3)

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
