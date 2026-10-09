# Armory 0.3.3: decisions

Mr. Pina was asleep while 0.3.3 was built (2026-10-08 into 2026-10-09). Every choice below
was made without asking him, using the default he wrote in his request, or, where he wrote
none, the safest free choice. Each entry says what was decided, why, and how to change it.
Entries marked **Yours to change** are ones he may want to decide differently.

## Hard rules applied everywhere

- Nothing costs money. No paid certificate, service, tool or API is used. Where a feature
  could only be finished with something paid, the free part was built and the gap is named
  here and in `docs/agent/feedback-audit.md`.
- Every feedback note and incident was treated as valid. Where one could not be done safely
  or for free, the closest safe free version was built and the gap is explained.

## A. SolidWorks years

### A1. The vault stays at SolidWorks 2025
Mixed years are permanent (school computers: SolidWorks Education 2025; students' own
computers: the sponsorship's 2026). Every file in the vault must stay openable and editable
in 2025. Armory reads each project's pinned year (`pinned_release`) and treats it as the
newest year a file may have.

### A2. Armory reads each file's SolidWorks year itself, free, with no Document Manager key
Until 0.3.2 the year check never ran: `AgentHost.cs` passed no reader (`ReleaseReader = null`),
so every SolidWorks file was "release not checked", Warn-mode projects (the server's default)
accepted anything, and a 2025 part saved in 2026 was checked in with no warning. 0.3.3 reads
the year from the file's own bytes: SolidWorks 2015 and later files carry the release code in
two independent places (the `_MO_VERSION_<code>` stream names and the last entry of the
History stream, which is exactly what SolidWorks' own `VersionHistory` returns). Armory gives a
year only when both agree and the code is in SolidWorks' published table (2025 = 18000, 2026 =
19000); anything else is "unknown", so a 2025 file can never be read as 2026. Measured on 158
public SolidWorks files from 2017 to 2025: 158 read correctly, 0 unknown, 94 ms in total;
6,320 truncated or corrupted copies gave 0 wrong years. Files saved in 2026 and files saved
down from 2026 to 2025 were not available here; they are tested on synthetic files and are
the first items of the lab checklist (`docs/agent/solidworks-lab-checklist.md`).

**The Document Manager key is not needed.** If Mr. Pina wants a vendor cross-check anyway, the
exact steps are in `docs/agent/SOLIDWORKS.md` (free to request, tied to his SolidWorks account,
never put in the repo, the installer or students' computers).

### A3. What the year check does now
- A file newer than the project's year is refused in both Warn and Enforce and stays on that
  computer as a private draft; it is never uploaded as 2026.
- Files sent earlier as "release not checked" are read from identical copies on each computer
  after a loop pass (at most 2 seconds a pass, never during a click) and flagged: "3 files in
  Robot 2027 were saved in SolidWorks 2026", with what a person on a 2026 computer must do.
- A disk error while reading a year used to be swallowed (the file went up unchecked); now the
  file waits for the next pass with a "can't read" notice.
- Stamps (the year the SolidWorks link saw a save write) are kept by content hash and pruned
  30 days after their bytes reach the server; stamps whose bytes never reach the server are
  dropped after 90 days.
- **Yours to change:** once the lab checklist passes, switching projects from Warn to Enforce
  makes an unreadable year a private draft too. Today Warn still uploads a file whose year
  can't be read, marked "release not checked".

## B. The SolidWorks add-in

### B1. The "add-in" is a SolidWorks link inside IdeaArmory.exe, not a DLL loaded by SolidWorks
**Question asked first:** can a SolidWorks add-in be installed per Windows user, with no
administrator, and load when SolidWorks starts?

**Answer: no, not as an in-process add-in.** COM classes can be registered per user
(`HKCU\Software\Classes\CLSID`), and SolidWorks reads a per-user start-up switch
(`HKCU\Software\SolidWorks\AddInsStartup\{clsid}`), but SolidWorks only lists, and only
creates at start, add-ins registered under `HKLM\SOFTWARE\SolidWorks\AddIns\{clsid}`, which
needs an administrator. SolidWorks' own API help says so ("Register the add-in's CLSID in
HKEY_LOCAL_MACHINE\SOFTWARE\SOLIDWORKS\AddIns"), and no source shows it reading a per-user
`AddIns` key. Sources and the full recipe: `docs/agent/SOLIDWORKS.md`.

**Decision:** Armory itself, already running per user in the tray, finds each SolidWorks of
this Windows user and attaches to it from outside (out of process, through the Windows
Running Object Table), the mode SolidWorks documents for "out-of-process add-in
applications". It sees opens and saves, reads the running year, can close and reopen a
document, and can save down. It needs no registration at all, so it installs, updates and
uninstalls with Armory on both routes (setup .exe and flash-drive zip) with no administrator,
and students never set anything up. It cannot crash SolidWorks, because none of its code runs
inside SolidWorks. The attach and the calls were already measured working on Mr. Pina's
SolidWorks 2026 SP04.1 (`docs/spike/solidworks-lock-file.md`); SolidWorks' events from
outside the process are not yet measured on a real computer and are the first item of the
lab checklist.

**What is different from a classic add-in:** it is not listed under Tools > Add-Ins (so a
student can't turn it off there either), and it has no SolidWorks task pane; Armory's own
window and Windows notifications carry its messages. If the lab shows events don't work out
of process on one of our licenses, the in-process add-in recipe (per-user COM keys plus the
optional administrator step for the HKLM key) is written down in `docs/agent/SOLIDWORKS.md`
and can be built then.

## C. File Explorer

### C1. Right-click items: static verbs Armory writes for this Windows user
One cascading "IDEA Armory" item on files and folders inside the Armory folder only, with
Check out, Check out and open, Check in, Undo check out, Show in Armory, and Force check in.
Armory writes them under `HKCU\Software\Classes` itself (no administrator), so they follow
the folder if it changes. On Windows 11 they sit under "Show more options" (or Shift+F10).
Each click starts a tiny native forwarder (`ArmoryShell.exe`, no .NET) that hands the path to
the running Armory over a per-user pipe and exits; Armory gathers the paths of one
right-click (300 ms) into one action and answers with its usual one sentence. Explorer passes
at most 100 selected items to a menu item like this; for more, right-click the folder.

### C2. Force check in appears only for people who can force check in
A menu item can't ask the server about roles, so Armory adds Force check in only while the
signed-in student is a mentor, a CAD lead or a site admin on a project, and only on that
project's folders. The server still decides, so a stale menu item can only produce a refusal
sentence.

### C3. Status badges: four overlays, an optional one-time administrator step
Windows only loads icon overlays registered machine-wide, so badges need an administrator
once per computer. They come in a separate small installer that both routes offer as an
optional step ("Show Armory status on file icons"); the rest of Armory still installs per
user with no administrator, and without that step the badges are simply absent.

Windows shows only 11 overlay handlers in total, shared with OneDrive, Google Drive, Dropbox
and others, so states are combined into four badges:

| Badge | Shown when |
|---|---|
| Attention (red "!") | can't be uploaded, can't be read, changed without a check out, kept copy |
| Mine (pencil) | checked out by you on this computer, changed or not ("changed and not checked in" is always a file you have checked out), or a new file here not in Armory yet |
| Locked (padlock) | checked out by someone else, or by you on another computer |
| Synced (check) | up to date and checked out by nobody |

They are named with one leading space, like OneDrive's, in this order, so Attention is the
last of ours to be pushed out on a crowded computer and Synced the first. The research that
fed this proposed three badges (no Synced badge); Mr. Pina named "synced" among the states to
show, so it got its own, placed where it costs the least. Settings says plainly when Windows
isn't showing Armory's badges and why. `tools/check-overlays.ps1` counts the overlays on a lab
computer.

### C4. The Windows 11 first-level menu is left out
It accepts only commands from apps with package identity, which needs a package signed by a
certificate each computer trusts. Microsoft describes self-signed and unsigned packages as
development and test tools, an unsigned package needs an administrator anyway, and a
self-signed certificate made by our optional administrator step would still have to sign and
register a package for every Windows account, which no CI runner can prove. It fails "no cost
and no extra step for students", so it is not shipped. The same items are one click away
under "Show more options".

### C5. The Cloud Files API is left out
Its free part shows only sync states (custom states need a paid signature), and it would turn
the Armory folder into on-demand placeholders managed by a filter driver, changing how files
sit on disk for SolidWorks.

### C6. How Explorer's items and the notifications reach the running Armory
- **One per-user pipe, one line format,** for the right-click forwarder and for a second
  `IdeaArmory.exe` started by a notification's button (an `idea-armory:` link). The research
  proposed a separate JSON channel; one channel is less to secure and test. Pipes are not per
  Windows session, so the same person signed in twice gets the pipe in the first session only.
- **Each sender checks the pipe's server is `IdeaArmory.exe` from its own folder** before sending
  a path, so a path never goes to another program.
- **Only the installed copy registers** the right-click items, the app identity for notifications
  (`IdeaBosco.Armory`), the `idea-armory:` link scheme and the Start menu shortcut's identity, all
  per user. A test copy leaves the registry alone.
- **Notification buttons carry a token, never a path**: single use, 30 minutes, kept in memory.
  A link Armory doesn't recognize only opens the window.
- **Notifications turned off in Windows means none from Armory**, not even a tray balloon; the
  window's card carries the question. The balloon is used only if the notification API fails.
- **"Check out and reopen" (C5).** When a vault file not checked out is opened, one notification
  asks "Check out <name> to edit it?" with Check out and reopen / Not now (several files opened
  together become one notification with Open Armory). It asks once per open, never while the
  Armory window is showing, and is withdrawn when the file closes. Nothing is checked out just
  because it was opened. With the SolidWorks link, only documents the student opened themselves
  ask (never the parts inside an assembly), and the document becomes editable in place without
  being closed; it is never closed with unsaved changes.
- **The app now targets the Windows 10 1809 SDK** for notifications: the install grows by about
  24 MB (6 MB zipped). Every supported Windows 10 and 11 has it.
- **On the Armory folder itself** the right-click Check in checks in every file checked out here;
  the other items ask the student to pick files or folders inside a project.

## D. The live server calls

### D1. Force check in many files: `armory_break_locks`, 500 at a time
Files go in id order, at most 500 per call. If the website doesn't have the function yet
(PGRST202), Armory falls back to one `armory_break_lock` per file. A whole call that ends in a
deadlock (40P01) or serialization failure (40001) is sent again up to 3 times, and a file that
the server reports as deadlocked inside the results goes again in a later call, up to 3
rounds, each round's operation id chained from the call that answered it busy (so asking again
gets fresh tries, never the old busy answer). Each call's record is saved before it is sent; after a crash the call is sent again
with the same operation id only if every file still has exactly the check out it named,
otherwise it is dropped, so a force check in never ends a check out someone made after the
crash.

### D2. Send feedback like the website's
Kinds bug, idea, praise, other; "What did you try?" (up to 1,000 characters); the area (the
window or view the person was on, up to 120); an optional picture of the Armory window only,
shown to the person exactly as it will be sent, at most 2 MiB. If the website doesn't have the
new form yet (PGRST202), the note goes through the old five-argument form, praise is sent as
"other" there (the old form refuses praise), what was tried, the area and the kind asked for go
in the note's context (so only the picture is lost), and Armory says so. A note without a
picture is saved first and sent later when it can't go now; a note with a picture is never
written to disk, and when its picture can't go the window keeps the words and offers to send it
without the picture.

### D3. "Your feedback"
Lists the person's own notes and their status (new, seen, resolved, closed). There are no
replies from the team anywhere (the website has none either), so Armory shows none.

### D4. The app version stays at 40 characters or fewer
The heartbeat refuses longer versions. A test holds Armory's version to 40, and if a version
were ever refused, the heartbeat stops sending one rather than being refused in a loop.

## E. Fixes found by the audit

### E1. A check in waits for SolidWorks to close the file (the data loss behind note N4)
When SolidWorks held a checked-out part open, Armory's scan couldn't read it and silently reused
the last reading, so a check in let the lock go with nothing uploaded; SolidWorks kept saving,
and the next pass treated those saves as "changed without a check out", kept them on the server
and put the old version back on disk. Now a check in (single, undo, Check in all, batches, an
add's automatic check in) never lets the lock go while the file is open or over bytes it didn't
just read: it sets the file read-only, reads it fresh, and only then releases. An open file stays
checked out and writable, and the first pass after it closes checks it in ("check in when
closed"). The window says so: "Plate.SLDPRT is open in SolidWorks. Save it there and close it;
Armory checks it in as soon as it's closed." Nobody's work was destroyed: the reverted edits are
on the server as kept copies, and File detail can now put one of your kept copies back on this
computer (checked out to you; it keeps any unsaved bytes first).

### E2. IDEA-06's 142 refused files were copies with names already taken
Not the year check: the 142 were copies (mostly from an imported folder) whose names other files
in the project already had; Armory names are unique per project, and the engine re-planned and
re-refused them on every pass, which also kept the window saying "Uploading 0 of 142". A
name-taken copy is now refused once and left alone, never counted as moving, and goes in by
itself when the name frees up. The card tells a student what a SolidWorks copy needs.

### E3. Small judgment calls
- An empty server record (created, but its first version never arrived) is removed by the
  computer that made it, after two scans without the file and once its saves are kept. Records
  whose computer never comes back stay until a lead can remove them (website request).
- Two computers with the same name are told apart across every project this computer knows
  ("IDEA-06 (a030)"), not only within one project.
- A force-checked-in holder reads "kept as Maria's own copy" (the server has only addresses, so
  Armory never guesses anyone's pronouns).
- Organizing files others have checked out: a mentor or CAD lead can force check them in and
  rename, delete or move in one action. Moving without breaking the check out, and an
  "instructor" role, need server changes (docs/agent/website-requests-v0.3.3.md).

## F. Several students taking turns on one computer

Full design: `docs/agent/PROFILES.md`. Settings: "This computer is shared by several students",
off by default; with it off Armory builds exactly the same objects as 0.3.2 and touches no
profiles folder (a test proves it).

### F1. Each student has a profile with their own sign-in
Each profile keeps its own sign-in (its own connection objects), so switching never needs the
browser again. Adding a student is the normal browser sign-in once; the waiting step tells them
to click "Not you? Use another account" because a shared browser is usually still signed in as
the last student. The same address renews that profile instead of adding a second.

### F2. A 4-digit PIN per profile, on by default (**yours to change**)
Set when the profile is added, asked at each switch, so one student can't check out, check in or
force check in as another. Easy PINs (1111, 1234) are refused. Stored only as a PBKDF2-SHA256
hash (600,000 iterations, a salt) next to the profile's sign-in, protected by Windows. Five wrong
tries are free, then waits from 30 seconds doubling to 15 minutes, never a permanent lockout; the
wait survives a restart. "Forgot your PIN?" is a browser sign-in as that same student. A PIN
stops casual impersonation on a shared Windows login; it is not a security boundary against a
determined student with tools (PROFILES.md says so).

**How Mr. Pina turns PINs off:** Settings > Shared computer > "Ask for a PIN when switching
students". It is per computer, only a mentor's own profile in use can change it (the server is
asked whether they are a mentor), and who changed it and when is shown under the switch. A
team-wide switch on the website would need a server field (a website request).

### F3. When the picker shows
When the window opens from hidden (after the X, from the taskbar, Start menu, desktop, tray or a
second launch), on the first open of the day, after Windows is locked, and on Switch student.
Not on minimize. Closing with X leaves Armory running and syncing as the current student.
While the picker shows, Armory keeps working for the student who was in use, but the window shows
none of their files, File Explorer's items and notification buttons open the picker instead of
acting, and the tray says "Switch student".

### F4. One shared folder, handed over by the 0.3.2 rule
The folder goes to the next student only when the last one has nothing waiting in it. If they do,
their tile says so ("Alex has 2 files checked out here") and the new student chooses: wait for
Alex, or continue in a folder of their own (`C:\IDEA\Armory-<name>`). **How waiting work finishes
later:** it stays that student's; when they pick themselves again they finish it in the shared
folder. Once nothing of a student's waits in their own folder, Armory moves them back to the
shared folder by itself. Assemblies saved in an own folder store that folder's paths; other
computers correct them by themselves (SolidWorks looks in the assembly's folder first), which
PROFILES.md explains. While the next student works in their own folder, the last student's
checked-out files in the shared folder are made read-only until they return.

### F5. Switching never lets two students write one folder
A switch stops the old student's sync for good before the new one starts (a new stop guarantee:
nothing is written to disk, state or journal after it returns; tested byte for byte), one switch
at a time, one sync per folder, and a lock file per folder. If a stop takes more than 15 seconds
the picker says "Still finishing..."; after 60 seconds that folder is parked and refused to anyone
else until it stops.

### F6. Remove a profile, and who is using Armory
Remove forgets the student's sign-in and PIN (and signs that session out on the server when it
can) and never deletes a file. A student may remove themselves, a mentor anyone, and anyone may
remove anyone when PINs are off. Settings and the tray show who is using Armory now. Saved notes
and incident reports are sent only while their writer is the student in use.

### F7. Not built, and why
- Profile pictures: initials on a color only. The pictures are on the website, and reading them
  needs the team list per student; left for a later version.
- The website's sending limits are kept per computer, not per student.
