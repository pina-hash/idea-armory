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
 *   dialog=newFolder|renameFolder|deleteFolder|checkOutAll|takeBack|forceAll|renameFile|report
 *     (renameFile asks about the first file in the open notice list; forceAll is Force check
 *     in all for the open folder; report is Report a problem)
 *   press=<control key> (the page presses that key once it is drawn, and the demo holds every
 *     answer, so what a press shows while it waits stays in view)
 *   drag=1 (files held over the list)
 *   at=browser (Home scrolled so the team's files are in view)
 *   result=<words> (the demo answers as if an action had just come back with these words;
 *     resultOk=0 makes it a refusal)
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
	 *   | 'keptCopy' | 'notInArmory' | 'notOnThisComputer'} FileStatus
	 * @typedef {'available' | 'mine' | 'other' | 'myOtherComputer'} CheckoutState
	 * @typedef {'upload' | 'download' | 'move'} Direction
	 * @typedef {'info' | 'look' | 'bad'} NoticeTone
	 * @typedef {'import' | 'nameShared' | 'newerWaiting' | 'keptCopy' | 'takenBack' | 'folderPutBack'
	 *   | 'projectPutBack' | 'projectRenaming' | 'projectDeleted' | 'cantSend' | 'cantRead' | 'checkInPartial'} NoticeKind
	 * @typedef {'version' | 'keptCopy' | 'removed'} HistoryKind
	 * @typedef {'system' | 'idea' | 'spaceWhite'} ThemeSetting
	 * @typedef {'idea' | 'spaceWhite'} EffectiveTheme
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
	 */

	/**
	 * @typedef {object} SettingsView
	 * @property {string} vaultRoot
	 * @property {boolean} startAtSignIn
	 * @property {ThemeSetting} theme
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
	 */

	/**
	 * Host to page.
	 * @typedef {{ type: 'view', view: AgentView } | { type: 'fileDetail', detail: FileDetailView } | { type: 'activity', activity: ActivityView } | { type: 'actionResult', requestId: string, ok: boolean, message: string }} HostMessage
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
	 *   createFolder: { projectId, parent, name }    renameFolder: { projectId, folder, newName }
	 *   deleteFolder: { projectId, folder }   renameFile: { path, newName }   (one file, in its folder)
	 *   addFiles: { projectId, folder }
	 *   dropFiles: { projectId, folder }      (sent with the dropped File objects)
	 *   dismissNotice: { key }               (a notice card's key, or the check-out question's)
	 *   saveSettings: { vaultRoot, startAtSignIn, theme }
	 *   reportProblem: { kind, body }         (kind: bug, idea or other; the host saves it with a
	 *                                          fresh incident and answers in one sentence)
	 *   sendFeedback: { kind, body }          (kind: bug, idea or other; a note on its own, no
	 *                                          incident: armory_submit_app_feedback, one sentence back)
	 *   openIncidents: none                   (opens the incidents folder in File Explorer)
	 * @typedef {'ready' | 'connect' | 'cancelConnect' | 'signOut' | 'pause' | 'resume'
	 *   | 'openVault' | 'openFile' | 'launchFile' | 'showInFolder' | 'checkOut' | 'checkIn'
	 *   | 'undoCheckOut' | 'takeBack' | 'createFolder' | 'renameFolder' | 'deleteFolder' | 'renameFile'
	 *   | 'addFiles' | 'dropFiles' | 'dismissNotice' | 'saveSettings' | 'chooseVaultRoot'
	 *   | 'reportProblem' | 'openIncidents' | 'sendFeedback'} PageMessageType
	 */

	/**
	 * Where the demo asks the page to start. Null inside WebView2.
	 * @typedef {object} DemoRoute
	 * @property {'home' | 'detail' | 'connect' | 'settings'} screen
	 * @property {string | null} fileId
	 * @property {string} state
	 * @property {string | null} project   a project id
	 * @property {string | null} folder    a folder path in that project
	 * @property {string[]} select         file names in that folder
	 * @property {string | null} expand    a notice key
	 * @property {string | null} dialog    newFolder, renameFolder, deleteFolder, checkOutAll, takeBack, forceAll, renameFile, report or feedback
	 * @property {boolean} drag
	 * @property {string | null} at        a part of Home to scroll into view: browser
	 * @property {string | null} press     a control key the page presses once it is drawn
	 */

	/* ------------------------------------------------------- Message lists */

	/** Page to host message types (BRIDGE.md, "Page to host"). */
	var PAGE_TO_HOST = ['ready', 'connect', 'cancelConnect', 'signOut', 'pause', 'resume', 'openVault', 'openFile', 'launchFile', 'showInFolder', 'checkOut', 'checkIn', 'undoCheckOut', 'takeBack', 'createFolder', 'renameFolder', 'deleteFolder', 'renameFile', 'addFiles', 'dropFiles', 'dismissNotice', 'saveSettings', 'chooseVaultRoot', 'reportProblem', 'openIncidents', 'sendFeedback'];

	/** Host to page message types (BRIDGE.md, "Host to page"). */
	var HOST_TO_PAGE = ['view', 'fileDetail', 'activity', 'actionResult'];

	/** Required fields per page-to-host type, as the host's message records name them. */
	var REQUIRED = {
		openFile: ['fileId'],
		launchFile: ['path'],
		showInFolder: ['path'],
		checkOut: ['paths', 'open'],
		checkIn: ['paths'],
		undoCheckOut: ['paths'],
		takeBack: ['fileId'],
		createFolder: ['projectId', 'parent', 'name'],
		renameFolder: ['projectId', 'folder', 'newName'],
		deleteFolder: ['projectId', 'folder'],
		renameFile: ['path', 'newName'],
		addFiles: ['projectId', 'folder'],
		dropFiles: ['projectId', 'folder'],
		dismissNotice: ['key'],
		saveSettings: ['vaultRoot', 'startAtSignIn', 'theme'],
		reportProblem: ['kind', 'body'],
		sendFeedback: ['kind', 'body']
	};

	/** Actions: each carries a requestId, and the host answers it with one actionResult. */
	var ACTIONS = ['launchFile', 'checkOut', 'checkIn', 'undoCheckOut', 'takeBack', 'createFolder', 'renameFolder', 'deleteFolder', 'renameFile', 'addFiles', 'dropFiles', 'reportProblem', 'sendFeedback'];

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
		if (ACTIONS.indexOf(type) >= 0) message.requestId = fields && fields.requestId ? String(fields.requestId) : 'r' + nextRequest++;
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
			press: params.get('press')
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
		function result(message, ok, words) {
			// A state drawn with a key just pressed keeps waiting for its answer.
			if (route.press) return;
			deliver({ type: 'actionResult', requestId: message.requestId, ok: ok, message: words });
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
					if (params.get('result')) deliver({ type: 'actionResult', requestId: 'r0', ok: params.get('resultOk') !== '0', message: params.get('result') });
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
					view.settings = { vaultRoot: message.vaultRoot, startAtSignIn: !!message.startAtSignIn, theme: message.theme };
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
					result(message, true, 'Sent. Thank you for the feedback.');
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

		/** True when a demo transport answers instead of the engine. */
		isDemo: !webview,

		/**
		 * Sends one message to the host.
		 * @param {PageMessageType} type
		 * @param {Record<string, unknown>} [fields]
		 * @returns {string | null} the requestId an action's actionResult will answer
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
		 * Calls `handler` with every host message (view, fileDetail, activity, actionResult).
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
