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
  Check out this folder, Check in this folder, and for a mentor or CAD lead Force check in
  this folder; 0.3.3: each says "this folder", and its tooltip and its question say how many
  files), the list's head ("Select all in this folder", a box that is checked, mixed or
  empty, and how many files), and its folder rows and file rows. My files' key says how
  many it checks in ("Check in my 1,401 files"). A file row's state key is Check out,
  Check in, or for a mentor or CAD lead on a file someone else has, Force check in. Files dragged from
  File Explorer drop into the open folder. How many files wait to upload is said once,
  by Right now (`activity.waiting`), never as a tag on each row.
- **File detail**: the file's display (its state and who has it), Open as the primary
  key, then Check out, Check out and open, Check in, Undo check out or Force check in as its
  state allows, Show in folder as a quiet link, Checked out (the person and computer,
  or "Available. Check it out to make changes."), and the history. A history entry of kind
  `keptCopy` has Put back on this computer (0.3.3, `putBackKeptCopy`; the host refuses
  another person's copy in one sentence, so the key shows on every kept copy until the view
  says which are yours). The year its version in Armory was saved in shows by its place when
  Armory knows it: "SolidWorks 2025", or "Saved in SolidWorks 2026" (amber) when that is
  newer than its project's year, as on its row. The status `checkingInWhenClosed` is the chip
  "Checks in when closed" (tone look) on every row and here, with `MyFileView.note` in My
  files.
- **Settings** is a sheet over Home with exactly the folder (and Change), Start Armory
  when I sign in, the theme, and Shared computer (0.3.3: "This computer is shared by
  several students", off by default; on a shared computer also who is using Armory now,
  "Ask for a PIN when switching students", and the students with Remove where it may be
  used; Change is not offered there, since the students take turns in one folder).
- **Who is using Armory?** (0.3.3, a computer several students share; see "Several
  students on one computer" below): the picker. It shows whenever `profiles.showing` is
  true, over every other screen, with no header keys; Home's account card then says who is
  using Armory and offers Switch student in place of Sign out and Switch account.
- **The small dialog** (`<dialog id="ask">`) asks New folder, Rename folder, Delete
  folder, Check out all (how many files, in that folder and its folders, and that
  nobody else can save them until they are checked in; it starts on Cancel), Rename
  file (a notice's file that shares its name with another file in the project) and
  Force check in (one file, the picked files, or all of a folder's: who has them, and that
  anything they hadn't checked in is kept as their own copy). For a mentor or CAD lead, Rename
  folder, Delete folder and Rename file have a second key while someone else has files there
  checked out (0.3.3, N5): "Force check in 3 files and rename" ("... and delete", "Force check
  in and rename"), which sends the same action with `force: true`. It is filled once when it opens and never redrawn by a host message, so
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
key (nothing can be checked out or in until it is added). A file whose first version never
arrived shows "No first version" (`noVersion`, 0.3.3), never "Uploading". Long lists (a folder, My
files, a notice's files) draw only the rows near the view at one fixed row height, so a
folder of 5,000 files keeps well under 150 rows in the page.

The page shows the engine's sentences as given (`SyncView.line`, `ActivityView.line`,
`DirectionView.line`, a notice's `title` and `detail`, an action's `message`) and never
picks them apart. A folder in an engine sentence is written with " › " (U+203A), as the
page's own crumbs are: "Added 4,987 of 5,000 files to Robot 2027 › CopyDesignTemp".

## Host to page

| `type` | fields | when |
|---|---|---|
| `view` | `view: AgentView` | on `ready` and whenever anything changes (at most every 500 ms during a pass); the page redraws from it. 0.3.3 (N7): the host never posts a view that is the same as the last one it posted (`LastViewPosted`; a new page load, or `ready`, forgets it), and the page draws nothing for one; a view whose `settings` or `effectiveTheme` alone changed redraws only the theme and Settings (see "Drawing" below) |
| `fileDetail` | `detail: FileDetailView` | the answer to `openFile` |
| `activity` | `activity: ActivityView` | while files move, at most 4 a second; the page patches only Right now and the status line, so focus, scroll and typing never move. 0.3.3 (N8): the running lines (`log`) only grow at the foot and lose their oldest at the top, and the newest one is always in sight (see "Drawing" below) |
| `actionResult` | `requestId`, `ok`, `message`, `offer` | once for each action (see Page to host); the page shows `message` in a quiet line at the window's foot, never an alert and never a focus change. `offer` is null, except `"withoutPicture"` after a `sendFeedback` with a picture that couldn't go (0.3.3; Send feedback shows that answer in its own dialog) | 0.3.3: also with `requestId: "shell"` for answers to File Explorer's right-click or a notification.
| `windowShot` | `requestId`, `ok`, `id`, `url`, `width`, `height`, `bytes`, `scaled`, `message` | the answer to `captureWindow` (0.3.3): a picture of this window (`WindowShotView`, its fields flat in the message); `url` serves exactly the bytes that would be sent; not `ok`: `message` says why, in one sentence |
| `myFeedback` | `requestId`, `state`, `pictures`, `message`, `notes` | the answer to `readMyFeedback` (0.3.3): Your feedback (`FeedbackListView`, its fields flat in the message) |
| `reveal` | `path` | 0.3.3: Show in Armory from File Explorer's right-click, after the host opened the window; `path` is vault-relative ("" is the Armory folder itself). The page shows the file's detail (a file in Armory), Team files at that folder (a folder, or the folder of a file Armory doesn't have yet), or Home; never while its small dialog is asking something. Sent once the page has said `ready`, right after its `view` |

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
  folderOwner: FolderOwnerView | null  // 0.3.3: connection vaultOwnedByOther: whose the folder is
  profiles: ProfilesView | null        // 0.3.3: a shared computer's students and picker; null otherwise
  solidWorks: SolidWorksView | null // 0.3.3: the SolidWorks link's line for Settings; null on a computer with no link
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
  upload: DirectionView | null,     // 0.3.3: or, while nothing uploads, a check in's, an undo's or a Force check in's count
  download: DirectionView | null,   // 0.3.3: or, while nothing downloads, a check out's count
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
  line: string                      // e.g. "Uploading 3 of 9 files, 48 MB left, about 20 sec", "Moving 120 files to Gearbox",
                                    // "Checking out 500 of 1,400 files" (0.3.3)
}
WaitingView { count: number, line: string }
  // "3 files are waiting to upload. They upload when this computer is back online."
  // "2 checked-out files have changes. Check them in to share them."
ActiveTransferView { path: string, name: string, direction: "upload" | "download" | "move", bytesDone: number, bytesTotal: number }
```

What the activity says, since 0.3.3 (feedback N2, N3 and N8; docs/agent/ENGINE.md "Activity"; the
records and their fields are unchanged, so the page needs nothing new to show it):

- **A long download or upload is one run.** Between the passes that move it, `sync.state` stays
  `syncing` and `activity.line` (the status line) keeps its count ("Downloading 1,160 of 1,429
  files, 701 MB left, about 11 min"); the page never sees "Checking for changes." or "Everything is
  saved to Armory." in the middle of it, and the tray never flips. The running lines (`log`) say
  how far it got at most every 10 seconds ("Downloaded 600 of 1,429 files"), then once when it is
  over ("Finished: 1,429 files downloaded in 7 min.", with "uploaded" and "kept copies saved"
  when there were some; no time under a minute). Paused, they say how far it got once the files
  in flight have landed ("Paused: 412 files downloaded so far."); going offline adds it to the
  offline line ("This computer is offline. Armory keeps trying by itself. 412 files downloaded so
  far."). "Sync finished: ..." after every slice is gone, and no running line says "sync".
- **A check out, an undo and a Force check in of several files count, from the click to the
  answer.** Shown in `download` (a check out: "Checking out 500 of 1,400 files") or `upload` (an
  undo: "Undoing 3 of 10 check outs"; a Force check in: "Force checking in 120 of 300 files")
  while nothing moves that way, as a check in's "Checking in 412 of 4,900 files" already was;
  `activity.line` is the line of whichever count has the most left. A Force check in's lines
  arrive while its calls are made ("Force checked in 500 of 1,200 files"), never all at once after.
- **Steps that take a while say so**: "Looking over 1,467 files on this computer", "Asking Armory
  what changed" (written once the step has taken a second, or 0.3 seconds in a click's own pass),
  "Read 600 of 1,400 files" (a check out or a check in reading the copies it must), "Copying 300 of
  1,000 files into Intake" (Add files), "Deleting Gearbox (120 files)", "Moved 120 files to
  Chassis". The answer of an action of many files (and of a folder's) is the last running line
  too ("Checked out 1,400 files.").

```
NoticeGroupView {
  key: string,                      // dismissNotice sends it back
  kind: "import" | "nameShared" | "newerWaiting" | "keptCopy" | "takenBack" | "folderPutBack"
      | "projectPutBack" | "projectRenaming" | "projectDeleted" | "cantSend" | "cantRead" | "checkInPartial"
      | "newerRelease" | "solidWorks",
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
  //   openFile (the first item's fileId), keepLocal {paths}, saveDown {paths} (0.3.3, solidWorks cards)
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
           | "noVersion"                    // 0.3.3: a record whose first version never arrived ("No first version";
                                            // File detail: "Added without its first version"), never "uploading"
SettingsView { vaultRoot: string, startAtSignIn: boolean, theme: "system" | "idea" | "spaceWhite",
               badges: BadgesView | null,     // 0.3.3; null until the host has checked
               sharedComputer: boolean }      // 0.3.3; false unless Settings turned it on
BadgesView {                        // 0.3.3: Armory's status on file icons (docs/agent/EXPLORER.md 2.6)
  state: "off" | "on" | "afterSignIn" | "crowded" | "partial" | "broken"   // BadgeHealthReport.Key
  line: string                      // Settings' sentence, e.g. "Armory's status shows on file icons."
}                                   // Settings draws the row with Turn on (turnOnBadges) for off and broken

FolderOwnerView {                   // 0.3.3: read from the folder by the engine, never from connect.message
  email: string, name: string,
  waiting: string[] | null          // what of theirs waits there ("1 file checked out"); null when not looked at yet
}
ProfilesView {                      // 0.3.3: see "Several students on one computer"
  showing: boolean,                 // the picker shows, and the view carries none of the student in use's
                                    // account, files, notices, prompt or activity names
  currentId: string | null, sharedFolder: string,
  pinsRequired: boolean, canChangePins: boolean,  // canChangePins: the student in use is a mentor (the server says)
  pinsNote: string | null,          // "Turned off by Mr. Pina on Oct 9."
  canTurnOff: boolean,
  note: string | null,              // one sentence for Home: a folder of their own, or back in the shared one
  profiles: ProfileView[],          // the student in use first, then the most recent
  step: PickerStepView
}
ProfileView {
  id: string,                       // 32 lowercase hex digits
  name: string, email: string, initials: string,
  hue: number,                      // 0 to 7: the picture's color, the same for an address every time
  current: boolean, lastUsedAt: string | null,
  folder: string, ownFolder: boolean,
  waiting: string | null,           // "2 files checked out": their work waiting in their folder (never for the student in use)
  needsSignIn: boolean, hasPin: boolean, canRemove: boolean
}
PickerStepView {
  kind: "choose" | "pin" | "newPin" | "adding" | "folderBusy" | "switching" | "signInAgain" | "tooNew",
  profileId: string | null, message: string | null,
  triesLeft: number | null, waitSeconds: number | null,           // pin
  ownFolder: string | null, ownerName: string | null, ownerWaiting: string | null,  // folderBusy
  fromName: string | null,                                        // switching
  connectPhase: "idle" | "waitingForBrowser" | "finishing" | "failed" | null  // adding, signInAgain
SolidWorksView {                    // 0.3.3: the SolidWorks link (docs/agent/SOLIDWORKS.md), drawn in Settings
                    // 0.3.3: docs/agent/SOLIDWORKS.md section 8
  state: "none" | "attached" | "cantSaveDown" | "administrator",
  line: string,                     // "SolidWorks isn't running.", "Linked to SolidWorks 2026 SP4.1. It saves team files in 2025.",
                                    // "Linked to SolidWorks 2026 SP2. It can't save team files in 2025.",
                                    // "SolidWorks was started as administrator, so Armory can't link to it."
  detail: string | null             // cantSaveDown: why, and what to do ("Update SolidWorks 2026 to Service Pack 3 or newer
                                    // so Armory can save team files in 2025. Until then, files you save stay on this
                                    // computer only."); administrator: "Close SolidWorks and start it normally, not as administrator."
}

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
any other card. `savedRelease` and `newerThanPin` are a tag on the row and File detail (0.3.3:
"Saved in SolidWorks 2026", amber, its tooltip "Saved in SolidWorks 2026. Robot 2027 uses
SolidWorks 2025, which can open it only to look."; File detail also shows a year that is not
newer, "SolidWorks 2025", as a plain tag). `newerThanPinCount` is not shown: the card counts
the files.

The SolidWorks link's card (0.3.3, kind `solidWorks`; docs/agent/SOLIDWORKS.md section 3 has
every sentence) holds one item per open document that needs something before or around a
save in its project's SolidWorks year, plus one when SolidWorks was started as administrator.
A card of one shows that item's own title, detail and button: "When you save Bracket.SLDASM,
Armory saves it in SolidWorks 2025" with "Keep this file on this computer only" (`keepLocal`);
"Bracket.SLDASM is saved on this computer only" (kept, or blocked by what 2025 doesn't have)
with "Save it in 2025 now" (`saveDown`) where that can work; "SolidWorks couldn't save
Plate.SLDPRT in 2025, so it isn't saved yet" (tone `bad`); "Plate.SLDPRT was saved in
SolidWorks 2026" with "Save it in 2025 now". A card of several says "SolidWorks needs you",
"Each one says what to do.", with "Show them" (`expand`); each item's `detail` is its title and
words. Tone `look` otherwise. Done dismisses it as any card. A page that does not know the kind
yet shows it as any other card, and a page that does not know `keepLocal` or `saveDown` shows
the button doing nothing: `app.js`'s `runNotice` needs the two cases (`act(command, {paths})`,
like `checkIn`), and Settings needs a line for `solidWorks` (its `line`, and `detail` under it
when there is one). Neither is in this change; the host side and `bridge.js` are.

The check-out question (`prompt`): "Check out Plate-Left.SLDPRT to edit it?", "SolidWorks
opened it read-only. Check it out, then close it in SolidWorks and open it again here to
save changes.", with Check out and reopen (`checkOut` with `open: true`; the host checks
it out and, with the SolidWorks link (0.3.3), makes it writable right there in SolidWorks:
"Checked out Plate-Left.SLDPRT. You can save it in SolidWorks now."; without the link, or
when SolidWorks couldn't, "Checked out Plate-Left.SLDPRT. Close it in SolidWorks and Armory
opens it again, ready to save.", and it opens again once the student closes it, within five
minutes; docs/agent/ENGINE.md, "SolidWorks opened a file") and Not now (`dismissNotice` with the question's `key`: that one question goes,
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
`sendFeedback`, `takeOverFolder`, and (0.3.3) `putBackKeptCopy`, `turnOnBadges`, `pickProfile`,
`enterPin`, `setPin`, `addProfile`, `forgotPin`, `chooseFolder`, `removeProfile`,
`setSharedComputer`, `setPinsRequired`, `keepLocal` and `saveDown` (`ACTIONS` in bridge.js). An
**ask** (0.3.3, `ASKS` in bridge.js) carries a `requestId` too and is answered by a message of its
own, never `actionResult`: `captureWindow` by `windowShot`, `readMyFeedback` by `myFeedback`.

The page shows an action is under way from the moment it is sent until its
`actionResult` arrives (v0.2.1): the pressed key gets `aria-busy="true"` and
`aria-disabled="true"`, a small spinner in place of its glyph, and ignores a second
press; the quiet line at the foot says what is under way in plain words ("Checking out
Bracket.SLDPRT...", "Checking in 3 files...", "Opening Bracket.SLDPRT...") with a
spinner (`data-working="true"` on `#result`); and the rows the action touches say so
("Checking out...") in place of who has them. The answer with the same `requestId`
replaces all of it. The spinner holds still under `prefers-reduced-motion`.

0.3.3 (N9): only the files the action can change say so: Check out marks the files nobody
has, Check in and Undo check out the files checked out here, Force check in the files it
names; a file that was already checked in stays as it was. The working line counts those
files ("Checking in 1,401 files..."). The answer to an action on more than one file also
stays under the status as **Last action** (the sentence and its time, with OK), since the
line at the foot fades after a few seconds; OK puts it away, and the next such answer
replaces it.

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
| `renameFolder` | `projectId`, `folder`, `newName`, `force`, `requestId` | Rename folder, after the small dialog (`force: false`); its second key, Force check in 3 files and rename, for a mentor or CAD lead while someone else has files in it checked out (`force: true`) | renames it for everyone (refused, and put back, when someone else has a file in it checked out; the refusal says to ask a mentor or CAD lead to force check in). With `force` (0.3.3, N5): the check outs in the way are force checked in first, in the same action, then it is renamed: "Force checked in 3 files from Maria Lopez, then renamed Gearbox to Gearbox v2. Anything Maria hadn't checked in is kept as Maria's own copy." Anyone else asking with `force` is told only a mentor or CAD lead can |
| `deleteFolder` | `projectId`, `folder`, `force`, `requestId` | Delete folder, after the small dialog (`force: false`); its second key, Force check in 3 files and delete, as for `renameFolder` | removes it and its files for everyone; their history is kept. `force`: as for `renameFolder` |
| `renameFile` | `path`, `newName`, `force`, `requestId` | Rename on a notice's file that shares a name (after the small dialog refuses a name the project has, a lost extension or a character Windows forbids; `force: false`); its second key, Force check in and rename, for a mentor or CAD lead while someone else has the file checked out (`force: true`) | renames that one file in its folder: a file Armory doesn't have is renamed on disk (and then added); a file in Armory is renamed for everyone (`armory_move_file`), refused while someone else has it checked out. `force`: as for `renameFolder` |
| `addFiles` | `projectId`, `folder`, `requestId` | Add files | host shows a file picker, then copies the files in (never over a file already there) and adds them: one import summary; closing the picker answers with an empty message, which the page doesn't show |
| `dropFiles` | `projectId`, `folder`, `requestId` (+ the dropped files) | a drop on the open folder's list | host copies the dropped files in, a dropped folder whole, the same way |
| `keepLocal` | `paths`, `requestId` | 0.3.3: "Keep this file on this computer only" on a `solidWorks` card | the open document's saves write this computer's SolidWorks year and stay on this computer only (a private draft) until it is saved in the project's year: "Bracket.SLDASM stays on this computer only when you save it. Nobody else gets those changes until it is saved in SolidWorks 2025." |
| `saveDown` | `paths`, `requestId` | 0.3.3: "Save it in 2025 now" on a `solidWorks` card | SolidWorks saves the open, checked-out document again in its project's year: "Saved Plate.SLDPRT in SolidWorks 2025.", or why not ("Check out Plate.SLDPRT first: SolidWorks has it read-only.", "Open Plate.SLDPRT in SolidWorks first.", ...) |
| `dismissNotice` | `key` | a notice's Done or OK (`dismissNotice` action); Not now or OK on the check-out question (its `PromptView.key`) | the host drops that notice card, or that one question and asks about the next file SolidWorks has open without a check out |
| `saveSettings` | `vaultRoot`, `startAtSignIn`, `theme` | a setting, Use (a folder of my own) | saves settings; host answers with `view` |
| `chooseVaultRoot` | | Change, Choose another folder | host shows a folder picker, then answers with `view` |
| `reportProblem` | `kind`, `body`, `requestId` | Send in Report a problem (Settings), after the page refuses empty words | `kind` is `bug`, `idea` or `other`; the host saves the words with a fresh `userReport` incident and sends them (docs/agent/TELEMETRY.md); the answer is one sentence: "Sent. Thank you for telling us.", or "Saved. It will be sent ..." when it can't go yet |
| `sendFeedback` | `kind`, `body`, `tried`, `area`, `shot`, `requestId` | Send (or Ctrl+Enter) in Send feedback (the header's key, or Settings), after the page refuses empty words; "Send without the picture" after the offer | 0.3.3, the same as the website's: `kind` is `bug`, `idea`, `praise` or `other`; `tried` what the person tried (at most 1,000 characters) or null; `area` the window or view it is about, filled in by the page (at most 120: "Settings", "File details: Gear.SLDPRT", "Home > Robot 2027 > Drivetrain", "Home"); `shot` the id of the picture `windowShot` answered with, or null (the host checks it is 32 lowercase hex digits). A note on its own (`armory_submit_app_feedback`), with Armory's version and what it was doing as its context, and no incident after it. Without a picture it is saved first and sent at once when it can be; with one it goes now and is never saved. The answer is one sentence ("Sent. Thank you for the feedback.", "Saved. It will be sent ..."); a picture that couldn't go is answered with `offer: "withoutPicture"` |
| `captureWindow` | `width`, `height`, `requestId` | Add a picture of this window, in Send feedback (an ask) | 0.3.3: the page's size in CSS pixels (1 to 16,384). The host takes a picture of this window only and answers `windowShot` (see "Send feedback's picture" below) |
| `readMyFeedback` | `requestId` | opening Settings or Send feedback (at most once a minute), and Your feedback (an ask) | 0.3.3: the host answers `myFeedback` |
| `openIncidents` | | Open incidents folder (Settings) | opens `%LOCALAPPDATA%\IDEA Armory\incidents` in File Explorer, so the files can be handed over by hand |
| `turnOnBadges` | `requestId` | Turn on, in Settings' "Status on file icons" row (only for `off` and `broken`); Settings closes so the answer shows at the window's foot | 0.3.3: the host runs `<app>\badges\IDEA-Armory-Badges-Setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART` with the `runas` verb (Windows asks for an administrator's password), waits for it, checks the badges again (a new `view`) and answers with the new Settings line ("Armory's status shows on file icons after you sign out of Windows and back in."), or "Nothing changed. This one step needs an administrator's password." for a canceled prompt (error 1223), "The badges setup stopped before it finished, so nothing changed." for an exit code other than 0, and "The badges setup isn't in Armory's folder on this computer. Ask an administrator to run IDEA-Armory-Badges-Setup." when the file is missing |
| `putBackKeptCopy` | `fileId`, `versionId`, `requestId` | Put back on this computer, on a File detail history entry of kind `keptCopy` (0.3.3, feedback N4) | `versionId` is the entry's `id`. The host's `PutBackKeptCopyAsync` (docs/agent/ENGINE.md, "Put back on this computer"): only the signed-in person's own kept copy; the file is checked out first when it is checked out to nobody; any save on disk the server doesn't have is kept first, and an open file is refused; the copy is put in place and stays checked out, shared only at check in. Answers "Put your copy of Plate.SLDPRT back on this computer. It's checked out to you: look at it in SolidWorks, then check it in to share it.", or why not ("That copy of Plate.SLDPRT is Maria Lopez's. Only your own kept copies can be put back here.", "Close Plate.SLDPRT in SolidWorks first, then put your copy back.") |
| `showPicker` | | Switch student (Home's account card on a shared computer) | 0.3.3: the picker shows |
| `cancelPicker` | | Back, Cancel, Escape on any picker step but the tiles | back to the tiles; a browser sign-in under way stops, and a student being added is not kept |
| `pickProfile` | `profileId`, `requestId` | a student's tile | the PIN step; straight in when PINs are off; the browser sign-in first when their sign-in here ended or they have no PIN yet |
| `enterPin` | `profileId`, `pin`, `requestId` | the fourth digit typed | right: the switch; wrong: the step says how many tries are left, then a wait (30 s doubling to 15 min, never a lockout) |
| `setPin` | `profileId`, `pin`, `requestId` | the fourth digit of the second field, when both match | a new student's PIN (they are kept now), or a new one after Forgot your PIN; a PIN too easy to guess is refused in one sentence |
| `addProfile` | `requestId` | Add a student, Try again, Open the browser again | the browser sign-in, once, into a profile of its own; again while one waits stops that one and starts a new one |
| `forgotPin` | `profileId`, `requestId` | Forgot your PIN?, Sign in with Google, Open the browser again | the browser sign-in as that same student; another account changes nothing |
| `chooseFolder` | `profileId`, `choice`, `requestId` | Wait for Alex, Use C:\IDEA\Armory-jordan | `choice` is `wait` (back to the tiles) or `own` (a folder of their own until their work there is done) |
| `removeProfile` | `profileId`, `requestId` | Remove (Settings), after the small dialog asks | forgets that student's sign-in and PIN here (ending the sign-in on the server when it can); never deletes a file |
| `setSharedComputer` | `on`, `pin`, `requestId` | the Shared computer switch, after the small dialog asks | on: the student signed in now becomes the first profile with the PIN given (`pin` is "" when nobody is signed in); off: only the student in use stays signed in |
| `setPinsRequired` | `on`, `requestId` | Ask for a PIN when switching students | a mentor in use only; who and when are kept and shown (`pinsNote`) |

## Send feedback's picture of the window (0.3.3)

"Add a picture of this window" in Send feedback. Before it asks, the page hides the dialog and
its scrim (`dialog.away`), puts `html.shooting` on (every file picture, `img.thumb`, is hidden
and its glyph shows), and replaces every email address in the page's text (`[\w.+-]+@[\w-]+(\.[\w-]+)+`,
the signed-in person's own included) with `•••@domain`, keeping the originals; `view`,
`activity` and `fileDetail` messages that arrive meanwhile wait. After two animation frames it
sends `captureWindow { width, height }`. The host (`MainWindow`) takes
`CoreWebView2.CapturePreviewAsync(Png)`: what this WebView draws, never the screen and never
another window. Over 2,097,152 bytes it is taken again smaller with the DevTools protocol's
`Page.captureScreenshot` (`{format: "png", captureBeyondViewport: false, clip: {x: 0, y: 0, width,
height, scale}}`, `scale` from `ScreenshotFit.NextScale`, at most three times, never below 0.25),
measured each time. Still over, or not taken: `windowShot` with `ok: false` and why. The host
keeps the picture in memory only (`WindowShots`, the last one; a new one replaces it, a sent one
is forgotten) and serves exactly those bytes at `https://armory.local/shot/<id>.png` with
`Cache-Control: no-store`, so not even the WebView's cache keeps it. On `windowShot` the page puts
back every address, picture and the dialog, applies the messages that waited, and shows the
picture at the dialog's width with "This is the picture that will be sent: 1,120 by 760 pixels,
214 KB. Email addresses and file pictures are hidden." ("It was made smaller to fit 2 MB." when
`scaled`) and Remove picture. The person sees what is sent, and nothing else is.

The dialog stays open, its keys held, until `sendFeedback` is answered: `ok` closes it and the
sentence shows at the window's foot; `offer: "withoutPicture"` keeps the words, says why in the
dialog and turns Send into "Send without the picture" (the same note, `shot: null`); any other
refusal says why in the dialog and leaves Send to try again. Escape and a click outside wait
while a note is on its way or a picture is being taken.

## Your feedback (0.3.3)

`myFeedback`: `state` is `shown` (`notes`, newest first, at most 50), `missing` (the website
doesn't have `armory_my_app_feedback` yet: the page hides Your feedback everywhere), `offline` or
`failed` (`message` says so in one sentence; Your feedback is still offered), or `signedOut`
(hidden). `pictures` is true while the website takes pictures; when false, Send feedback offers no
picture. Each `FeedbackNoteView` has `id`, `createdAt`, `kind`, `body`, `tried`, `area`,
`hasScreenshot`, `appVersion`, `deviceName`, `status` (new, seen, resolved or closed; spam reads
closed) and `statusWords` ("Not read yet", "Read by the IDEA team", "Done", "Closed"), and
`reviewedAt`. Settings' "Something not working?" row offers "Your feedback (3)"; Send feedback
offers it too, and Back to your note returns with the words kept. The list shows each note's kind,
status, the first lines of its words, what was tried, and when, from which computer, about what,
with a picture. **There are no replies in Armory**: the site keeps none, and the list's foot says
"The IDEA team reads every note. There are no replies in Armory: the status shows where yours is."

## Several students on one computer (0.3.3)

docs/agent/PROFILES.md has the rules and why. For the page: when `settings.sharedComputer`
is true, `profiles` is filled; while `profiles.showing` is true the window draws the
picker and nothing else (no header keys, the Settings sheet and the small dialog close).
The view then carries no account, files, notices, prompt or file names in `activity`, so
nothing of the student in use's shows to whoever sits down; their files keep moving
meanwhile.

The picker's steps (`profiles.step.kind`):

- `choose`: a tile per student (`initials` in a disc colored by `hue`, the name, and one
  line: "Using Armory now", "Alex has 2 files checked out here", "Last here 2 hours ago",
  "Sign in again to continue") and Add a student. Tab reaches every tile, the arrows move
  between them, Enter or a click picks one. The student in use's tile is lit.
- `pin`: one 4-digit field (digits only, never shown); the fourth digit sends `enterPin`.
  During a wait (`waitSeconds`) the field is disabled and Forgot your PIN? is the
  primary key; the page counts the wait down from when the view arrived.
- `newPin`: the PIN twice; the page refuses two that differ before anything is sent.
- `adding` and `signInAgain`: Connect's steps and status plate for the browser sign-in,
  with the one instruction a shared computer needs: when the browser page shows someone
  else, click "Not you? Use another account" first.
- `folderBusy`: the shared folder holds another student's work (`ownerName`,
  `ownerWaiting`): Use `ownFolder` (primary) or Wait for them, and the same-path trade-off
  in one sentence.
- `switching`: the status plate while the last student's engine stops and the next one's
  starts.
- `tooNew`: a newer Armory set this computer's students up; nothing to press.

Escape goes back to the tiles from every step but `choose` (where nothing lets the picker
go without a student picked) and `switching`.

## Host-facing, not the page (0.3.3)

What Windows notifications (C5, built in the host) read from `AgentHost`, never sent to the
page:

- `OpenPrompts` and `OpenPromptsChanged`: `OpenPrompt(Path, Name, FileId, CheckedOutBy,
  ViaSolidWorks, Group, Kind)`, one per file SolidWorks opened that this computer hasn't
  checked out (with the SolidWorks link only the documents the student opened, once per
  SolidWorks session); prompts with the same `Group` are one notification; `Kind` is
  `CheckOut`, `Reopen`, `HeldByOther` or `HeldOnMyOtherComputer` (`CheckedOutBy` then says
  "Maria Lopez on LAB-PC-07"). Show each (Path, Group) once.
- `CheckOutAndReopenAsync(paths)`: the notification's "Check out and reopen" (one
  `ActionResult` sentence, as `checkOut` with `open: true`).
- `SaveDownPrompts`: `SaveDownPrompt(Key, Path, Name, Title, Text, PinnedRelease)`, the
  question before a save down that drops something, with "Save in 2025"
  (`AnswerSaveDownAsync(path, keepLocal: false)`; no answer means the same) and "Keep this file
  on this computer only" (`AnswerSaveDownAsync(path, keepLocal: true)`).
- `KeepLocalAsync(paths)` and `SaveDownNowAsync(paths)`: what `keepLocal` and `saveDown` call.

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

0.3.3 (N15): a thumbnail handler is someone else's code, so the window never waits on one for
long. A picture not made within 5 seconds is answered 404 (the glyph stays); a handler stuck on
one file for 20 seconds is left behind on its thread, a new thread makes the next pictures, and
the file is written to agent.log and counted (the flight recorder's `thumbnailStuck`); after 3
stuck threads no picture is made until Armory starts again. At most 64 pictures wait, the
newest (the rows in view): an older one is answered 404 at once. Two asks for one file share
one picture. The key (path, size, time written) is read before the picture is made, so a
picture is never kept under bytes it wasn't made from; a file with no picture is asked again
after 60 seconds. A picture is served with `Cache-Control: private, max-age=31536000,
immutable` (its address carries the version), and the page keeps a picture that arrived, and
the glyph of one that never will, when it draws the row again: a row scrolled away and back,
or redrawn by a view, never asks twice.

## The demo

Outside WebView2, `?state=<name>` picks a demo state (`demo/states.js`), `theme=idea`,
`spaceWhite` or `space-white` the theme, `screen=home|detail|connect|settings|picker` the
screen and `file=<fileId>` the file on File detail. The page-only places, so every
state can be drawn without a click, are `project=<projectId>`, `folder=<path in the
project>`, `select=<name>,<name>` (files in that folder), `expand=<notice key>`,
`dialog=newFolder|renameFolder|deleteFolder|checkOutAll|takeBack|forceAll|renameFile|report|feedback|myFeedback`
(renameFile asks about the first file of the open notice list; forceAll is Force check in
all for the open folder; report is Report a problem; feedback is Send feedback; myFeedback is
Your feedback), `words=<text>` (Send feedback opens with these words typed), `shot=1` (Send
feedback takes a picture once it is open; `shot=offer` then sends it, and the demo answers that
the picture can't go), `feedback=shown|missing|offline` (how the demo answers `readMyFeedback`),
`drag=1` (files held over the list), `at=browser` (Home
scrolled to Team files) and `press=<control key>` (the page presses that key once it is
drawn, and the demo holds every answer, so the working state stays in view); `result=<words>` (with
`resultOk=0` for a refusal) has the demo answer as if an action had just come back.
The demo transport answers every page-to-host type the way the engine would (a check
out changes the rows and answers with an `actionResult`, a rename adds the file and
shortens its notice; `captureWindow` gets a small drawing of a window, the app's being the
window itself, `readMyFeedback` three sample notes, and `putBackKeptCopy` puts the copy back
checked out); `openVault`, `showInFolder`, `addFiles` and `dropFiles` only log, since a browser
has no File Explorer to open. 0.3.3's states for the window's own pieces are `checkingOut` (a
check out of a folder under way, its running lines and its rows), `forcingIn` (a force check
in under way), `forceManyConfirm` (Force check in of files 19 people have), `checkInWaits`
(files that check in when closed, in the COTS folder) and `newerRelease` (files saved in a
newer SolidWorks year, its card, rows and File detail). The shared computer's states are
`pickerChoose`, `pickerWaiting`, `pickerPin`, `pickerPinWrong`, `pickerPinWait`,
`pickerAdding`, `pickerNewPin`, `pickerFolderBusy`, `pickerSwitching`,
`pickerSignInAgain`, `pickerFirst`, `pickerPinsOff`, `sharedHome`, `sharedOwnFolder`,
`sharedSettings` and `sharedSettingsMentor`; in them every student's PIN is 2580, a
browser sign-in finishes by itself after 3 seconds, and Add a student adds Sam Patel.

## Tooltips (0.3.3)

Every control says what it does in one plain sentence (feedback N1): every key, link, tab,
checkbox and switch, in every screen and dialog, has a `data-tip`, filled from one table in
app.js (`TIPS`, with the file's or folder's name and counts put in). Hold the mouse on a
control for 750 ms, or reach it with Tab (300 ms), and one card (`#tip`, `role="tooltip"`)
shows the sentence under the control, or over it near the window's foot, always inside the
window, at most 280px wide, in the theme's own colors. While it shows, the control is
described by it (`aria-describedby="tip"`, the control's own ids kept). It goes when the mouse
leaves or presses, on any key, a scroll, when its control is drawn again or goes, and on
Escape (which then does nothing else: a dialog stays open). Inside an open dialog the card
moves into the dialog, above its scrim. It fades in over 120 ms, at once under
`prefers-reduced-motion`. A key that is off says why ("Nothing here is checked out by you"):
it is `aria-disabled="true"`, never `disabled`, so the mouse still reaches it, and a press does
nothing. Tagged words that are cut short (who has a file, the newest running line) show the
whole of themselves the same way. tools/agent-ui/check-ui.mjs fails a page with a visible
control that has no tooltip, or a tooltip with a word the page's words may not use.

The host's own controls say it too: every tray menu item has a `ToolTipText` (`HostTips`,
`ShowItemToolTips`), and the window's "Get WebView2" key, shown when WebView2 is missing,
has a `ToolTip`.

## Drawing (0.3.3)

The page draws from the newest view, and only what changed (feedback N7, X-full-render):

- **Only what changed.** Home, File detail, the header and the Settings sheet are laid over
  what is on the page: an element with the same key (`data-key`, `id` or `data-part`) and tag
  stays and only its changed attributes and words are set; a region whose markup is the same
  is not touched. Long lists keep each row whose words did not change (rows are known by their
  file), so focus, scroll places, typing and pictures stay. Another file's File detail is drawn
  anew.
- **The same view twice** draws nothing. A view whose settings alone changed (a theme picked,
  Start Armory when I sign in) changes the theme and Settings in place; Home is not drawn.
- **A theme picked** is worn in the next frame, all at once: every transition is off for that
  frame (`data-theming`), so no frame shows the two themes' colors mixed. A view that arrives
  before the frame is painted waits for it (at most 250 ms, for a window that is hidden), and
  only the newest one is drawn.
- **The running lines** (`#act-log`, Right now) take new lines at their foot and let old ones go
  at their top, and nothing else in them is drawn again: a student reading an older line keeps
  their place, and the box follows the newest line only when it was already at its foot. The
  newest line is always in sight: under the status line in a wide window (the left column),
  and in a slim strip under the header in a narrow one (`#latest-strip`).
- **Focus never hides.** The pinned keys at the top of the scrolling column (the selection bar,
  the folder's place) set `scroll-padding-top`, so a control reached with Tab is scrolled into
  view below them, never under them.

tools/agent-ui/check-ui.mjs checks each of these with a stand-in WebView2 host: a view the page
already has changes nothing in `#main` or the header, a view whose settings alone changed
changes only the theme, a theme picked with 1,401 files on screen and a CPU four times slower
paints within one frame (and the view that arrived meanwhile follows), ten near-identical
views of those files are drawn within a time limit and keep their rows, and the running lines
are added and let go without redrawing the rest.
