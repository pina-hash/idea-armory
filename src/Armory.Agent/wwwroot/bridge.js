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
 * the host's PostWebMessageAsJson messages as parsed objects.
 *
 * Outside WebView2 (a plain browser, the screenshot tools) it loads demo/states.js and a
 * demo transport answers instead, from the query string:
 *   ?state=<name>&theme=idea|spaceWhite&screen=home|detail|connect|settings&file=<fileId>
 * (theme=space-white is accepted too). Nothing here touches the network.
 */
(function () {
	'use strict';

	/* ---------------------------------------------------------------- Types */

	/**
	 * @typedef {'signedOut' | 'connecting' | 'signedIn' | 'vaultOwnedByOther'} Connection
	 * @typedef {'idle' | 'waitingForBrowser' | 'finishing' | 'failed'} ConnectPhase
	 * @typedef {'synced' | 'syncing' | 'offline' | 'paused' | 'attention'} SyncState
	 * @typedef {'synced' | 'syncing' | 'waitingToSend' | 'editingByMe' | 'editingByOther'
	 *   | 'newerWaiting' | 'conflict' | 'refused' | 'notOnThisComputer'} FileStatus
	 * @typedef {'newerWaiting' | 'sideVersion' | 'refused' | 'lockBroken' | 'nameTaken'
	 *   | 'releaseNotChecked'} AttentionKind
	 * @typedef {'system' | 'idea' | 'spaceWhite'} ThemeSetting
	 * @typedef {'idea' | 'spaceWhite'} EffectiveTheme
	 */

	/**
	 * @typedef {object} ConnectInfo
	 * @property {ConnectPhase} phase
	 * @property {string | null} message
	 */

	/**
	 * @typedef {object} Account
	 * @property {string} email
	 * @property {string} deviceName
	 */

	/**
	 * @typedef {object} SyncInfo
	 * @property {SyncState} state
	 * @property {string} line           Plain student sentence, e.g. "Everything is saved to Armory."
	 * @property {string | null} detail  e.g. "Last checked 2 minutes ago"
	 * @property {number} pendingCount
	 */

	/**
	 * @typedef {object} MyFile
	 * @property {string | null} fileId  null until the server has the file
	 * @property {string} path           vault-relative, forward slashes
	 * @property {string} name
	 * @property {string} project
	 * @property {FileStatus} status
	 * @property {string | null} note
	 */

	/**
	 * @typedef {object} Attention
	 * @property {AttentionKind} kind
	 * @property {string | null} fileId
	 * @property {string} path
	 * @property {string} name
	 * @property {string} title
	 * @property {string} detail
	 * @property {string | null} at      ISO-8601
	 */

	/**
	 * @typedef {object} Holder
	 * @property {string} name
	 * @property {string} email
	 * @property {string} device
	 * @property {string} since          ISO-8601
	 * @property {boolean} isMe
	 * @property {boolean} isMyOtherComputer
	 * @property {boolean} savedToArmory
	 */

	/**
	 * @typedef {object} FileRow
	 * @property {string} fileId
	 * @property {string} name
	 * @property {string} path
	 * @property {FileStatus} status
	 * @property {Holder | null} holder
	 * @property {boolean} releaseNotChecked
	 * @property {string | null} updatedAt
	 * @property {string | null} updatedBy
	 */

	/**
	 * @typedef {object} Folder
	 * @property {string} path           "" for the project's root folder
	 * @property {string} name
	 * @property {FileRow[]} files
	 */

	/**
	 * @typedef {object} Project
	 * @property {string} id
	 * @property {string} name
	 * @property {Folder[]} folders
	 */

	/**
	 * @typedef {object} Settings
	 * @property {string} vaultRoot
	 * @property {boolean} startAtSignIn
	 * @property {ThemeSetting} theme
	 */

	/**
	 * @typedef {object} AgentView
	 * @property {Connection} connection
	 * @property {ConnectInfo} connect
	 * @property {Account | null} account
	 * @property {SyncInfo} sync
	 * @property {string} vaultRoot
	 * @property {MyFile[]} myFiles
	 * @property {Attention[]} needsMe
	 * @property {Project[]} projects
	 * @property {Settings} settings
	 * @property {EffectiveTheme} effectiveTheme
	 */

	/**
	 * @typedef {object} HistoryEntry
	 * @property {string} id
	 * @property {'version' | 'sideVersion'} kind
	 * @property {string} author
	 * @property {string} at             ISO-8601
	 * @property {number} bytes
	 * @property {string} note
	 * @property {boolean} releaseNotChecked
	 * @property {boolean} isCurrent
	 */

	/**
	 * @typedef {object} FileDetail
	 * @property {string} fileId
	 * @property {string} name
	 * @property {string} path
	 * @property {string} project
	 * @property {string} folder
	 * @property {FileStatus} status
	 * @property {Holder | null} holder
	 * @property {boolean} releaseNotChecked
	 * @property {HistoryEntry[]} history  newest first
	 */

	/**
	 * Host to page.
	 * @typedef {{ type: 'view', view: AgentView } | { type: 'fileDetail', detail: FileDetail }} HostMessage
	 */

	/**
	 * Page to host. Fields per type:
	 *   ready, connect, cancelConnect, signOut, pause, resume, openVault, chooseVaultRoot: none
	 *   openFile: { fileId }
	 *   showInFolder: { path }
	 *   saveSettings: { vaultRoot, startAtSignIn, theme }
	 * @typedef {'ready' | 'connect' | 'cancelConnect' | 'signOut' | 'pause' | 'resume'
	 *   | 'openVault' | 'openFile' | 'showInFolder' | 'saveSettings' | 'chooseVaultRoot'} PageMessageType
	 */

	/**
	 * Where the demo asks the page to start. Null inside WebView2.
	 * @typedef {object} DemoRoute
	 * @property {'home' | 'detail' | 'connect' | 'settings'} screen
	 * @property {string | null} fileId
	 * @property {string} state
	 */

	/* ------------------------------------------------------- Message lists */

	/** Page to host message types (BRIDGE.md, "Page to host"). */
	var PAGE_TO_HOST = ['ready', 'connect', 'cancelConnect', 'signOut', 'pause', 'resume', 'openVault', 'openFile', 'showInFolder', 'saveSettings', 'chooseVaultRoot'];

	/** Host to page message types (BRIDGE.md, "Host to page"). */
	var HOST_TO_PAGE = ['view', 'fileDetail'];

	/** Required fields per page-to-host type. */
	var REQUIRED = {
		openFile: ['fileId'],
		showInFolder: ['path'],
		saveSettings: ['vaultRoot', 'startAtSignIn', 'theme']
	};

	/* ----------------------------------------------------------- Plumbing */

	/** @type {Array<(message: HostMessage) => void>} */
	var handlers = [];

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
			post: function (message) {
				webview.postMessage(message);
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
			state: stateName
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

		/** Answers one page message the way the engine would. */
		function handle(message) {
			switch (message.type) {
				case 'ready':
					postView();
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
					clearTimers();
					useState('signedOut');
					postView();
					break;
				case 'signOut':
					clearTimers();
					useState('signedOut');
					postView();
					break;
				case 'pause':
					if (view.sync.state !== 'paused') {
						pausedFrom = clone(view.sync);
						view.sync = {
							state: 'paused',
							line: 'Paused. Nothing is sent or received until you resume.',
							detail: view.sync.pendingCount ? view.sync.pendingCount + ' changes are waiting on this computer.' : null,
							pendingCount: view.sync.pendingCount
						};
					}
					postView();
					break;
				case 'resume':
					view.sync = pausedFrom || clone(demo.states.synced.view.sync);
					pausedFrom = null;
					postView();
					break;
				case 'openFile':
					deliver({ type: 'fileDetail', detail: demo.detailFor(stateName, message.fileId) });
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
				case 'openVault':
				case 'showInFolder':
					// In the app these open File Explorer. The demo has nothing to open.
					console.info('Armory demo: ' + message.type, message.path || view.vaultRoot);
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
			queued.forEach(handle);
		};
		document.head.appendChild(script);

		if (window.matchMedia) {
			window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', function () {
				if (view && view.settings.theme === 'system') postView();
			});
		}

		return {
			post: function (message) {
				if (waiting) waiting.push(message);
				else handle(message);
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

		/** True when a demo transport answers instead of the engine. */
		isDemo: !webview,

		/**
		 * Sends one message to the host.
		 * @param {PageMessageType} type
		 * @param {Record<string, unknown>} [fields]
		 */
		send: function (type, fields) {
			transport.post(buildMessage(type, fields));
		},

		/**
		 * Calls `handler` with every host message ({type: 'view'} or {type: 'fileDetail'}).
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
