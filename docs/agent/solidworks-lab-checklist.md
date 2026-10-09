# SolidWorks lab checklist (0.3.3)

What the SolidWorks link (`docs/agent/SOLIDWORKS.md`) can't prove on a computer without
SolidWorks. Each step names the open question it settles. Run them in order; P1 decides
whether the link works at all on our licenses.

## Computers and rules

Two computers on the same Armory project, Armory 0.3.3 installed per user, signed in as a
normal Windows user, SolidWorks started normally (not "Run as administrator"):

- **A**: an IDEA PC, SolidWorks Education Edition 2025. Record the service pack (SP5 is
  needed for L9's future-version step).
- **B**: a student's personal computer, SolidWorks 2026 from the FRC sponsorship. Record the
  service pack (SP3 or later for L1 to L8).

Rules (AGENTS.md):

- Use only files made for the test. Never team or customer files, and never put CAD files in
  the repo.
- Keep originals: copy a file before each destructive step.
- Probes stay within Documents and the vault, and look at no more than 50 files.
- Never change Windows Defender settings.
- Record every value in the table at the end. For each file: its **SHA-256** (Armory's file
  details, or `Get-FileHash`), Armory's reader result, and `ISldWorks.VersionHistory`
  (`tools/Armory.Probe`).
- Armory's log is `%LOCALAPPDATA%\IDEA Armory\logs\agent.log`; every link line starts with
  `solidworks`.

## The link itself

**P1. Events out of process (decides the 0.3.3 link; A and B).** Start SolidWorks, then
Armory. In the log, record `solidworks link attached pid=<pid> revision=<revision>` and the
moniker line (`found in the running object table as ...`). Record
`HKCR\Interface\{83A33D22-37C5-11CE-BFD4-00400513BB57}\ProxyStubClsid32` and the same value for
`{83A33D32-...}`, `{83A33D35-...}` and `{83A33D34-...}` (`reg query`). Record the log line
`FileSavePostCancelNotify for document type 1: <number or "not published">` (and types 2 and
3). In a scratch folder in the vault, make a new part, change it, save it, Save As a copy,
close it. Record: each step's Armory reaction (Settings line, notices, activity log) and that
saving took no visible extra time. Close SolidWorks by hand and record that SLDWORKS.exe is
gone within 10 seconds (Task Manager) and the log says `solidworks link detached`. Repeat
with Armory ended from Task Manager while SolidWorks has a file open: SolidWorks still saves
and closes normally. Pass: every event arrives, nothing slows, SolidWorks exits.

**P2. Only if P1 fails: `LoadAddIn` of a per-user add-in (B).** Follow
`docs/agent/SOLIDWORKS.md` section 10 with a tiny probe add-in that writes one log line in
`ConnectToSW`. (a) With `HKCU\Software\SolidWorks\AddIns\{probe}` and `AddInsStartup` = 1,
start SolidWorks: does it load, is it listed in Tools > Add-Ins? (b) Without (a), call
`LoadAddIn(<dll path>)` from `tools/Armory.Probe` after attach and record the
`swLoadAddinError_e` code. Remove every key and file afterwards.

**P3. SolidWorks as administrator (A or B).** Close SolidWorks, start it with "Run as
administrator". Record: Settings says "SolidWorks was started as administrator, so Armory
can't link to it.", one notice says so, and the log has `runs as administrator; not linked`.
Close it and start it normally: Armory links within a few seconds.

## Check out and reopen (C5)

**N1. Markers (A).** Close Armory. Make an assembly with three generated parts in
`Documents\ArmoryLab`; open the assembly; record every `~$` file, when it appeared and when it
went (one per component? does the assembly's come first or last?).

**N2. Notifications (B; needs C5's notifications, else read the window's question).** Open a
vault part not checked out: one notification (once per file per SolidWorks session). Open five parts at once from File Explorer: one notification for the
five. Open an assembly of 30 parts: one notification, for the assembly only. Record what each
showed.

**N3. In place (B).** Check out a part SolidWorks has open read-only, with "Check out and
reopen": (a) unchanged, (b) changed before the check out, (c) also loaded by an open assembly;
then an assembly; then a drawing. Record Armory's sentence, whether SolidWorks' title lost
"Read-Only" without the window closing, and that Save then writes the vault file (its hash
changes).

**N4. Reload (B).** Where N3 didn't make it writable in place: an unchanged part should reload
writable (`ReloadOrReplace(false, null, false)`), a changed one must keep its changes and get
the "close it in SolidWorks and open it again from Armory" sentence; a drawing likewise
(`CloseAndReopen(doc, 4)`). Record each `solidworks: reload of` or `close and reopen of` log
line. Pass: no change is ever lost.

**N5. Dirtiness (B).** Open a 2025 vault file in 2026 and change nothing: does SolidWorks
mark it changed (an asterisk), and does the log say `solidworks: <name> changed` without the
student touching it? This decides whether "changed" can be trusted for a reload.

**N6. Which opens ask (B).** Open an assembly; open one of its parts in its own window; open a
part in Large Design Review. Record which ones Armory asked about.

## Save down (B2 to B5)

**L0. Setup (A).** In a new project folder `C:\IDEA\Armory\Lab 2025-26\`, create:
`LabPlate.SLDPRT` (extrude, Hole Wizard hole, fillet; custom properties `PartNumber =
LAB-0001`, `Description = Lab plate`; material 6061; one face colored red), `LabPin.SLDPRT`,
`LabBracket.SLDASM` (LabPlate and two LabPin, concentric and coincident mates, one exploded
view with 2 steps), `LabPlate.SLDDRW` (three views, dimensions, a note). Save all, check in.
Record A's Help > About (version, SP), the four hashes, reader results (expect 2025), and
VersionHistory (expect the last entry `18000[...]`).

**L1. The two preference numbers (B).** In PowerShell on B:
`Add-Type -Path "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.swconst.dll"`,
then print `[int][SolidWorks.Interop.swconst.swUserPreferenceToggle_e]::swEnableSaveToVersion`
and `[int][SolidWorks.Interop.swconst.swUserPreferenceIntegerValue_e]::swSaveToVersion`.
Record both numbers and B's `RevisionNumber`. Compare with Armory's log line `Save to Version
preferences are <n> and <n>`. If Armory found none, write them into
`%LOCALAPPDATA%\IDEA Armory\solidworks.json` as
`"saveToVersionIds": {"enableToggle": <n>, "versionValue": <n>}` and restart Armory.

**L2. Licensing (B4, B).** On a copy outside the vault (`Documents\ArmoryLab\Copy.SLDPRT`):
File > Save As: is "SOLIDWORKS 2025 Part" in Save as type? Tools > Options > System Options >
Backup/Recover: is "Save to Version" present and selectable? Save the copy as 2025: does it
succeed? Record yes or no with screenshots, the license type in Help > About, and Armory's
Settings line.

**L3. In-place save down of a part (B2, B).** Check out LabPlate. Open it, change the hole
diameter, add a blue appearance on another face, add a custom property `Finish = anodized`,
and Save. Record: any dialog; Armory's drop list before the save; the save time; the path and
name unchanged; the new hash; the reader result; VersionHistory (does the last entry read
`18000[...]`? is a 19000 entry present?); Explorer's "SW Last saved with" on A and on B; in the
still-open document, are the appearances still shown? Close and reopen on B: appearances,
custom properties (`PartNumber`, `Description`, `Finish`), material, feature tree. Then edit
and Save twice more and record each save's VersionHistory (a document may save back "one time
only"). Check in.

**L4. The assembly on 2025 after a part was saved down (B3, A).** Get the latest on A. Open
LabBracket.SLDASM. Record: any "internal ID does not match" or missing-reference dialog
(screenshot); a future-version icon on LabPlate; Ctrl+Q rebuild errors; every mate's state;
File > Find References paths; the hole-diameter change visible. Open LabPlate.SLDPRT alone on
A: editable (not a future version)? Feature tree complete? Custom properties and appearances
as on B after reopen?

**L5. Assembly save down (B).** Check out LabBracket. Open on B, change a mate distance, Save.
Record which files changed hash, the VersionHistory of each, and the explode steps after
reopening on B. Check in. On A: open LabBracket, record mates, explode steps, rebuild errors.

**L6. When the option must be set (B).** Armory sets Save to Version when a vault document
becomes the active one. Make LabPin active, then switch to a `Documents\ArmoryLab` part and
back, and save LabPin: record that the save wrote 2025 (VersionHistory). With one vault
document and one `Documents\ArmoryLab` document open and changed, File > Save All: record each
file's year. Record Tools > Options > Backup/Recover while each is active, and after closing
SolidWorks (the student's own setting must be back).

**L7. A blocked save (B).** On a copy part in the vault, add a 2026-only feature (one that
Tools > Evaluate > Previous Release Check lists for 2025). Save. Record: the log's
`solidworks: checked <name> for 2025: result <n>, <n> blocked, ...` line, the items (`Message`, `Action`), what Armory showed, that
the file was saved as 2026 here, that Armory kept it as a private draft and never uploaded
it, and the words shown. Then remove the feature, Save, and record that it saved as 2025 and
uploaded.

**L8. A drawing, and timing (B, then A).** Check out LabPlate.SLDDRW; on B change a note,
Save, check in; on A open it and record views, dimensions, note. Time
`CheckVersionCompatibility` on the team's largest robot assembly (open it and record the
milliseconds at the end of the log's `solidworks: checked <name> for 2025` line; do not save it, and keep it out of the
repo).

**L9. Reader cross-checks (A and B).** For every file above, side by side: Armory's reader,
the last VersionHistory entry, Explorer's "SW Last saved with", and (only if Mr. Pina has a
Document Manager key, `docs/agent/SOLIDWORKS.md` section 9) `GetVersion`. On A, open the 2026
file left from L7: record SolidWorks' behavior (future version, read-only; drawings refused)
and Armory's notice ("was saved in a newer SolidWorks than 2025").

**L10. A new file (B).** File > New part, save it into the vault folder: record its year
(expect 2025) and that it uploaded.

## Record table

One row per step and file:

| Step | Computer (SW version, SP, license) | File | Hash before | Hash after | Reader | VersionHistory | Explorer column | Armory showed | Dialogs (screenshot names) | Result (pass/fail, notes) |
|---|---|---|---|---|---|---|---|---|---|---|

## Pass criteria

- P1: every event arrives and SolidWorks exits within 10 seconds of closing it.
- N3 and N4: no change is ever lost; a changed document is never reloaded or closed.
- L3 and L5: the files read 2025 everywhere.
- L4 and L5 on A: no internal-ID dialog, no missing reference, no mate error that wasn't there
  before.
- L7: never uploaded as 2026.
- L9: the reader never disagrees with VersionHistory.

After the lab passes, Mr. Pina can switch projects to Enforce.
