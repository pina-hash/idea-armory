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
