# Good morning: Armory 0.3.3

Everything in the overnight brief is built, tested and on `main`. This page covers:

- how to publish the release, what to install and what to try first;
- where every feedback note stands;
- what was proved and what was only reasoned;
- the lab checklist.

Nothing cost money and nobody was asked anything. Every choice is recorded, with the default
used, in `docs/agent/decisions-0.3.3.md`.

## 1. Publish the release (2 minutes, then about an hour of CI)

1. Open https://github.com/pina-hash/idea-armory/releases/new
2. **Choose a tag**: type `v0.3.3`, then pick "Create new tag: v0.3.3 on publish".
3. **Target**: `main`.
4. **Release title**: `IDEA Armory v0.3.3`
5. **Description**: paste `docs/agent/release-notes/v0.3.3.md`. These are the students' notes.
6. Click **Publish release**.

The **Agent release** workflow (Actions tab) then does the rest:

- It builds both installers and the optional badges setup.
- It runs the same Windows cycles as CI: flash drive, setup.exe, upgrades from 0.1.0 and 0.3.2, SolidWorks open, and badges.
- It attaches these files, each with a `.sha256`:
  - `IDEA-Armory-Setup-v0.3.3.exe`
  - `IDEA-Armory-USB-v0.3.3.zip`
  - `IDEA-Armory-Badges-Setup-v0.3.3.exe`

If a cycle fails, nothing is attached and the run's log names the cycle.

## 2. What to install

- **Your computer and students' laptops:** `IDEA-Armory-Setup-v0.3.3.exe`. It needs no administrator. Installing over 0.3.2 keeps the folder, the files and the sign-in.
- **Lab computers:** the flash drive zip. Run `Install IDEA Armory.cmd` once per Windows account.
- **Optional, once per computer, needs an administrator:** status badges on file icons. Any one of these turns them on:
  - tick "Also show Armory's status on file icons" on setup.exe's last page;
  - run `Show Armory status on file icons.cmd` from the flash drive;
  - press **Turn on** in Settings;
  - for IT: `IDEA-Armory-Badges-Setup-v0.3.3.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

  Windows shows the badges after each person signs out and back in. Skipping this step changes nothing else.

## 3. What to test first, in this order

1. **The N4 data loss (Abraham's lost work).**
   - Check out a part, open it in SolidWorks, change it and save.
   - While it is still open, click Check in. Armory must say: "... is open in SolidWorks. Save it there and close it; Armory checks it in as soon as it's closed."
   - Close the file. It checks in, and your change is in it.
   - If a student lost work earlier, open the file in Armory, go to the kept copy, and press **Put back on this computer**.
2. **Every click at once (N6).** In a big folder, press Check out, Check in, Open, a file's page, and Pause. Each key shows that it is working at once, and a running line appears within a moment.
3. **A big download (N2, N3).** Download a fresh folder of 1,000 or more files. The progress line keeps counting with no "Checking for changes" pauses, and says "Finished" once, at the end.
4. **SolidWorks 2026 on your computer.** Do lab steps P1, L1, L2 and L3 first (section 7).
   - Settings should show "Linked to SolidWorks 2026 SP...".
   - Open a vault part you haven't checked out. A Windows notification should offer **Check out and reopen**.
5. **File Explorer.** Right-click a file in `C:\IDEA\Armory`, choose Show more options, then IDEA Armory.
6. **A shared lab computer.** In Settings, turn on "This computer is shared by several students". Add two students and switch between them.
7. **Send feedback** with a picture, then open Settings and look at Your feedback.

## 4. The audit

`docs/agent/feedback-audit.md` is one table plus a note for every item, with file references and how each was verified. It has 105 items:

- all 15 feedback notes;
- all 77 incident reports, grouped into 26 groups;
- every claim of the 0.3.0, 0.3.1 and 0.3.2 rounds (22);
- 42 further problems the audit found.

| Status at 0.3.3 | Notes | Incident groups | Earlier claims | Other findings | All |
|---|---:|---:|---:|---:|---:|
| Done | 13 | 25 | 21 | 40 | 99 |
| Partly done | 2 | 0 | 1 | 1 | 4 |
| Not done | 0 | 0 | 0 | 0 | 0 |
| Not reproducible | 0 | 1 | 0 | 1 | 2 |
| Won't do | 0 | 0 | 0 | 0 | 0 |

### Not fully done, and why

- **N5, "let people organize files others have checked out" (Partly done), and its part X-N5a.**
  - Built: mentors and CAD leads can rename, move or delete around other people's check outs in one action. Armory force checks those files in first, and the holders' unsaved work is kept as their own copies.
  - Not built, part 1: moving a file someone else keeps checked out *without* forcing. The website's `armory_move_file` allows only the lock holder.
  - Not built, part 2: an "instructor" role. The website has mentor, CAD lead, student and site admin only.
  - Both need a change to idea-app, which this round could not touch. They are written up as requests in `docs/agent/website-requests-v0.3.3.md`, sections 1 and 3.
  - You can force check in today because you are a mentor on FRC 2026 Off-Season.
- **N15, "a thumbnail of each part", and the 0.3.2 thumbnail claim (Partly done).**
  - Built: everything around the pictures. They never hold up the window, a stuck Windows thumbnail handler is left behind and logged, and the queue is bounded.
  - Not done: a check with real SLDPRT, SLDASM and SLDDRW files. This computer has no SolidWorks, and AGENTS.md forbids committing team CAD files.
  - It is lab item G1.
- **X-two-idea06-engines (Not reproducible).** No Armory defect: two different computers are both named IDEA-06. Armory now tells same-named computers apart.
- **I-crash-IDEA-06-0.3.1 (Not reproducible).** The report was about a 0.2.0 run that left no evidence at all. 0.3.3 keeps what a future report needs to tell a crash from a quit.

### Brief items left out on purpose

These are recorded in the decisions file with the reasons:

- **The Windows 11 first-level menu (C3/C4).** It needs a package signed by a certificate each computer trusts, which is not free and not step-free for students. The same items are under Show more options.
- **The Cloud Files API (C5).** Left out, as the brief said.
- **An in-process SolidWorks add-in (B1).** SolidWorks loads add-ins only from a machine-wide (HKLM) registry key, so it can't be per user without an administrator. Armory links to SolidWorks from its own process instead, with nothing to install.
- **Profile pictures (F7).** Profiles show initials on a color.

## 5. What was proved, and what was only reasoned

**Proved by tests that ran.** The Linux runs here used a throwaway PostgreSQL. The Windows runs were on GitHub's Windows Server 2025 runner, in Agent installers run 35 on the final branch.

- **The data loss (N4).**
  - The field reproduction fails on 0.3.2 and passes now.
  - The Core check-in rule has tests.
  - The checkout simulation's invariants are only stronger.
- **The SolidWorks year reader.**
  - Tested on synthetic files for every year code and every malformed case.
  - It read 158 public SolidWorks files (2017 to 2025, kept outside the repo) correctly.
  - It gave no wrong answer on 6,320 corrupted copies.
- **IDEA-06's 142 refusals.** End-to-end tests: each name-taken copy is refused once, says why, and is never counted as uploading.
- **Speed (N6).** `LargeVaultResponsivenessTests` time 22 window actions on a 1,500-file vault with school network latency, with Windows' open-files cost modeled. Every action answered within its allowance (`docs/agent/responsiveness-0.3.3.md`).
- **Continuous downloads (N2, N3).** 1,500 files never go a second without a download running, and one stalled file does not hold the others.
- **The website calls (D1 to D4).** Tested against the test server, which carries idea-app's 0234 and 0235 contract:
  - `armory_break_locks` (500 per call, fallback, retries);
  - the eight-argument feedback form with its picture and every refusal;
  - `armory_my_app_feedback`;
  - the 40-character version.
- **On real Windows (CI):**
  - **Tests:** every test project, including 93 platform tests (real NTFS, Restart Manager, DPAPI, child processes) and 75 SolidWorks-link tests against a fake SolidWorks. The fake is a real out-of-process COM server with SolidWorks' own event numbers.
  - **Right-click items:** their registry layout, appearing only inside the vault, and Explorer-style launches reaching Armory as one batch.
  - **Badges:** the native badge handlers answer like the C# rules. The badges setup installs, and Windows creates all four handlers. `check-overlays.ps1` lists them as shown, and the uninstaller removes everything.
  - **Notifications:** the registration and the Start menu shortcut identity on both install routes.
  - **Upgrade with SolidWorks open:** SolidWorks kept running, and the new Armory linked again with no SolidWorks registry footprint.
- **Shared computers (F).** The picker, PIN (right, wrong, and the wait surviving a restart), switching mid-download and mid-check-in with nothing written after the stop, hand-over with and without waiting work, removing a profile, and single-user mode unchanged.
- **The window.**
  - `check-ui`: 312 rendered pages in both themes and all sizes, 86 click flows, every bridge message, and a tooltip on every one of 11,216 controls.
  - `bbox-diff`: identical geometry in both themes.
  - Both run in headless Chromium.

**Only reasoned, not proved:**

- **That SolidWorks' events arrive in another process on our licenses.** The attach itself was measured working on your SolidWorks 2026 SP4.1 in an earlier spike; the events were not. This is lab step P1, and everything SolidWorks-related depends on it.
- **Saving down to 2025 (B2 to B4):**
  - the two "Save to Version" preference numbers (Armory reads them from the installed SolidWorks' own files, and does nothing if it can't);
  - whether the sponsorship license allows it;
  - that assemblies still find their parts afterwards (B3);
  - what exactly 2025 drops.

  If any of these fails, the file stays a private draft on that computer and is never uploaded as 2026.
- **Check out and reopen** making the open document editable in place in real SolidWorks.
- **The Windows notifications themselves:** that they appear, and that their buttons reach Armory. The CI runner has no interactive notification center. The links and tokens behind the buttons are tested.
- **Timing on a lab computer and the school network.** All timings come from the test world.
- **Windows sign-out and sleep handlers**, which have unit tests only.

## 6. Could not be tested here at all

- Real SolidWorks 2025 or 2026 (no SolidWorks on this machine or on CI).
- Windows 10 22H2 and Windows 11 desktop Explorer as a person sees them: the menu's placement and badges changing on screen. CI is Windows Server 2025 without a person.
- A real toast notification and its buttons.
- The live ideabosco.com site. Every call was tested against the contract's test server, never against production (no credentials here, by design).
- WebView2 on a real screen, at 100% and 150% scaling (the feedback picture).
- Thumbnails of real SolidWorks files.

## 7. Lab checklist

Do these in order. The SolidWorks steps are written out in full in `docs/agent/solidworks-lab-checklist.md`, which also has a table for the results.

**A. SolidWorks.** Use two computers: one IDEA PC with SolidWorks 2025 Education, and one laptop with SolidWorks 2026 SP3 or newer. Use only files made for the test.

1. **P1 (both computers).** Start SolidWorks normally, then Armory. Settings says "Linked to ...". Make, change, save and close a scratch part in the vault, and check that Armory reacts to each step. If P1 fails, stop and send the log: the in-process add-in recipe is ready in `docs/agent/SOLIDWORKS.md`.
2. **P3.** SolidWorks started "as administrator": Settings says Armory can't link to it.
3. **N1 to N6.** The check-out question and Check out and reopen, including an assembly and a drawing.
4. **L1 (2026).** The two preference numbers. If Settings already says "It saves team files in 2025", Armory found them by itself.
5. **L2.** Licensing: does Save to Version work on the sponsorship license?
6. **L3 to L8.** Save a part, an assembly and a drawing down. Open them on the 2025 PC: references intact, nothing lost that wasn't listed. Also check a blocked save, and the timing.
7. **L9.** Armory's year reader against SolidWorks' own, for every file above.
8. **L10.** A brand-new file saved from 2026.

**B. Windows 11 Explorer.** Use a student's laptop.

1. Right-click inside `C:\IDEA\Armory`, then Show more options, then IDEA Armory.
   - The items appear there, and not outside the vault.
   - Force check in appears only for a mentor or CAD lead.
2. Select 16, then 40, then 100, then 101 files. The menu shows up to 100, and Armory's log shows one action per right-click.
3. Check in from the menu while Armory's window is closed. A Windows notification gives the one-sentence answer.

**C. The overlay (badges) step.**

1. Run the badges setup as an administrator, then sign out of Windows and back in.
2. As a student, check that Settings > Status on file icons says they are shown. The flash drive's `Check IDEA Armory.cmd` prints the same line.
3. From a copy of the repository, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-overlays.ps1`. Keep its output. Try it with OneDrive signed in: Windows shows only 11 overlays, and the script and Settings say when Armory's are pushed out.
4. Badges change within a second of a check out, check in, undo and Force check in, in an open Explorer window and in SolidWorks' Open dialog.

**D. The profile picker.** Use a lab PC.

1. Settings > "This computer is shared by several students" > on.
2. Add two students, each with a PIN. Easy PINs are refused.
3. Switch between them. A wrong PIN counts; the fifth wrong one waits.
4. Close the window with X: Armory keeps running, and the picker shows on reopen.
5. Lock Windows: the picker shows when it is unlocked. It also shows on the first open of the day.
6. Switch while a download runs. The next student starts cleanly.
7. Leave a file checked out, then switch. The picker says the work waits, and offers to wait or to work in an own folder.
8. Remove a profile: no file is deleted.
9. Turn the PIN requirement off from a mentor's profile if you don't want it (decision F2).
10. Also check the five items under "Owed to the lab" in `docs/agent/PROFILES.md`:
    - PIN speed on the oldest PC;
    - "Not you?" in a shared browser;
    - what SolidWorks does with a sealed file;
    - Referenced Documents;
    - DPAPI.

**E. Notifications.** Open a vault part you haven't checked out while Armory's window is closed. A notification asks; Check out and reopen works; Not now goes away. Repeat with Focus Assist on.

**F. Send feedback** with a picture at 100% and 150% display scaling. It arrives on the website, and Your feedback lists it.

**G. Thumbnails.**

1. A folder with SLDPRT, SLDASM and SLDDRW files shows their pictures.
2. A file whose picture Windows can't make shows the plain icon.

## 8. Everything else

- **Decisions:** `docs/agent/decisions-0.3.3.md`. Entries marked **Yours to change** are the ones you may want differently. The 4-digit PIN default is one.
- **Dependencies:** `docs/agent/dependencies-0.3.3.md` lists every new dependency, service and tool. All are free, and the optional badges setup is the only step that needs an administrator.
- **Website requests:** `docs/agent/website-requests-v0.3.3.md`. Armory works without them; they are for the next idea-app pass.
- **Install details:** `docs/agent/INSTALL.md`. What each route installs, and what uninstall removes and keeps.
- **CI:** these workflows ran on `main`. Results are in the final message of this round and in the Actions tab.
  - Agent installers
  - Armory tests
  - Core tests
  - Agent window checks
