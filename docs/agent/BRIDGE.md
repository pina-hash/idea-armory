# Agent window bridge (v2)

The Armory Agent window is WebView2 hosting a local page shipped with the app
(`src/Armory.Agent/wwwroot/`). It loads nothing from the network: no CDN, no web
fonts, no remote images. The page talks to the engine only through this bridge.

Transport: `window.chrome.webview.postMessage(object)` from the page, and
`CoreWebView2.PostWebMessageAsJson(json)` from the host. Every message is one JSON
object with a `type` field. Names are camelCase; data objects use `kind` and
`direction`, never a `type` field. The C# records live in
`src/Armory.Agent.Engine/View/AgentView.cs`; `wwwroot/bridge.js` mirrors them in JSDoc.
`AgentViewContractTests` keeps the two lists of message types (`BridgeMessages.HostToPage`
and `PageToHost` in C#, `HOST_TO_PAGE` and `PAGE_TO_HOST` in bridge.js) equal, and
requires every type the page sends or reads to be on them; it also holds every C#
message and view record to the fields bridge.js documents (the engine builds the v2
`AgentView`, so every record matches, field for field), and `Bridge.cs` to one case per
page-to-host type. `tools/agent-ui/check-ui.mjs` holds every demo view and file detail to
the same JSDoc typedefs, so the demo the screens are drawn from can't invent a field the
host never sends, and the page reads only those fields.

Dropped files are the one exception to plain JSON: the page sends `dropFiles` with
`window.chrome.webview.postMessageWithAdditionalObjects(message, files)`, where `files`
is the drop's `FileList`, and the host reads `CoreWebView2File.Path` from each of the
event's `AdditionalObjects`.

When the page runs outside WebView2 (a plain browser, for screenshots), `bridge.js`
uses a demo transport that answers from `wwwroot/demo/states.js`. See "The demo" below.

## Screens

- **Connect** (first run): one key, Connect this computer; then the browser sign-in
  and its status plate; or, when the Armory folder belongs to another account, a
  one-click folder of the student's own.
- **Home**: on the left the status display (its line is the activity line while files
  move) with Pause or Resume under it, and This computer (how many team files are up
  to date here, who is signed in, Sign out). On the right, in one scrolling column: the
  selection bar (while files are selected), the quiet check-out question (`prompt`),
  Right now (`activity`), the notices (one card per kind), My files (the files this
  computer has checked out, in every project, an archived one too), and Team files:
  project tabs, then the open project's card with where you are (Project › Folder ›
  Subfolder), the folder's keys (New folder, Add files, Rename folder, Delete folder,
  Check out all, Check in all, and for a mentor or CAD lead Force check in all), and its
  folder rows and file rows. A file row's state key is Check out, Check in, or for a
  mentor or CAD lead on a file someone else has, Force check in. Files dragged from
  File Explorer drop into the open folder. How many files wait to upload is said once,
  by Right now (`activity.waiting`), never as a tag on each row.
- **File detail**: the file's display (its state and who has it), Open as the primary
  key, then Check out, Check out and open, Check in, Undo check out or Force check in as its
  state allows, Show in folder as a quiet link, Checked out (the person and computer,
  or "Available. Check it out to make changes."), and the history. Left for the page (0.3.3,
  the host's half is in): a history entry of kind `keptCopy` gets Put back on this computer
  (`putBackKeptCopy`; the host refuses another person's copy in one sentence, so the key may
  show on every kept copy until the view says which are yours), and the status
  `checkingInWhenClosed` gets its chip ("Checks in when closed", tone look) in the page's
  status words; until then such a row shows no chip (`MyFileView.note` carries the words).
- **Settings** is a sheet over Home with exactly the folder (and Change), Start Armory
  when I sign in, and the theme.
- **The small dialog** (`<dialog id="ask">`) asks New folder, Rename folder, Delete
  folder, Check out all (how many files, in that folder and its folders, and that
  nobody else can save them until they are checked in; it starts on Cancel), Rename
  file (a notice's file that shares its name with another file in the project) and
  Force check in (one file, the picked files, or all of a folder's: who has them, and that
  anything they hadn't checked in is kept as their own copy). It is filled once when it opens and never redrawn by a host message, so
  typed words stay.

Every file row shows who has it checked out, always: "Checked out by you" or "Checked
out by Maria Lopez on LAB-PC-07" as a tag with the person's initials (green when it is
checked out here, amber for someone else or my other computer), or "Available" as plain
text; when the line is short of room the computer's name gives way first, and the whole
label is the tag's tooltip. Every file row has a select key (a 44px `role="checkbox"`
that always draws its box), Open, and one state key before it: Check out when nobody
has it, Check in when it is checked out here, nothing when someone else has it (the row
says who). A file that isn't in Armory yet shows "Not in Armory" (`notInArmory`) or
"New, not uploaded yet" (`waiting`) instead of a check out and has no select or state
key (nothing can be checked out or in until it is added). Long lists (a folder, My
files, a notice's files) draw only the rows near the view at one fixed row height, so a
folder of 5,000 files keeps well under 150 rows in the page.

The page shows the engine's sentences as given (`SyncView.line`, `ActivityView.line`,
`DirectionView.line`, a notice's `title` and `detail`, an action's `message`) and never
picks them apart. A folder in an engine sentence is written with " › " (U+203A), as the
page's own crumbs are: "Added 4,987 of 5,000 files to Robot 2027 › CopyDesignTemp".

## Host to page

| `type` | fields | when |
|---|---|---|
| `view` | `view: AgentView` | on `ready` and whenever anything changes (at most every 500 ms during a pass); the page redraws from it |
| `fileDetail` | `detail: FileDetailView` | the answer to `openFile` |
| `activity` | `activity: ActivityView` | while files move, at most 4 a second; the page patches only Right now and the status line, so focus, scroll and typing never move |
| `actionResult` | `requestId`, `ok`, `message` | once for each action (see Page to host); the page shows `message` in a quiet line at the window's foot, never an alert and never a focus change |

```
AgentView {
  connection: "signedOut" | "connecting" | "signedIn" | "vaultOwnedByOther"
  connect: ConnectView
  account: AccountView | null
  sync: SyncView
  activity: ActivityView
  vaultRoot: string                 // e.g. C:\IDEA\Armory
  notices: NoticeGroupView[]        // at most one per kind
  prompt: PromptView | null
  myFiles: MyFileView[]             // the files this computer has checked out, every project (archived too)
  projects: ProjectView[]
  settings: SettingsView
  effectiveTheme: "idea" | "spaceWhite"
}
ConnectView { phase: "idle" | "waitingForBrowser" | "finishing" | "failed", message: string | null }
AccountView { email: string, deviceName: string }
SyncView {
  state: "synced" | "syncing" | "offline" | "paused" | "attention",
  line: string,                     // e.g. "Everything is saved to Armory."
  detail: string | null,            // e.g. "Last checked 2 minutes ago."
  pendingCount: number              // files waiting; the page says so when detail is null
}

ActivityView {
  line: string | null,              // e.g. "Downloading 412 of 1,280 files, 2.1 GB left, about 3 min"
  upload: DirectionView | null,
  download: DirectionView | null,
  move: DirectionView | null,
  waiting: WaitingView | null,
  active: ActiveTransferView[],     // at most 8, each drawn with its own progress track
  log: ActivityLineView[]           // 0.3.2: what Armory did in the last 3 minutes, oldest first, at most 40
}
ActivityLineView {
  at: string,                       // ISO-8601 UTC
  line: string                      // e.g. "Downloaded Plate.SLDPRT (612 KB)", "Checked out 500 of 1,400 files"
}
DirectionView {
  filesDone: number, filesTotal: number, bytesDone: number, bytesTotal: number,
  bytesPerSecond: number,
  secondsLeft: number | null,       // null until 3 seconds and 2 files have gone by
  line: string                      // e.g. "Uploading 3 of 9 files, 48 MB left, about 20 sec", "Moving 120 files to Gearbox"
}
WaitingView { count: number, line: string }
  // "3 files are waiting to upload. They upload when this computer is back online."
  // "2 checked-out files have changes. Check them in to share them."
ActiveTransferView { path: string, name: string, direction: "upload" | "download" | "move", bytesDone: number, bytesTotal: number }

NoticeGroupView {
  key: string,                      // dismissNotice sends it back
  kind: "import" | "nameShared" | "newerWaiting" | "keptCopy" | "takenBack" | "folderPutBack"
      | "projectPutBack" | "projectRenaming" | "projectDeleted" | "cantSend" | "cantRead" | "checkInPartial"
      | "newerRelease",
  tone: "info" | "look" | "bad",    // the page shows info green, look amber, bad red
  title: string,                    // e.g. "14 files share a name with other files in this project"
  detail: string,
  count: number,                    // the total; items holds at most 200
  action: NoticeActionView | null,  // the card's one key
  items: NoticeItemView[]           // the card's list, opened by its own key
}
NoticeActionView { label: string, command: string, paths: string[] }
  // command is a page-to-host type the page sends for it:
  //   checkOut {paths, open: false}, checkIn {paths}, undoCheckOut {paths},
  //   launchFile {path: paths[0]}, showInFolder {path: paths[0]}, dismissNotice {key},
  //   openFile (the first item's fileId)
  // or "expand", which the page handles itself: it opens and closes the card's list.
NoticeItemView { fileId: string | null, path: string, name: string, detail: string | null }

PromptView {                        // SolidWorks opened a file this computer hasn't checked out
  key: string,                      // one per open: "prompt:<path>:<when SolidWorks opened it, ISO-8601>"; dismissNotice {key} hides this one only
  fileId: string | null, path: string, name: string,
  checkout: CheckoutView,
  canCheckOut: boolean              // false when someone else has it: the card says who
}
CheckoutView {
  state: "available" | "mine" | "other" | "myOtherComputer",
  label: string,                    // "Checked out by you", "Checked out by Maria Lopez on LAB-PC-07", "Available"
  name: string | null, email: string | null, device: string | null,
  since: string | null              // ISO-8601
}

MyFileView { fileId: string | null, path: string, name: string, project: string, status: FileStatus, note: string | null, checkout: CheckoutView }
  // note: what is under way for it, or null: "Checking in.", "Undoing the check out.", "Checks in as
  // soon as you close it in SolidWorks." (0.3.3, with status checkingInWhenClosed), "Checks in as soon
  // as Armory can read it. Close any program that might be using it.", "You added it while it was
  // open. It is checked in by itself when you close it.", or the offline words.
ProjectView {
  id: string, name: string,
  archived: boolean,                // shown as "Archived. It no longer updates." with no keys
  role: string,                     // student, cad_lead, mentor, instructor
  canTakeBack: boolean,             // the server's can_take_back (v0.3: mentor, CAD lead or site admin); a role check before 0233
  folders: FolderView[],            // flat: every folder once, empty ones too; "" is the project's top
  pinnedRelease: number,            // 0.3.3: the SolidWorks year the project uses (2025)
  newerThanPinCount: number         // 0.3.3: its files whose row has newerThanPin (the newerRelease notice lists them)
}
FolderView { path: string, name: string, fileCount: number, files: FileRowView[] }
  // path is in the project ("Drivetrain/Gearbox"); fileCount is the files directly in it.
  // A file's own path is vault-relative ("Robot 2027/Drivetrain/Gearbox/Shaft.SLDPRT").
FileRowView {
  fileId: string | null,            // null for a file in the folder that isn't in Armory
  name: string, path: string,
  status: FileStatus,
  checkout: CheckoutView,
  changed: boolean,                 // its bytes here differ from the last check in
  releaseNotChecked: boolean,       // shown only on File detail, as a small tag
  updatedAt: string | null, updatedBy: string | null,
  savedRelease: number | null,      // 0.3.3: the SolidWorks year its version in Armory was saved in, when known:
                                    // the server checked it, or this computer read its identical copy
                                    // (a file uploaded "release not checked"); null for other files and unknown years
  newerThanPin: boolean             // 0.3.3: savedRelease is newer than the project's pinnedRelease
}
FileStatus = "synced" | "changed" | "uploading" | "downloading" | "waiting" | "newerWaiting"
           | "keptCopy" | "notInArmory" | "notOnThisComputer"
           | "checkingInWhenClosed"   // 0.3.3: checked out by you and asked to be checked in, but open in
                                      // SolidWorks (or unreadable) now; checked in as soon as it is closed
SettingsView { vaultRoot: string, startAtSignIn: boolean, theme: "system" | "idea" | "spaceWhite" }

FileDetailView {
  fileId: string, name: string, path: string, project: string, folder: string,
  status: FileStatus, checkout: CheckoutView, releaseNotChecked: boolean,
  canTakeBack: boolean,
  history: HistoryEntryView[],      // newest first
  savedRelease: number | null,      // 0.3.3: as on the file's row
  newerThanPin: boolean
}
HistoryEntryView {
  id: string, kind: "version" | "keptCopy" | "removed",
  author: string, at: string, bytes: number,
  note: string,                     // e.g. "Added to Armory", "Checked in", "Added again, with its history",
                                    // "Saved while checked out", "Kept when the check out was undone",
                                    // "Changed without a check out, kept as Sam Lee's own copy",
                                    // "Kept as Sam Lee's own copy: someone else checked in first"
  releaseNotChecked: boolean, isCurrent: boolean,
  routine: boolean                  // a kept copy that is the ordinary record of work ("Saved while checked out",
                                    // "An earlier save, kept"): the neutral tone, never YOUR COPY; the page reads
                                    // this, never the note
}
```

"SolidWorks year not checked" is never a notice: File detail shows it as a small tag
beside the file's place, and on the history entries it applies to.

A year known to be newer than the project's pin is (0.3.3, B5): the files whose version in
Armory was saved in a newer SolidWorks are one `newerRelease` card, "3 files in Robot 2027
were saved in SolidWorks 2026" ("Plate.SLDPRT in Robot 2027 was saved in SolidWorks 2026"
for one; files of several projects or years are counted together), each item saying "Saved
in SolidWorks 2026. Robot 2027 uses SolidWorks 2025." The card's detail is what this
computer can do, from what the SolidWorks link says runs here (docs/agent/ENGINE.md, "The
SolidWorks year"): on a SolidWorks 2026 computer that saves down, tone `look`, "You can fix
them here: 1. Check one out in Armory. 2. Open it in SolidWorks 2026. 3. Click Save. Armory
saves it as SolidWorks 2025 for you. 4. Check it in."; on a SolidWorks 2025 computer, tone
`info`, "You can open parts and assemblies to look (SolidWorks 2025 SP5 shows them as a
future version), but you can't change them here, and drawings won't open. Someone with
SolidWorks 2026 can fix them: check it out, open it, click Save, and check it in."; with no
link, tone `look`, who can look and who can fix them. The card has "Show them" (`expand`)
for more than one file, no key for one. A page that does not know the kind yet shows it as
any other card. `savedRelease`, `newerThanPin` and `newerThanPinCount` are for a tag on the
row and File detail ("SolidWorks 2026") and a count on the project; the window's page does
not show them yet.

The check-out question (`prompt`): "Check out Plate-Left.SLDPRT to edit it?", "SolidWorks
opened it read-only. Check it out, then close it in SolidWorks and open it again here to
save changes.", with Check out and reopen (`checkOut` with `open: true`; the host checks
it out and opens it again here, or, while SolidWorks still has it open read-only, answers
"Checked out Plate-Left.SLDPRT. Close Plate-Left.SLDPRT in SolidWorks first, then open it
again.") and Not now (`dismissNotice` with the question's `key`: that one question goes,
the page hides it by that key at once, the host moves on to the next file SolidWorks has
open without a check out, and the next open of the file asks again). When someone else
has it, it says who and offers OK (the same `dismissNotice`).

## Page to host

Paths are vault-relative with forward slashes. A path in `checkOut`, `checkIn` or
`undoCheckOut` may be a folder, which means every file under it. `projectId` is a
project's id; `parent` and `folder` are paths in that project ("" for its top).

An **action** carries a `requestId` (bridge.js makes one, `r1`, `r2`, ...) and the host
answers it with exactly one `actionResult` carrying the same id and a plain sentence
("Checked out 12 of 14 files. Maria Lopez has 2 of them checked out.", "Checked in
Plate.SLDPRT.", "Close Plate.SLDPRT in SolidWorks first."). The actions are
`launchFile`, `checkOut`, `checkIn`, `undoCheckOut`, `takeBack`, `takeBackAll`, `createFolder`,
`renameFolder`, `deleteFolder`, `renameFile`, `addFiles`, `dropFiles`, `reportProblem`,
`sendFeedback`, `takeOverFolder` and `putBackKeptCopy`.

The page shows an action is under way from the moment it is sent until its
`actionResult` arrives (v0.2.1): the pressed key gets `aria-busy="true"` and
`aria-disabled="true"`, a small spinner in place of its glyph, and ignores a second
press; the quiet line at the foot says what is under way in plain words ("Checking out
Bracket.SLDPRT...", "Checking in 3 files...", "Opening Bracket.SLDPRT...") with a
spinner (`data-working="true"` on `#result`); and the rows the action touches say so
("Checking out...") in place of who has them. The answer with the same `requestId`
replaces all of it. The spinner holds still under `prefers-reduced-motion`.

| `type` | fields | sent by | effect |
|---|---|---|---|
| `ready` | | the page, once, first | host answers with `view` |
| `connect` | | Connect this computer, Try again, Open the browser again | starts the browser sign-in for this computer |
| `cancelConnect` | | Cancel while waiting | stops waiting for the browser |
| `signOut` | | Sign out of Armory | forgets this computer's sign-in (files stay) |
| `switchAccount` | | Switch account (the account panel) | 0.3.2: signs out and starts the next person's browser sign-in at once; the Armory folder stays, and is handed over with `takeOverFolder` |
| `takeOverFolder` | `requestId` | Use this folder, on the screen that says the folder is someone else's | 0.3.2: the account signed in now takes over this computer's Armory folder when the account it belongs to has nothing waiting in it (no check out, no save not sent, no change Armory hasn't kept, no new file not in Armory yet, no folder change not sent); otherwise the folder stays theirs and the answer says what is waiting, "Alex Kim still has 1 file checked out in this folder. ..." |
| `pause` / `resume` | | Pause, Resume (the tray's Pause and Resume too) | stops or restarts uploading and downloading ("Paused. Nothing uploads or downloads until you resume.") |
| `openVault` | | Open Armory folder | opens the Armory folder in File Explorer |
| `openFile` | `fileId` | a file row, a notice item, a My files row | host answers with `fileDetail` (the page shows File detail) |
| `launchFile` | `path`, `requestId` | Open (rows, File detail, a notice) | opens the file in its own program (SolidWorks for a part); refuses programs and scripts |
| `showInFolder` | `path` | Show in folder, a row for a file that isn't in Armory | opens File Explorer with the file selected |
| `checkOut` | `paths`, `open`, `requestId` | Check out (a file row, File detail, the selection bar), Check out and open on File detail and Check out and reopen on the question (`open: true`), Check out all after the small dialog (the folder's path) | takes each file to change it, makes it writable here, downloads a newer version first; with `open`, then opens it (asking first for SolidWorks to close it, if it has it open) |
| `checkIn` | `paths`, `requestId` | Check in (a file row, File detail, My files, the selection bar), Check in all | uploads the changes, makes the file read-only, reads it again and lets it go only over what it shared; a file open in SolidWorks stays checked out and writable and is checked in as soon as it is closed (0.3.3): "Plate.SLDPRT is open in SolidWorks. Save it there and close it; Armory checks it in as soon as it's closed.", "Checked in 12 of 15 files. 3 are open in SolidWorks: Armory checks them in as you close them." |
| `undoCheckOut` | `paths`, `requestId` | Undo check out (File detail, the selection bar) | puts back the version from before the check out (changes are kept in the history), lets it go |
| `takeBack` | `fileId`, `requestId` | Force check in of one file (a file row, File detail, the selection bar), after the small dialog asks (mentors and CAD leads) | ends the check out for its holder (the type keeps its old name); anything they hadn't checked in is kept as their own copy |
| `takeBackAll` | `fileIds`, `requestId` | Force check in of more than one file (Force check in all, the selection bar), after the small dialog asks; at most 20,000 ids | one action: each lock broken as for one file (16 at a time), then one pass for all of them, and one sentence back (since 0.3.1; before, the page sent one `takeBack` per file and each ran a whole pass) |
| `createFolder` | `projectId`, `parent`, `name`, `requestId` | New folder, after the small dialog | makes the folder |
| `renameFolder` | `projectId`, `folder`, `newName`, `requestId` | Rename folder, after the small dialog | renames it for everyone (refused, and put back, when someone else has a file in it checked out) |
| `deleteFolder` | `projectId`, `folder`, `requestId` | Delete folder, after the small dialog | removes it and its files for everyone; their history is kept |
| `renameFile` | `path`, `newName`, `requestId` | Rename on a notice's file that shares a name (after the small dialog refuses a name the project has, a lost extension or a character Windows forbids) | renames that one file in its folder: a file Armory doesn't have is renamed on disk (and then added); a file in Armory is renamed for everyone (`armory_move_file`), refused while someone else has it checked out |
| `addFiles` | `projectId`, `folder`, `requestId` | Add files | host shows a file picker, then copies the files in (never over a file already there) and adds them: one import summary; closing the picker answers with an empty message, which the page doesn't show |
| `dropFiles` | `projectId`, `folder`, `requestId` (+ the dropped files) | a drop on the open folder's list | host copies the dropped files in, a dropped folder whole, the same way |
| `dismissNotice` | `key` | a notice's Done or OK (`dismissNotice` action); Not now or OK on the check-out question (its `PromptView.key`) | the host drops that notice card, or that one question and asks about the next file SolidWorks has open without a check out |
| `saveSettings` | `vaultRoot`, `startAtSignIn`, `theme` | a setting, Use (a folder of my own) | saves settings; host answers with `view` |
| `chooseVaultRoot` | | Change, Choose another folder | host shows a folder picker, then answers with `view` |
| `reportProblem` | `kind`, `body`, `requestId` | Send in Report a problem (Settings), after the page refuses empty words | `kind` is `bug`, `idea` or `other`; the host saves the words with a fresh `userReport` incident and sends them (docs/agent/TELEMETRY.md); the answer is one sentence: "Sent. Thank you for telling us.", or "Saved. It will be sent ..." when it can't go yet |
| `sendFeedback` | `kind`, `body`, `requestId` | Send in Send feedback (the header's key, or Settings), after the page refuses empty words | v0.3: `kind` is `bug`, `idea` or `other`; a note on its own (`armory_submit_app_feedback`), saved first and sent at once when it can be, with Armory's version and what it was doing as its context, and no incident after it; the answer is one sentence, "Sent. Thank you for the feedback." or "Saved. It will be sent ..." |
| `openIncidents` | | Open incidents folder (Settings) | opens `%LOCALAPPDATA%\IDEA Armory\incidents` in File Explorer, so the files can be handed over by hand |
| `putBackKeptCopy` | `fileId`, `versionId`, `requestId` | Put back on this computer, on a File detail history entry of kind `keptCopy` (0.3.3, feedback N4) | `versionId` is the entry's `id`. The host's `PutBackKeptCopyAsync` (docs/agent/ENGINE.md, "Put back on this computer"): only the signed-in person's own kept copy; the file is checked out first when it is checked out to nobody; any save on disk the server doesn't have is kept first, and an open file is refused; the copy is put in place and stays checked out, shared only at check in. Answers "Put your copy of Plate.SLDPRT back on this computer. It's checked out to you: look at it in SolidWorks, then check it in to share it.", or why not ("That copy of Plate.SLDPRT is Maria Lopez's. Only your own kept copies can be put back here.", "Close Plate.SLDPRT in SolidWorks first, then put your copy back.") |

## Thumbnails (0.3.2)

Not a message: inside the app (`https://armory.local`) a file row and File detail show the
file's own picture as an image at `/thumb/<vault path>?v=<version>`, which the host answers
from Windows' thumbnail handlers (`ShellThumbnails`, the pictures File Explorer shows;
SolidWorks installs one for parts, assemblies and drawings) as PNG, or 404 when Windows has
no picture for it (never the file type's icon). Only files on this computer that are parts,
assemblies, drawings or pictures ask; rows ask as they are drawn (`loading="lazy"`), so a
folder of 5,000 files asks only for the rows in view. The glyph stays until a picture
arrives, and stays when there is none. The host serves only existing files inside the vault
(never `.armory`), one at a time on an STA thread of its own, and keeps the last 600 answers
by path, size and time written. The demo and the check pages are not on `armory.local` and
ask for nothing (tools/agent-ui/check-ui.mjs serves the page there from a request route to
check it).

## The demo

Outside WebView2, `?state=<name>` picks a demo state (`demo/states.js`), `theme=idea`,
`spaceWhite` or `space-white` the theme, `screen=home|detail|connect|settings` the
screen and `file=<fileId>` the file on File detail. The page-only places, so every
state can be drawn without a click, are `project=<projectId>`, `folder=<path in the
project>`, `select=<name>,<name>` (files in that folder), `expand=<notice key>`,
`dialog=newFolder|renameFolder|deleteFolder|checkOutAll|takeBack|forceAll|renameFile|report|feedback`
(renameFile asks about the first file of the open notice list; forceAll is Force check in
all for the open folder; report is Report a problem; feedback is Send feedback), `drag=1` (files held over the list), `at=browser` (Home
scrolled to Team files) and `press=<control key>` (the page presses that key once it is
drawn, and the demo holds every answer, so the working state stays in view); `result=<words>` (with
`resultOk=0` for a refusal) has the demo answer as if an action had just come back.
The demo transport answers every page-to-host type the way the engine would (a check
out changes the rows and answers with an `actionResult`, a rename adds the file and
shortens its notice); `openVault`, `showInFolder`, `addFiles` and `dropFiles` only log,
since a browser has no File Explorer to open.
