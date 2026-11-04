# A SolidWorks save blocked by Armory's read-only rule

This change is not a verified reproduction of the November 3, 2026 lab incident.
The reported dialogs, crash and AutoRecover recovery are the student's report, not
observations made by these tests. Real SolidWorks validation remains required.

## Found in main

Checked against `c78bb4f`, including merged PRs 14 (tutorial), 15 (installs) and 16
(previews). `ReadOnlyPolicy` and `CheckoutRules.IsReadOnlyOnDisk` keep a managed file
read-only unless this device has its checkout. `SealHeld` also protects work left
for another student on the shared PC. That protection is not changed here.

`LocalChangeDetector` watches successful file/directory/attribute changes.
`LockMarkers` and the existing SolidWorks notes identify `~$` as a document-open
marker. Its creation is not evidence of saving. There is no measured temporary-save
filename pattern in this tree, including for `.swp`.
A failed target write does not provide a reliable `FileSystemWatcher` event.
No temporary-file heuristic is added.

The existing out-of-process SolidWorks link subscribes to `FileSaveNotify` and
`FileSaveAsNotify2` and emits `LinkSaving` with the target path. The existing fake
SolidWorks child fires that event before appending bytes to its target. Its event
interfaces are tested against the repository's recorded type information. That
establishes the adapter contract, not that every real read-only failure emits this
event. In particular, SolidWorks may refuse a read-only component before this
notification; this feature cannot detect that case without another measured signal.

## Built

`SaveBlockedDetector.cs` consumes explicit save attempts, resolves the protection
from known ownership, held work and project membership, and puts one card per file
at the front of Home's things to look at. Assembly components are included even
when they were not opened as top-level documents. It also explains a checkout on
another computer and a project that cannot be edited, rather than mislabeling them.
Unknown ownership, untracked files, writable files and paths outside Armory do not
raise save cards. Before reporting an attempt, the Windows adapter verifies the
current file id still matches Armory's existing read-only intent and the bit is set,
using the same attributes-only handle for both facts. An immutable snapshot of
existing intents keeps this metadata read out of the whole-vault scan gate. It does
not apply any intent.

The first event publishes the card using the last complete view, including while a
server read is stalled. It does not wait for a disk scan, hint poll, or sync pass.
Events less than three seconds apart for the same file form a sliding burst. There
is one stable card key per burst and one Windows notification tag per file. A new
burst replaces the prior card/notification. Memory is capped at 200 files.
The notification planner deduplicates by the detector's burst key alone. Changes
to a standing card's reason or checkout availability refresh Home without another
notification. A notification click resolves that key against the current action.
Notifications appear even if Home is visible; the generic open-file question is
suppressed for the affected file. Closing or successfully saving removes the card;
checkout removes it when protection clears. Signing out or switching students
invalidates its in-memory key.

The only primary action is Check out when the known state permits it online, or
Save a copy in your folder. Both Home and the notification send the same random
card key, not a UI-provided path. The click rechecks ownership. Checkout uses the
existing explicit checkout path, including making the open document writable
without discarding edits. Detection itself never checks out, changes attributes,
copies, moves, opens for writing, or saves a document.

There is no authoritative mapping from school sign-ins to class folders in the
current API. Save a copy therefore makes/opens a separate destination under
`Documents\Armory copies\Student-<32 hex characters derived from the school sign-in>`.
It refuses existing link/reparse-point ancestors and never reads or copies the
source CAD file. SolidWorks performs Save As; the card says so. The copy remains
local until the student adds it to a project. This is an explicit fallback, not an
inferred class folder and not a private Windows security boundary on a shared
Windows account.

## Automated evidence

`SaveBlockedDetectorTests` covers each reason, checkout/copy choices, per-file
sliding bursts, case-insensitive paths, changed ownership, sign-in boundaries,
unknown/unmanaged paths, and bounded memory. `SaveBlockedTests` exercises the real
engine, a stalled server read, Home model and notification planner without any
network writes or captured bytes. `SaveBlockedCheckoutTests` uses a throwaway
PostgreSQL server to verify no checkout before a click, the existing checkout action
after a click, and someone else acquiring the checkout before that click.

`Blocked save Windows evidence` runs the real Windows disk adapter, real read-only
attribute and an out-of-process COM fixture. It attempts an actual target write,
checks Windows refuses it, checks the Home reason and notification plan within two
seconds, and checks original bytes and read-only protection remain unchanged for
each of the three requested reasons. It also compares watcher hints before/after
the refused write. The test is skipped on Linux. This is a controlled API/write
fixture, not proof of real SolidWorks' save algorithm or native toast delivery.

### Release-test flake investigation, November 3, 2026 (America/Los_Angeles)

The v0.5.5 [release-event run](https://github.com/pina-hash/idea-armory/actions/runs/41770895570/job/126383427191)
failed at `SaveBlockedTests.cs:205` on `da982f1`: `Assert.Equal(hints,
disk.HintCount)` expected 1, actual 2, after the free-file case had passed. This
counter measures filesystem notifications, not Home cards or Windows notifications.
The [push run of the same commit](https://github.com/pina-hash/idea-armory/actions/runs/41770895265)
and [PR 17 evidence run](https://github.com/pina-hash/idea-armory/actions/runs/41762529903)
passed. The old test changed the target's read-only attribute with the watcher
running and assumed a 250 ms sleep drained its asynchronous hints. It then compared
the counter after another sleep. Neither sleep establishes a watcher barrier.
The log does not record the delayed hint's path or operation, so it cannot identify
that individual hint or establish duplicate COM events, cards or notifications.

The fixture now persists protection before the Windows adapter and its watcher
exist. It waits for the attached view, completed COM open event, each refused-write
response, and the engine's record barrier. It retains strict equality for the hint
counter and additionally counts every notification plan and COM save event. No
sleep determines burst membership: an injected monotonic clock tests 3 seconds
minus one tick, exactly 3 seconds and 3 seconds plus one tick.

Separately, a regression test reproduced a product defect: `SaveBlockedAsks` included
detail/action text in notification identity. A refresh within one unchanged burst
could notify twice. Notification identity now uses only the burst key. The detector
already retains one card/key per file during its sliding burst; its timing rule and
the host's current-action recheck remain unchanged.

Three portable engine stress cases each run 200 bursts with duplicate deliveries,
two link process ids and sliding retries (800 records, 200 notification plans per
case). The Windows stress case runs 200 bursts with two actual refused COM writes
per burst and asserts one standing card, exactly one notification per burst, one
stable notification tag, unchanged original bytes/protection, and no extra watcher
hints. `Blocked save Windows evidence` runs all affected Agent, detector and
protection tests 20 times: four independent Windows jobs with five repetitions
each, retaining a separate TRX for every suite/repetition. The PR records completed
run counts and measured latency ranges, rather than treating this configuration as
evidence that the runs passed.

`tools/agent-ui/check-ui.mjs` checks the three reasons in both themes and at
1280x800, 420x720 and 320x480, including Tab/Enter, focus, one primary action, layout,
contrast and bridge fields. Screenshots go to `evidence/save-blocked` in the normal
`ui-evidence` Actions artifact. The checked-in [IDEA](screens/save-blocked/idea.png)
and [Space White](screens/save-blocked/space-white.png) card crops are generated
demo views, not lab screenshots. The PR records dated results and measured timings.

## Required lab-PC check with real SolidWorks

1. Use a disposable project and synthetic/scratch CAD files. Record Windows,
   Armory and SolidWorks versions, PC name, student sign-in, project membership,
   file checkout and read-only state. Keep production/student originals untouched.
2. Capture the real failing save using Process Monitor filtered to `SLDWORKS.exe`
   and the scratch project. Record operation, path, result and relative timing,
   including any temporary-file activity and ACCESS DENIED on the target. Do not
   assume `~$` or `.swp` means a save. Collect Armory's existing link logs alongside
   the trace and establish whether `FileSaveNotify` / `LinkSaving` occurs for that
   exact failed attempt, before claiming the signal is validated.
3. Try Ctrl+S, Save All and saving a new assembly referencing read-only components:
   (a) an available file not checked out, (b) work held for another student on that
   PC and a file checked out by another student, (c) a known project the signed-in
   student is not on. Also try Save As over an existing protected file. If the
   application refuses any case before the existing save event, record it as a
   detection gap; do not ship a guessed temp-name detector.
4. Time from the real save attempt to both the Home card and Windows notification.
   Require at most two seconds. Verify filename, exact reason and one primary
   action. Repeat rapidly: one explanation per file per burst. Wait at least three
   seconds and try again: a fresh explanation replaces the old one. Opening a file,
   creating an open marker or saving a writable/unmanaged file must not trigger it.
5. Check out only by clicking, save again in SolidWorks, and verify the open
   document keeps unsaved edits. Have a second student acquire the checkout just
   before the click and verify Armory refuses checkout and offers a copy instead.
   Do not dismiss or close the student's unsaved assembly as part of the test.
6. For copy cases, click the primary action and verify the folder belongs to the
   current school sign-in. Use SolidWorks Save As there, including the assembly's
   reference/copy options as appropriate. Verify the original parts' bytes,
   attributes, ownership and locations have not changed. Switch students with a
   notification outstanding; its old button must not act for the next student.
7. Try Home visible/hidden, offline and during a slow server read, both themes,
   keyboard only, Windows scaling and a screen reader. Check Windows notification
   settings and Focus Assist separately: the app cannot force Windows to display
   a disabled/suppressed toast. Try two SolidWorks processes and components not
   opened in their own window. Record every uncovered case.

No installer, updater, retire, preview, tutorial, version or release change is
included. The documented concurrent-writer limitation in safe replacement remains.
