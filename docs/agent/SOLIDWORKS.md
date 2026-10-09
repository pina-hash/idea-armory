# The SolidWorks link (0.3.3)

`src/Armory.SolidWorks`, inside IdeaArmory.exe. Decision B1 (`docs/agent/decisions-0.3.3.md`)
in full: why there is no add-in, how the link works, saving down to a project's pinned year
(B2), what is known and unknown about references (B3) and licensing (B4), the hints for files
already saved in a newer year (B5), and the SolidWorks half of "Check out and reopen" (C5).
The engine's side is `docs/agent/ENGINE.md` ("The SolidWorks link"); the page's side is
`docs/agent/BRIDGE.md`; the lab steps that settle every open question are
`docs/agent/solidworks-lab-checklist.md`.

Labels: **VERIFIED** (official documentation, vendor source, or measured), **SOURCED**
(credible secondary source), **REASONED** (inference only). The sources are listed at the end.

## 1. Can an add-in be installed per Windows user? No

1. **Per-user COM registration works for a normal SolidWorks.** A class under
   `HKCU\Software\Classes\CLSID\{guid}` is visible through the merged `HKEY_CLASSES_ROOT` to a
   process at medium integrity, which is how a student's SLDWORKS.exe runs; it is ignored when
   SolidWorks runs elevated. VERIFIED ([ms-merged], [ms-uac]).
2. **SolidWorks lists, and creates at start, only add-ins registered in
   `HKLM\SOFTWARE\SOLIDWORKS\AddIns\{clsid}`** (or the version-specific
   `HKLM\SOFTWARE\SOLIDWORKS\SOLIDWORKS <year>\AddIns\{clsid}`). The API help: "Register the
   add-in's CLSID in HKEY_LOCAL_MACHINE\SOFTWARE\SOLIDWORKS\AddIns". VERIFIED ([sw-swaddin],
   [sw-icons]). `HKCU\Software\SolidWorks\AddInsStartup\{clsid}` (DWORD 1) is per user but only
   sets the start-up state of an add-in already listed in HKLM. VERIFIED ([sw-icons],
   [codestack-manual], [xcad-reg]). No source shows SolidWorks reading a per-user `AddIns` key.
   REASONED: it does not.
3. Writing HKLM needs an administrator ([javelin]); Inno Setup's `restartreplace` and
   `uninsrestartdelete` do nothing without one ([inno-files]). So no per-user installer can
   make SolidWorks load an in-process add-in at start.
4. **Decision: no add-in.** Armory, already running per user, attaches to SolidWorks from
   outside, the mode SolidWorks documents for "out-of-process add-in applications" (its
   `StartupProcessCompleted` property exists for them, VERIFIED [sw-startup]). Nothing is
   registered and nothing is loaded into SolidWorks, so both install routes, upgrade and
   uninstall are unchanged, the link can't crash SolidWorks, and it is never listed under
   Tools > Add-Ins. The attach and the calls were MEASURED on SolidWorks 2026 SP04.1
   (`docs/spike/solidworks-lock-file.md`); events out of process are lab step P1.

## 2. How the link works

**Where.** `src/Armory.SolidWorks` (`net10.0-windows`), referenced by `Armory.Agent`. No
Dassault assembly and no interop package anywhere (the NuGet repackagings carry no license,
and `tools/package-agent.ps1` refuses any `SolidWorks.Interop*` file). Every call is late bound
by name through `IDispatch` (`Type.InvokeMember`, the `tools/Armory.Probe/ComDispatch.cs`
pattern, never C# `dynamic`, which failed on one installation); every event goes through
`ComEventsHelper.Combine` with the interface ids and event numbers in `SwConstants.cs`, the one
table, guarded by `SwConstantsTests` (values MEASURED from SolidWorks' type information for
2025 SP5 and 2026; identical in both). `FileSavePostCancelNotify` is not in the measured
table: its number is read at run time from SolidWorks' own type information
(`IDispatch.GetTypeInfo`, its type library, `GetIDsOfNames`), and when SolidWorks doesn't
publish it the event is simply not followed (the log says which).

**One link per vault runtime.** `AgentHost.SolidWorks.cs` makes it with the runtime (its
settings file is `%LOCALAPPDATA%\IDEA Armory\solidworks.json`), passes it to the engine
(`EngineDependencies.SolidWorks`), starts it right after the engine, and disposes it after
the engine when the runtime stops (Armory quits, or the vault root changes).

**Discovery** (every 2 seconds, on the link's thread). The `SLDWORKS` processes in this
Windows session whose token belongs to this Windows user. One started as administrator (an
elevated token, or a token Armory can't read) can't be reached, because COM does not cross
integrity levels ([3ds-elev], [ms-uac]): the engine hears `LinkRefused` once, and the student
reads "SolidWorks was started as administrator, so Armory can't link to it." in Settings and
on a notice. A test-only variable, `ARMORY_SOLIDWORKS_PROCESS`, names another process.

**Attach.** The application object comes from the Running Object Table, the entry named
`SolidWorks_PID_<pid>` (an item moniker shows it as `!SolidWorks_PID_<pid>`; both forms match;
[codestack-connect]; P1 records the real one), else `GetActiveObject("SldWorks.Application")` when its `GetProcessID()` is that process. The
link waits until `StartupProcessCompleted` is true (asked once per tick, each call allowed one
second) before it subscribes to anything, then reads `RevisionNumber` ("34.4.1"; one it can't
read means not linked).

**One STA thread** with its own message loop (`MsgWaitForMultipleObjectsEx`, then
`PeekMessage`/`DispatchMessage`) and an `IOleMessageFilter`: a call SolidWorks answers "busy"
(`SERVERCALL_RETRYLATER`) is retried every 100 ms for up to 30 seconds ([ms-filter]); while a
SolidWorks starts or closes, one or two seconds, so a quit never waits long.

**Events.** Application: `FileOpenPostNotify`, `FileNewNotify2`, `ActiveModelDocChangeNotify`,
`FileCloseNotify`, `DestroyNotify`. For each vault document (and each new document, until it
is saved somewhere else), by its type: `FileSaveNotify`, `FileSaveAsNotify2`,
`FileSavePostNotify`, `FileSavePostCancelNotify`, `ModifyNotify`, `DestroyNotify2` (a
"hidden" destroy is a document still loaded by an assembly, so still open). SolidWorks waits
for every reply: each handler answers 0 from memory and posts anything that calls back into
SolidWorks to run after it. No handler ever vetoes a save. An exception never reaches
SolidWorks.

**Catch-up.** A SolidWorks already running when Armory starts: `GetDocuments()`; the active
document and every visible one count as opened by the student, and their changes as unknown.

**The end.** On `DestroyNotify`, when the process is gone, when Armory quits and when the vault
root changes: every sink removed (`ComEventsHelper.Remove`), every object released
(`Marshal.FinalReleaseComObject`), the student's own Save to Version setting put back, and at
the very end the message filter revoked. A COM failure that means SolidWorks is gone ends
that session quietly (`LinkDetached`), and discovery goes on; a SolidWorks that said it is
closing is not attached to again while it finishes.

**Event marshaling.** SolidWorks' `Advise` asks Armory's sink for the event interface. Across
processes that needs the interface's proxy, which SolidWorks' installed type library
registers (REASONED; P1 records `HKCR\Interface\{83A33D22-37C5-...}\ProxyStubClsid32`). The
test fake, on computers without SolidWorks, asks for the event interface and falls back to
`IDispatch`, which the events arrive on anyway; the tests write nothing to the registry.

### Records and commands

Link to engine, in order, on one in-process channel (`ISolidWorksLink.Records`; full paths;
only documents under the vault root):

| Record | When |
|---|---|
| `LinkAttached(pid, revision, runningYear, saveDown)` | after attach, and again when save down stops working (B4) |
| `LinkRefused(pid, reason)` | once per SolidWorks started as administrator |
| `LinkOpened(pid, path, docType, readOnly, futureVersion, topLevel, at)` | `FileOpenPostNotify`, a new document saved into the vault, catch-up |
| `LinkActivated(pid, path, at)` | the document became SolidWorks' active one |
| `LinkModified(pid, path)` | `ModifyNotify` |
| `LinkSaving(pid, path, saveId, mode)` | `FileSaveNotify` or `FileSaveAsNotify2`: mode `SaveDown`, `Current` or `PrivateDraft` |
| `LinkSaved(pid, path, saveId, saveType, stamp)` | `FileSavePostNotify`, with the stamp of the bytes (section 3) |
| `LinkSaveCanceled(pid, path, saveId, mode)` | `FileSavePostCancelNotify` |
| `LinkCompatibility(pid, path, targetYear, checked, blocked, drops)` | after an open, after changes are quiet for 3 seconds, after each save |
| `LinkStamped(pid, path, stamp)` | at open: the year SolidWorks reads in the bytes on disk (B5) |
| `LinkClosed(pid, path)` | `FileCloseNotify`, a destroy that isn't "hidden", a Save As elsewhere |
| `LinkDetached(pid, reason)` | SolidWorks closed or died, Armory quit; the student's setting is back first |

Engine to link: `MakeWritableAsync(path)`, `SaveInPinnedReleaseAsync(path)`,
`SetPins(projects)` (each project's folder, pinned year and gate mode, sent when they change),
`KeepLocal(path, on)`, `StatusText(text)` and `Replay()` (what is true now, for an engine that
just started).

## 3. Saving down to the pinned year (B2)

Since 2024 SolidWorks saves parts, assemblies and drawings in the previous two releases
([sw-prev]). `IModelDocExtension.SaveAs3` with `IAdvancedSaveAsOptions.SaveAsPreviousVersion`
can't overwrite the same file ([sw-sapv]), so the link uses the **Save to Version** system
option of 2026 SP3: "any type of save (such as File > Save, File > Save All ...) always uses
the Save to Version option you selected" ([sw-wn]), through
`swUserPreferenceToggle_e.swEnableSaveToVersion` and
`swUserPreferenceIntegerValue_e.swSaveToVersion` ([sw-so]), whose values are relative
(`swSaveToVersion_e`: 1 is the release before, 2 the one before that, [sw-stv]). One file, one
path, one hash, and SolidWorks handles an assembly's components itself. VERIFIED.

**At attach** (`SaveDownManager`). The link reads the student's own two values and keeps them
in `solidworks.json` (so a crash, or a SolidWorks that closed first, puts them back at the
next attach), then sets each to its own value and reads it back. `SaveDown.Support` (Core)
says what this SolidWorks can do: `Available`, `OldServicePack` (2026 before SP3),
`NotConfigured` (the two preference numbers are unknown here), `NotLicensed` (they didn't read
back, or a save with the option on still wrote this release) or `Unsupported` (before 2026).
Anything but `Available` means Armory never touches the option.

**The two preference numbers are not printed in the API help** and are missing from the
public 2026 SP0 interop (research, MEASURED). Armory never guesses one (a wrong number would
change another of the student's preferences). It reads them from `solidworks.json`
(`"saveToVersionIds": {"enableToggle": n, "versionValue": n}`, written by hand from lab step L1;
there is no default), else from the installed SolidWorks' own
`api\redist\SolidWorks.Interop.swconst.dll` beside SLDWORKS.exe, read as metadata
(`System.Reflection.Metadata`, never loaded as code). Neither: no save down, and Settings says
"Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save team files in 2025.
Until then, files you save stay on this computer only."

**While a document is SolidWorks' active one** (set ahead of the save, never inside an event;
lab L6 decides whether setting it inside `FileSaveNotify` also works). `SaveDown.Choose`
(Core): a vault document in a project pinned one or two releases back, on a SolidWorks that is
`Available`, saves down (option on, value running minus pin), unless SolidWorks' own check
says it can't go back ("blocked") or the student chose to keep it here; then the option is off,
so the save writes this release and the file stays a private draft. Everything else gets the
student's own setting back.

**Checked early.** After an open, after changes are quiet for 3 seconds and after each save:
`IModelDocExtension.CheckVersionCompatibility(target, NeverShow)` (2026 SP3, [sw-cvc]): its
`IVersionCompatibilityItem`s (`Message`, `Action`, the object's name) are the blockers, its
warnings are drops; plus an inventory of what a save down drops that the check may not list
(research 3.3): appearances (`GetRenderMaterialsCount2`), decals, lights, custom properties
(the document's and each configuration's), explode steps (assemblies), and simulation studies
when SolidWorks Simulation is loaded (else "simulation studies, if any", which alone never
asks).

**Before the save, never silently** (research 3.4 step 4). The first time a vault document
with drops is checked (and whenever the list changes), a `solidWorks` notice and a
`SaveDownPrompt` say:

> **When you save Bracket.SLDASM, Armory saves it in SolidWorks 2025.** Your team uses 2025,
> and 2025 can't keep: 3 appearances (colors), 2 explode steps. Part numbers and descriptions
> are kept by Armory.
> [Keep this file on this computer only]

No answer, or "Save in 2025" on the notification, is the team's rule: it saves in 2025.
"Keep this file on this computer only" turns the option off for it:

> **Bracket.SLDASM is saved on this computer only.** You chose to keep it here. Until it is
> saved in SolidWorks 2025, nobody else gets these changes. [Save it in 2025 now]

**Blocked documents** (research 3.4 step 3), in SolidWorks' own words:

> **Plate.SLDPRT is saved on this computer only.** It uses Hole Wizard instances on sketch
> geometry, which SolidWorks 2025 doesn't have. Change it to something 2025 has (SolidWorks
> suggests: clear "Create instances on sketch geometry"), then save again. Until then nobody
> else gets these changes.

**The save.** `FileSaveNotify` (memory only) sends `LinkSaving`; the engine holds that path
(no capture, no upload decision) until `LinkSaved`, `LinkSaveCanceled` or two minutes. After
`FileSavePostNotify` the link stamps the bytes (`ReleaseStamp`): their SHA-256, read twice
around SolidWorks' own `VersionHistory(path)` (no stamp when the file changed meanwhile), the
year of the history's last entry (`VersionHistory.LastYear`) when it is the year the save
meant (`SavedReleaseRule.StampYear`), the writer's revision and year, and the Save to Version
target. The engine keeps the stamp by hash and uploads the bytes as usual when the stamp (and
the reader) say 2025. Confirmation, in the activity log: "Saved Bracket.SLDASM in SolidWorks
2025. Not kept: 3 appearances (colors), 2 explode steps." (no "sent it to the team": in v2 the
team gets it at check in).

**A save that couldn't be written in 2025 is never uploaded as 2026.** A stamp whose save had
the option on but whose year SolidWorks didn't confirm (`SavedReleaseRule.UnverifiedSaveDown`)
makes the gate Enforce for those bytes even in a Warn project, so they stay a private draft,
never "release not checked"; the words are "SolidWorks on this computer couldn't save
Plate.SLDPRT in 2025, so it stays on this computer only. Nobody else gets these changes yet.
Ask a CAD lead or a mentor what to do." A save down that still wrote 2026 also switches this
SolidWorks to `NotLicensed` (B4) and puts the student's setting back.

**Canceled** (`FileSavePostCancelNotify`, for example SolidWorks' own Previous Release Check
stopped it): the document counts as blocked until a check says otherwise, and:

> **SolidWorks couldn't save Plate.SLDPRT in 2025, so it isn't saved yet.** Click Save again to
> keep it on this computer (only you will have it), then fix what SolidWorks listed.

The work is still in SolidWorks' memory and auto-recover.

**"Save it in 2025 now"** (research 3.4 step 8, and B5): the open, checked-out document's own
pinned year is set, then `SetSaveFlag` and `Save3(Silent)`, then the active document's setting
again; the save is stamped as any other.

**When saving down is not possible** (research 3.5; `SaveDownReason`, shown in Settings and in
the answers):

| Case | Words |
|---|---|
| 2026 before SP3, or the preference numbers unknown | "Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save team files in 2025. Until then, files you save stay on this computer only." |
| The option didn't take (B4) | "SolidWorks on this computer couldn't save files in 2025, so files you save stay on this computer only. Nobody else gets these changes yet. Ask a CAD lead or a mentor what to do." |
| More than two releases apart | "SolidWorks 2028 can't save files as 2025. Files you save stay on this computer only. A mentor can raise Robot 2027's SolidWorks year once everyone can use the new one." |
| Pin equals the running year | nothing; the student's own setting stays |

## 4. What a save down drops, and references (B3)

Blocked: features the target release doesn't have ("Incompatible Items"). Dropped without
blocking ("Other Items"): annotations; appearances, decals, scenes and lights; custom
properties; add-in data; explode steps ([sw-prc], VERIFIED); simulation studies ([jav-inc],
SOURCED). The help's wording reads as "only the ones 2025 can't hold", Javelin reads it as
"custom properties are dropped": the inventory counts them all (so the words may overstate),
and lab L3 records what really survives. Armory keeps part numbers and descriptions in its own
database, so nothing of Armory's is lost.

References: SolidWorks finds a referenced file by an open document of the same name first, then
its search folders ([sw-search]); the path and the internal ID of a reference are saved
([sw-extref]). Save to Version is a normal Save of the same document to its own path, so every
parent's stored name and path stay right (VERIFIED). That the internal ID and mate faces
survive a 2025 rebuild of a 2026-written tree is REASONED and measured by lab L3 to L5.

## 5. Licensing (B4): unknown, fail safe

"SOLIDWORKS users must have an active subscription license to access this functionality"
([sw-prev], VERIFIED). The FRC sponsorship gives each student a Student Edition license
([sw-sponsor]) with no subscription terms ([sw-frc]); no source says whether that counts. The
IDEA PCs' 2025 never saves down. So the link proves it on each computer: the option must read
back at attach, and the first save down's history must say 2025; otherwise `NotLicensed`, no
save down, and the words above. Lab L2 answers it on a sponsorship computer.

## 6. Files already saved in a newer year (B5)

- On a computer with the pinned year (2025): `IModelDocExtension.IsFutureVersion()` at open
  ([sw-future]) for a file the reader couldn't place gives a `solidWorks` notice: "Plate.SLDPRT
  was saved in a newer SolidWorks than 2025. You can open parts and assemblies to look, but you
  can't change them here, and drawings won't open. Someone with a newer SolidWorks can fix it:
  check it out, open it, click Save, and check it in."
- On a 2026 computer that can save down, a checked-out open document whose bytes are 2026:
  "Plate.SLDPRT was saved in SolidWorks 2026. Robot 2027 uses SolidWorks 2025. Armory can save
  it in 2025 for you now." with [Save it in 2025 now].
- The stamp the link makes at open (`LinkStamped`) gives the engine SolidWorks' own year for
  those bytes, beside the file reader (`docs/core/solidworks-version-gate.md`).

## 7. Check out and reopen (C5, the link's half)

The link reports which documents the student opened (`topLevel`: the active document at
`FileOpenPostNotify`, or one that became active; never a reference an assembly or a drawing
loaded). The engine asks about those only, once per document per SolidWorks session, grouped
within 1.5 seconds (`docs/agent/ENGINE.md`, "SolidWorks opened a file"). Nothing is ever
checked out because it was opened.

After "Check out and reopen" the engine calls `MakeWritableAsync` for each checked-out file
SolidWorks has open, with a 15 second budget. The link carries out `Armory.Core.WritablePlan`
one step at a time: `SetReadOnlyState(false)` first (in place, unsaved changes kept,
[sw-srs]); if that didn't take and the student changed the document (a `ModifyNotify` since it
was opened or saved; unknown counts as changed, because `GetSaveFlag` is true for every 2025
file opened in 2026), stop and say how to save; an unchanged part or assembly
`ReloadOrReplace(false, null, false)` ([sw-reload]); an unchanged drawing
`CloseAndReopen(doc, MatchSheet)` ([sw-cnr]). Never `CloseDoc` (it closes a changed document
without saving, [sw-closedoc]), never a "discard changes" flag; `WritablePlanTests` prove no
state leads to one. Lab N3 to N6 measure each step on real files.

## 8. Settings

`AgentView.solidWorks` (`docs/agent/BRIDGE.md`): "SolidWorks isn't running.", "Linked to
SolidWorks 2026 SP4.1. It saves team files in 2025.", "Linked to SolidWorks 2026 SP2. It can't
save team files in 2025." with the reason, or "SolidWorks was started as administrator, so
Armory can't link to it." with "Close SolidWorks and start it normally, not as administrator."
Null on a computer with no link (the page shows nothing).

## 9. The Document Manager key: not needed

Armory's own reader places every file (`docs/core/solidworks-version-gate.md`), so the
SolidWorks Document Manager API key is needed for nothing. If Mr. Pina still wants a vendor
cross-check on his own computer (research 1.6):

1. Open https://www.solidworks.com/support/subscription/key-request/ and sign in with his own
   SOLIDWORKS ID, the account where the school's serial number is registered (an active
   subscription and serial are required, [goe-dm]; whether an education or sponsorship serial
   qualifies is unknown until he tries).
2. Pick the school and his role if asked; click **New Key Request Form**; tick **Basic** only;
   accept and submit. The key arrives by email within a few days.
3. Never put the key in the repo, the installer, the server, logs or a student's computer: the
   license says "Do not share this license key with anyone outside your company or distribute
   it with any software that you ship" ([dm-start]). A key must be renewed for each major
   release and can't open newer files. Armory has no field for it in 0.3.3.

## 10. The in-process fallback (written down, not built)

Only if lab P1 shows events don't work out of process on one of our licenses, or a later
version needs a SolidWorks task pane: a .NET Framework 4.8 class library
(`src/Armory.SolidWorks.Addin`, `net48`, x64, `Microsoft.NETFramework.ReferenceAssemblies`
1.0.3; MEASURED to build on Linux) with its own `[ComImport] ISwAddin`
(`DA306A0D-EAC5-4406-8610-B1DA805D9270`, `ConnectToSW`, `DisconnectFromSW`), no NuGet
dependencies, registered per user like `regasm /codebase` but under
`HKCU\Software\Classes\CLSID\{clsid}` (`InprocServer32` = `mscoree.dll`, `ThreadingModel`
Both, `Class`, `Assembly`, `RuntimeVersion` v4.0.30319, `CodeBase`), plus
`HKCU\Software\SolidWorks\AddInsStartup\{clsid}` = 1, in a versioned folder
`%LOCALAPPDATA%\Programs\IDEA Armory SolidWorks\<version>\` (a loaded DLL can't be replaced or
renamed). It loads only where the optional one-time administrator step wrote
`HKLM\SOFTWARE\SolidWorks\AddIns\{clsid}` (Default 0, `Title` "IDEA Armory"), or, without it,
if lab P2 shows `ISldWorks.LoadAddIn` accepts an unlisted per-user add-in. It would talk to
Armory over a per-user pipe (`\\.\pipe\IDEA-Armory-SolidWorks-<user SID>`, `CurrentUserOnly`,
one JSON object per line, the same records as above). Uninstall deletes the keys first, then
the folders (a locked one through `RunOnce` at next sign-in).

## 11. Tests

- `Armory.Core.Tests`: `RevisionNumberTests`, `SaveDownPlanTests` (`Plan`, `Support`,
  `Choose`), `SavedReleaseRuleTests` (`StampYear`, `UnverifiedSaveDown`,
  `VersionHistory.LastYear`), `OpenAskTests`, `WritablePlanTests`.
- `Armory.SolidWorks.Tests` (Linux and Windows): `SwConstantsTests` (the one table),
  `LinkPolicyTests` (vault documents, the student's own opens, save modes, the message filter's
  retries, the ROT name), `SaveDownManagerTests` (the option for every case against a fake
  session, the student's setting back, stamps, a save down that wrote 2026, a canceled save),
  `SaveToVersionEnumsTests` (metadata of stand-in assemblies compiled in the test, the
  settings file).
- `FakeSolidWorksTests` (`[WindowsFact]`, skipped elsewhere): `tests/Armory.FakeSolidWorks`, a
  real out-of-process COM server written by hand (`ComWrappers` vtables: `IDispatch` by name,
  `IConnectionPointContainer` firing the real event numbers with real VARIANTs, its own message
  filter, the Running Object Table, `IExternalConnection` to count other processes'
  references), driven over stdin: attach only after start-up, every event, a busy SolidWorks
  retried, Armory quitting with every reference let go, a killed SolidWorks, catch-up, two
  SolidWorks, make writable, and Save to Version following the active document. Under Wine 9.0
  (this Linux host, 2026-10-09) 7 of the 8 pass; the busy test can't, because Wine does not
  implement `SERVERCALL_RETRYLATER` ("retry call later not implemented"); it runs on Windows CI.
- `Armory.EndToEnd.Tests.SolidWorksLinkTests`: the engine with an in-memory link (C5 prompts,
  check out and reopen, saving holds, stamps, blocked and kept documents in both gate modes, an
  unverified save down never uploaded, the drops question, the Settings line).

What none of this proves (lab checklist): real SolidWorks events out of process (P1), the two
preference numbers (L1), licensing (L2), what survives a save down (L3 to L5), the option's
timing (L6), real `CheckVersionCompatibility` items (L7, L8), and the make-writable steps on
real files (N3 to N6).

## Sources

- [sw-swaddin] SOLIDWORKS API Help 2025, Using SwAddin to Create a SOLIDWORKS Add-In, https://help.solidworks.com/2025/english/api/sldworksapiprogguide/OVERVIEW/Using_SwAddin_to_Create_a_SOLIDWORKS_Addin.htm
- [sw-icons] SOLIDWORKS API Help 2026, Add-in Icons, https://help.solidworks.com/2026/English/api/sldworksapiprogguide/OVERVIEW/Add-in_Icons.htm
- [sw-startup] ISldWorks::StartupProcessCompleted, https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~StartupProcessCompleted.html
- [codestack-manual] CodeStack, Installing SOLIDWORKS add-in by manual registration, https://www.codestack.net/solidworks-api/deployment/manual/
- [codestack-connect] CodeStack, Create VB.NET stand-alone application, https://www.codestack.net/solidworks-api/getting-started/stand-alone/connect-vbnet/
- [xcad-reg] xCAD RegistrationHelper.cs, https://github.com/xarial/xcad/blob/master/src/SolidWorks/Utils/RegistrationHelper.cs
- [javelin] Javelin, Why you need local administrative rights to install SolidWorks, https://www.javelin-tech.com/blog/2014/02/need-local-administrative-rights-install-solidworks/
- [3ds-elev] SOLIDWORKS forum, C# Stand Alone: Get active SW App, https://3dswym.3dexperience.3ds.com/question/solidworks-user-forum/c-stand-alone-get-active-sw-app_VUsHNDgDQ5iuNf3pOi9yaw
- [ms-merged] Microsoft, Merged View of HKEY_CLASSES_ROOT, https://learn.microsoft.com/en-us/windows/win32/sysinfo/merged-view-of-hkey-classes-root
- [ms-uac] Microsoft, UAC: COM Per-User Configuration, https://learn.microsoft.com/en-us/previous-versions/bb756926(v=msdn.10)
- [ms-filter] Microsoft, IOleMessageFilter ("Application is Busy" and "Call was Rejected By Callee"), https://learn.microsoft.com/en-us/previous-versions/ms228772(v=vs.140)
- [inno-files] Inno Setup Help, [Files] section flags, https://jrsoftware.org/ishelp/topic_filessection.htm
- [sw-prev] SOLIDWORKS 2026, Saving documents as previous versions, https://help.solidworks.com/2026/English/SolidWorks/sldworks/c_Save_SW_Docs_Previous_Versions.htm
- [sw-wn] What's New 2026, Saving to previous versions, https://help.solidworks.com/2026/english/WhatsNew/c_wn2026_fundamentals_saving_previous_versions.htm
- [sw-sapv] IAdvancedSaveAsOptions::SaveAsPreviousVersion, https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IAdvancedSaveAsOptions~SaveAsPreviousVersion.html
- [sw-so] System options, Backup/Recover (API), https://help.solidworks.com/2026/english/api/swconst/SO_BackupRecover.htm
- [sw-stv] swSaveToVersion_e, https://help.solidworks.com/2026/english/api/swconst/SolidWorks.Interop.swconst~SolidWorks.Interop.swconst.swSaveToVersion_e.html
- [sw-cvc] IModelDocExtension::CheckVersionCompatibility, https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDocExtension~CheckVersionCompatibility.html
- [sw-prc] Previous Release Check dialog box, https://help.solidworks.com/2026/English/SolidWorks/sldworks/r_previous_release_check_dialog_box.htm
- [jav-inc] Javelin 2024, Save as previous versions: incompatible items errors, https://www.javelin-tech.com/blog/2024/11/solidworks-save-as-previous-versions-incompatible-items-errors/
- [sw-search] Search Routine for Referenced Documents, https://help.solidworks.com/2025/English/SolidWorks/Sldworks/c_Search_Routine_for_Referenced_Documents.htm
- [sw-extref] File Management with External References, https://help.solidworks.com/2025/English/SolidWorks/sldworks/r_File_Management_with_External_References.htm
- [sw-sponsor] SOLIDWORKS Student Sponsorship datasheet, https://files.solidworks.com/pdf/EDU_Student_Sponsorship_Datasheet_ENG.pdf
- [sw-frc] SOLIDWORKS supports FRC teams for REBUILT, https://blogs.solidworks.com/products/solidworks/solidworks-supports-first-robotics-competition-frc-student-teams-for-rebuilt
- [sw-future] IModelDocExtension::IsFutureVersion, https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDocExtension~IsFutureVersion.html
- [sw-srs] IModelDoc2::SetReadOnlyState, https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDoc2~SetReadOnlyState.html
- [sw-reload] IModelDoc2::ReloadOrReplace, https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDoc2~ReloadOrReplace.html
- [sw-cnr] ISldWorks::CloseAndReopen, https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~CloseAndReopen.html
- [sw-closedoc] ISldWorks::CloseDoc, https://help.solidworks.com/2025/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~CloseDoc.html
- [dm-start] Document Manager API, Getting Started, https://help.solidworks.com/2026/english/api/swdocmgrapi/GettingStarted-swdocmgrapi.html
- [goe-dm] GoEngineer, Request a SOLIDWORKS Document Manager API key, https://www.goengineer.com/blog/request-solidworks-document-manager-api-key
