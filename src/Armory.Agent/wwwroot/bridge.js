/*
 * bridge.js: the only way the Armory Agent window talks to the engine.
 *
 * Contract: docs/agent/BRIDGE.md. The types below mirror the C# records in
 * src/Armory.Agent.Engine/View/AgentView.cs; the two message lists (PAGE_TO_HOST and
 * HOST_TO_PAGE) are what AgentViewContractTests compares with the C# side, so keep
 * each one a plain array of string literals.
 *
 * Inside WebView2 the page posts message objects with window.chrome.webview.postMessage
 * (the host reads CoreWebView2WebMessageReceivedEventArgs.WebMessageAsJson) and receives
 * the host's PostWebMessageAsJson messages as parsed objects. Dropped files go with
 * window.chrome.webview.postMessageWithAdditionalObjects(message, files); the host reads
 * each CoreWebView2File.Path from the event's AdditionalObjects.
 *
 * Outside WebView2 (a plain browser, the screenshot tools) it loads demo/states.js and a
 * demo transport answers instead, from the query string:
 *   ?state=<name>&theme=idea|spaceWhite&screen=home|detail|connect|settings&file=<fileId>
 * (theme=space-white is accepted too), plus page-only places the page opens on, so every
 * state can be drawn without a click:
 *   project=<projectId>  folder=<folder path in the project; empty for its top>
 *   select=<name>,<name> (files in that folder)  expand=<notice key>
 *   dialog=newFolder|renameFolder|deleteFolder|checkOutAll|takeBack|forceAll|renameFile|report|feedback|myFeedback
 *     (renameFile asks about the first file in the open notice list; forceAll is Force check
 *     in all for the open folder; report is Report a problem; feedback is Send feedback;
 *     myFeedback is the "Your feedback" list)
 *   words=<text> (Send feedback opens with these words typed)
 *   shot=1 (Send feedback takes a picture of the window once it is open; shot=offer then sends
 *     it, and the demo answers that the picture can't go, offering the note without it)
 *   feedback=shown|missing|offline (how the demo answers readMyFeedback; shown by default)
 *   press=<control key> (the page presses that key once it is drawn, and the demo holds every
 *     answer, so what a press shows while it waits stays in view)
 *   drag=1 (files held over the list)
 *   at=browser (Home scrolled so the team's files are in view)
 *   result=<words> (the demo answers as if an action had just come back with these words;
 *     resultOk=0 makes it a refusal)
 *   screen=picker (a shared computer's "Who is using Armory?"; the demo's PIN is 2580)
 * Nothing here touches the network.
 */
(function () {
	'use strict';

	/* ---------------------------------------------------------------- Types */

	/**
	 * @typedef {'signedOut' | 'connecting' | 'signedIn' | 'vaultOwnedByOther'} Connection
	 * @typedef {'idle' | 'waitingForBrowser' | 'finishing' | 'failed'} ConnectPhase
	 * @typedef {'synced' | 'syncing' | 'offline' | 'paused' | 'attention'} SyncState
	 * @typedef {'synced' | 'changed' | 'uploading' | 'downloading' | 'waiting' | 'newerWaiting'
	 *   | 'keptCopy' | 'notInArmory' | 'notOnThisComputer' | 'checkingInWhenClosed' | 'noVersion'} FileStatus
	 * @typedef {'available' | 'mine' | 'other' | 'myOtherComputer'} CheckoutState
	 * @typedef {'upload' | 'download' | 'move'} Direction
	 * @typedef {'info' | 'look' | 'bad'} NoticeTone
	 * @typedef {'import' | 'nameShared' | 'newerWaiting' | 'keptCopy' | 'takenBack' | 'folderPutBack'
	 *   | 'projectPutBack' | 'projectRenaming' | 'projectDeleted' | 'cantSend' | 'cantRead' | 'checkInPartial' | 'newerRelease'} NoticeKind
	 * @typedef {'version' | 'keptCopy' | 'removed'} HistoryKind
	 * @typedef {'system' | 'idea' | 'spaceWhite'} ThemeSetting
	 * @typedef {'idea' | 'spaceWhite'} EffectiveTheme
	 * @typedef {'off' | 'on' | 'afterSignIn' | 'crowded' | 'partial' | 'broken'} BadgesState
	 * @typedef {'choose' | 'pin' | 'newPin' | 'adding' | 'folderBusy' | 'switching' | 'signInAgain' | 'tooNew'} PickerStep
	 */

	/**
	 * @typedef {object} ConnectView
	 * @property {ConnectPhase} phase
	 * @property {string | null} message
	 */

	/**
	 * @typedef {object} AccountView
	 * @property {string} email
	 * @property {string} deviceName
	 */

	/**
	 * @typedef {object} SyncView
	 * @property {SyncState} state
	 * @property {string} line           Plain student sentence, e.g. "Everything is saved to Armory."
	 * @property {string | null} detail  e.g. "Last checked 2 minutes ago"
	 * @property {number} pendingCount
	 */

	/**
	 * One direction of what is moving right now.
	 * @typedef {object} DirectionView
	 * @property {number} filesDone
	 * @property {number} filesTotal
	 * @property {number} bytesDone
	 * @property {number} bytesTotal
	 * @property {number} bytesPerSecond
	 * @property {number | null} secondsLeft   null until 3 seconds and 2 files have gone by
	 * @property {string} line                e.g. "Downloading 412 of 1,280 files, 2.1 GB left, about 3 min"
	 */

	/**
	 * @typedef {object} WaitingView
	 * @property {number} count
	 * @property {string} line  e.g. "3 files are waiting to upload. They upload when this computer is back online."
	 */

	/**
	 * @typedef {object} ActiveTransferView
	 * @property {string} path            vault-relative, forward slashes
	 * @property {string} name
	 * @property {Direction} direction
	 * @property {number} bytesDone
	 * @property {number} bytesTotal
	 */

	/**
	 * What is happening right now. Also sent on its own as the 'activity' message.
	 * @typedef {object} ActivityView
	 * @property {string | null} line              the status line while files move
	 * @property {DirectionView | null} upload
	 * @property {DirectionView | null} download
	 * @property {DirectionView | null} move
	 * @property {WaitingView | null} waiting
	 * @property {ActiveTransferView[]} active      at most 8
	 * @property {ActivityLineView[]} log           what Armory did in the last few minutes, oldest first, at most 40
	 */

	/**
	 * @typedef {object} ActivityLineView
	 * @property {string} at     ISO-8601 UTC
	 * @property {string} line   one plain sentence: "Downloaded Plate.SLDPRT (612 KB)", "Checked out 500 of 1,400 files"
	 */

	/**
	 * @typedef {object} NoticeActionView
	 * @property {string} label
	 * @property {string} command   a page-to-host type (checkOut, checkIn, launchFile, dismissNotice...) or "expand" (page only)
	 * @property {string[]} paths
	 */

	/**
	 * @typedef {object} NoticeItemView
	 * @property {string | null} fileId
	 * @property {string} path
	 * @property {string} name
	 * @property {string | null} detail
	 */

	/**
	 * One card per kind, never one per file.
	 * @typedef {object} NoticeGroupView
	 * @property {string} key
	 * @property {NoticeKind} kind
	 * @property {NoticeTone} tone
	 * @property {string} title       e.g. "14 files share a name with other files in this project"
	 * @property {string} detail
	 * @property {number} count       the total; items holds at most 200
	 * @property {NoticeActionView | null} action
	 * @property {NoticeItemView[]} items
	 */

	/**
	 * Who has a file checked out.
	 * @typedef {object} CheckoutView
	 * @property {CheckoutState} state
	 * @property {string} label          "Checked out by you", "Checked out by Maria Lopez on LAB-PC-07", "Available"
	 * @property {string | null} name
	 * @property {string | null} email
	 * @property {string | null} device
	 * @property {string | null} since    ISO-8601
	 */

	/**
	 * The quiet question when SolidWorks opens a file this computer has not checked out.
	 * @typedef {object} PromptView
	 * @property {string} key            one per open ("prompt:<path>:<when SolidWorks opened it>"); Not now sends it back in dismissNotice, which hides this one only
	 * @property {string | null} fileId
	 * @property {string} path
	 * @property {string} name
	 * @property {CheckoutView} checkout
	 * @property {boolean} canCheckOut
	 */

	/**
	 * @typedef {object} MyFileView
	 * @property {string | null} fileId  null until the server has the file
	 * @property {string} path           vault-relative, forward slashes
	 * @property {string} name
	 * @property {string} project
	 * @property {FileStatus} status
	 * @property {string | null} note
	 * @property {CheckoutView} checkout
	 */

	/**
	 * @typedef {object} FileRowView
	 * @property {string | null} fileId  null for a file in the folder that isn't in Armory
	 * @property {string} name
	 * @property {string} path
	 * @property {FileStatus} status
	 * @property {CheckoutView} checkout
	 * @property {boolean} changed       its bytes here differ from the last check in
	 * @property {boolean} releaseNotChecked
	 * @property {string | null} updatedAt
	 * @property {string | null} updatedBy
	 * @property {number | null} savedRelease  the SolidWorks year its version in Armory was saved in, when known
	 * @property {boolean} newerThanPin  that year is newer than the project's pinnedRelease
	 */

	/**
	 * @typedef {object} FolderView
	 * @property {string} path           in the project: "" for its top folder, "Drivetrain/Gears" for a subfolder
	 * @property {string} name
	 * @property {number} fileCount      files directly in it
	 * @property {FileRowView[]} files
	 */

	/**
	 * @typedef {object} ProjectView
	 * @property {string} id
	 * @property {string} name
	 * @property {boolean} archived
	 * @property {string} role           student, cad_lead, mentor or instructor
	 * @property {boolean} canTakeBack   a mentor or CAD lead
	 * @property {FolderView[]} folders  a flat list, every folder once, empty ones too
	 * @property {number} pinnedRelease  the SolidWorks year the project uses
	 * @property {number} newerThanPinCount  its files saved in a newer SolidWorks (the newerRelease notice lists them)
	 */

	/**
	 * Armory's status on file icons in File Explorer (the badges), as the host found it.
	 * @typedef {object} BadgesView
	 * @property {BadgesState} state   Settings offers Turn on (turnOnBadges) for off and broken
	 * @property {string} line         Settings' sentence about it
	 */

	/**
	 * @typedef {object} SettingsView
	 * @property {string} vaultRoot
	 * @property {boolean} startAtSignIn
	 * @property {ThemeSetting} theme
	 * @property {BadgesView | null} badges   null until the host has checked
	 * @property {boolean} sharedComputer  several students take turns on this computer, each with a profile (PROFILES.md)
	 */

	/**
	 * The account whose files are in the Armory folder, read from the folder itself: the
	 * "belongs to someone else" screen names them (null on every other screen).
	 * @typedef {object} FolderOwnerView
	 * @property {string} email
	 * @property {string} name
	 * @property {string[] | null} waiting  what of theirs waits there ("2 files checked out"), null when not looked at yet
	 */

	/**
	 * A computer shared by several students (null when it isn't). While showing is true the
	 * page shows the picker and nothing else; the view then carries none of the student in
	 * use's files, notices or account.
	 * @typedef {object} ProfilesView
	 * @property {boolean} showing
	 * @property {string | null} currentId  the student in use, null before anyone is
	 * @property {string} sharedFolder       the Armory folder the students take turns in
	 * @property {boolean} pinsRequired      each student types their 4-digit PIN to switch
	 * @property {boolean} canChangePins     the student in use is a mentor
	 * @property {string | null} pinsNote    who turned PINs on or off, and when ("Turned off by Mr. Pina on Oct 1.")
	 * @property {boolean} canTurnOff        the student in use may turn shared mode off
	 * @property {string | null} note        one sentence for Home: a folder of their own, or back in the shared one
	 * @property {ProfileView[]} profiles    the student in use first, then the most recent
	 * @property {PickerStepView} step
	 */

	/**
	 * One student's tile.
	 * @typedef {object} ProfileView
	 * @property {string} id               32 hex digits
	 * @property {string} name
	 * @property {string} email
	 * @property {string} initials         one or two letters, drawn in the picture's place
	 * @property {number} hue              0 to 7, the picture's color, the same for an address every time
	 * @property {boolean} current
	 * @property {string | null} lastUsedAt  ISO-8601
	 * @property {string} folder           their Armory folder (the shared one, or C:\IDEA\Armory-<name>)
	 * @property {boolean} ownFolder       a folder of their own, not the shared one
	 * @property {string | null} waiting   their work waiting in their folder ("2 files checked out"); never for the student in use
	 * @property {boolean} needsSignIn     their sign-in here ended: picking them opens the browser
	 * @property {boolean} hasPin
	 * @property {boolean} canRemove       Remove in Settings: themselves, or anyone for a mentor
	 */

	/**
	 * Where the picker is. The fields a kind doesn't use are null.
	 * @typedef {object} PickerStepView
	 * @property {PickerStep} kind
	 * @property {string | null} profileId     the student this step is for
	 * @property {string | null} message       one sentence to show (a wrong PIN, a wait, a refusal)
	 * @property {number | null} triesLeft     pin: wrong tries left before a wait
	 * @property {number | null} waitSeconds   pin: seconds before another try
	 * @property {string | null} ownFolder     folderBusy: the folder of their own Armory would use
	 * @property {string | null} ownerName     folderBusy: whose work waits in the shared folder
	 * @property {string | null} ownerWaiting  folderBusy: what of theirs waits ("2 files checked out")
	 * @property {string | null} fromName      switching: the student whose last file finishes first
	 * @property {ConnectPhase | null} connectPhase  adding and signInAgain: where the browser sign-in is
	 */

	/**
	 * @typedef {object} AgentView
	 * @property {Connection} connection
	 * @property {ConnectView} connect
	 * @property {AccountView | null} account
	 * @property {SyncView} sync
	 * @property {ActivityView} activity
	 * @property {string} vaultRoot
	 * @property {NoticeGroupView[]} notices
	 * @property {PromptView | null} prompt
	 * @property {MyFileView[]} myFiles
	 * @property {ProjectView[]} projects
	 * @property {SettingsView} settings
	 * @property {EffectiveTheme} effectiveTheme
	 * @property {FolderOwnerView | null} folderOwner  connection vaultOwnedByOther: whose the folder is
	 * @property {ProfilesView | null} profiles        a shared computer's students and picker
	 */

	/**
	 * @typedef {object} HistoryEntryView
	 * @property {string} id
	 * @property {HistoryKind} kind
	 * @property {string} author
	 * @property {string} at             ISO-8601
	 * @property {number} bytes
	 * @property {string} note
	 * @property {boolean} releaseNotChecked
	 * @property {boolean} isCurrent
	 * @property {boolean} routine        a kept copy that is the ordinary record of work (saved while checked out, an earlier save): the neutral tone
	 */

	/**
	 * @typedef {object} FileDetailView
	 * @property {string} fileId
	 * @property {string} name
	 * @property {string} path
	 * @property {string} project
	 * @property {string} folder
	 * @property {FileStatus} status
	 * @property {CheckoutView} checkout
	 * @property {boolean} releaseNotChecked
	 * @property {boolean} canTakeBack
	 * @property {HistoryEntryView[]} history  newest first
	 * @property {number | null} savedRelease
	 * @property {boolean} newerThanPin
	 */

	/**
	 * Send feedback's picture of the Armory window (the answer to captureWindow; its fields come
	 * flat in the windowShot message). The host keeps it in memory only and serves exactly the
	 * bytes that would be sent at url (armory.local/shot/<id>.png, never cached).
	 * @typedef {object} WindowShotView
	 * @property {boolean} ok
	 * @property {string | null} id         32 hex digits; sendFeedback's shot
	 * @property {string | null} url
	 * @property {number} width             the PNG's pixels
	 * @property {number} height
	 * @property {number} bytes             at most 2,097,152
	 * @property {boolean} scaled           taken again smaller to fit 2 MB
	 * @property {string | null} message    why there is no picture, when not ok
	 */

	/**
	 * @typedef {'new' | 'seen' | 'resolved' | 'closed'} FeedbackStatus
	 * @typedef {'shown' | 'missing' | 'offline' | 'signedOut' | 'failed'} FeedbackListState
	 */

	/**
	 * One note of "Your feedback", newest first. There are no replies: the status is all there is.
	 * @typedef {object} FeedbackNoteView
	 * @property {string} id
	 * @property {string} createdAt          ISO-8601 UTC
	 * @property {string} kind               bug, idea, praise or other
	 * @property {string} body
	 * @property {string | null} tried
	 * @property {string | null} area
	 * @property {boolean} hasScreenshot
	 * @property {string} appVersion
	 * @property {string | null} deviceName  the computer it was sent from
	 * @property {FeedbackStatus} status
	 * @property {string} statusWords        "Not read yet", "Read by the IDEA team", "Done", "Closed"
	 * @property {string | null} reviewedAt  ISO-8601 UTC
	 */

	/**
	 * "Your feedback" (the answer to readMyFeedback; its fields come flat in the myFeedback message).
	 * missing: the website doesn't have it yet, and the window hides it.
	 * @typedef {object} FeedbackListView
	 * @property {FeedbackListState} state
	 * @property {boolean} pictures          Send feedback may offer a picture of the window
	 * @property {string | null} message     offline or failed, in one sentence
	 * @property {FeedbackNoteView[]} notes
	 */

	/**
	 * Host to page.
	 * An actionResult with requestId 'shell' answers something File Explorer's right-click or a
	 * notification asked for; reveal is Show in Armory (a file's detail, or Team files at a
	 * folder; '' is Home).
	 * @typedef {{ type: 'view', view: AgentView } | { type: 'fileDetail', detail: FileDetailView } | { type: 'activity', activity: ActivityView } | { type: 'actionResult', requestId: string, ok: boolean, message: string, offer: string | null } | { type: 'windowShot', requestId: string, ok: boolean, id: string | null, url: string | null, width: number, height: number, bytes: number, scaled: boolean, message: string | null } | { type: 'myFeedback', requestId: string, state: FeedbackListState, pictures: boolean, message: string | null, notes: FeedbackNoteView[] } | { type: 'reveal', path: string }} HostMessage
	 */

	/**
	 * Page to host. Fields per type (an action also carries the requestId its actionResult
	 * answers; ACTIONS below lists them):
	 *   ready, connect, cancelConnect, signOut, pause, resume, openVault, chooseVaultRoot, openIncidents: none
	 *   openFile: { fileId }                  (the host answers with fileDetail)
	 *   launchFile: { path }                  (opens it in its own program: SolidWorks for a part)
	 *   showInFolder: { path }
	 *   checkOut: { paths, open }             (files or folders, vault-relative)
	 *   checkIn: { paths }    undoCheckOut: { paths }    takeBack: { fileId }
 *   takeBackAll: { fileIds }              (Force check in of more than one file: one action, one pass)
 *   takeOverFolder: none                  (the folder is another account's: take it over when nothing of theirs waits)
 *   switchAccount: none                   (sign out, and the next person signs in now)
	 *   createFolder: { projectId, parent, name }    renameFolder: { projectId, folder, newName, force }
	 *   deleteFolder: { projectId, folder, force }   renameFile: { path, newName, force }   (one file, in its folder)
	 *                                         (force: a mentor or CAD lead force checks in the check outs
	 *                                          in the way first, in the same action; false for everyone else)
	 *   addFiles: { projectId, folder }
	 *   dropFiles: { projectId, folder }      (sent with the dropped File objects)
	 *   dismissNotice: { key }               (a notice card's key, or the check-out question's)
	 *   saveSettings: { vaultRoot, startAtSignIn, theme }
	 *   reportProblem: { kind, body }         (kind: bug, idea or other; the host saves it with a
	 *                                          fresh incident and answers in one sentence)
	 *   sendFeedback: { kind, body, tried, area, shot }
	 *                                         (kind: bug, idea, praise or other; tried and shot may be
	 *                                          null; a note on its own, no incident; one sentence back,
	 *                                          and offer: 'withoutPicture' when its picture couldn't go)
	 *   captureWindow: { width, height }      (an ask: the host answers windowShot, a picture of this
	 *                                          window only, after the page hid its dialog and masks)
	 *   readMyFeedback: none                  (an ask: the host answers myFeedback)
	 *   openIncidents: none                   (opens the incidents folder in File Explorer)
	 *   putBackKeptCopy: { fileId, versionId } (File detail: one of your kept copies, a keptCopy history
	 *                                          entry's id, put back on this computer and checked out to you)
	 *   turnOnBadges: none                    (Settings: the badges setup, as an administrator; one sentence back)
	 * A shared computer (PROFILES.md):
	 *   showPicker: none                      (Switch student: the picker shows)
	 *   cancelPicker: none                    (back to the tiles; a browser sign-in under way stops)
	 *   pickProfile: { profileId }            (a tile: the PIN step, or straight in when PINs are off)
	 *   enterPin: { profileId, pin }          (sent at the fourth digit)
	 *   setPin: { profileId, pin }            (a new student's PIN, or a new one after Forgot your PIN)
	 *   addProfile: none                      (Add a student: the browser sign-in, once)
	 *   forgotPin: { profileId }              (the browser sign-in as that same student)
	 *   chooseFolder: { profileId, choice }   (choice: wait, or own: a folder of their own)
	 *   removeProfile: { profileId }          (forgets their sign-in and PIN; no file is deleted)
	 *   setSharedComputer: { on, pin }        (pin: the student in use's first PIN when turning on, else "")
	 *   setPinsRequired: { on }               (a mentor only)
	 * @typedef {'ready' | 'connect' | 'cancelConnect' | 'signOut' | 'pause' | 'resume'
	 *   | 'openVault' | 'openFile' | 'launchFile' | 'showInFolder' | 'checkOut' | 'checkIn'
	 *   | 'undoCheckOut' | 'takeBack' | 'createFolder' | 'renameFolder' | 'deleteFolder' | 'renameFile'
	 *   | 'addFiles' | 'dropFiles' | 'dismissNotice' | 'saveSettings' | 'chooseVaultRoot'
	 *   | 'reportProblem' | 'openIncidents' | 'sendFeedback' | 'takeBackAll' | 'takeOverFolder' | 'switchAccount'
	 *   | 'putBackKeptCopy' | 'captureWindow' | 'readMyFeedback' | 'turnOnBadges'
	 *   | 'showPicker' | 'pickProfile' | 'enterPin' | 'setPin' | 'addProfile' | 'forgotPin' | 'cancelPicker'
	 *   | 'chooseFolder' | 'removeProfile' | 'setSharedComputer' | 'setPinsRequired'} PageMessageType
	 */

	/**
	 * Where the demo asks the page to start. Null inside WebView2.
	 * @typedef {object} DemoRoute
	 * @property {'home' | 'detail' | 'connect' | 'settings' | 'picker'} screen
	 * @property {string | null} fileId
	 * @property {string} state
	 * @property {string | null} project   a project id
	 * @property {string | null} folder    a folder path in that project
	 * @property {string[]} select         file names in that folder
	 * @property {string | null} expand    a notice key
	 * @property {string | null} dialog    newFolder, renameFolder, deleteFolder, checkOutAll, takeBack, forceAll, renameFile, report, feedback or myFeedback
	 * @property {boolean} drag
	 * @property {string | null} at        a part of Home to scroll into view: browser
	 * @property {string | null} press     a control key the page presses once it is drawn
	 * @property {string | null} words     Send feedback opens with these words typed
	 * @property {string | null} shot      1: Send feedback takes a picture; offer: and sends it, and the picture can't go
	 */

	/* ------------------------------------------------------- Message lists */

	/** Page to host message types (BRIDGE.md, "Page to host"). */
	var PAGE_TO_HOST = ['ready', 'connect', 'cancelConnect', 'signOut', 'pause', 'resume', 'openVault', 'openFile', 'launchFile', 'showInFolder', 'checkOut', 'checkIn', 'undoCheckOut', 'takeBack', 'createFolder', 'renameFolder', 'deleteFolder', 'renameFile', 'addFiles', 'dropFiles', 'dismissNotice', 'saveSettings', 'chooseVaultRoot', 'reportProblem', 'openIncidents', 'sendFeedback', 'takeBackAll', 'takeOverFolder', 'switchAccount', 'putBackKeptCopy', 'captureWindow', 'readMyFeedback', 'turnOnBadges', 'showPicker', 'pickProfile', 'enterPin', 'setPin', 'addProfile', 'forgotPin', 'cancelPicker', 'chooseFolder', 'removeProfile', 'setSharedComputer', 'setPinsRequired'];

	/** Host to page message types (BRIDGE.md, "Host to page"). */
	var HOST_TO_PAGE = ['view', 'fileDetail', 'activity', 'actionResult', 'windowShot', 'myFeedback', 'reveal'];

	/** Required fields per page-to-host type, as the host's message records name them. */
	var REQUIRED = {
		openFile: ['fileId'],
		launchFile: ['path'],
		showInFolder: ['path'],
		checkOut: ['paths', 'open'],
		checkIn: ['paths'],
		undoCheckOut: ['paths'],
		takeBack: ['fileId'],
		takeBackAll: ['fileIds'],
		createFolder: ['projectId', 'parent', 'name'],
		renameFolder: ['projectId', 'folder', 'newName', 'force'],
		deleteFolder: ['projectId', 'folder', 'force'],
		renameFile: ['path', 'newName', 'force'],
		addFiles: ['projectId', 'folder'],
		dropFiles: ['projectId', 'folder'],
		dismissNotice: ['key'],
		saveSettings: ['vaultRoot', 'startAtSignIn', 'theme'],
		reportProblem: ['kind', 'body'],
		sendFeedback: ['kind', 'body', 'tried', 'area', 'shot'],
		putBackKeptCopy: ['fileId', 'versionId'],
		captureWindow: ['width', 'height'],
		pickProfile: ['profileId'],
		enterPin: ['profileId', 'pin'],
		setPin: ['profileId', 'pin'],
		forgotPin: ['profileId'],
		chooseFolder: ['profileId', 'choice'],
		removeProfile: ['profileId'],
		setSharedComputer: ['on', 'pin'],
		setPinsRequired: ['on']
	};

	/** Actions: each carries a requestId, and the host answers it with one actionResult. */
	var ACTIONS = ['launchFile', 'checkOut', 'checkIn', 'undoCheckOut', 'takeBack', 'createFolder', 'renameFolder', 'deleteFolder', 'renameFile', 'addFiles', 'dropFiles', 'reportProblem', 'sendFeedback', 'takeBackAll', 'takeOverFolder', 'putBackKeptCopy', 'turnOnBadges', 'pickProfile', 'enterPin', 'setPin', 'addProfile', 'forgotPin', 'chooseFolder', 'removeProfile', 'setSharedComputer', 'setPinsRequired'];

	/** Asks: each carries a requestId too, and the host answers it with a message of its own
	 *  (windowShot, myFeedback), never actionResult. */
	var ASKS = ['captureWindow', 'readMyFeedback'];

	/* ----------------------------------------------------------- Plumbing */

	/** @type {Array<(message: HostMessage) => void>} */
	var handlers = [];
	var nextRequest = 1;

	function dispatch(message) {
		if (!message || typeof message !== 'object' || HOST_TO_PAGE.indexOf(message.type) < 0) {
			console.warn('Armory bridge: ignored a message it does not know', message);
			return;
		}
		handlers.slice().forEach(function (h) {
			h(message);
		});
	}

	function buildMessage(type, fields) {
		if (PAGE_TO_HOST.indexOf(type) < 0) throw new Error('Armory bridge: unknown message type ' + type);
		var message = { type: type };
		var needed = REQUIRED[type] || [];
		for (var i = 0; i < needed.length; i++) {
			if (!fields || !(needed[i] in fields)) throw new Error('Armory bridge: ' + type + ' needs ' + needed[i]);
			message[needed[i]] = fields[needed[i]];
		}
		if (ACTIONS.indexOf(type) >= 0 || ASKS.indexOf(type) >= 0) message.requestId = fields && fields.requestId ? String(fields.requestId) : 'r' + nextRequest++;
		return message;
	}

	var params = new URLSearchParams(location.search);
	var webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;

	/** @param {string | null} raw @returns {EffectiveTheme | null} */
	function themeParam(raw) {
		if (!raw) return null;
		var t = raw.toLowerCase().replace(/[^a-z]/g, '');
		if (t === 'idea' || t === 'dark') return 'idea';
		if (t === 'spacewhite' || t === 'light') return 'spaceWhite';
		return null;
	}

	/** The theme Windows (or the browser) prefers. @returns {EffectiveTheme} */
	function systemTheme() {
		var forced = webview ? null : themeParam(params.get('theme'));
		if (forced) return forced;
		return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'spaceWhite' : 'idea';
	}

	// Paint the right theme before the first view arrives, so nothing flashes.
	document.documentElement.setAttribute('data-theme', systemTheme());

	/* ---------------------------------------------------- WebView2 transport */

	function webviewTransport() {
		webview.addEventListener('message', function (e) {
			dispatch(e.data);
		});
		return {
			post: function (message, files) {
				if (files && files.length && typeof webview.postMessageWithAdditionalObjects === 'function') webview.postMessageWithAdditionalObjects(message, files);
				else webview.postMessage(message);
			},
			now: function () {
				return Date.now();
			},
			route: null
		};
	}

	/* -------------------------------------------------------- Demo transport */

	function demoTransport() {
		var demo = null;
		var waiting = [];
		var stateName = params.get('state') || 'synced';
		var view = null;
		var pausedFrom = null;
		var timers = [];
		var sharedTimer = null;

		var route = {
			screen: /** @type {any} */ (params.get('screen') || ''),
			fileId: params.get('file'),
			state: stateName,
			project: params.get('project'),
			folder: params.get('folder'),
			select: (params.get('select') || '').split(',').filter(Boolean),
			expand: params.get('expand'),
			dialog: params.get('dialog'),
			drag: params.get('drag') === '1',
			at: params.get('at'),
			press: params.get('press'),
			words: params.get('words'),
			shot: params.get('shot')
		};

		function firstFileId(v) {
			for (var i = 0; i < v.myFiles.length; i++) if (v.myFiles[i].fileId) return v.myFiles[i].fileId;
			for (var p = 0; p < v.projects.length; p++)
				for (var f = 0; f < v.projects[p].folders.length; f++)
					if (v.projects[p].folders[f].files.length) return v.projects[p].folders[f].files[0].fileId;
			return null;
		}
		function clone(x) {
			return JSON.parse(JSON.stringify(x));
		}
		function deliver(message) {
			setTimeout(function () {
				dispatch(clone(message));
			}, 0);
		}
		function postView() {
			view.effectiveTheme = view.settings.theme === 'system' ? systemTheme() : view.settings.theme;
			deliver({ type: 'view', view: view });
		}
		function result(message, ok, words, offer) {
			// A state drawn with a key just pressed keeps waiting for its answer.
			if (route.press) return;
			deliver({ type: 'actionResult', requestId: message.requestId, ok: ok, message: words, offer: offer || null });
		}
		/** "Your feedback" as the demo has it: two notes, or the website without it, or offline. */
		function myFeedback(message) {
			var how = params.get('feedback') || 'shown';
			var shown = how === 'shown';
			deliver({
				type: 'myFeedback',
				requestId: message.requestId,
				state: how,
				pictures: shown,
				message: how === 'offline' ? 'You\'re offline. Your feedback shows here once this computer is back online.' : null,
				notes: shown ? demo.myFeedback : []
			});
		}
		function useState(name) {
			var s = demo.states[name];
			var settings = view ? view.settings : null;
			stateName = name;
			view = clone(s.view);
			if (settings) {
				view.settings = settings;
				view.vaultRoot = settings.vaultRoot;
			}
		}
		function clearTimers() {
			timers.forEach(clearTimeout);
			timers = [];
		}
		function projectById(id) {
			return view.projects.filter(function (p) {
				return p.id === id;
			})[0];
		}
		/** Every row of the view, with its project and folder. */
		function eachRow(fn) {
			view.projects.forEach(function (p) {
				p.folders.forEach(function (f) {
					f.files.forEach(function (r) {
						fn(r, p, f);
					});
				});
			});
		}
		/** The rows a list of vault-relative paths names: a file, or every file under a folder. */
		function rowsFor(paths) {
			var out = [];
			eachRow(function (r) {
				for (var i = 0; i < paths.length; i++) {
					if (r.path === paths[i] || r.path.indexOf(paths[i] + '/') === 0) {
						out.push(r);
						break;
					}
				}
			});
			return out;
		}
		function words(n, one, many) {
			return n + ' ' + (n === 1 ? one : many);
		}
		/** "Maria Lopez has ", "Maria Lopez and Sam Lee have ", the way the engine names who has the rest. */
		function whoHas(names) {
			if (names.length === 1) return names[0] + ' has ';
			if (names.length === 2) return names[0] + ' and ' + names[1] + ' have ';
			if (names.length === 3) return names[0] + ', ' + names[1] + ' and ' + names[2] + ' have ';
			return names[0] + ', ' + names[1] + ' and ' + (names.length - 2) + ' others have ';
		}
		/** Keeps My files in step: the files this computer has checked out, in any project
		 *  (an archived one too, so they can always be checked in). */
		function refreshMine() {
			var mine = [];
			eachRow(function (r, p) {
				if (r.checkout.state === 'mine') mine.push({ fileId: r.fileId, path: r.path, name: r.name, project: p.name, status: r.status, note: null, checkout: r.checkout });
			});
			view.myFiles = mine;
		}
		/** The question about a file SolidWorks opened goes once that file is checked out here. */
		function settlePrompt() {
			if (!view.prompt) return;
			var asked = view.prompt.path;
			eachRow(function (r) {
				if (r.path === asked && r.checkout.state === 'mine') view.prompt = null;
			});
		}

		/** Answers one page message the way the engine would. */
		function handle(message, files) {
			var rows;
			var p;
			switch (message.type) {
				case 'ready':
					postView();
					// A state drawn with an action's answer at the window's foot.
					if (params.get('result')) deliver({ type: 'actionResult', requestId: 'r0', ok: params.get('resultOk') !== '0', message: params.get('result'), offer: null });
					break;
				case 'connect':
					clearTimers();
					view.connection = 'connecting';
					view.connect = { phase: 'waitingForBrowser', message: demo.states.connecting.view.connect.message };
					postView();
					timers.push(
						setTimeout(function () {
							view.connect = { phase: 'finishing', message: 'Signed in. Getting your files list.' };
							postView();
						}, 2500)
					);
					timers.push(
						setTimeout(function () {
							useState(demo.afterConnect);
							postView();
						}, 4000)
					);
					break;
				case 'cancelConnect':
				case 'signOut':
					clearTimers();
					useState('signedOut');
					postView();
					break;
				case 'switchAccount':
					clearTimers();
					useState('connecting');
					postView();
					break;
				case 'takeOverFolder':
					useState('synced');
					postView();
					result(message, true, 'This Armory folder is yours now. Maria Lopez\'s files here were all saved to Armory, so nothing of theirs changes.');
					break;
				case 'pause':
					if (view.sync.state !== 'paused') {
						pausedFrom = clone(view.sync);
						view.sync = demo.pausedSync(view.sync.pendingCount);
					}
					postView();
					break;
				case 'resume':
					view.sync = pausedFrom || clone(demo.states.synced.view.sync);
					pausedFrom = null;
					postView();
					break;
				case 'openFile':
					deliver({ type: 'fileDetail', detail: demo.detailFor(stateName, message.fileId, view) });
					break;
				case 'launchFile':
					// In the app this opens the file in its own program (SolidWorks for a part).
					console.info('Armory demo: launchFile', message.path);
					result(message, true, 'Opening ' + message.path.split('/').pop() + '.');
					break;
				case 'checkOut':
					rows = rowsFor(message.paths);
					var got = 0;
					var held = {};
					var wasOpen = !!view.prompt && rows.length === 1 && rows[0].path === view.prompt.path;
					rows.forEach(function (r) {
						if (r.checkout.state === 'available' && r.fileId) {
							r.checkout = demo.checkoutMine();
							got++;
						} else if (r.checkout.state === 'other') held[r.checkout.name] = (held[r.checkout.name] || 0) + 1;
					});
					refreshMine();
					settlePrompt();
					postView();
					var names = Object.keys(held);
					var heldCount = names.reduce(function (n, k) {
						return n + held[k];
					}, 0);
					// Open: the engine opens it, except while SolidWorks still has it open (the
					// question's case), when it asks for it to be closed first.
					if (rows.length === 1 && got === 1 && message.open && wasOpen)
						result(message, true, 'Checked out ' + rows[0].name + '. Close ' + rows[0].name + ' in SolidWorks first, then open it again.');
					else if (rows.length === 1 && got === 1) result(message, true, 'Checked out ' + rows[0].name + '.');
					else if (!heldCount) result(message, got > 0, got ? 'Checked out ' + words(got, 'file', 'files') + '.' : 'Those files are already checked out by you.');
					else
						result(
							message,
							got > 0,
							'Checked out ' + got + ' of ' + words(rows.length, 'file', 'files') + '. ' + whoHas(names) + heldCount + ' of them checked out.'
						);
					break;
				case 'checkIn':
				case 'undoCheckOut':
					rows = rowsFor(message.paths).filter(function (r) {
						return r.checkout.state === 'mine';
					});
					rows.forEach(function (r) {
						r.checkout = demo.checkoutAvailable();
						r.changed = false;
						if (r.status === 'changed' || r.status === 'waiting') r.status = 'synced';
					});
					refreshMine();
					postView();
					if (!rows.length) result(message, false, 'Nothing there is checked out by you.');
					else if (message.type === 'checkIn') result(message, true, rows.length === 1 ? 'Checked in ' + rows[0].name + '.' : 'Checked in ' + words(rows.length, 'file', 'files') + '.');
					else
						result(
							message,
							true,
							rows.length === 1
								? 'Undid the check out of ' + rows[0].name + '. Your changes are kept as your own copy.'
								: 'Undid ' + words(rows.length, 'check out', 'check outs') + '. Your changes are kept as your own copies.'
						);
					break;
				case 'takeBack':
					var taken = null;
					eachRow(function (r) {
						if (r.fileId === message.fileId && r.checkout.state !== 'available') {
							taken = { row: r, from: r.checkout.name };
							r.checkout = demo.checkoutAvailable();
						}
					});
					refreshMine();
					postView();
					result(
						message,
						!!taken,
						taken ? 'Force checked in ' + taken.row.name + ' from ' + taken.from + '. Anything they hadn\'t checked in is kept as their own copy.' : 'That file isn\'t checked out.'
					);
					break;
				case 'takeBackAll':
					var forced = 0;
					eachRow(function (r) {
						if (message.fileIds.indexOf(r.fileId) >= 0 && r.checkout.state !== 'available' && r.checkout.state !== 'mine') {
							forced++;
							r.checkout = demo.checkoutAvailable();
						}
					});
					refreshMine();
					postView();
					result(
						message,
						forced > 0,
						forced > 0
							? 'Force checked in ' + words(forced, 'file', 'files') + '. Anything that wasn\'t checked in is kept as its holder\'s own copy.'
							: 'None of those files is checked out by someone else now.'
					);
					break;
				case 'putBackKeptCopy':
					// The engine's PutBackKeptCopyAsync: the student's own copy goes back on this
					// computer, checked out to them (another person's is refused in one sentence).
					var putBack = null;
					eachRow(function (r) {
						if (r.fileId === message.fileId) putBack = r;
					});
					if (!putBack) {
						result(message, false, 'That file isn\'t in Armory on this computer.');
						break;
					}
					if (putBack.checkout.state !== 'available' && putBack.checkout.state !== 'mine') {
						result(message, false, 'Close ' + putBack.name + ' first: ' + putBack.checkout.label.toLowerCase() + '.');
						break;
					}
					putBack.checkout = demo.checkoutMine();
					putBack.changed = true;
					putBack.status = 'changed';
					refreshMine();
					postView();
					result(message, true, 'Put your copy of ' + putBack.name + ' back on this computer. It\'s checked out to you: look at it in SolidWorks, then check it in to share it.');
					break;
				case 'createFolder':
					p = projectById(message.projectId);
					p.folders.push({ path: (message.parent ? message.parent + '/' : '') + message.name, name: message.name, fileCount: 0, files: [] });
					postView();
					result(message, true, 'Made the folder ' + message.name + '.');
					break;
				case 'renameFolder':
					p = projectById(message.projectId);
					var from = message.folder;
					var to = from.split('/').slice(0, -1).concat([message.newName]).join('/');
					p.folders.forEach(function (f) {
						if (f.path === from || f.path.indexOf(from + '/') === 0) {
							f.path = to + f.path.slice(from.length);
							if (f.path === to) f.name = message.newName;
							f.files.forEach(function (r) {
								r.path = p.name + '/' + f.path + '/' + r.name;
							});
						}
					});
					postView();
					result(message, true, 'Renamed ' + from.split('/').pop() + ' to ' + message.newName + '.');
					break;
				case 'deleteFolder':
					p = projectById(message.projectId);
					var gone = 0;
					p.folders = p.folders.filter(function (f) {
						var inside = f.path === message.folder || f.path.indexOf(message.folder + '/') === 0;
						if (inside) gone += f.files.length;
						return !inside;
					});
					refreshMine();
					postView();
					result(message, true, 'Deleted ' + message.folder.split('/').pop() + ' and the ' + words(gone, 'file', 'files') + ' in it. Their history is kept.');
					break;
				case 'renameFile':
					var renamed = null;
					eachRow(function (r) {
						if (r.path === message.path) renamed = r;
					});
					if (!renamed) {
						result(message, false, 'That file is not in your Armory folder.');
						break;
					}
					var oldName = renamed.name;
					renamed.name = message.newName;
					renamed.path = renamed.path.split('/').slice(0, -1).concat([message.newName]).join('/');
					if (!renamed.fileId) {
						// Its own name now: Armory adds it, checked in, like any new file.
						renamed.fileId = 'f-new-' + message.newName.toLowerCase().replace(/[^a-z0-9]+/g, '-');
						renamed.status = 'synced';
						renamed.updatedBy = demo.me;
						renamed.updatedAt = new Date(Date.parse(demo.now)).toISOString();
					}
					// The notice about shared names loses that file, and goes with its last one.
					view.notices = view.notices
						.map(function (n) {
							var left = n.items.filter(function (it) {
								return it.path !== message.path;
							});
							if (left.length === n.items.length) return n;
							var count = n.count - 1;
							var out = JSON.parse(JSON.stringify(n));
							out.items = left;
							out.count = count;
							if (n.kind === 'nameShared') out.title = count === 1 ? '1 file shares a name with another file in this project' : count + ' files share a name with other files in this project';
							return count > 0 ? out : null;
						})
						.filter(Boolean);
					postView();
					result(message, true, 'Renamed ' + oldName + ' to ' + message.newName + '.');
					break;
				case 'dismissNotice':
					view.notices = view.notices.filter(function (n) {
						return n.key !== message.key;
					});
					// Not now on the check-out question: that one question goes.
					if (view.prompt && view.prompt.key === message.key) view.prompt = null;
					postView();
					break;
				case 'saveSettings':
					var movedOut = view.connection === 'vaultOwnedByOther' && message.vaultRoot !== view.settings.vaultRoot;
					view.settings = { vaultRoot: message.vaultRoot, startAtSignIn: !!message.startAtSignIn, theme: message.theme, badges: view.settings.badges, sharedComputer: view.settings.sharedComputer };
					view.vaultRoot = message.vaultRoot;
					// A folder of the student's own ends the "folder belongs to someone else"
					// stop, as the engine would: the demo goes on as if connected.
					if (movedOut) useState(demo.afterConnect);
					postView();
					break;
				case 'chooseVaultRoot':
					view.settings.vaultRoot = demo.pickedVaultRoot;
					view.vaultRoot = demo.pickedVaultRoot;
					postView();
					break;
				case 'reportProblem':
					result(message, true, 'Sent. Thank you for telling us.');
					break;
				case 'sendFeedback':
					// shot=offer: the picture can't go (it is on another note), and the note can
					// go without it.
					if (message.shot && route.shot === 'offer')
						result(message, false, 'Your note wasn\'t sent: that screenshot is already on another note. You can send it without the picture.', 'withoutPicture');
					else result(message, true, 'Sent. Thank you for the feedback.');
					break;
				case 'captureWindow':
					// In the app this is a picture of the window itself; the demo has a small
					// drawing of one, and says what the app's picture of this size would weigh.
					deliver({
						type: 'windowShot',
						requestId: message.requestId,
						ok: true,
						id: demo.windowShot.id,
						url: demo.windowShot.url,
						width: message.width,
						height: message.height,
						bytes: demo.windowShot.bytes,
						scaled: false,
						message: null
					});
					break;
				case 'readMyFeedback':
					myFeedback(message);
					break;
				case 'turnOnBadges':
					// In the app Windows asks for an administrator's password first; the demo's is given.
					view.settings.badges = { state: 'afterSignIn', line: 'Armory\'s status shows on file icons after you sign out of Windows and back in.' };
					postView();
					result(message, true, view.settings.badges.line);
					break;
				case 'showPicker':
				case 'pickProfile':
				case 'enterPin':
				case 'setPin':
				case 'addProfile':
				case 'forgotPin':
				case 'cancelPicker':
				case 'chooseFolder':
				case 'removeProfile':
				case 'setSharedComputer':
				case 'setPinsRequired':
					handleShared(message);
					break;
				case 'openVault':
				case 'showInFolder':
				case 'addFiles':
				case 'dropFiles':
				case 'openIncidents':
					// In the app these open File Explorer or a file picker, or copy the dropped
					// files in. The demo has nothing to open or copy, so it only says so.
					console.info('Armory demo: ' + message.type, message.path || message.folder || view.vaultRoot, files ? files.length + ' dropped' : '');
					break;
			}
		}

		/** A shared computer's messages, the way the host answers them (demo.shared draws
		 *  each step). A browser sign-in finishes by itself after a few seconds. */
		function handleShared(message) {
			var shared = demo.shared;
			var later = function (fn) {
				clearTimeout(sharedTimer);
				sharedTimer = setTimeout(function () {
					sharedTimer = null;
					fn();
					postView();
				}, 3000);
			};
			if (message.type === 'setSharedComputer') {
				if (message.on) {
					view = view.account ? shared.turnedOn(view) : shared.step(shared.turnedOn(view), 'choose');
					if (!view.account) view.profiles.profiles = [];
				} else {
					view.settings.sharedComputer = false;
					view.profiles = null;
				}
				postView();
				return result(message, true, message.on ? 'This computer is shared now. Other students add themselves with Add a student.' : 'This computer is used by one student now. Nothing in any Armory folder changed.');
			}
			if (!view.profiles) return result(message, false, "This computer isn't set up for several students.");
			var id = message.profileId;
			var ok = true;
			var words = '';
			switch (message.type) {
				case 'showPicker':
					view = shared.picking(view);
					break;
				case 'cancelPicker':
					clearTimeout(sharedTimer);
					view = shared.step(view, 'choose');
					break;
				case 'pickProfile':
					view = shared.pick(view, id);
					break;
				case 'enterPin':
					var tried = shared.enterPin(view, id, message.pin);
					view = tried.view;
					ok = tried.ok;
					break;
				case 'setPin':
					if (/^(\d)\1{3}$/.test(message.pin) || '0123456789'.indexOf(message.pin) >= 0 || '9876543210'.indexOf(message.pin) >= 0) {
						view.profiles.step.message = "Pick a PIN that's harder to guess than 1234.";
						ok = false;
					} else view = shared.use(view, id);
					break;
				case 'addProfile':
					view = shared.step(view, 'adding', { connectPhase: 'waitingForBrowser' });
					later(function () {
						view = shared.added(view);
					});
					break;
				case 'forgotPin':
					var who = view.profiles.profiles.filter(function (t) {
						return t.id === id;
					})[0];
					view = shared.step(view, 'signInAgain', { profileId: id, connectPhase: 'waitingForBrowser', message: who ? 'Sign in as ' + who.email + ' to choose a new PIN.' : null });
					later(function () {
						view = shared.step(view, 'newPin', { profileId: id });
					});
					break;
				case 'chooseFolder':
					if (message.choice === 'own') {
						view = shared.use(view, id, shared.ownFolder);
						view.profiles.note = "You're in your own folder, " + shared.ownFolder + ", while " + (view.profiles.step.ownerName || 'Alex Kim').split(' ')[0] + "'s work waits in " + view.profiles.sharedFolder + '.';
					} else view = shared.step(view, 'choose');
					break;
				case 'removeProfile':
					var gone = shared.remove(view, id);
					view = gone.view;
					words = gone.name + ' was removed from this computer. No files were deleted.';
					break;
				case 'setPinsRequired':
					view.profiles.pinsRequired = !!message.on;
					view.profiles.pinsNote = 'Turned ' + (message.on ? 'on' : 'off') + ' by ' + (view.account ? view.account.email.split('@')[0] : 'a mentor') + ' on Oct 1.';
					words = message.on ? 'Each student types their PIN when they switch on this computer.' : 'PINs are off on this computer. Picking a name switches at once.';
					break;
			}
			postView();
			if (ACTIONS.indexOf(message.type) >= 0) result(message, ok, words);
		}

		var script = document.createElement('script');
		script.src = 'demo/states.js';
		script.onload = function () {
			demo = window.ArmoryDemoStates;
			if (!demo.states[stateName]) stateName = demo.defaultState;
			route.state = stateName;
			var s = demo.states[stateName];
			if (!route.screen) route.screen = s.screens[0];
			if (route.screen === 'detail' && !route.fileId) route.fileId = s.detailFileId || firstFileId(s.view);
			useState(stateName);
			var queued = waiting;
			waiting = null;
			queued.forEach(function (q) {
				handle(q[0], q[1]);
			});
		};
		document.head.appendChild(script);

		if (window.matchMedia) {
			window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', function () {
				if (view && view.settings.theme === 'system') postView();
			});
		}

		return {
			post: function (message, files) {
				if (waiting) waiting.push([message, files]);
				else handle(message, files);
			},
			now: function () {
				return demo ? Date.parse(demo.now) : Date.now();
			},
			route: route
		};
	}

	var transport = webview ? webviewTransport() : demoTransport();

	/* ------------------------------------------------------------ Public API */

	window.ArmoryBridge = Object.freeze({
		PAGE_TO_HOST: Object.freeze(PAGE_TO_HOST.slice()),
		HOST_TO_PAGE: Object.freeze(HOST_TO_PAGE.slice()),
		ACTIONS: Object.freeze(ACTIONS.slice()),
		ASKS: Object.freeze(ASKS.slice()),

		/** True when a demo transport answers instead of the engine. */
		isDemo: !webview,

		/**
		 * Sends one message to the host.
		 * @param {PageMessageType} type
		 * @param {Record<string, unknown>} [fields]
		 * @returns {string | null} the requestId an action's actionResult (or an ask's own answer) will carry
		 */
		send: function (type, fields) {
			var message = buildMessage(type, fields);
			transport.post(message, null);
			return message.requestId || null;
		},

		/**
		 * Sends one message with the files dropped on the window (WebView2's
		 * postMessageWithAdditionalObjects; the host reads each CoreWebView2File.Path).
		 * @param {PageMessageType} type
		 * @param {Record<string, unknown>} fields
		 * @param {ArrayLike<File>} files
		 * @returns {string | null}
		 */
		sendWithFiles: function (type, fields, files) {
			var message = buildMessage(type, fields);
			transport.post(message, files);
			return message.requestId || null;
		},

		/**
		 * Calls `handler` with every host message (view, fileDetail, activity, actionResult,
		 * windowShot, myFeedback, reveal).
		 * @param {(message: HostMessage) => void} handler
		 * @returns {() => void} stops listening
		 */
		onMessage: function (handler) {
			handlers.push(handler);
			return function () {
				var i = handlers.indexOf(handler);
				if (i >= 0) handlers.splice(i, 1);
			};
		},

		/** Milliseconds since the epoch; the demo's clock is fixed so screenshots hold still. */
		now: function () {
			return transport.now();
		},

		/**
		 * Where the demo asks the page to start (null inside WebView2). Read it after the
		 * first view arrives.
		 * @returns {DemoRoute | null}
		 */
		route: function () {
			return transport.route;
		}
	});
})();
