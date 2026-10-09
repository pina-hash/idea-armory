/*
 * app.js: the Armory Agent window.
 *
 * Renders everything from the AgentView the host sends (docs/agent/BRIDGE.md) and talks
 * back only through window.ArmoryBridge. Three screens: Connect, Home (the status, what
 * is moving right now, notices, My files and the team's files to browse) and File
 * detail, plus the Settings sheet and one small dialog for folder questions. The words
 * are for students who have never used a shared CAD folder: plain sentences, no jargon.
 *
 * Long lists (a folder of 5,000 files) are drawn a screenful at a time: rows have one
 * fixed height, only the rows near the view (and the focused one) are in the page, and
 * spacers whose heights are set through CSSOM stand in for the rest. A host 'activity'
 * message (four times a second while files move) patches only the activity panel and
 * the status line, so focus, typing and scroll never jump.
 */
(function () {
	'use strict';

	var bridge = window.ArmoryBridge;
	var main = document.getElementById('main');
	var headerKeys = document.getElementById('header-keys');
	var scroller = document.getElementById('scroller');
	var sheet = document.getElementById('settings');
	var ask = document.getElementById('ask');
	var windowCue = document.getElementById('window-cue');
	var resultBox = document.getElementById('result');
	var resultWord = document.getElementById('result-word');

	/** Rows drawn beyond each edge of the view in a long list. */
	var OVERSCAN = 30;

	/** Page state the host does not own. */
	var ui = {
		/** @type {import('./bridge.js').AgentView | null} */ view: null,
		themeWanted: null, // a theme just picked, worn until a view carries it
		index: null, // lookups built once per view
		screen: 'home', // 'home' | 'detail' (Connect is chosen by the view)
		/** @type {any} */ detail: null,
		projectId: null,
		folders: {}, // project id -> the folder open in the browser ("" for its top)
		selected: {}, // vault-relative path -> true, files picked in the open folder
		anchor: null, // the last file picked, for a Shift range
		expanded: {}, // notice key -> its list is open
		promptGone: null, // the prompt the student answered with Not now
		returnKey: null, // the control that opened detail, so Back can put focus there again
		homeScroll: 0,
		homeRecess: 0, // the recessed column's own scroll, in a wide window
		vrange: {}, // list id -> the rows it drew last, so a redraw starts there
		active: {}, // list id -> the row Tab enters the list on
		pin: [], // control keys whose rows must stay drawn through a redraw
		ask: null, // the open question in the small dialog
		routeDialog: null, // a demo dialog to open once its file's detail has arrived
		follow: null, // a folder renamed from here: the browser follows it to its new name
		drag: false,
		resultTimer: 0,
		pending: {}, // requestId -> an action the host has not answered yet (see Working)
		myFeedback: null, // the host's last answer about Your feedback (see Send feedback)
		mineAsked: 0, // when Your feedback was last asked for
		shooting: null, // while the host takes a picture of the window: what was masked, what waits
		routeShot: null, // a demo route's picture to take (1) or to take and send (offer)
		waits: {}, // answers a demo route waits for before the page is ready
		readyWanted: false,
		routed: false,
		ready: false,
		waitingForDetail: false
	};

	/* ------------------------------------------------------------- Words */

	/*
	 * ONE SEVERITY SCALE, everywhere a state is shown (the status display, notices, the
	 * chips, File detail): ok is green, look is amber, bad (blocked) is red, off is gray.
	 * The word always carries the meaning; the color only adds to it.
	 */
	var LAMP = { ok: 'green', look: 'amber', bad: 'red', off: '' };

	/** A notice's tone on the page's scale: news that is not a problem reads green. */
	var NOTICE_TONE = { info: 'ok', look: 'look', bad: 'bad' };

	/** Status chips. A file that is just up to date has none; its line says enough. A file
	 *  waiting to upload has none either: how many wait is said once, in Right now (a
	 *  changed one still says Changed). */
	var STATUS = {
		synced: null,
		changed: { chip: 'Changed', tone: 'look' },
		uploading: { chip: 'Uploading', tone: 'ok' },
		downloading: { chip: 'Downloading', tone: 'ok' },
		waiting: null,
		newerWaiting: { chip: 'Newer version waiting', tone: 'look' },
		keptCopy: { chip: 'Your copy kept', tone: 'look' },
		notInArmory: { chip: 'Not in Armory', tone: 'off' },
		notOnThisComputer: { chip: 'Not here yet', tone: 'off' }
	};

	/** A notice card's glyph, by kind. */
	var NOTICE_GLYPH = {
		import: 'import',
		nameShared: 'names',
		newerWaiting: 'newer',
		keptCopy: 'copy',
		takenBack: 'takeback',
		folderPutBack: 'folder-back',
		projectPutBack: 'folder-back',
		projectRenaming: 'rename',
		projectDeleted: 'trash',
		cantSend: 'cant',
		cantRead: 'cant',
		checkInPartial: 'person'
	};

	var SYNC = {
		synced: { readout: 'All saved', tone: 'ok' },
		syncing: { readout: 'Updating', tone: 'ok' },
		offline: { readout: 'Offline', tone: 'look' },
		paused: { readout: 'Paused', tone: 'off' },
		attention: { readout: 'Needs you', tone: 'look' }
	};

	var DIRECTION = {
		upload: { word: 'Uploading', glyph: 'up' },
		download: { word: 'Downloading', glyph: 'down' },
		move: { word: 'Moving', glyph: 'move' }
	};

	var KIND_WORD = { part: 'Part', asm: 'Assembly', drw: 'Drawing', file: 'File' };

	/** Files whose team version is the one on this computer. */
	var UP_TO_DATE = { synced: true, changed: true, uploading: true, waiting: true, keptCopy: true };

	/** Characters Windows never allows in a folder name. */
	var BAD_NAME = /[\\/:*?"<>|]/;

	/** A file nobody has checked out. */
	var AVAILABLE = { state: 'available', label: 'Available', name: null, email: null, device: null, since: null };

	/* ----------------------------------------------------------- Helpers */

	function esc(s) {
		return String(s == null ? '' : s)
			.replace(/&/g, '&amp;')
			.replace(/</g, '&lt;')
			.replace(/>/g, '&gt;')
			.replace(/"/g, '&quot;')
			.replace(/'/g, '&#39;');
	}

	function icon(name, cls) {
		return '<svg class="' + (cls || 'icon') + '" aria-hidden="true" focusable="false"><use href="#i-' + name + '"/></svg>';
	}

	/** 1,280 with a thousands separator. */
	function num(n) {
		return Number(n || 0).toLocaleString('en-US');
	}

	function plural(n, one, many) {
		return num(n) + ' ' + (n === 1 ? one : many);
	}

	function clamp(v, lo, hi) {
		return Math.max(lo, Math.min(hi, v));
	}

	/** "just now", "5 minutes ago", "2 hours ago", "yesterday", "3 days ago". */
	function ago(iso) {
		if (!iso) return '';
		var s = Math.max(0, (bridge.now() - Date.parse(iso)) / 1000);
		if (s < 45) return 'just now';
		var m = Math.round(s / 60);
		if (m < 60) return plural(m, 'minute', 'minutes') + ' ago';
		var h = Math.round(m / 60);
		if (h < 24) return plural(h, 'hour', 'hours') + ' ago';
		var d = Math.round(h / 24);
		if (d === 1) return 'yesterday';
		if (d < 14) return d + ' days ago';
		var w = Math.round(d / 7);
		if (w < 9) return w + ' weeks ago';
		return plural(Math.round(d / 30), 'month', 'months') + ' ago';
	}

	/** Keeps a number with the word after it ("1 thing", "20 minutes"), so a line never
	 *  ends on a bare number. */
	function glue(s) {
		return String(s == null ? '' : s).replace(/(\d)\s+(?=\S)/g, '$1\u00a0');
	}

	/** A relative time that never breaks across lines ("3\u00a0weeks\u00a0ago"). */
	function agoWhole(iso) {
		return ago(iso).replace(/ /g, '\u00a0');
	}

	/** Meta facts joined by dots. Each dot travels with the fact after it, so a wrapped
	 *  line starts "· 20 minutes ago" instead of ending on a lone dot. */
	function metaLine(parts) {
		return parts.filter(Boolean).join(' ·\u00a0');
	}

	/** "25 minutes", "2 hours", "3 days": how long something has been going on. */
	function lasting(iso) {
		var s = Math.max(0, (bridge.now() - Date.parse(iso)) / 1000);
		var m = Math.max(1, Math.round(s / 60));
		if (m < 60) return plural(m, 'minute', 'minutes');
		var h = Math.round(m / 60);
		if (h < 24) return plural(h, 'hour', 'hours');
		return plural(Math.round(h / 24), 'day', 'days');
	}

	/** A full local time for a tooltip. */
	function fullTime(iso) {
		try {
			return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
		} catch (e) {
			return iso;
		}
	}

	/** "612 KB", "2.1 GB", "48 MB": one decimal, dropped when it is zero. */
	function bytes(b) {
		b = Math.max(0, Math.round(b || 0));
		if (b < 1024) return plural(b, 'byte', 'bytes');
		var units = ['KB', 'MB', 'GB', 'TB'];
		var v = b / 1024;
		var u = 0;
		while (v >= 1024 && u < units.length - 1) {
			v /= 1024;
			u++;
		}
		var t = v.toFixed(1);
		if (/\.0$/.test(t)) t = t.slice(0, -2);
		return t + ' ' + units[u];
	}

	/** "about 3 min", "about 20 sec", "less than a minute". */
	function timeLeft(seconds) {
		if (seconds == null) return '';
		if (seconds >= 90) return 'about ' + Math.round(seconds / 60) + ' min';
		if (seconds >= 55) return 'about 1 min';
		if (seconds >= 10) return 'about ' + Math.round(seconds / 5) * 5 + ' sec';
		return 'less than a minute';
	}

	/** "Robot 2027 › Drivetrain" for "Robot 2027/Drivetrain/Gearbox.SLDASM". The path is
	 *  one unbreakable piece (no-break spaces throughout), so a narrow line ends it with an
	 *  ellipsis and never splits it. */
	function whereIs(path) {
		var parts = String(path || '').split('/');
		parts.pop();
		return parts
			.map(function (p) {
				return p.replace(/ /g, '\u00a0');
			})
			.join('\u00a0\u203a\u00a0');
	}

	function firstName(name) {
		var n = String(name || '').trim();
		if (/^(Mr|Mrs|Ms|Dr|Mx)\.?\s/i.test(n)) return n;
		return n.split(/\s+/)[0] || n;
	}

	/** "ML" for "Maria Lopez", "P" for "Mr. Pina". */
	function initials(name) {
		var words = String(name || '')
			.replace(/^(Mr|Mrs|Ms|Dr|Mx)\.?\s+/i, '')
			.split(/\s+/)
			.filter(Boolean);
		if (!words.length) return '?';
		var a = words[0].charAt(0);
		var b = words.length > 1 ? words[words.length - 1].charAt(0) : '';
		return (a + b).toUpperCase();
	}

	/** "Alex Kim" for "alex.kim@boscotech.edu". */
	function nameFromEmail(email) {
		var local = String(email || '').split('@')[0];
		return local
			.split(/[._-]+/)
			.filter(Boolean)
			.map(function (w) {
				return w.charAt(0).toUpperCase() + w.slice(1);
			})
			.join(' ');
	}

	/** The signed-in student's name, as the view knows it. */
	function myName(v) {
		var mine = (v.myFiles || []).filter(function (f) {
			return checkoutOf(f).state === 'mine' && f.checkout.name;
		})[0];
		if (mine) return mine.checkout.name;
		return v && v.account ? nameFromEmail(v.account.email) : '';
	}

	/** A row's check out, never missing: a row without one reads as Available, so a view
	 *  that leaves it out still draws (and the contract test says what is missing). */
	function checkoutOf(r) {
		var c = r && r.checkout;
		return c && c.state ? c : AVAILABLE;
	}

	function kindOf(name) {
		var ext = String(name).split('.').pop().toUpperCase();
		if (ext === 'SLDPRT') return 'part';
		if (ext === 'SLDASM') return 'asm';
		if (ext === 'SLDDRW') return 'drw';
		return 'file';
	}

	function chip(words, tone, extra) {
		var lamp = LAMP[tone] || '';
		return '<span class="chip' + (lamp ? ' lamp ' + lamp : '') + (extra ? ' ' + extra : '') + '">' + esc(words) + '</span>';
	}

	/** What kind of file it is, in words a student knows: Part, Assembly, Drawing. */
	function kindChip(name) {
		return '<span class="chip kind">' + esc(KIND_WORD[kindOf(name)]) + '</span>';
	}

	/** Who has it checked out, always shown: their initials in a disc and the engine's
	 *  own words ("Checked out by Maria Lopez on LAB-PC-07"), green when it is you here and
	 *  amber when it is someone else or your other computer, or plain "Available" with no
	 *  chip. When the line is short of room the computer's name gives way first (it ends in
	 *  an ellipsis), then the person's; the whole label is in the tooltip. */
	function checkoutMark(c) {
		c = c || AVAILABLE;
		if (c.state === 'available') return '<span class="row-avail">' + esc(c.label || 'Available') + '</span>';
		var tone = c.state === 'mine' ? 'ok' : 'look';
		var label = String(c.label || '');
		var tail = c.device ? ' on ' + c.device : '';
		var head = tail && label.slice(-tail.length) === tail ? label.slice(0, -tail.length) : label;
		if (head === label) tail = '';
		return (
			'<span class="chip who" data-tone="' + tone + '" title="' + esc(label) + '"><span class="avatar" aria-hidden="true">' + esc(initials(c.name)) + '</span>' +
			'<span class="who-word"><span class="who-head">' + esc(head) + '</span>' + (tail ? '<span class="who-tail">' + esc(tail) + '</span>' : '') + '</span></span>'
		);
	}

	function statusChip(status, changed) {
		var s = STATUS[status];
		if (!s && changed) s = STATUS.changed;
		return s ? chip(s.chip, s.tone) : '';
	}

	/** "2 things", "1 file": a count in words, for the right end of a label row. */
	function count(n, one, many) {
		return '<span class="count">' + esc(plural(n, one, many)) + '</span>';
	}

	/** Attribute-safe text for a querySelector value. */
	function sel(v) {
		return String(v).replace(/["\\]/g, '\\$&');
	}

	/** A title bar: spaced mono caps between two hatched rails (the site's engraved
	 *  title). `lead` and `end` are optional keys at its two ends. */
	function titleBar(tag, text, attrs, lead, end) {
		return (
			'<div class="title-bar' + (lead || end ? ' has-keys' : '') + '">' +
			'<span class="tb-lead">' + (lead || '') + '</span>' +
			'<' + tag + ' class="plate-title"' + (attrs || '') + '>' + esc(text) + '</' + tag + '>' +
			'<span class="tb-end">' + (end || '') + '</span>' +
			'</div>'
		);
	}

	/** A key with a glyph and its word; the word hides in a narrow window (`tight`), and
	 *  stays for screen readers and in the tooltip. */
	function key(o) {
		var busy = o.key && busyKey(o.key);
		return (
			'<button class="key' + (o.cls ? ' ' + o.cls : '') + '" type="button" data-action="' + o.action + '"' +
			(o.key ? ' data-key="' + esc(o.key) + '"' : '') +
			(busy ? busyAttrs(o.key) : '') +
			(o.path != null ? ' data-path="' + esc(o.path) + '"' : '') +
			(o.fileId ? ' data-file-id="' + esc(o.fileId) + '"' : '') +
			(o.extra || '') +
			(o.title ? ' title="' + esc(o.title) + '"' : '') +
			(o.label ? ' aria-label="' + esc(o.label) + '"' : '') +
			(o.disabled ? ' disabled' : '') +
			'>' + (busy ? spinHtml() : '') + (o.glyph ? icon(o.glyph) : '') + '<span class="key-word">' + esc(o.word) + '</span></button>'
		);
	}

	/* ------------------------------------------------------- View lookups */

	// One collator for every sort: "Part-2" before "Part-10", case ignored.
	var collator = typeof Intl !== 'undefined' ? new Intl.Collator('en', { sensitivity: 'base', numeric: true }) : null;
	function byName(a, b) {
		return collator ? collator.compare(String(a), String(b)) : String(a).localeCompare(String(b));
	}

	/** Everything the page looks up more than once, built once per view: rows by file id
	 *  and by path, and each project's folder tree with the files under every folder. */
	function buildIndex(v) {
		var byId = {};
		var byPath = {};
		var named = {}; // "<project folder>\n<lowercased name>" -> a file in Armory with that name
		var projects = {};
		(v.projects || []).forEach(function (p) {
			var folders = {};
			var children = {};
			var root = null;
			(p.folders || []).forEach(function (f) {
				folders[f.path] = f;
				(f.files || []).forEach(function (r) {
					var hit = { row: r, project: p, folder: f };
					if (r.fileId) byId[r.fileId] = hit;
					byPath[r.path] = hit;
					if (!root) root = r.path.split('/')[0];
					var nameKey = r.path.split('/')[0] + '\n' + String(r.name).toLowerCase();
					if (r.fileId && !named[nameKey]) named[nameKey] = hit;
				});
			});
			if (!folders['']) folders[''] = { path: '', name: p.name, fileCount: 0, files: [] };
			// Every folder's parents are folders too, even when the host lists only the leaves.
			Object.keys(folders).forEach(function (path) {
				var parts = path ? path.split('/') : [];
				for (var i = parts.length; i > 0; i--) {
					var self = parts.slice(0, i).join('/');
					var parent = parts.slice(0, i - 1).join('/');
					if (!folders[self]) folders[self] = { path: self, name: parts[i - 1], fileCount: 0, files: [] };
					(children[parent] = children[parent] || {})[self] = true;
				}
			});
			var under = {};
			var subfolders = {};
			Object.keys(folders).forEach(function (path) {
				var parts = path ? path.split('/') : [];
				for (var i = parts.length; i >= 0; i--) {
					var a = parts.slice(0, i).join('/');
					under[a] = (under[a] || 0) + (folders[path].files || []).length;
					if (i < parts.length) subfolders[a] = (subfolders[a] || 0) + 1;
				}
			});
			var kids = {};
			Object.keys(children).forEach(function (k) {
				kids[k] = Object.keys(children[k]).sort(function (a, b) {
					return byName(folders[a].name, folders[b].name);
				});
			});
			projects[p.id] = { project: p, folders: folders, children: kids, under: under, subfolders: subfolders, root: root || p.name };
		});
		return { byId: byId, byPath: byPath, byName: named, projects: projects };
	}

	/** After a rename sent from here lands, the open folder is the renamed one. */
	function followRename() {
		var f = ui.follow;
		var pi = f ? ui.index.projects[f.projectId] : null;
		if (!pi || !pi.folders[f.to] || pi.folders[f.from]) return;
		var open = ui.folders[f.projectId] || '';
		if (open === f.from || open.indexOf(f.from + '/') === 0) ui.folders[f.projectId] = f.to + open.slice(f.from.length);
		ui.follow = null;
	}

	function findRow(fileId) {
		return ui.index && fileId ? ui.index.byId[fileId] || null : null;
	}

	/** The vault-relative path of a folder in a project ("Robot 2027/Drivetrain"). */
	function folderPathOf(pi, folder) {
		return pi.root + (folder ? '/' + folder : '');
	}

	/** Every row under a folder (the folder's own files and all its subfolders'). */
	function rowsUnder(pi, folder) {
		var out = [];
		Object.keys(pi.folders).forEach(function (path) {
			if (folder === '' || path === folder || path.indexOf(folder + '/') === 0) out = out.concat(pi.folders[path].files || []);
		});
		return out;
	}

	/** The browser's place: which project, which folder in it, and that project's lookups. */
	function browserPlace() {
		var v = ui.view;
		var projects = v.projects || [];
		if (!projects.length) return null;
		var current = projects.filter(function (p) {
			return p.id === ui.projectId;
		})[0] || projects.filter(function (p) {
			return !p.archived;
		})[0] || projects[0];
		ui.projectId = current.id;
		var pi = ui.index.projects[current.id];
		var folder = ui.folders[current.id] || '';
		// A folder that is gone (deleted, or renamed by someone else) leaves the browser at
		// the nearest folder above it that is still there.
		while (folder && !pi.folders[folder]) folder = folder.split('/').slice(0, -1).join('/');
		ui.folders[current.id] = folder;
		return { project: current, pi: pi, folder: folder };
	}

	/* --------------------------------------------------------- Rendering */

	function activeKey() {
		var el = document.activeElement;
		return el && el.getAttribute ? el.getAttribute('data-key') : null;
	}

	function restoreFocus(k) {
		if (!k) return;
		var el = document.querySelector('[data-key="' + sel(k) + '"]');
		if (el && el !== document.activeElement) el.focus({ preventScroll: true });
	}

	/** The theme a choice wears: System follows Windows (the page reads Windows' app mode as
	 *  the browser's color scheme), until the host's view says what it decided. */
	function effectiveOf(theme, fallback) {
		if (theme === 'idea' || theme === 'spaceWhite') return theme;
		if (window.matchMedia) return window.matchMedia('(prefers-color-scheme: light)').matches ? 'spaceWhite' : 'idea';
		return fallback === 'spaceWhite' ? 'spaceWhite' : 'idea';
	}

	function render() {
		var v = ui.view;
		if (!v) return;
		// A theme just picked holds until a view carries it, so an older view never flips it back.
		if (ui.themeWanted && v.settings && v.settings.theme === ui.themeWanted) ui.themeWanted = null;
		document.documentElement.setAttribute('data-theme', ui.themeWanted ? effectiveOf(ui.themeWanted, v.effectiveTheme) : v.effectiveTheme === 'spaceWhite' ? 'spaceWhite' : 'idea');
		var screen = v.connection === 'signedIn' ? ui.screen : 'connect';
		var focus = activeKey();
		// In a wide window Home's lists scroll inside the recessed column, and a new view
		// must not throw the student back to the top of them.
		var keepRecess = document.body.getAttribute('data-screen') === 'home' && screen === 'home' ? recessTop() : null;
		ui.pin = [focus, ui.returnKey];
		lists = {};
		lastActivity = null;
		document.body.setAttribute('data-screen', screen);
		headerKeys.innerHTML = screen === 'connect' ? '' : headerHtml(v);
		if (screen === 'connect') main.innerHTML = connectHtml(v);
		else if (screen === 'detail') main.innerHTML = detailHtml(v);
		else main.innerHTML = homeHtml(v);
		// Long lists first draw where they were, so the page is its full height before
		// anything is measured, and a kept scroll position is never cut short.
		mountLists(true);
		if (keepRecess !== null) setRecessTop(keepRecess);
		applyBars(main);
		logToEnd(main);
		if (sheet.open) {
			if (screen === 'connect') sheet.close();
			else sheet.innerHTML = settingsHtml(v);
		}
		if (ask.open && screen === 'connect') ask.close();
		document.title = screen === 'detail' && ui.detail ? ui.detail.name + ' · Armory' : 'Armory';
		mountLists(false);
		restoreFocus(focus);
		paintDrag();
		updateCues();
	}

	/** The header's keys. In a narrow window they keep only their icons; the words stay
	 *  for screen readers and the tooltip says where each one goes. Send feedback (v0.3) is
	 *  there once the computer is signed in, on every screen but Connect. */
	function headerHtml(v) {
		return (
			'<button class="key hdr-key" type="button" data-action="openVault" data-key="hdr-vault" title="Open ' + esc(v.vaultRoot) + ' in File Explorer">' +
			icon('folder') +
			'<span class="key-word">Open Armory folder</span></button>' +
			(v.connection === 'signedIn'
				? '<button class="key hdr-key" type="button" data-action="askFeedback" data-key="feedback" aria-haspopup="dialog" title="Send feedback to the IDEA team">' +
					icon('feedback') +
					'<span class="key-word">Send feedback</span></button>'
				: '') +
			'<button class="key hdr-key" type="button" data-action="openSettings" data-key="hdr-settings" aria-haspopup="dialog" title="Settings">' +
			icon('settings') +
			'<span class="key-word">Settings</span></button>'
		);
	}

	/* ---- Connect ---- */

	/** The first thing a student ever sees: the IDEA Armory mark, one sentence about what
	 *  Armory is, three steps and one key, centered in the recessed tray. */
	function connectHtml(v) {
		var phase = v.connect.phase;
		if (v.connection === 'vaultOwnedByOther') return folderTakenHtml(v);

		var waiting = v.connection === 'connecting' && phase === 'waitingForBrowser';
		var finishing = v.connection === 'connecting' && phase === 'finishing';
		var failed = phase === 'failed';
		var current = finishing ? 3 : waiting ? 2 : 1;

		var html = titleBar('h1', 'Connect this computer');
		html += '<div class="connect plate-recess"><div class="connect-inner brackets">';
		html += heroHtml();
		// The welcome sentence is for before the first click; after it, the status plate
		// says what is happening instead.
		if (!waiting && !finishing && !failed)
			html += '<p class="lead">You only do this once. After that, Armory keeps your team\'s files up to date on this computer by itself.</p>';
		html += '<ol class="steps">';
		html += step(1, current, failed ? 'Click <strong>Try again</strong> below.' : 'Click the button below.');
		html += step(2, current, 'Sign in with your school Google account in the browser that opens.');
		html += step(3, current, 'Come back here. Your files show up in <span class="mono-inline">' + esc(v.vaultRoot) + '</span>.');
		html += '</ol>';
		if (waiting) html += lcdPlate('look', 'Waiting for Google', v.connect.message || 'Finish signing in in your browser. This window updates by itself.', true);
		else if (finishing) html += lcdPlate('ok', 'Getting your files', v.connect.message || 'Almost done.', true);
		else if (failed) html += lcdPlate('bad', "Sign-in didn't finish", v.connect.message || 'Check that you\'re online, then try again.', false);
		html += '<div class="connect-actions">';
		if (waiting) {
			html += '<button class="key" type="button" data-action="connect" data-key="cn-reopen">Open the browser again</button>';
			html += '<button class="key" type="button" data-action="cancelConnect" data-key="cn-cancel">Cancel</button>';
		} else if (!finishing) {
			html += '<button class="key primary" type="button" data-action="connect" data-key="cn-connect">' + (failed ? 'Try again' : 'Connect this computer') + '</button>';
		}
		html += '</div>';
		html += '</div></div>';
		return html;
	}

	function heroHtml() {
		return (
			'<div class="hero">' +
			'<span class="wordmark wordmark-hero" role="img" aria-label="IDEA"></span>' +
			'<span class="hero-word">Armory</span>' +
			'<p class="hero-line">Armory is your team\'s shared SolidWorks folder.</p>' +
			'</div>'
		);
	}

	/** A dark glass plate with a lit readout: what is happening right now, in a few
	 *  words, and one plain sentence under it. */
	function lcdPlate(tone, readout, words, live) {
		return (
			'<div class="lcd-plate" data-tone="' + tone + '" role="status">' +
			'<p class="lcd' + (live ? ' live' : '') + '">' + icon(tone === 'bad' ? 'cant' : tone === 'ok' ? 'check' : 'note', 'lcd-icon') + '<span>' + esc(readout) + '</span></p>' +
			(words ? '<p class="lcd-words">' + esc(words) + '</p>' : '') +
			'</div>'
		);
	}

	function step(n, current, text) {
		var state = n < current ? 'done' : n === current ? 'current' : 'todo';
		var label = state === 'done' ? icon('check') + '<span class="visually-hidden">Done:</span>' : String(n);
		return (
			'<li class="step panel" data-step="' + state + '"' + (state === 'current' ? ' aria-current="step"' : '') + '>' +
			'<span class="step-num">' + label + '</span><span class="step-text">' + text + '</span></li>'
		);
	}

	/** The Armory folder on this computer already holds someone else's files: say whose,
	 *  offer a folder of the student's own in one click, and say who to ask. */
	function folderTakenHtml(v) {
		var msg = v.connect.message || '';
		var m = msg.match(/[\w.+-]+@[\w-]+(?:\.[\w-]+)+/);
		var owner = m ? m[0] : null;
		var ownerName = owner ? nameFromEmail(owner) : null;
		var me = v.account ? v.account.email : null;
		var mine = v.vaultRoot.replace(/[\\/]+$/, '') + '-' + (me ? nameFromEmail(me).split(' ')[0].toLowerCase() : 'mine');

		var html = titleBar('h1', 'Choose your folder');
		html += '<div class="connect plate-recess"><div class="connect-inner brackets">';
		html += lcdPlate('look', ownerName ? 'This folder belongs to ' + ownerName : 'This folder belongs to someone else', null, false);
		html +=
			'<p class="lead"><span class="mono-inline">' + esc(v.vaultRoot) + '</span> is ' + (ownerName ? esc(ownerName) + '\'s' : 'someone else\'s') + ' Armory folder. ' +
			'If ' + (ownerName ? esc(ownerName.split(' ')[0]) : 'they') + ' saved everything to Armory, you can use it now: Armory checks first, and nothing of theirs changes. ' +
			'If something of theirs is still waiting, use a folder of your own.</p>';
		if (!owner && msg) html += '<p class="connect-where">' + esc(msg) + '</p>';
		html += '<dl class="accounts">';
		if (owner) html += '<div><dt class="label">This folder belongs to</dt><dd class="mono-plate">' + esc(owner) + '</dd></div>';
		if (me) html += '<div><dt class="label">You\'re signed in as</dt><dd class="mono-plate">' + esc(me) + '</dd></div>';
		html += '</dl>';
		html += '<div class="connect-actions">';
		html += '<button class="key primary" type="button" data-action="takeOverFolder" data-key="cn-take">Use this folder</button>';
		html += '<button class="key" type="button" data-action="useFolder" data-path="' + esc(mine) + '" data-key="cn-own">Use <span class="key-path">' + esc(mine) + '</span></button>';
		html += '<button class="key" type="button" data-action="chooseVaultRoot" data-key="cn-choose">' + icon('folder') + '<span>Choose another folder</span></button>';
		html += '</div>';
		html += '<p class="connect-foot">Not sure? Ask your teacher. Not you? <button class="textlink" type="button" data-action="signOut" data-key="cn-signout">Sign out</button></p>';
		html += '</div></div>';
		return html;
	}

	/* ---- Home ---- */

	/*
	 * Home: the status display and this computer on the left; the recessed column on the
	 * right holding, in order, the selection bar (while files are picked), the quiet
	 * check-out question, what is moving right now, the notices (one card per kind), My
	 * files (my check outs and my files that aren't in Armory) and the team's files to
	 * browse. In a wide window the column scrolls inside its own frame, so the status
	 * stays in sight; in a narrow one the window scrolls and the parts stack in reading
	 * order, the status shrunk to one lit strip.
	 */
	function homeHtml(v) {
		return (
			titleBar('h1', 'Home') +
			'<div class="home">' +
			'<svg class="plate-engrave" viewBox="0 0 120 22" preserveAspectRatio="none" aria-hidden="true" focusable="false">' +
			'<path class="e-dk" d="M0 17.5 H62 L76 3.5 H120" /><path class="e-lt" d="M0 18.5 H62.4 L76.4 4.5 H120" /></svg>' +
			statusHtml(v) +
			'<div class="main-col plate-recess"><div class="recess-scroll" id="recess-scroll">' +
			selectionBarHtml(v) +
			promptHtml(v) +
			activityHtml(v.activity) +
			noticesHtml(v) +
			myFilesHtml(v) +
			browserHtml(v) +
			'</div>' +
			cueHtml('recess-cue') +
			'</div>' +
			accountHtml(v) +
			'</div>'
		);
	}

	function cueHtml(id) {
		return '<div class="scroll-cue" id="' + id + '" data-on="false" aria-hidden="true"><span class="cue-chip">' + icon('chev-down') + '<span class="cue-word"></span></span></div>';
	}

	function recessEl() {
		return document.getElementById('recess-scroll');
	}

	/** What scrolls Home's lists: the recessed column in a wide window, else the window. */
	function homeScroller() {
		var r = recessEl();
		return r && getComputedStyle(r).overflowY !== 'visible' ? r : scroller;
	}

	function recessTop() {
		var r = recessEl();
		return r ? r.scrollTop : 0;
	}

	function setRecessTop(top) {
		var r = recessEl();
		if (r) r.scrollTop = top;
	}

	/** The display holds the readout and its words: while files move, the line is what is
	 *  moving ("Downloading 412 of 1,280 files, 2.1 GB left, about 3 min"). Its one key
	 *  sits under it: Pause sending (gone while offline, when there is nothing to pause),
	 *  or Resume as the screen's green primary while paused. */
	function statusHtml(v) {
		var s = SYNC[v.sync.state] || SYNC.synced;
		var tone = s.tone;
		var readout = s.readout;
		if (v.sync.state === 'attention') {
			tone = noticesTone(v.notices);
			if (tone === 'bad' && (v.notices || []).every(function (n) { return n.tone === 'bad'; })) readout = "Can't upload";
		}
		var line = (v.activity && v.activity.line) || v.sync.line;
		// How many files wait is said once: by Right now when it shows them, else here.
		var waitingShown = !!(v.activity && v.activity.waiting);
		var detail = v.sync.detail || (v.sync.pendingCount > 0 && !waitingShown ? plural(v.sync.pendingCount, 'file is', 'files are') + ' waiting to upload.' : null);
		var k = '';
		if (v.sync.state === 'paused')
			k =
				'<button class="key primary status-key" type="button" data-action="resume" data-key="sync-toggle" title="Start uploading and downloading files again">' +
				icon('play') + '<span class="key-word">Resume</span></button>';
		else if (v.sync.state !== 'offline')
			k =
				'<button class="key status-key" type="button" data-action="pause" data-key="sync-toggle" title="Stop uploading and downloading files for now">' +
				icon('pause') + '<span class="key-word">Pause</span></button>';
		return (
			'<section class="group top status-group' + (k ? '' : ' no-key') + '" aria-labelledby="status-label">' +
			'<h2 class="section-label" id="status-label">Status</h2>' +
			'<div class="display" data-tone="' + tone + '">' +
			'<p class="screen lcd" role="status"><span>' + esc(readout) + '</span></p>' +
			'<p class="sync-line" id="sync-line">' + esc(glue(line)) + '</p>' +
			(detail ? '<p class="sync-detail">' + esc(glue(detail)) + '</p>' : '') +
			'</div>' +
			k +
			'</section>'
		);
	}

	/** How many team files are current on this computer, as a ring. */
	function upToDate(v) {
		var total = 0;
		var good = 0;
		(v.projects || []).forEach(function (p) {
			if (p.archived) return;
			p.folders.forEach(function (f) {
				f.files.forEach(function (r) {
					if (!r.fileId) return; // a file that isn't in Armory is not a team file yet
					total++;
					if (UP_TO_DATE[r.status]) good++;
				});
			});
		});
		return { good: good, total: total };
	}

	/** The ring: how many team files are current here, after the site's GRADE ring. */
	function ringHtml(v, where) {
		var c = upToDate(v);
		if (!c.total) return '';
		var pct = Math.round((c.good / c.total) * 100);
		var all = c.good === c.total;
		var tone = all || v.sync.state === 'syncing' ? 'ok' : 'look';
		var words = (all ? 'All ' + num(c.total) : num(c.good) + ' of ' + num(c.total)) + ' team files are up to date on ' + where + '.';
		return (
			'<div class="gauge">' +
			'<div class="ring" data-tone="' + tone + '">' +
			'<svg viewBox="0 0 100 100" aria-hidden="true" focusable="false">' +
			'<circle class="ring-track" cx="50" cy="50" r="41" />' +
			'<circle class="ring-arc" cx="50" cy="50" r="41" pathLength="100" stroke-dasharray="' + pct + ' 100" transform="rotate(-90 50 50)" /></svg>' +
			'<span class="ring-glass" aria-hidden="true"><span class="ring-value">' + esc(c.good > 9999 ? Math.round(c.good / 1000) + 'k' : String(c.good)) + '</span><span class="ring-of">of ' + esc(c.total > 9999 ? Math.round(c.total / 1000) + 'k' : String(c.total)) + '</span></span>' +
			'</div>' +
			'<p class="gauge-words">' + esc(glue(words)) + '</p>' +
			'</div>'
		);
	}

	/** This computer, in one card: how much of the team's work is up to date here (the
	 *  ring), who is signed in, and Sign out as a quiet link, never a big key on the main
	 *  screen. */
	function accountHtml(v) {
		var a = v.account;
		return (
			'<section class="group top account-group" aria-labelledby="acct-label">' +
			'<h2 class="section-label" id="acct-label">This computer</h2>' +
			'<div class="panel acct-panel">' +
			ringHtml(v, a ? a.deviceName : 'this computer') +
			(a ? '<dl class="acct"><div><dt class="label">Signed in as</dt><dd class="acct-email">' + esc(a.email) + '</dd></div></dl>' : '') +
			// Switch account: the next student signs in now, on the same Armory folder.
			(a ? '<button class="textlink acct-signout" type="button" data-action="switchAccount" data-key="switch-account">' + icon('person') + '<span>Switch account</span></button>' : '') +
			'<button class="textlink acct-signout" type="button" data-action="signOut" data-key="signout">' + icon('signout') + '<span>Sign out of Armory</span></button>' +
			'</div></section>'
		);
	}

	/* ---- The quiet check-out question ---- */

	/** SolidWorks opened a file this computer has not checked out: one slim card at the
	 *  top of the column, never a window that takes focus. When someone else has it, it
	 *  says who instead. SolidWorks keeps a file it opened read-only that way until it is
	 *  opened again, so the card says so, and its key checks the file out and opens it
	 *  again from here (the host asks for it to be closed in SolidWorks first). One
	 *  question per open: its key comes from the host, and Not now (or OK) sends it back
	 *  with dismissNotice, so the host asks about the next open file. */
	function promptHtml(v) {
		var p = v.prompt;
		if (!p || ui.promptGone === promptKey(p)) return '';
		var free = !!p.canCheckOut;
		// The file's name never breaks at its hyphens ("Plate- / Left.SLDPRT").
		var name = '<span class="fname">' + esc(p.name) + '</span>';
		var title = free ? 'Check out ' + name + ' to edit it?' : name + ' is ' + esc(glue(lowerFirst(checkoutOf(p).label))) + '.';
		var words = free
			? 'SolidWorks opened it read-only. Check it out, then close it in SolidWorks and open it again here to save changes.'
			: 'You can look, but you can\'t save changes.';
		var keys = free
			? key({ action: 'promptCheckOut', key: 'prompt-checkout', cls: 'primary', glyph: 'checkout', word: 'Check out and reopen', path: p.path, title: 'Check out ' + p.name + ', then open it again here' }) +
			  key({ action: 'promptLater', key: 'prompt-later', word: 'Not now' })
			: key({ action: 'promptLater', key: 'prompt-later', word: 'OK' });
		return (
			'<section class="prompt-card panel" data-tone="' + (free ? 'ok' : 'look') + '" aria-labelledby="prompt-title">' +
			'<span class="attn-glyph">' + icon(free ? 'checkout' : 'person') + '</span>' +
			'<div class="attn-body">' +
			'<h2 class="attn-title" id="prompt-title">' + title + '</h2>' +
			'<p class="attn-detail">' + esc(words) + '</p>' +
			'<div class="attn-actions">' + keys + '</div>' +
			'</div></section>'
		);
	}

	/** The question's own key, one per open of the file (PromptView.key): Not now hides
	 *  that one question, and the next open of the file asks again. */
	function promptKey(p) {
		return p ? p.key || p.path + '|' + checkoutOf(p).state : null;
	}

	function lowerFirst(s) {
		s = String(s || '');
		return s.charAt(0).toLowerCase() + s.slice(1);
	}

	/* ---- Right now: what is moving ---- */

	function hasActivity(a) {
		return !!(a && (a.upload || a.download || a.move || a.waiting || (a.active && a.active.length) || (a.log && a.log.length)));
	}

	/** "Right now": each direction with its count, what is left, the speed, the time left
	 *  and a progress track; each file moving now with its own track; and how many files
	 *  wait. Always in the page (hidden when nothing moves) so an 'activity' message can
	 *  patch it in place. */
	function activityHtml(a) {
		return (
			'<section class="group top activity-group" id="activity-group" aria-labelledby="act-label"' + (hasActivity(a) ? '' : ' hidden') + '>' +
			'<h2 class="section-label" id="act-label">Right now</h2>' +
			'<div class="panel act-panel" id="act-panel">' + activityInner(a) + '</div>' +
			'</section>'
		);
	}

	function track(pct, label) {
		pct = clamp(Math.round(pct), 0, 100);
		return (
			'<span class="track" role="progressbar" aria-label="' + esc(label) + '" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + pct + '">' +
			'<span class="track-fill" data-pct="' + pct + '"></span></span>'
		);
	}

	function directionHtml(name, d) {
		if (!d) return '';
		var w = DIRECTION[name];
		var pct = d.bytesTotal > 0 ? (d.bytesDone / d.bytesTotal) * 100 : d.filesTotal > 0 ? (d.filesDone / d.filesTotal) * 100 : 0;
		// Bytes, speed and time left when there are bytes to count; else (a move) the
		// engine's own line as it wrote it, never picked apart.
		var meta;
		if (d.bytesTotal > 0) meta = metaLine([bytes(d.bytesTotal - d.bytesDone) + ' left', d.bytesPerSecond > 0 ? bytes(d.bytesPerSecond) + '/s' : '', timeLeft(d.secondsLeft)]);
		else meta = d.line || '';
		return (
			'<div class="act-dir" data-direction="' + name + '" title="' + esc(d.line || '') + '">' +
			'<p class="act-head label">' + icon(w.glyph) + '<span class="act-word">' + esc(w.word) + '</span></p>' +
			'<p class="act-count">' + esc(glue(num(d.filesDone) + ' of ' + plural(d.filesTotal, 'file', 'files'))) + '</p>' +
			track(pct, w.word + ' ' + num(d.filesDone) + ' of ' + num(d.filesTotal)) +
			(meta ? '<p class="act-meta">' + esc(meta) + '</p>' : '') +
			'</div>'
		);
	}

	function activityInner(a) {
		if (!hasActivity(a)) return '';
		var html = '';
		var dirs = directionHtml('upload', a.upload) + directionHtml('download', a.download) + directionHtml('move', a.move);
		if (dirs) html += '<div class="act-dirs">' + dirs + '</div>';
		if (a.active && a.active.length) {
			html += '<ul class="act-files" aria-label="Files moving now">';
			a.active.slice(0, 8).forEach(function (f) {
				var w = DIRECTION[f.direction] || DIRECTION.download;
				var pct = f.bytesTotal > 0 ? (f.bytesDone / f.bytesTotal) * 100 : 0;
				html +=
					'<li class="act-file" data-direction="' + esc(f.direction) + '">' +
					icon(w.glyph, 'act-glyph') +
					'<span class="act-name" title="' + esc(f.path) + '">' + esc(f.name) + '</span>' +
					'<span class="act-bytes">' + esc(f.bytesTotal > 0 ? bytes(f.bytesDone) + ' of ' + bytes(f.bytesTotal) : w.word) + '</span>' +
					track(pct, w.word + ' ' + f.name) +
					'</li>';
			});
			html += '</ul>';
		}
		if (a.waiting) html += '<p class="act-wait">' + icon('clock') + '<span>' + esc(glue(a.waiting.line)) + '</span></p>';
		html += logHtml(a.log);
		return html;
	}

	/** What Armory did in the last few minutes, a line each, the newest at the foot (kept in
	 *  view), so a long check out or download shows it is working, not stuck. */
	function logHtml(lines) {
		if (!lines || !lines.length) return '';
		var html = '<div class="act-log-wrap"><p class="act-head label">' + icon('note') + '<span class="act-word">What Armory is doing</span></p>';
		html += '<ol class="act-log" id="act-log" tabindex="0" aria-label="What Armory did just now">';
		lines.forEach(function (l) {
			html += '<li><time class="act-log-at" datetime="' + esc(l.at) + '">' + esc(clockTime(l.at)) + '</time><span class="act-log-line">' + esc(glue(l.line)) + '</span></li>';
		});
		return html + '</ol></div>';
	}

	/** "3:41:05 PM" for a line's time. */
	function clockTime(iso) {
		var d = new Date(iso);
		return isNaN(d.getTime()) ? '' : d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', second: '2-digit' });
	}

	/** The running lines stay at their newest. */
	function logToEnd(root) {
		var log = (root || document).querySelector('#act-log');
		if (log) log.scrollTop = log.scrollHeight;
	}

	/** Progress widths go in through CSSOM: the page's CSP refuses inline styles. */
	function applyBars(root) {
		var fills = (root || document).querySelectorAll('.track-fill[data-pct]');
		for (var i = 0; i < fills.length; i++) fills[i].style.width = fills[i].getAttribute('data-pct') + '%';
	}

	var lastActivity = null;

	/** A host 'activity' message: the panel and the status line change, nothing else. */
	function patchActivity(a) {
		if (!ui.view) return;
		ui.view.activity = a;
		var group = document.getElementById('activity-group');
		if (group) {
			var panel = document.getElementById('act-panel');
			var html = activityInner(a);
			if (lastActivity !== html) {
				panel.innerHTML = html;
				applyBars(panel);
				logToEnd(panel);
			}
			lastActivity = html;
			group.hidden = !hasActivity(a);
		}
		var line = document.getElementById('sync-line');
		if (line) {
			var text = glue((a && a.line) || ui.view.sync.line);
			if (line.textContent !== text) line.textContent = text;
		}
		updateCues();
	}

	/* ---- Notices: one card per kind ---- */

	function noticesTone(items) {
		var rank = { info: 0, look: 1, bad: 2 };
		var worst = -1;
		(items || []).forEach(function (n) {
			worst = Math.max(worst, rank[n.tone] == null ? 1 : rank[n.tone]);
		});
		return worst === 2 ? 'bad' : worst === 1 ? 'look' : 'ok';
	}

	/** Notices under their own lit strip ("2 things to look at" in amber, "to fix" in red
	 *  when something can't be uploaded). One card per kind, its count in the title, one
	 *  action key, and a key that opens the list of its files inside the card. */
	function noticesHtml(v) {
		var items = v.notices || [];
		// Nothing to say: the group is not drawn at all. The status display already says
		// so, and the group appears the moment something needs the student.
		if (!items.length) return '';
		var tone = noticesTone(items);
		var words = tone === 'bad' ? plural(items.length, 'thing', 'things') + ' to fix' : tone === 'look' ? plural(items.length, 'thing', 'things') + ' to look at' : plural(items.length, 'update', 'updates');
		var html = '<section class="group top needs-group" aria-labelledby="needs-label">';
		html += '<h2 class="needs-strip lcd" id="needs-label" data-tone="' + tone + '">' + icon(tone === 'bad' ? 'cant' : tone === 'ok' ? 'check' : 'note', 'lcd-icon') + '<span>' + esc(words) + '</span></h2>';
		html += '<ul class="attn-list">';
		items.forEach(function (n, i) {
			html += noticeCard(n, i);
		});
		html += '</ul>';
		return html + '</section>';
	}

	function noticeCard(n, i) {
		var id = 'nt-' + i;
		var items = n.items || [];
		// A list is for two files or more: one file is named in the card and gets its own
		// key, never a list of one.
		var hasItems = items.length > 1 && n.kind !== 'projectDeleted';
		var open = hasItems && !!ui.expanded[n.key];
		var keys = '';
		var a = n.action;
		var expandKey = function (word) {
			return (
				'<button class="key" type="button" data-action="expand" data-notice="' + esc(n.key) + '" data-key="nt-expand-' + esc(n.key) + '" aria-expanded="' + open + '" aria-controls="' + id + '-items">' +
				icon(open ? 'chev-down' : 'chev-right') + '<span>' + esc(word) + '</span></button>'
			);
		};
		if (a && a.command !== 'expand')
			keys += '<button class="key" type="button" data-action="notice" data-notice="' + esc(n.key) + '" data-key="nt-act-' + esc(n.key) + '" aria-describedby="' + id + '-t">' + esc(a.label) + '</button>';
		if (hasItems) keys += expandKey(open ? (a && a.command === 'expand' ? 'Hide them' : 'Hide the files') : a && a.command === 'expand' ? a.label : 'Show the ' + plural(items.length, 'file', 'files'));
		else if (items.length === 1 && items[0].fileId && (!a || a.command === 'expand'))
			keys += '<button class="key" type="button" data-action="openFile" data-file-id="' + esc(items[0].fileId) + '" data-key="nt-file-' + esc(n.key) + '" aria-describedby="' + id + '-t">' + icon('chev-right') + '<span>See the file</span></button>';
		var list = '';
		if (open) {
			list += '<div class="notice-well list-well" id="' + id + '-items" data-scroll-own="true">' + registerList({
				id: 'vl-' + id,
				cls: 'items',
				label: n.title,
				items: items.map(function (it) {
					return { kind: 'item', notice: n.key, noticeKind: n.kind, item: it };
				}),
				key: function (x) {
					return 'ni:' + x.notice + ':' + (x.item.fileId || x.item.path);
				},
				row: itemRow,
				scrollEl: function () {
					return document.getElementById(id + '-items');
				}
			}) + '</div>';
			if (n.count > items.length) list += '<p class="notice-more">' + esc('Showing ' + num(items.length) + ' of ' + num(n.count) + '.') + '</p>';
		}
		return (
			'<li class="attn-card panel" data-tone="' + (NOTICE_TONE[n.tone] || 'look') + '" data-kind="' + esc(n.kind) + '">' +
			'<span class="attn-glyph">' + icon(NOTICE_GLYPH[n.kind] || 'note') + '</span>' +
			'<div class="attn-body">' +
			'<h3 class="attn-title" id="' + id + '-t">' + esc(glue(n.title)) + '</h3>' +
			(n.detail ? '<p class="attn-detail">' + esc(glue(n.detail)) + '</p>' : '') +
			(keys ? '<div class="attn-actions">' + keys + '</div>' : '') +
			list +
			'</div></li>'
		);
	}

	/** The project's file in Armory with the same name as a file that isn't in Armory
	 *  (the one it shares its name with), or null. */
	function namesake(it) {
		var hit = ui.index.byName[String(it.path).split('/')[0] + '\n' + String(it.name).toLowerCase()];
		return hit && hit.row.path !== it.path ? hit : null;
	}

	/** A notice's file. Its row opens the file's page in the app. A file Armory doesn't
	 *  have because the project already has its name opens the page of the file that has
	 *  it, and carries Rename, so the student gives it a name of its own right here (never
	 *  in File Explorer). */
	function itemRow(x, i, active) {
		var it = x.item;
		var k = 'ni:' + x.notice + ':' + (it.fileId || it.path);
		var other = it.fileId ? null : namesake(it);
		var extra =
			x.noticeKind === 'nameShared' && !it.fileId
				? '<button class="key row-key" type="button" data-rove="rename" data-action="askRenameFile" data-path="' + esc(it.path) + '" data-key="rename-' + esc(k) + '"' +
				  ' aria-label="Rename ' + esc(it.name) + '" title="Give ' + esc(it.name) + ' a name of its own">' + icon('rename') + '<span class="key-word">Rename</span></button>'
				: null;
		var hit = it.fileId
			? { action: 'openFile', fileId: it.fileId }
			: other
				? { action: 'openFile', fileId: other.row.fileId, hint: 'See the ' + other.row.name + ' in ' + whereIs(other.row.path).replace(/\u00a0/g, ' ') }
				: { action: 'showInFolder', path: it.path, hint: 'Show in folder' };
		return rowHtml({
			i: i,
			vkey: k,
			active: active,
			hit: hit,
			k: k,
			name: it.name,
			line: '<span class="row-meta">' + esc(it.detail || whereIs(it.path)) + '</span>',
			extra: extra,
			go: it.fileId || other ? 'chev-right' : 'folder-go'
		});
	}

	/* ---- Rows ---- */

	/**
	 * One row of a long list, at one fixed height. The whole row is a click target (an
	 * invisible key laid over it), with any extra key above it; Tab enters the list on one
	 * row and the arrow keys move between rows (data-rove marks the keys that move). Every
	 * row ends in a glyph that says where a click goes: a chevron to the file's page, into
	 * a folder, or a folder-out glyph for a file that has no page yet.
	 */
	function rowHtml(o) {
		var t = o.active ? '0' : '-1';
		var hit = o.hit;
		var html =
			'<li class="row vrow' + (o.cls ? ' ' + o.cls : '') + '" data-vkey="' + esc(o.vkey) + '" data-i="' + o.i + '"><div class="row-main' + (o.select !== undefined ? ' has-select' : '') + (o.extra ? ' has-extra' : '') + (o.thumb ? ' has-thumb' : '') + '">' +
			'<button class="row-hit" type="button" data-rove="row" tabindex="' + t + '" data-action="' + hit.action + '"' +
			(hit.fileId ? ' data-file-id="' + esc(hit.fileId) + '"' : '') +
			(hit.path != null ? ' data-path="' + esc(hit.path) + '"' : '') +
			(hit.folder != null ? ' data-folder="' + esc(hit.folder) + '"' : '') +
			' data-key="row-' + esc(o.k) + '" aria-labelledby="n-' + esc(o.k) + '"' + (hit.hint ? ' title="' + esc(hit.hint) + '"' : '') + '></button>';
		if (o.select !== undefined) html += o.select ? o.select.replace('data-rove="sel"', 'data-rove="sel" tabindex="' + t + '"') : '<span class="sel-slot" aria-hidden="true"></span>';
		html +=
			(o.thumb ? '<span class="row-icon thumb-slot">' + icon(o.glyph || kindOf(o.name), 'thumb-glyph') + thumbImg(o.thumb) + '</span>' : icon(o.glyph || kindOf(o.name), 'row-icon')) +
			'<span class="row-body">' +
			'<span class="row-name" id="n-' + esc(o.k) + '">' + nameHtml(o.name, o.glyph === 'folder') + '</span>' +
			'<span class="row-line">' + (o.line || '') + '</span>' +
			'</span>' +
			(o.extra ? '<span class="row-extra">' + o.extra.replace(/data-rove="(\w+)"/g, 'data-rove="$1" tabindex="' + t + '"') + '</span>' : '') +
			icon(o.go || 'chev-right', 'row-go') +
			'</div></li>';
		return html;
	}

	/* ---- Thumbnails: File Explorer's own picture of a file ---- */

	// Only inside the app: the host answers /thumb/<vault path> on its own page address with the
	// picture Windows has for the file (SolidWorks draws its parts', assemblies' and
	// drawings'), or 404. The demo and the check pages have none, and ask for nothing.
	var THUMBS = location.protocol === 'https:' && location.hostname === 'armory.local';
	var THUMB_KINDS = /\.(sldprt|sldasm|slddrw|png|jpe?g|bmp|gif|webp|tiff?)$/i;

	/** The picture's address for a file on this computer, or null. Its version is in the
	 *  query, so a file checked in or saved since gets its new picture. */
	function thumbAddress(r) {
		if (!THUMBS || !r || !r.path || r.status === 'notOnThisComputer' || !THUMB_KINDS.test(r.name || r.path)) return null;
		return (
			'/thumb/' + String(r.path).split('/').map(encodeURIComponent).join('/') +
			'?v=' + encodeURIComponent((r.updatedAt || '') + (r.changed || r.status === 'changed' ? '-' + (r.status || '') : ''))
		);
	}

	// eager: File detail's one picture, in a box that stays hidden until it arrives (a lazy
	// image in a hidden box is never asked for).
	function thumbImg(url, eager) {
		return '<img class="thumb" src="' + esc(url) + '" alt="" loading="' + (eager ? 'eager' : 'lazy') + '" decoding="async" draggable="false">';
	}

	/** A file's name that keeps its extension in sight: when the row is short of room the
	 *  middle gives way ("Sub-Assembly-00....SLDASM"), so a part and an assembly never
	 *  look the same. The words are the whole name, for every reader. */
	function nameHtml(name, folder) {
		name = String(name || '');
		var dot = name.lastIndexOf('.');
		if (folder || dot <= 0 || name.length - dot > 8) return esc(name);
		return '<span class="nm-base">' + esc(name.slice(0, dot)) + '</span><span class="nm-ext">' + esc(name.slice(dot)) + '</span>';
	}

	/** The line under a file's name: who has it checked out, always and first; its state
	 *  when it has one worth a chip; what kind of file it is; who checked it in last. A
	 *  file that isn't in Armory can't be checked out yet, so it says that instead: a new
	 *  file that uploads by itself says so in plain words, one Armory can't take wears a
	 *  gray chip (the notice says why). */
	function fileLine(r) {
		var meta = r.updatedBy ? metaLine(['Checked in by ' + r.updatedBy, r.updatedAt ? agoWhole(r.updatedAt) : '']) : '';
		var first;
		if (r.fileId) first = pendingHtml(r) + checkoutMark(checkoutOf(r)) + statusChip(r.status, r.changed);
		else if (r.status === 'notInArmory') first = statusChip(r.status, false);
		else first = '<span class="row-avail">' + esc(r.status === 'uploading' ? 'New, uploading now' : 'New, not uploaded yet') + '</span>';
		return first + kindChip(r.name) + (meta ? '<span class="row-meta">' + esc(meta) + '</span>' : '');
	}

	function openKey(path, name, k) {
		return (
			'<button class="key row-key" type="button" data-rove="open" data-action="launch" data-path="' + esc(path) + '" data-key="open-' + esc(k) + '"' + busyAttrs('open-' + k) +
			' aria-label="Open ' + esc(name) + '" title="Open ' + esc(name) + ' to look at it">' + busyGlyph('open-' + k, 'open') + '<span class="key-word">Open</span></button>'
		);
	}

	/** A file row's one state key, beside Open: Check out while nobody has it, Check in
	 *  while I have it here, and for a mentor or CAD lead, Force check in while someone else
	 *  has it. Anyone else's view of someone else's file has none: its line says who has it. */
	function stateKey(r, k) {
		if (!r.fileId) return '';
		var c = checkoutOf(r);
		var sk = 'state-' + k;
		if (c.state === 'available')
			return (
				'<button class="key row-key state-key" type="button" data-rove="state" data-action="checkOut" data-path="' + esc(r.path) + '" data-key="' + esc(sk) + '"' + busyAttrs(sk) +
				' aria-label="Check out ' + esc(r.name) + '" title="Check out ' + esc(r.name) + ' to make changes">' + busyGlyph(sk, 'checkout') + '<span class="key-word">Check out</span></button>'
			);
		if (c.state === 'mine')
			return (
				'<button class="key row-key state-key" type="button" data-rove="state" data-action="checkIn" data-path="' + esc(r.path) + '" data-key="' + esc(sk) + '"' + busyAttrs(sk) +
				' aria-label="Check in ' + esc(r.name) + '" title="Check in ' + esc(r.name) + ' to share your changes">' + busyGlyph(sk, 'checkin') + '<span class="key-word">Check in</span></button>'
			);
		var found = findRow(r.fileId);
		if ((c.state === 'other' || c.state === 'myOtherComputer') && found && found.project.canTakeBack)
			return (
				'<button class="key row-key state-key" type="button" data-rove="state" data-action="askTakeBack" data-file-id="' + esc(r.fileId) + '" data-key="' + esc(sk) + '"' + busyAttrs(sk) +
				' aria-label="Force check in ' + esc(r.name) + '" title="Force check in ' + esc(r.name) + ' (' + esc(c.label || '') + ')">' + busyGlyph(sk, 'takeback') + '<span class="key-word">Force check in</span></button>'
			);
		return '';
	}

	/* ---- My files ---- */

	/*
	 * My files: the files this computer has checked out, in every project (an archived one
	 * too, so they can always be checked in), one row each with its Open and Check in.
	 * Files that aren't in Armory are never a row each here (an unzip can make thousands):
	 * the notices say what became of them, and each sits in its folder in Team files.
	 */
	function myFilesHtml(v) {
		var files = v.myFiles || [];
		var html = '<section class="group top" aria-labelledby="mine-label">';
		html += '<h2 class="section-label" id="mine-label"><span>My files</span>' + (files.length ? count(files.length, 'file', 'files') : '') + '</h2>';
		if (!files.length) {
			html += '<div class="empty-tile">' + icon('asm', 'empty-glyph') + '<div class="empty-words">';
			html += '<p>Nothing checked out. Files you check out show up here, each with its Check in.</p>';
			html += '<p class="empty-where">Your team\'s files are in <span class="mono-plate">' + esc(v.vaultRoot) + '</span></p>';
			html += '<button class="key" type="button" data-action="openVault" data-key="empty-vault">' + icon('folder') + '<span>Open Armory folder</span></button>';
			html += '</div></div>';
			return html + '</section>';
		}
		// More than one: the keys for all of them, first, where they are always in sight.
		var mineIn = files.filter(function (f) {
			return checkoutOf(f).state === 'mine';
		}).length;
		if (files.length > 1) {
			html += '<div class="folder-keys mine-keys" role="group" aria-label="All my files">';
			html += key({ action: 'mineCheckIn', key: 'mk-in', cls: 'tool keep-word', glyph: 'checkin', word: 'Check in all', title: 'Check in all ' + plural(mineIn, 'file', 'files') + ' you have checked out', disabled: !mineIn });
			html += key({ action: 'askUndoMine', key: 'mk-undo', cls: 'tool keep-word', glyph: 'undo', word: 'Undo all', title: 'Undo all ' + plural(mineIn, 'check out', 'check outs') + ', keeping your changes as your own copies', disabled: !mineIn });
			html += '</div>';
		}
		// A long list scrolls in a box of its own, so Team files (and its keys) stay right
		// under it: a student with 1,400 files checked out found no keys below them (0.3.1).
		var own = files.length > MINE_ROWS;
		html += '<div class="list-well mine-well' + (own ? ' mine-scroll' : '') + '" id="mine-well"' + (own ? ' data-scroll-own="true"' : '') + '>' + registerList({
			id: 'vl-mine',
			label: 'My files',
			items: files.map(function (f) {
				return { kind: 'mine', file: f };
			}),
			key: function (x) {
				return 'mine:' + (x.file.fileId || x.file.path);
			},
			row: mineRow,
			scrollEl: own
				? function () {
						return document.getElementById('mine-well');
					}
				: homeScroller
		}) + '</div>';
		return html + '</section>';
	}

	// How many My files rows show before the list scrolls in its own box.
	var MINE_ROWS = 6;

	/** Every project folder with a file of mine in it: Check in all and Undo all send these
	 *  (the host takes only the files this computer has checked out under them). */
	function mineFolders(v) {
		var tops = {};
		(v.myFiles || []).forEach(function (f) {
			if (checkoutOf(f).state === 'mine') tops[String(f.path).split('/')[0]] = true;
		});
		return Object.keys(tops);
	}

	function mineRow(x, i, active) {
		var f = x.file;
		var k = 'mine:' + (f.fileId || f.path);
		var found = findRow(f.fileId);
		var r = found ? found.row : { name: f.name, status: f.status, changed: false, checkout: f.checkout, updatedBy: null };
		var extra = '';
		if (checkoutOf(f).state === 'mine')
			extra +=
				'<button class="key row-key state-key" type="button" data-rove="in" data-action="checkIn" data-path="' + esc(f.path) + '" data-key="in-' + esc(k) + '"' + busyAttrs('in-' + k) +
				' aria-label="Check in ' + esc(f.name) + '" title="Check in ' + esc(f.name) + ' to share your changes">' + busyGlyph('in-' + k, 'checkin') + '<span class="key-word">Check in</span></button>';
		extra += openKey(f.path, f.name, k);
		return rowHtml({
			i: i,
			vkey: k,
			active: active,
			hit: f.fileId ? { action: 'openFile', fileId: f.fileId } : { action: 'showInFolder', path: f.path, hint: 'Show in folder' },
			k: k,
			name: f.name,
			line: pendingHtml(f) + checkoutMark(checkoutOf(f)) + statusChip(f.status, r.changed) + kindChip(f.name) + '<span class="row-meta">' + esc(whereIs(f.path)) + '</span>',
			extra: extra,
			thumb: thumbAddress(found ? found.row : f),
			go: f.fileId ? 'chev-right' : 'folder-go'
		});
	}

	/* ---- Team files: the browser ---- */

	/** Team files: two-line project tabs (the name, then how many files), then the open
	 *  project's card: where you are (Project › Folder › Subfolder), the folder's keys, and
	 *  its folders and files, every file with who has it checked out. Files dragged in from
	 *  File Explorer drop into the open folder. */
	function browserHtml(v) {
		var projects = v.projects || [];
		var html = '<section class="group top browser-group" aria-labelledby="proj-label">';
		html += '<h2 class="section-label" id="proj-label">Team files</h2>';
		var place = browserPlace();
		if (!place) {
			html += '<p class="group-help">You\'re not in any projects yet. Ask your teacher or CAD lead to add you.</p>';
			return html + '</section>';
		}
		html += '<div class="pads" role="tablist" aria-labelledby="proj-label">';
		projects.forEach(function (p) {
			var on = p.id === place.project.id;
			var pi = ui.index.projects[p.id];
			html +=
				'<button class="pad project-pad" type="button" role="tab" id="tab-' + esc(p.id) + '" aria-selected="' + on + '" aria-controls="project-panel"' +
				' data-action="project" data-project="' + esc(p.id) + '" data-key="tab-' + esc(p.id) + '">' +
				'<span class="pad-name">' + esc(p.name) + '</span><span class="pad-sub">' + esc(p.archived ? 'Archived' : plural(pi.under[''] || 0, 'file', 'files')) + '</span></button>';
		});
		html += '</div>';
		var p = place.project;
		if (p.archived) {
			// Check outs are kept when a project is archived (D8): say how many are mine and
			// where to check them in (My files lists them).
			var held = rowsUnder(place.pi, '').filter(function (r) {
				return checkoutOf(r).state === 'mine';
			}).length;
			html +=
				'<div class="panel archived-note" id="project-panel" role="tabpanel" aria-labelledby="tab-' + esc(p.id) + '">' + icon('archive', 'archived-glyph') +
				'<div><p class="archived-line">Archived. It no longer updates.</p>' +
				'<p class="group-help">' + esc(p.name) + '\'s folder stays on this computer just as it is. A teacher can bring the project back on ideabosco.com.</p>' +
				(held
					? '<p class="group-help archived-held">' + esc(glue('You still have ' + plural(held, 'file', 'files') + ' checked out in it. Check ' + (held === 1 ? 'it' : 'them') + ' in from My files.')) + '</p>'
					: '') +
				'</div></div>';
			return html + '</section>';
		}
		var pi = place.pi;
		var folder = place.folder;
		html += '<div class="list-well project-card browser" id="project-panel" role="tabpanel" aria-labelledby="tab-' + esc(p.id) + '" data-drop="true">';
		html += '<div class="browser-head">' + crumbsHtml(p, pi, folder) + folderKeysHtml(p, pi, folder) + '</div>';
		// The cue lies over the list only, so where you are and the folder's keys stay in sight.
		html += '<div class="browser-body">';
		html += '<div class="drop-cue" aria-hidden="true"><span class="drop-word">' + icon('addfile') + '<span>' + esc('Drop to add to ' + crumbWords(p, folder)) + '</span></span></div>';
		var items = browserItems(pi, folder);
		if (!items.length) html += '<p class="group-help browser-empty">This folder is empty. Add files, or drag them here from File Explorer.</p>';
		else
			html += registerList({
				id: 'vl-browser',
				label: 'In ' + crumbWords(p, folder),
				items: items,
				key: function (x) {
					return x.kind === 'folder' ? 'dir:' + x.path : x.row.fileId || 'local:' + x.row.path;
				},
				row: browserRow,
				scrollEl: homeScroller
			});
		html += '</div></div>';
		return html + '</section>';
	}

	/** "Robot 2027 › Drivetrain › Gears". */
	function crumbWords(p, folder) {
		return [p.name].concat(folder ? folder.split('/') : []).join(' \u203a ');
	}

	function crumbsHtml(p, pi, folder) {
		var parts = folder ? folder.split('/') : [];
		var html = '<nav class="crumbs" aria-label="Where you are"><ol>';
		var steps = [{ path: '', name: p.name }];
		parts.forEach(function (part, i) {
			steps.push({ path: parts.slice(0, i + 1).join('/'), name: part });
		});
		steps.forEach(function (s, i) {
			var last = i === steps.length - 1;
			html += '<li>' + (i ? '<span class="crumb-sep" aria-hidden="true">\u203a</span>' : '');
			html += last
				? '<span class="crumb-here" aria-current="location">' + icon('folder') + '<span>' + esc(s.name) + '</span></span>'
				: '<button class="textlink crumb" type="button" data-action="folder" data-folder="' + esc(s.path) + '" data-key="crumb-' + esc(s.path) + '">' + esc(s.name) + '</button>';
			html += '</li>';
		});
		return html + '</ol></nav>';
	}

	/** The open folder's keys. The project's own top folder can't be renamed or deleted
	 *  here (project names are changed on ideabosco.com). Check out all and Check in all
	 *  take the folders inside too, and Check out all asks first, with the count. */
	function folderKeysHtml(p, pi, folder) {
		var rows = rowsUnder(pi, folder);
		var canOut = rows.some(function (r) {
			return r.fileId && checkoutOf(r).state === 'available';
		});
		var canIn = rows.some(function (r) {
			return checkoutOf(r).state === 'mine';
		});
		var held = rows.filter(function (r) {
			var st = checkoutOf(r).state;
			return r.fileId && (st === 'other' || st === 'myOtherComputer');
		});
		var name = folder ? folder.split('/').pop() : p.name;
		var inside = (pi.children[folder] || []).length ? ' and the folders in it' : '';
		var html = '<div class="folder-keys" role="group" aria-label="' + esc('Folder ' + name) + '">';
		html += key({ action: 'askNewFolder', key: 'fk-new', cls: 'tool', glyph: 'newfolder', word: 'New folder', title: 'Make a folder in ' + name });
		html += key({ action: 'addFiles', key: 'fk-add', cls: 'tool', glyph: 'addfile', word: 'Add files', title: 'Copy files from this computer into ' + name });
		if (folder) {
			html += key({ action: 'askRenameFolder', key: 'fk-rename', cls: 'tool', glyph: 'rename', word: 'Rename folder', title: 'Rename ' + name });
			html += key({ action: 'askDeleteFolder', key: 'fk-delete', cls: 'tool', glyph: 'trash', word: 'Delete folder', title: 'Delete ' + name });
		}
		html += key({ action: 'askCheckOutAll', key: 'fk-out', cls: 'tool keep-word', glyph: 'checkout', word: 'Check out all', title: 'Check out every file in ' + name + inside, disabled: !canOut });
		html += key({ action: 'folderCheckIn', key: 'fk-in', cls: 'tool keep-word', glyph: 'checkin', word: 'Check in all', title: 'Check in every file you have checked out in ' + name + inside, disabled: !canIn });
		// A mentor or CAD lead can check in, for everyone, every file someone else has here.
		if (p.canTakeBack)
			html += key({ action: 'askForceAll', key: 'fk-force', cls: 'tool keep-word', glyph: 'takeback', word: 'Force check in all', title: 'Force check in every file someone else has checked out in ' + name + inside, disabled: !held.length });
		return html + '</div>';
	}

	/** The open folder's rows: its folders first, then its files. */
	function browserItems(pi, folder) {
		var out = [];
		(pi.children[folder] || []).forEach(function (path) {
			out.push({ kind: 'folder', path: path, name: pi.folders[path].name, files: pi.under[path] || 0, folders: (pi.children[path] || []).length });
		});
		var files = (pi.folders[folder].files || []).slice().sort(function (a, b) {
			return byName(a.name, b.name);
		});
		files.forEach(function (r) {
			out.push({ kind: 'file', row: r });
		});
		return out;
	}

	function browserRow(x, i, active) {
		if (x.kind === 'folder') {
			var fk = 'dir:' + x.path;
			var what = x.files || x.folders ? metaLine([x.files ? plural(x.files, 'file', 'files') : '', x.folders ? plural(x.folders, 'folder', 'folders') : '']) : 'Empty';
			return rowHtml({
				i: i,
				vkey: fk,
				cls: 'folder-item',
				active: active,
				select: null,
				hit: { action: 'folder', folder: x.path },
				k: fk,
				name: x.name,
				glyph: 'folder',
				line: '<span class="row-meta">' + esc(what) + '</span>',
				go: 'chev-right'
			});
		}
		var r = x.row;
		var k = r.fileId || 'local:' + r.path;
		var picked = !!ui.selected[r.path];
		// The pick key always draws its box, empty or ticked, so it reads as a checkbox.
		var selectKey = r.fileId
			? '<button class="key sel-key" type="button" role="checkbox" data-rove="sel" aria-checked="' + picked + '" data-action="select" data-path="' + esc(r.path) + '" data-key="sel-' + esc(k) + '" aria-label="Select ' + esc(r.name) + '">' +
			  '<span class="sel-box" aria-hidden="true">' + icon('check') + '</span></button>'
			: null;
		return rowHtml({
			i: i,
			vkey: k,
			active: active,
			select: selectKey,
			hit: r.fileId ? { action: 'openFile', fileId: r.fileId } : { action: 'showInFolder', path: r.path, hint: 'Show in folder' },
			k: k,
			name: r.name,
			line: fileLine(r),
			// The state key first, so Open keeps one column down the list.
			extra: stateKey(r, k) + openKey(r.path, r.name, k),
			thumb: thumbAddress(r),
			go: r.fileId ? 'chev-right' : 'folder-go'
		});
	}

	/* ---- Picking files ---- */

	function pickedRows() {
		var out = [];
		Object.keys(ui.selected).forEach(function (path) {
			var hit = ui.index.byPath[path];
			if (hit && hit.row.fileId) out.push(hit);
		});
		return out;
	}

	/** While files are picked, one bar at the top of the column (it stays in sight as
	 *  the column scrolls) says how many and offers what fits them. */
	function selectionBarHtml(v) {
		var picked = pickedRows();
		if (!picked.length) return '';
		var any = function (state) {
			return picked.some(function (h) {
				return checkoutOf(h.row).state === state;
			});
		};
		var lead = picked.some(function (h) {
			return h.project.canTakeBack;
		});
		// One wrapping row: the count, what fits the selected files, then Clear. A narrow
		// window puts Clear beside the count and the file keys on the lines under them.
		var html = '<div class="sel-bar panel" role="region" aria-label="Selected files">';
		html += '<p class="sel-count" role="status"><span class="avatar" data-tone="ok" aria-hidden="true">' + icon('check') + '</span>' + esc(num(picked.length) + ' selected') + '</p>';
		html += key({ action: 'selCheckOut', key: 'sel-out', cls: 'tool', glyph: 'checkout', word: 'Check out', disabled: !any('available') });
		html += key({ action: 'selCheckIn', key: 'sel-in', cls: 'tool', glyph: 'checkin', word: 'Check in', disabled: !any('mine') });
		html += key({ action: 'selUndo', key: 'sel-undo', cls: 'tool', glyph: 'undo', word: 'Undo check out', disabled: !any('mine') });
		if (lead) html += key({ action: 'askTakeBackPicked', key: 'sel-take', cls: 'tool', glyph: 'takeback', word: 'Force check in', disabled: !any('other') && !any('myOtherComputer') });
		html += key({ action: 'selClear', key: 'sel-clear', cls: 'tool sel-clear', word: 'Clear' });
		html += '<span class="sel-break" aria-hidden="true"></span>';
		return html + '</div>';
	}

	function setPicked(path, on) {
		if (on) ui.selected[path] = true;
		else delete ui.selected[path];
	}

	function clearPicked() {
		ui.selected = {};
		ui.anchor = null;
	}

	/** A click on a pick key; with Shift, every file from the last one picked to here. */
	function togglePick(path, range) {
		var place = browserPlace();
		var files = browserItems(place.pi, place.folder)
			.filter(function (x) {
				return x.kind === 'file' && x.row.fileId;
			})
			.map(function (x) {
				return x.row.path;
			});
		var on = !ui.selected[path];
		if (range && ui.anchor && files.indexOf(ui.anchor) >= 0) {
			var a = files.indexOf(ui.anchor);
			var b = files.indexOf(path);
			for (var i = Math.min(a, b); i <= Math.max(a, b); i++) setPicked(files[i], true);
		} else setPicked(path, on);
		ui.anchor = path;
		render();
	}

	/* ---- File detail ---- */

	/** The detail with the newest state from the view laid over it. */
	function currentDetail() {
		var d = ui.detail;
		var found = findRow(d.fileId);
		if (!found) return d;
		var out = {};
		for (var k in d) out[k] = d[k];
		out.status = found.row.status;
		out.checkout = found.row.checkout;
		out.changed = found.row.changed;
		out.releaseNotChecked = found.row.releaseNotChecked;
		out.canTakeBack = d.canTakeBack || found.project.canTakeBack;
		return out;
	}

	/** What the file's display says (readout, tone, a line and a sentence). */
	function detailWords(d) {
		var c = d.checkout || { state: 'available', label: 'Available' };
		if (c.state === 'mine')
			return {
				readout: 'Checked out by you',
				tone: 'ok',
				line: d.changed || d.status === 'changed' ? 'You have changes that aren\'t checked in' : 'Checked out by you',
				meta: d.changed || d.status === 'changed' ? 'Each save is kept safe. Check in to share your changes with the team.' : 'Save in SolidWorks as often as you like. Check in to share your changes with the team.'
			};
		if (c.state === 'myOtherComputer')
			return { readout: 'On your other computer', tone: 'look', line: c.label, meta: 'Check it in on ' + c.device + ' first, then check it out here.' };
		if (c.state === 'other')
			return { readout: 'Checked out', tone: 'look', line: c.label, meta: 'You can open it to look, but you can\'t save changes. To change it, ask ' + firstName(c.name) + ' to check it in.' };
		switch (d.status) {
			case 'newerWaiting':
				return { readout: 'Newer version waiting', tone: 'look', line: 'A newer version is waiting', meta: 'Close it in SolidWorks to get the newer version.' };
			case 'downloading':
				return { readout: 'Downloading', tone: 'ok', line: 'Downloading the newest version', meta: 'It\'s ready in a moment. You can keep working.' };
			case 'notOnThisComputer':
				return { readout: 'Not here yet', tone: 'off', line: 'Not on this computer yet', meta: 'Armory is getting it. It shows up in the folder soon.' };
			case 'keptCopy':
				return { readout: 'Your copy kept', tone: 'look', line: 'Your changes were kept as your own copy', meta: 'Your change is kept in its history. Nothing was lost.' };
			default:
				return { readout: 'Available', tone: 'ok', line: 'Available', meta: 'Anyone can open it to look. Check it out to make changes.' };
		}
	}

	/** The file's keys, as its state allows: Open first, always the screen's primary. */
	function detailKeysHtml(d) {
		var c = d.checkout || { state: 'available' };
		var here = d.status !== 'notOnThisComputer';
		var html = '<div class="detail-act">';
		if (here) html += key({ action: 'launch', key: 'd-open', cls: 'primary side-key', glyph: 'open', word: 'Open', path: d.path, title: 'Open ' + d.name + ' in its program' });
		var keys = '';
		if (c.state === 'available' && d.fileId) {
			keys += key({ action: 'checkOut', key: 'd-checkout', cls: 'tool', glyph: 'checkout', word: 'Check out', path: d.path });
			if (here) keys += key({ action: 'checkOutOpen', key: 'd-checkout-open', cls: 'tool', glyph: 'open', word: 'Check out and open', path: d.path });
		} else if (c.state === 'mine') {
			keys += key({ action: 'checkIn', key: 'd-checkin', cls: 'tool', glyph: 'checkin', word: 'Check in', path: d.path });
			keys += key({ action: 'undoCheckOut', key: 'd-undo', cls: 'tool', glyph: 'undo', word: 'Undo check out', path: d.path });
		}
		if ((c.state === 'other' || c.state === 'myOtherComputer') && d.canTakeBack) keys += key({ action: 'askTakeBack', key: 'd-takeback', cls: 'tool', glyph: 'takeback', word: 'Force check in', fileId: d.fileId });
		if (keys) html += '<div class="detail-keys">' + keys + '</div>';
		html += '<button class="textlink" type="button" data-action="showInFolder" data-path="' + esc(d.path) + '" data-key="show-in-folder">' + icon('folder') + '<span>Show in folder</span></button>';
		return html + '</div>';
	}

	function detailHtml(v) {
		var d = currentDetail();
		var w = detailWords(d);
		var me = myName(v);
		var html = '<article class="detail" aria-labelledby="detail-title">';
		html += titleBar(
			'h1',
			d.name,
			' id="detail-title" tabindex="-1" data-key="detail-title"',
			'<button class="key back-key" type="button" data-action="back" data-key="back" aria-label="Back to Home" title="Back to Home">' + icon('chev-left') + '</button>'
		);
		html +=
			'<p class="title-sub">' + kindChip(d.name) + '<span class="meta-text">' + esc(whereIs(d.path)) + '</span>' +
			// The one place a release that couldn't be checked shows: a small tag, never a notice.
			(d.releaseNotChecked ? chip('SolidWorks year not checked', 'look', 'year-tag') : '') + '</p>';
		html += '<div class="detail-grid">';

		html += '<div class="detail-side">';
		var detailThumb = thumbAddress(findRow(d.fileId) ? findRow(d.fileId).row : d);
		if (detailThumb) html += '<div class="detail-thumb">' + thumbImg(detailThumb, true) + '</div>';
		html += '<section class="display" data-tone="' + w.tone + '" aria-labelledby="holder-line">';
		html += '<p class="screen lcd"><span>' + esc(w.readout) + '</span></p>';
		html += '<h2 class="holder-line" id="holder-line">' + esc(glue(w.line)) + '</h2>';
		if (w.meta) html += '<p class="holder-meta">' + esc(w.meta) + '</p>';
		html += '</section>';
		html += detailKeysHtml(d);
		html += whoHtml(d);
		html += '</div>';

		html += '<section class="detail-main plate-recess" aria-labelledby="history-label"><div class="history-group brackets">';
		html += '<h2 class="section-label history-head" id="history-label"><span>History</span>' + (d.history ? count(d.history.length, 'entry', 'entries') : '') + '</h2>';
		if (!d.history) html += '<p class="hist-loading">Getting the history\u2026</p>';
		else {
			html += '<ol class="history-list list-well">';
			d.history.forEach(function (e) {
				html += historyEntry(e, me);
			});
			html += '</ol>';
		}
		html += '</div></section>';
		html += '</div></article>';
		return html;
	}

	/** Checked out: the person (initials, name, computer, how long, and how to reach
	 *  them), or that it is free. */
	function whoHtml(d) {
		var c = d.checkout || { state: 'available' };
		var html = '<section class="group top who-group" aria-labelledby="who-label"><h2 class="section-label" id="who-label">Checked out</h2>';
		html += '<div class="panel who-panel">';
		if (c.state !== 'available') {
			var isMe = c.state === 'mine' || c.state === 'myOtherComputer';
			html +=
				// Green only when it is checked out here; my other computer reads amber, as on its row.
				'<span class="avatar big" data-tone="' + (c.state === 'mine' ? 'ok' : 'look') + '" aria-hidden="true">' + esc(initials(c.name)) + '</span>' +
				'<div class="who-words"><p class="who-name">' + esc(isMe ? c.name + ' (you)' : c.name) + '</p>' +
				'<p class="who-where">' + esc(metaLine([c.device, c.since ? 'for ' + lasting(c.since) : ''])) + '</p>' +
				// Someone else's school email, so the student knows how to ask them.
				(!isMe && c.email ? '<p class="who-where who-email">' + esc(c.email) + '</p>' : '') +
				'</div>';
		} else {
			html +=
				'<span class="avatar big" data-tone="off" aria-hidden="true">' + icon('check') + '</span>' +
				'<div class="who-words"><p class="who-name">Available.</p><p class="who-where">Check it out to make changes.</p></div>';
		}
		return html + '</div></section>';
	}

	/** A history entry: who and when is the title (what a student scans for). A kept copy
	 *  is its own title and, when it is the student's, is marked YOUR COPY on a tinted
	 *  row; a routine one (HistoryEntryView.routine: saved while checked out, an earlier
	 *  save) is the ordinary record of work and keeps the neutral tone. A removal says so.
	 *  The size is in the tooltip, not the line. */
	function historyEntry(e, me) {
		// A save made while checked out is kept as a copy too, but it is the ordinary thing
		// (each save is backed up until the check in), so it reads like any other save. The
		// engine says which kept copies are routine; the page never guesses from the note.
		var saved = e.kind === 'keptCopy' && e.routine === true;
		var copy = e.kind === 'keptCopy' && !saved;
		var removed = e.kind === 'removed';
		var routine = saved || (e.kind === 'version' && /^(Checked in|Saved|Added to Armory)$/.test(e.note));
		var when = '<time datetime="' + esc(e.at) + '" title="' + esc(fullTime(e.at)) + '">' + esc(agoWhole(e.at)) + '</time>';
		var mine = copy && me && (e.author.toLowerCase() === me.toLowerCase() || firstName(e.author).toLowerCase() === firstName(me).toLowerCase());
		var chips = '';
		if (e.isCurrent) chips += chip('Current', 'ok');
		if (copy) chips += chip(mine ? 'Your copy' : firstName(e.author) + '\'s copy', 'look');
		if (removed) chips += chip('Removed', 'off');
		if (e.releaseNotChecked) chips += chip('Year not checked', 'look');
		return (
			'<li class="hist"' + (mine ? ' data-mine="true"' : '') + (copy ? ' data-copy="true"' : '') + (removed ? ' data-removed="true"' : '') + (e.bytes ? ' title="' + esc(bytes(e.bytes)) + '"' : '') + '>' +
			'<div class="hist-top"><span class="hist-title">' + (routine ? esc(e.author) + ' · ' + when : esc(e.note)) + '</span>' +
			(chips ? '<span class="hist-chips">' + chips + '</span>' : '') + '</div>' +
			'<div class="hist-meta">' + (routine ? esc(e.note) : esc(e.author) + ' · ' + when) + '</div></li>'
		);
	}

	/* ---- Settings sheet ---- */

	function settingsHtml(v) {
		var s = v.settings;
		var themes = [
			['system', 'Match Windows', 'Follows Windows'],
			['idea', 'IDEA', 'Dark'],
			['spaceWhite', 'Space White', 'Light']
		];
		var html = titleBar('h2', 'Settings', ' id="settings-title"', null, '<button class="key" type="button" data-action="closeSettings" data-key="set-done">Done</button>');

		html += '<section class="setting" aria-labelledby="set-root-label">';
		html += '<h3 class="section-label" id="set-root-label">Where your files are kept</h3>';
		html += '<div class="setting-row"><p class="path-plate" id="set-root-value">' + icon('folder') + '<span>' + esc(s.vaultRoot) + '</span></p>';
		html += '<button class="key" type="button" data-action="chooseVaultRoot" data-key="set-root" aria-describedby="set-root-label set-root-value">Change</button></div>';
		html += '<p class="setting-help">Armory keeps a copy of your team\'s files in this folder. Most people never change it.</p>';
		html += '</section>';

		html += '<section class="setting" aria-labelledby="set-start-label">';
		html += '<h3 class="section-label" id="set-start-label">Start Armory when I sign in</h3>';
		html +=
			'<button class="switch" type="button" data-action="toggleStart" data-key="set-start" aria-pressed="' + !!s.startAtSignIn + '" aria-labelledby="set-start-label set-start-word">' +
			'<span class="ts-glyph" aria-hidden="true"></span><span class="ts-word" id="set-start-word">' + (s.startAtSignIn ? 'On' : 'Off') + '</span></button>';
		html += '<p class="setting-help">When this is on, Armory opens by itself when you sign in to Windows, so your work always reaches the team.</p>';
		html += '</section>';

		html += '<section class="setting" aria-labelledby="set-theme-label">';
		html += '<h3 class="section-label" id="set-theme-label">Theme</h3>';
		html += '<div class="segmented" role="group" aria-labelledby="set-theme-label">';
		themes.forEach(function (t) {
			html +=
				'<button class="pad seg" type="button" data-action="theme" data-value="' + t[0] + '" data-key="set-theme-' + t[0] + '" aria-pressed="' + ((ui.themeWanted || s.theme) === t[0]) + '">' +
				'<span class="swatch" data-swatch="' + t[0] + '" aria-hidden="true"></span>' +
				'<span class="seg-words"><span class="seg-name">' + esc(t[1]) + '</span><span class="seg-sub">' + esc(t[2]) + '</span></span></button>';
		});
		html += '</div></section>';

		// Something wrong: a person's own report, and the folder of saved reports to hand over by hand.
		html += '<section class="setting" aria-labelledby="set-report-label">';
		html += '<h3 class="section-label" id="set-report-label">Something not working?</h3>';
		html += '<div class="setting-row">';
		html += '<button class="key" type="button" data-action="askReport" data-key="set-report" aria-haspopup="dialog">Report a problem</button>';
		html += '<button class="key" type="button" data-action="askFeedback" data-key="set-feedback" aria-haspopup="dialog">Send feedback</button>';
		html += '<span class="set-mine" id="set-mine-slot">' + mineKeyHtml() + '</span>';
		html += '<button class="textlink" type="button" data-action="openIncidents" data-key="set-incidents">' + icon('folder') + '<span>Open incidents folder</span></button>';
		html += '</div>';
		html += '<p class="setting-help">Armory keeps a short record of what it was doing when something goes wrong: file names, never what is in your files. A report sends your words with it. Feedback sends your words, and a picture of this window if you add one, with Armory\'s version and what it was doing.</p>';
		html += '</section>';
		return html;
	}

	function openSettings() {
		if (!ui.view || ui.view.connection !== 'signedIn') return;
		// Settings is a sheet over Home: from a file's detail, Home comes back under it.
		if (ui.screen !== 'home') {
			ui.screen = 'home';
			ui.detail = null;
			render();
			scroller.scrollTop = ui.homeScroll;
			setRecessTop(ui.homeRecess);
			mountLists(false);
		}
		// "Your feedback" shows in the sheet once the host says the website has it.
		readMyFeedback();
		sheet.innerHTML = settingsHtml(ui.view);
		if (!sheet.open) sheet.showModal();
		// The sheet itself takes focus, so a screen reader reads its name; Tab then
		// reaches each setting in order.
		sheet.focus();
	}

	function saveSettings(change) {
		var s = ui.view.settings;
		var next = { vaultRoot: s.vaultRoot, startAtSignIn: s.startAtSignIn, theme: s.theme };
		for (var k in change) next[k] = change[k];
		bridge.send('saveSettings', next);
	}

	/* ------------------------------------------------------ The small dialog */

	/*
	 * New folder, Rename folder, Delete folder and Force check in ask in one small housing.
	 * It is filled once when it opens and never redrawn by a host update, so the words a
	 * student is typing stay put. Its name field is the inset field recipe at 44px.
	 */
	function openAsk(kind, ctx, returnKey) {
		ui.ask = { kind: kind, ctx: ctx, returnKey: returnKey || null };
		ask.innerHTML = askHtml(kind, ctx);
		if (!ask.open) ask.showModal();
		var field = ask.querySelector('input, textarea');
		if (field) {
			field.focus();
			// A file's new name usually keeps its kind: only the part before the dot is picked.
			var dot = kind === 'renameFile' ? field.value.lastIndexOf('.') : -1;
			if (dot > 0) field.setSelectionRange(0, dot);
			// Words being written (a note brought back from Your feedback) keep going at their end.
			else if (kind === 'feedback') field.setSelectionRange(field.value.length, field.value.length);
			else field.select();
		} else (ask.querySelector('[data-ask-first]') || ask).focus();
	}

	function askHtml(kind, c) {
		var title;
		var body;
		var field = '';
		var ok;
		var danger = false;
		// A question whose answer locks the team out of files, or removes something, starts
		// on Cancel, so Enter never does it by accident.
		var cancelFirst = false;
		var extra = '';
		// Send feedback and Your feedback draw their own housing contents.
		if (kind === 'feedback') return feedbackHtml(c);
		if (kind === 'myFeedback') return myFeedbackHtml(c);
		if (kind === 'report') {
			title = 'Report a problem';
			body = 'Tell us what went wrong, or what would make Armory better. Your words go with a short record of what Armory was doing: file names, never what is in your files.';
			extra = reportKindsHtml(c.kind);
			field = areaHtml('What happened?');
			ok = 'Send';
		} else if (kind === 'checkOutAll') {
			title = 'Check out all';
			var them = c.count === 1 ? 'it' : 'them';
			body =
				'Check out ' + plural(c.count, 'file', 'files') + ' in ' + c.name + (c.inside ? ' and its folders' : '') + '? ' +
				'Nobody else can save ' + them + ' until you check ' + them + ' in.' +
				(c.held ? ' ' + plural(c.held, 'other file is', 'other files are') + ' checked out by someone else, and ' + (c.held === 1 ? 'stays' : 'stay') + ' with them.' : '');
			ok = 'Check out ' + plural(c.count, 'file', 'files');
			cancelFirst = true;
		} else if (kind === 'undoMine') {
			title = 'Undo all';
			body =
				'Undo all ' + plural(c.count, 'check out', 'check outs') + '? Each file goes back to the version from before you checked it out, and anyone can check it out. ' +
				'Changes you saved are kept as your own copies in each file\'s history, so nothing is lost.';
			ok = 'Undo ' + plural(c.count, 'check out', 'check outs');
			cancelFirst = true;
		} else if (kind === 'renameFile') {
			title = 'Rename file';
			body = 'Rename ' + c.name + ' in ' + c.where + '. A project keeps one file per name, so pick a name no other file in ' + c.project + ' has.';
			field = fieldHtml('New name', c.name);
			ok = 'Rename';
		} else if (kind === 'newFolder') {
			title = 'New folder';
			body = 'Make a folder in ' + c.where + '. Everyone on the team sees it.';
			field = fieldHtml('Folder name', '');
			ok = 'Make folder';
		} else if (kind === 'renameFolder') {
			title = 'Rename folder';
			body = 'Rename ' + c.name + ' in ' + c.parentWhere + '. Everyone on the team sees the new name, and its files keep their history.';
			field = fieldHtml('New name', c.name);
			ok = 'Rename';
		} else if (kind === 'deleteFolder') {
			title = 'Delete folder';
			body =
				'Delete ' + c.name + (c.files ? ' and the ' + plural(c.files, 'file', 'files') + ' in it' : '') + ' from ' + c.project + '? It goes for everyone on the team. ' +
				(c.files ? 'The history of every file is kept, so a file can be brought back later.' : 'It has no files in it.');
			ok = 'Delete folder';
			danger = true;
		} else {
			title = c.count === 1 ? 'Force check in' : 'Force check in all';
			body =
				'Force check in ' + c.what + '? ' + c.holders + (c.people === 1 ? ' has ' : ' have ') + (c.count === 1 ? 'it' : 'them') + ' checked out now. ' +
				'Any changes ' + c.who + ' hasn\'t checked in are kept as ' + c.whose + ' own copy in ' + (c.count === 1 ? 'the file\'s history' : 'each file\'s history') +
				', so nothing is lost. Then anyone can check ' + (c.count === 1 ? 'it' : 'them') + ' out.';
			ok = c.count === 1 ? 'Force check in' : 'Force check in ' + plural(c.count, 'file', 'files');
			danger = true;
		}
		return (
			titleBar('h2', title, ' id="ask-title"') +
			'<div class="ask-body">' +
			'<p class="ask-words" id="ask-words">' + esc(glue(body)) + '</p>' +
			extra +
			field +
			'<div class="ask-keys">' +
			'<button class="key' + (danger ? ' danger' : cancelFirst ? '' : ' primary') + '" type="button" data-action="askOk" data-key="ask-ok">' + esc(glue(ok)) + '</button>' +
			'<button class="key" type="button" data-action="askCancel" data-key="ask-cancel"' + (field || !(danger || cancelFirst) ? '' : ' data-ask-first="true"') + '>Cancel</button>' +
			'</div></div>'
		);
	}

	/** A report is a bug, an idea or something else: one choice of three, like the theme. */
	function reportKindsHtml(picked) {
		var kinds = [
			['bug', 'Bug', 'Something broke'],
			['idea', 'Idea', 'Something to add'],
			['other', 'Other', 'Anything else']
		];
		var html = '<div class="segmented report-kinds" role="group" aria-label="What kind of report">';
		kinds.forEach(function (k) {
			html +=
				'<button class="pad seg seg-plain" type="button" data-action="reportKind" data-value="' + k[0] + '" data-key="ask-kind-' + k[0] + '" aria-pressed="' + (picked === k[0]) + '">' +
				'<span class="seg-words"><span class="seg-name">' + esc(k[1]) + '</span><span class="seg-sub">' + esc(k[2]) + '</span></span></button>';
		});
		return html + '</div>';
	}

	function areaHtml(label) {
		return (
			'<label class="field-label label" for="ask-report">' + esc(label) + '</label>' +
			'<textarea class="field field-area" id="ask-report" data-key="ask-report" rows="5" maxlength="8000" spellcheck="true" aria-describedby="ask-words ask-error"></textarea>' +
			'<p class="field-error" id="ask-error" aria-live="polite"></p>'
		);
	}

	function fieldHtml(label, value) {
		return (
			'<label class="field-label label" for="ask-name">' + esc(label) + '</label>' +
			'<input class="field" id="ask-name" data-key="ask-name" type="text" value="' + esc(value) + '" maxlength="120" autocomplete="off" spellcheck="false" aria-describedby="ask-words ask-error" />' +
			'<p class="field-error" id="ask-error" aria-live="polite"></p>'
		);
	}

	/** Why a file's new name won't do, in plain words, or null when it will. */
	function fileNameProblem(name, c) {
		if (!name) return 'Type a name for the file.';
		if (BAD_NAME.test(name)) return 'A file name can\'t use any of these: \\ / : * ? " < > |';
		if (/^\.+$/.test(name) || /[. ]$/.test(name)) return 'A file name can\'t end with a dot or a space.';
		if (name === c.name) return 'That is already its name.';
		var ext = c.name.lastIndexOf('.') > 0 ? c.name.slice(c.name.lastIndexOf('.')).toLowerCase() : '';
		if (ext && name.slice(-ext.length).toLowerCase() !== ext) return 'Keep ' + c.name.slice(c.name.lastIndexOf('.')) + ' at the end, so SolidWorks can still open it.';
		if (c.taken[name.toLowerCase()] && name.toLowerCase() !== c.name.toLowerCase()) return c.project + ' already has a file named ' + name + '.';
		return null;
	}

	/** Why a folder name won't do, in plain words, or null when it will. */
	function nameProblem(name, c) {
		if (!name) return 'Type a name for the folder.';
		if (BAD_NAME.test(name)) return 'A folder name can\'t use any of these: \\ / : * ? " < > |';
		if (/^\.+$/.test(name) || /[. ]$/.test(name)) return 'A folder name can\'t end with a dot or a space.';
		if (c.kind === 'renameFolder' && name === c.name) return 'That is already its name.';
		var taken = (c.siblings || []).some(function (s) {
			return s.toLowerCase() === name.toLowerCase() && !(c.kind === 'renameFolder' && s.toLowerCase() === c.name.toLowerCase());
		});
		if (taken) return (c.kind === 'renameFolder' ? c.parentWhere : c.where) + ' already has a folder named ' + name + '.';
		return null;
	}

	function askOk() {
		var a = ui.ask;
		if (!a) return;
		var c = a.ctx;
		if (a.kind === 'report') {
			var area = ask.querySelector('#ask-report');
			var words = area.value.trim();
			if (!words) {
				ask.querySelector('#ask-error').textContent = 'Write a few words about what happened.';
				area.setAttribute('aria-invalid', 'true');
				area.focus();
				return;
			}
			act('reportProblem', { kind: c.kind, body: words }, { key: a.returnKey });
		} else if (a.kind === 'feedback') {
			// The dialog stays open until the host answers (sendFeedback, below).
			sendFeedback();
			return;
		} else if (a.kind === 'myFeedback') {
			return;
		} else if (a.kind === 'renameFile') {
			var fileInput = ask.querySelector('#ask-name');
			var newName = fileInput.value.trim();
			var wrong = fileNameProblem(newName, c);
			if (wrong) {
				ask.querySelector('#ask-error').textContent = wrong;
				fileInput.setAttribute('aria-invalid', 'true');
				fileInput.focus();
				return;
			}
			act('renameFile', { path: c.path, newName: newName }, { key: a.returnKey });
		} else if (a.kind === 'newFolder' || a.kind === 'renameFolder') {
			var input = ask.querySelector('#ask-name');
			var name = input.value.trim();
			var problem = nameProblem(name, { kind: a.kind, name: c.name, siblings: c.siblings, where: c.where, parentWhere: c.parentWhere });
			if (problem) {
				ask.querySelector('#ask-error').textContent = problem;
				input.setAttribute('aria-invalid', 'true');
				input.focus();
				return;
			}
			if (a.kind === 'newFolder') act('createFolder', { projectId: c.projectId, parent: c.folder, name: name }, { key: a.returnKey });
			else {
				act('renameFolder', { projectId: c.projectId, folder: c.folder, newName: name }, { key: a.returnKey });
				// When the host's next view has the new name, the browser goes with it.
				ui.follow = { projectId: c.projectId, from: c.folder, to: c.folder.split('/').slice(0, -1).concat([name]).join('/') };
			}
		} else if (a.kind === 'checkOutAll') act('checkOut', { paths: [c.path], open: false }, { key: a.returnKey });
		else if (a.kind === 'undoMine') act('undoCheckOut', { paths: mineFolders(ui.view) }, { key: a.returnKey, words: 'Undoing ' + plural(c.count, 'check out', 'check outs') + '...' });
		else if (a.kind === 'deleteFolder') act('deleteFolder', { projectId: c.projectId, folder: c.folder }, { key: a.returnKey });
		// One file is takeBack; more go in ONE takeBackAll, so the host forces them in one action
		// and one pass (one message per file ran a whole pass for each).
		else if (c.count === 1) act('takeBack', { fileId: c.fileIds[0] }, { key: a.returnKey });
		else act('takeBackAll', { fileIds: c.fileIds }, { key: a.returnKey });
		ask.close();
	}

	/** The folder questions' facts: where, what is beside it, how many files go with it. */
	function folderContext() {
		var place = browserPlace();
		var p = place.project;
		var folder = place.folder;
		var parent = folder.split('/').slice(0, -1).join('/');
		var names = function (path) {
			return (place.pi.children[path] || []).map(function (f) {
				return place.pi.folders[f].name;
			});
		};
		var rows = rowsUnder(place.pi, folder);
		return {
			projectId: p.id,
			project: p.name,
			folder: folder,
			path: folderPathOf(place.pi, folder),
			name: folder ? folder.split('/').pop() : p.name,
			where: crumbWords(p, folder),
			parentWhere: crumbWords(p, parent),
			inside: (place.pi.children[folder] || []).length > 0,
			files: place.pi.under[folder] || 0,
			// For Check out all: the files nobody has, and those someone else has.
			count: rows.filter(function (r) {
				return r.fileId && checkoutOf(r).state === 'available';
			}).length,
			held: rows.filter(function (r) {
				var st = checkoutOf(r).state;
				return r.fileId && (st === 'other' || st === 'myOtherComputer');
			}).length,
			siblingsHere: names(folder),
			siblingsParent: names(parent)
		};
	}

	function askFolder(kind, from) {
		var c = folderContext();
		if (kind === 'checkOutAll' && !c.count) return;
		if (kind !== 'newFolder' && kind !== 'checkOutAll' && !c.folder) return;
		c.siblings = kind === 'newFolder' ? c.siblingsHere : c.siblingsParent;
		openAsk(kind, c, from);
	}

	/** Rename one file (a notice's file that shares its name): where it is, and every name
	 *  the project already uses, so a name that is taken is refused before anything is sent. */
	function askRenameFile(path, from) {
		var root = String(path).split('/')[0];
		var project = null;
		var taken = {};
		Object.keys(ui.index.projects).forEach(function (id) {
			var pi = ui.index.projects[id];
			if (pi.root !== root) return;
			project = pi.project;
			rowsUnder(pi, '').forEach(function (r) {
				if (r.path !== path) taken[String(r.name).toLowerCase()] = true;
			});
		});
		var parts = String(path).split('/');
		var name = parts.pop();
		openAsk('renameFile', { path: path, name: name, where: parts.join(' \u203a '), project: project ? project.name : root, taken: taken }, from);
	}

	/** Force check in one file, or the picked files (or a folder's files) someone else has,
	 *  naming who has them. */
	function askTakeBack(hits, from) {
		hits = hits.filter(function (h) {
			var st = h ? checkoutOf(h.row).state : 'available';
			return st !== 'available' && st !== 'mine';
		});
		if (!hits.length) return;
		var people = {};
		hits.forEach(function (h) {
			people[checkoutOf(h.row).name || 'someone'] = true;
		});
		var names = Object.keys(people);
		var holders = names.length <= 2 ? names.join(' and ') : names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
		openAsk(
			'takeBack',
			{
				fileIds: hits.map(function (h) {
					return h.row.fileId;
				}),
				count: hits.length,
				people: names.length,
				holders: holders,
				what: hits.length === 1 ? hits[0].row.name : plural(hits.length, 'file', 'files'),
				who: names.length === 1 ? firstName(names[0]) : 'anyone',
				whose: names.length === 1 ? firstName(names[0]) + '\'s' : 'their'
			},
			from
		);
	}

	/* ------------------------------------------- Send feedback, Your feedback */

	/*
	 * Send feedback, the same as the website's (0.3.3): what kind (Bug, Idea, Praise, Other),
	 * the words, what was tried, the window or view it is about (filled in here, read only),
	 * and an optional picture of the Armory window. The picture is taken by the host from this
	 * page alone, with the dialog hidden, every email address shown as •••@domain and the
	 * file pictures hidden; it comes back as the very bytes that would be sent, shown here
	 * before anything goes, with a key to remove it. The dialog stays open until the host
	 * answers: a note that went closes it; one whose picture couldn't go keeps the words and
	 * offers "Send without the picture". Ctrl+Enter sends.
	 *
	 * "Your feedback" lists this account's notes and where each one is. There are no replies
	 * in Armory, and the list says so; when the website doesn't have it yet it is hidden.
	 */
	var FEEDBACK_KINDS = [
		['bug', 'Bug', 'Something broke'],
		['idea', 'Idea', 'Something to add'],
		['praise', 'Praise', 'Something you like'],
		['other', 'Other', 'Anything else']
	];
	var KIND_NAME = { bug: 'Bug', idea: 'Idea', praise: 'Praise', other: 'Other' };
	var STATUS_TONE = { new: 'off', seen: 'ok', resolved: 'ok', closed: 'off' };
	var TRIED_MAX = 1000;
	var AREA_MAX = 120;
	/** Every email address in a text node, masked while a picture is taken. */
	var ADDRESS = /[\w.+-]+@[\w-]+(\.[\w-]+)+/g;
	/** How long an answer about Your feedback stands before the host is asked again. */
	var FEEDBACK_FRESH = 60000;

	/** At most n characters as the website counts them (never half of a pair). */
	function cutChars(s, n) {
		var chars = Array.from(String(s || ''));
		return chars.length <= n ? String(s || '') : chars.slice(0, n).join('');
	}

	/** The window or view the note is about: "Settings", "File details: Gear.SLDPRT",
	 *  "Home > Robot 2027 > Drivetrain", or "Home". Team folder and file names, never an
	 *  address; at most 120 characters. */
	function feedbackArea(fromSettings) {
		var where = 'Home';
		if (fromSettings) where = 'Settings';
		else if (ui.screen === 'detail' && ui.detail) where = 'File details: ' + ui.detail.name;
		else if (ui.view && ui.view.connection === 'signedIn' && ui.index) {
			var place = browserPlace();
			if (place) where = ['Home', place.project.name].concat(place.folder ? place.folder.split('/') : []).join(' > ');
		}
		return cutChars(where, AREA_MAX);
	}

	/** Asks the host for Your feedback, at most once a minute. */
	function readMyFeedback() {
		var now = Date.now();
		if (ui.mineAsked && now - ui.mineAsked < FEEDBACK_FRESH) return;
		ui.mineAsked = now;
		bridge.send('readMyFeedback');
	}

	/** True while the list can be offered: the website has it (shown), or it couldn't be
	 *  read just now (offline, failed). Hidden when the website doesn't have it, or signed out. */
	function mineOffered() {
		var m = ui.myFeedback;
		return !!m && (m.state === 'shown' || m.state === 'offline' || m.state === 'failed');
	}

	/** The Settings sheet's "Your feedback" key, with how many notes there are. */
	function mineKeyHtml() {
		if (!mineOffered()) return '';
		var n = ui.myFeedback.state === 'shown' ? ui.myFeedback.notes.length : null;
		return (
			'<button class="key" type="button" data-action="openMyFeedback" data-key="set-mine" aria-haspopup="dialog"' +
			(n != null ? ' aria-label="Your feedback, ' + esc(plural(n, 'note', 'notes')) + '"' : '') + '>' +
			'Your feedback' + (n != null ? ' (' + esc(num(n)) + ')' : '') + '</button>'
		);
	}

	/** Opens Send feedback. draft: a note being written, brought back from Your feedback. */
	function openFeedback(fromSettings, returnKey, draft) {
		readMyFeedback();
		var d = draft || {};
		openAsk('feedback', { kind: d.kind || 'idea', area: d.area || feedbackArea(fromSettings), body: d.body || '', tried: d.tried || '' }, returnKey);
		ui.ask.shot = d.shot || null;
		ui.ask.withoutPicture = !!d.withoutPicture;
		paintShot();
		paintSendKey();
		paintTriedCount();
	}

	function feedbackHtml(c) {
		var html = '<div class="segmented feedback-kinds" role="group" aria-label="What kind of feedback">';
		FEEDBACK_KINDS.forEach(function (k) {
			html +=
				'<button class="pad seg seg-plain" type="button" data-action="reportKind" data-value="' + k[0] + '" data-key="ask-kind-' + k[0] + '" aria-pressed="' + (c.kind === k[0]) + '">' +
				'<span class="seg-words"><span class="seg-name">' + esc(k[1]) + '</span><span class="seg-sub">' + esc(k[2]) + '</span></span></button>';
		});
		html += '</div>';
		return (
			titleBar('h2', 'Send feedback', ' id="ask-title"') +
			'<div class="ask-body">' +
			'<p class="ask-words" id="ask-words">' +
			esc('Tell us what got in your way, what would make Armory better, or what you like. It goes to the IDEA team with Armory\'s version and what it was doing: file names, never what is in your files.') +
			'</p>' +
			html +
			'<label class="field-label label" for="ask-report">Your feedback</label>' +
			'<textarea class="field field-area" id="ask-report" data-key="ask-report" rows="5" maxlength="8000" spellcheck="true" aria-describedby="ask-words ask-error">' + esc(c.body) + '</textarea>' +
			'<label class="field-label label" for="ask-tried">What did you try? (optional)</label>' +
			'<textarea class="field field-tried" id="ask-tried" data-key="ask-tried" rows="3" maxlength="' + TRIED_MAX + '" spellcheck="true" aria-describedby="ask-tried-count">' + esc(c.tried) + '</textarea>' +
			'<p class="field-count" id="ask-tried-count" aria-live="polite"></p>' +
			'<p class="ask-about" id="ask-area"><span class="label">About</span> <span class="ask-about-where">' + esc(c.area) + '</span></p>' +
			'<div class="ask-shot" id="ask-shot"></div>' +
			'<p class="field-error" id="ask-error" aria-live="polite"></p>' +
			'<div class="ask-keys">' +
			'<span class="ask-mine" id="ask-mine-slot">' + mineLinkHtml() + '</span>' +
			'<button class="key primary" type="button" data-action="askOk" data-key="ask-ok">Send</button>' +
			'<button class="key" type="button" data-action="askCancel" data-key="ask-cancel">Cancel</button>' +
			'</div></div>'
		);
	}

	function mineLinkHtml() {
		return mineOffered() ? '<button class="textlink" type="button" data-action="openMyFeedback" data-key="ask-mine">Your feedback</button>' : '';
	}

	/** The picture part: the key that takes one, or the picture itself as it would be sent. */
	function shotHtml(a) {
		var s = a.shot;
		if (s)
			return (
				'<figure class="shot-figure">' +
				'<img class="shot-img" id="ask-shot-img" src="' + esc(s.url) + '" alt="The picture of this window that would be sent" draggable="false">' +
				'<figcaption class="shot-caption" id="ask-shot-caption">' +
				esc('This is the picture that will be sent: ' + num(s.width) + ' by ' + num(s.height) + ' pixels, ' + bytes(s.bytes) + '. Email addresses and file pictures are hidden.' +
					(s.scaled ? ' It was made smaller to fit 2 MB.' : '')) +
				'</figcaption></figure>' +
				'<button class="key" type="button" data-action="removeShot" data-key="ask-shot-remove">Remove picture</button>'
			);
		// The website can't take pictures yet (it said so, or it has no Your feedback either,
		// which came with them): nothing to offer. Offline, the host's answer says so instead.
		var m = ui.myFeedback;
		if (m && (m.state === 'missing' || (m.state === 'shown' && !m.pictures))) return '';
		return (
			'<button class="key" type="button" data-action="addShot" data-key="ask-shot" aria-describedby="ask-shot-help">Add a picture of this window</button>' +
			'<p class="setting-help" id="ask-shot-help">Only Armory\'s window is in it, with email addresses and file pictures hidden. You see it before it goes.</p>'
		);
	}

	function paintShot() {
		var box = ask.querySelector('#ask-shot');
		if (box && ui.ask && ui.ask.kind === 'feedback') box.innerHTML = shotHtml(ui.ask);
	}

	/** Send, or "Send without the picture" once the host offered it and no picture is on. */
	function paintSendKey() {
		var ok = ask.querySelector('[data-key="ask-ok"]');
		if (ok && ui.ask && !ok.querySelector('.spin')) ok.textContent = ui.ask.withoutPicture && !ui.ask.shot ? 'Send without the picture' : 'Send';
	}

	/** Near the limit, how much of it is used. */
	function paintTriedCount() {
		var box = ask.querySelector('#ask-tried');
		var out = ask.querySelector('#ask-tried-count');
		if (!box || !out) return;
		var n = box.value.length;
		out.textContent = n >= TRIED_MAX * 0.8 ? num(n) + ' of ' + num(TRIED_MAX) + ' characters' : '';
	}

	function setAskError(words) {
		var out = ask.querySelector('#ask-error');
		if (out) out.textContent = words || '';
	}

	/** The note as it stands, so Your feedback can bring it back. */
	function feedbackDraft() {
		var a = ui.ask;
		return {
			kind: a.ctx.kind,
			area: a.ctx.area,
			body: ask.querySelector('#ask-report').value,
			tried: ask.querySelector('#ask-tried').value,
			shot: a.shot,
			withoutPicture: a.withoutPicture
		};
	}

	function sendFeedback() {
		var a = ui.ask;
		if (!a || a.sending || a.shooting) return;
		var box = ask.querySelector('#ask-report');
		var words = box.value.trim();
		if (!words) {
			setAskError('Write a few words first.');
			box.setAttribute('aria-invalid', 'true');
			box.focus();
			return;
		}
		box.removeAttribute('aria-invalid');
		setAskError('');
		var tried = ask.querySelector('#ask-tried').value.trim();
		a.sending = act('sendFeedback', { kind: a.ctx.kind, body: words, tried: tried || null, area: a.ctx.area, shot: a.shot ? a.shot.id : null }, { key: 'ask-ok' });
		lockFeedback(true);
	}

	/** While a note is on its way, nothing in the dialog changes it. */
	function lockFeedback(on) {
		Array.prototype.forEach.call(ask.querySelectorAll('textarea'), function (t) {
			t.readOnly = on;
		});
		Array.prototype.forEach.call(ask.querySelectorAll('button'), function (b) {
			if (b.getAttribute('data-key') !== 'ask-ok') b.disabled = on;
		});
	}

	/** The host's answer to the note this dialog sent. */
	function feedbackAnswered(m) {
		var a = ui.ask;
		a.sending = null;
		lockFeedback(false);
		if (m.ok) {
			ask.close();
			showResult(true, m.message);
		} else {
			// The words stay in the dialog, and so does what the host said; the working line goes.
			showResult(false, '');
			if (m.offer === 'withoutPicture') {
				a.withoutPicture = true;
				a.shot = null;
				paintShot();
			}
			setAskError(m.message);
			paintSendKey();
			var ok = ask.querySelector('[data-key="ask-ok"]');
			if (ok) ok.focus();
		}
		routeAnswered('sent');
	}

	/* ---- The picture of the window ---- */

	/** Hides the dialog and what a picture must not show, waits two frames so the window is
	 *  drawn that way, then asks the host for the picture. */
	function takeShot() {
		var a = ui.ask;
		if (!a || a.kind !== 'feedback' || a.shooting || a.sending) return;
		a.shooting = true;
		setAskError('');
		hideForShot();
		requestAnimationFrame(function () {
			requestAnimationFrame(function () {
				if (ui.ask !== a) return restoreAfterShot();
				a.shooting = bridge.send('captureWindow', { width: Math.max(1, Math.round(window.innerWidth)), height: Math.max(1, Math.round(window.innerHeight)) });
			});
		});
	}

	function hideForShot() {
		var masked = [];
		var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
		for (var node = walker.nextNode(); node; node = walker.nextNode()) {
			var text = node.nodeValue;
			if (text.indexOf('@') < 0) continue;
			var hidden = text.replace(ADDRESS, function (address) {
				return '•••@' + address.split('@')[1];
			});
			if (hidden !== text) {
				masked.push([node, text]);
				node.nodeValue = hidden;
			}
		}
		ui.shooting = { masked: masked, held: [] };
		ask.classList.add('away');
		document.documentElement.classList.add('shooting');
	}

	/** Puts back every address, the pictures and the dialog, then the host messages that
	 *  waited (a view or activity drawn meanwhile would have shown an address). */
	function restoreAfterShot() {
		var s = ui.shooting;
		if (!s) return;
		ui.shooting = null;
		s.masked.forEach(function (m) {
			m[0].nodeValue = m[1];
		});
		document.documentElement.classList.remove('shooting');
		ask.classList.remove('away');
		s.held.forEach(onHost);
	}

	/** True when a host message must wait until the picture is taken. */
	function holdForShot(message) {
		if (!ui.shooting || (message.type !== 'view' && message.type !== 'activity' && message.type !== 'fileDetail')) return false;
		ui.shooting.held.push(message);
		return true;
	}

	/** The host's answer to captureWindow. */
	function shotTaken(m) {
		restoreAfterShot();
		var a = ui.ask;
		if (!a || a.kind !== 'feedback' || a.shooting !== m.requestId) return;
		a.shooting = null;
		if (m.ok) a.shot = { id: m.id, url: m.url, width: m.width, height: m.height, bytes: m.bytes, scaled: !!m.scaled };
		else setAskError(m.message || 'Armory couldn\'t take a picture of its window. You can send your note without one.');
		paintShot();
		paintSendKey();
		var next = ask.querySelector('[data-key="ask-shot-remove"]') || ask.querySelector('[data-key="ask-shot"]');
		if (next) next.focus({ preventScroll: true });
		if (ui.routeShot === 'offer') {
			// The demo's "can't go" state: the picture is sent, and its answer is awaited.
			ui.routeShot = null;
			waitFor('sent');
			sendFeedback();
		}
		routeAnswered('shot');
	}

	/* ---- Your feedback ---- */

	/** draft: the note being written when the list was opened from Send feedback. */
	function openMyFeedback(returnKey, draft) {
		readMyFeedback();
		openAsk('myFeedback', { draft: draft || null }, returnKey);
	}

	function myFeedbackHtml(c) {
		return (
			titleBar('h2', 'Your feedback', ' id="ask-title"') +
			'<div class="ask-body">' +
			'<div class="mine-list" id="mine-list" aria-live="polite">' + mineListHtml() + '</div>' +
			'<p class="setting-help mine-foot" id="ask-words">The IDEA team reads every note. There are no replies in Armory: the status shows where yours is.</p>' +
			'<div class="ask-keys">' +
			(c.draft ? '<button class="key" type="button" data-action="myFeedbackBack" data-key="mine-back">Back to your note</button>' : '') +
			'<button class="key primary" type="button" data-action="askCancel" data-key="mine-done" data-ask-first="true">Done</button>' +
			'</div></div>'
		);
	}

	function mineListHtml() {
		var m = ui.myFeedback;
		if (!m) return '<p class="ask-words">Getting your feedback...</p>';
		if (m.state !== 'shown') return '<p class="ask-words">' + esc(m.message || 'Your feedback isn\'t here yet.') + '</p>';
		if (!m.notes.length) return '<p class="ask-words">You haven\'t sent any feedback from this account yet.</p>';
		return (
			'<ul class="mine-notes">' +
			m.notes
				.map(function (n) {
					var when = '<time datetime="' + esc(n.createdAt) + '" title="' + esc(fullTime(n.createdAt)) + '">' + esc(agoWhole(n.createdAt)) + '</time>';
					var facts = [n.deviceName ? 'from ' + esc(n.deviceName) : '', n.area ? 'about ' + esc(n.area) : '', n.hasScreenshot ? 'with a picture' : ''];
					return (
						'<li class="mine-note">' +
						'<div class="mine-top"><span class="mine-kind">' + esc(KIND_NAME[n.kind] || 'Other') + '</span>' + chip(n.statusWords, STATUS_TONE[n.status] || 'off') + '</div>' +
						'<p class="mine-body">' + esc(n.body) + '</p>' +
						(n.tried ? '<p class="mine-tried"><span class="label">Tried</span> ' + esc(n.tried) + '</p>' : '') +
						'<p class="mine-meta">' + metaLine([when].concat(facts)) + '</p>' +
						'</li>'
					);
				})
				.join('') +
			'</ul>'
		);
	}

	/** The host's answer to readMyFeedback. */
	function myFeedbackRead(m) {
		ui.myFeedback = { state: m.state, pictures: !!m.pictures, message: m.message || null, notes: m.notes || [] };
		var slot = document.getElementById('set-mine-slot');
		if (slot && sheet.open) slot.innerHTML = mineKeyHtml();
		if (ui.ask && ui.ask.kind === 'myFeedback') {
			var list = ask.querySelector('#mine-list');
			if (list) list.innerHTML = mineListHtml();
		}
		if (ui.ask && ui.ask.kind === 'feedback' && !ui.ask.sending) {
			var link = ask.querySelector('#ask-mine-slot');
			if (link) link.innerHTML = mineLinkHtml();
			if (!ui.ask.shot) paintShot();
		}
		routeAnswered('mine');
	}

	/* ---- The demo's places that wait for an answer ---- */

	/** A demo route opened something that waits for the host (the demo answers at once):
	 *  the page says it is ready only once the answer is drawn. */
	function waitFor(name) {
		ui.waits[name] = true;
	}

	function routeAnswered(name) {
		if (!ui.waits[name]) return;
		delete ui.waits[name];
		if (ui.readyWanted && !Object.keys(ui.waits).length) {
			ui.readyWanted = false;
			markReady();
		}
	}

	/* ------------------------------------------------------------- Actions */

	/* ------------------------------------------------------------- Working */

	/*
	 * The instant a key is pressed the page says it heard. The key turns busy (a small
	 * spinner in place of its glyph, aria-busy, and a second press does nothing) until the
	 * host answers that action by its requestId; the line at the window's foot says what
	 * is under way in plain words ("Checking out Bracket.SLDPRT..."), with a spinner; and
	 * the rows it touches say so ("Checking out...") in place of who has them. The answer
	 * replaces all of it. The spinner holds still when the student asks for less motion.
	 */
	function spinHtml() {
		return '<span class="spin" aria-hidden="true"></span>';
	}

	/** The requestId of the action a control is waiting on, or null. */
	function busyKey(dataKey) {
		for (var id in ui.pending) if (ui.pending[id].key && ui.pending[id].key === dataKey) return id;
		return null;
	}

	/** A busy control carries its request (data-req), so its answer can end it in place. */
	function busyAttrs(dataKey) {
		var id = busyKey(dataKey);
		return id ? ' aria-busy="true" aria-disabled="true" data-req="' + esc(id) + '"' : '';
	}

	/** Its glyph, with the spinner beside it while it is busy (the style shows one of them). */
	function busyGlyph(dataKey, glyph) {
		return (busyKey(dataKey) ? spinHtml() : '') + icon(glyph);
	}

	/** What a row says while an action on it is under way: the words, and the request. */
	function pendingOf(r) {
		for (var id in ui.pending) {
			var p = ui.pending[id];
			if (!p.row) continue;
			if (r.fileId && p.fileIds.indexOf(r.fileId) >= 0) return { id: id, words: p.row };
			for (var i = 0; i < p.paths.length; i++) if (r.path === p.paths[i] || String(r.path).indexOf(p.paths[i] + '/') === 0) return { id: id, words: p.row };
		}
		return null;
	}

	/** The working label a row shows ahead of who has it (the style hides who has it while
	 *  the label is there). */
	function pendingHtml(r) {
		var p = pendingOf(r);
		return p ? '<span class="row-avail row-pending" data-req="' + esc(p.id) + '">' + spinHtml() + esc(p.words) + '</span>' : '';
	}

	/** An action was sent: its key and the rows it touches say so, in place (no redraw, so
	 *  focus and scroll stay where they are; rows a long list draws later say it as drawn). */
	function startWorking(id) {
		var p = ui.pending[id];
		if (p.key)
			Array.prototype.forEach.call(document.querySelectorAll('[data-key="' + String(p.key).replace(/["\\]/g, '') + '"]'), function (el) {
				el.setAttribute('aria-busy', 'true');
				el.setAttribute('aria-disabled', 'true');
				el.setAttribute('data-req', id);
				if (!el.querySelector('.spin')) el.insertAdjacentHTML('afterbegin', spinHtml());
			});
		if (!p.row || !ui.index) return;
		Object.keys(ui.index.byPath).forEach(function (path) {
			var r = ui.index.byPath[path].row;
			var mine = pendingOf(r);
			if (!r.fileId || !mine || mine.id !== id) return;
			[r.fileId, 'mine:' + r.fileId].forEach(function (vkey) {
				var line = document.querySelector('li[data-vkey="' + String(vkey).replace(/["\\]/g, '') + '"] .row-line');
				if (line && !line.querySelector('.row-pending')) line.insertAdjacentHTML('afterbegin', pendingHtml(r));
			});
		});
	}

	/** An action was answered: its controls and rows go back as they were, in place (no
	 *  redraw, so focus and scroll stay exactly where they are). */
	function endWorking(id) {
		Array.prototype.forEach.call(document.querySelectorAll('[data-req="' + String(id).replace(/["\\]/g, '') + '"]'), function (el) {
			if (el.classList.contains('row-pending')) {
				el.parentNode.removeChild(el);
				return;
			}
			el.removeAttribute('aria-busy');
			el.removeAttribute('aria-disabled');
			el.removeAttribute('data-req');
			var spin = el.querySelector('.spin');
			if (spin) spin.parentNode.removeChild(spin);
		});
	}

	function leaf(path) {
		return String(path).split('/').pop();
	}

	/** "Bracket.SLDPRT", or "3 files" for a folder or several. */
	function filesWords(paths) {
		if (paths.length === 1 && ui.index && ui.index.byPath[paths[0]]) return leaf(paths[0]);
		var n = 0;
		if (ui.index)
			Object.keys(ui.index.byPath).forEach(function (path) {
				for (var i = 0; i < paths.length; i++)
					if (path === paths[i] || path.indexOf(paths[i] + '/') === 0) {
						n++;
						break;
					}
			});
		return n ? plural(n, 'file', 'files') : paths.length === 1 ? leaf(paths[0]) : plural(paths.length, 'file', 'files');
	}

	/** The working line for an action, and what its rows say meanwhile. */
	function workingOf(type, f) {
		var paths = f.paths || (f.path ? [f.path] : []);
		var row = findRow(f.fileId);
		switch (type) {
			case 'checkOut':
				return { line: (f.open ? 'Checking out and opening ' : 'Checking out ') + filesWords(paths) + '...', row: 'Checking out...' };
			case 'checkIn':
				return { line: 'Checking in ' + filesWords(paths) + '...', row: 'Checking in...' };
			case 'undoCheckOut':
				return { line: 'Undoing the check out of ' + filesWords(paths) + '...', row: 'Undoing the check out...' };
			case 'launchFile':
				return { line: 'Opening ' + leaf(f.path) + '...', row: null };
			case 'takeBack':
				return { line: 'Force checking in ' + (row ? row.row.name : 'the file') + '...', row: 'Force checking in...' };
			case 'takeBackAll':
				return { line: 'Force checking in ' + plural(f.fileIds.length, 'file', 'files') + '...', row: 'Force checking in...' };
			case 'renameFile':
				return { line: 'Renaming ' + leaf(f.path) + ' to ' + f.newName + '...', row: 'Renaming...' };
			case 'createFolder':
				return { line: 'Making the folder ' + f.name + '...', row: null };
			case 'renameFolder':
				return { line: 'Renaming ' + leaf(f.folder) + ' to ' + f.newName + '...', row: null };
			case 'deleteFolder':
				return { line: 'Deleting ' + leaf(f.folder) + '...', row: null };
			case 'addFiles':
				return { line: 'Choosing files to add...', row: null };
			case 'dropFiles':
				return { line: 'Adding files...', row: null };
			case 'reportProblem':
				return { line: 'Sending your report...', row: null };
			case 'sendFeedback':
				return { line: 'Sending your feedback...', row: null };
		}
		return { line: null, row: null };
	}

	/** Sends an action and shows at once that it is under way; its actionResult comes back
	 *  to the quiet line at the foot. how: { key: the control pressed, words: a line of its own }. */
	function act(type, fields, how) {
		var id = bridge.send(type, fields);
		if (!id) return id;
		how = how || {};
		var w = workingOf(type, fields || {});
		ui.pending[id] = {
			type: type,
			key: how.key || null,
			paths: (fields && (fields.paths || (fields.path ? [fields.path] : []))) || [],
			fileIds: fields && fields.fileId ? [fields.fileId] : (fields && fields.fileIds) || [],
			row: w.row
		};
		showWorking(how.words || w.line);
		startWorking(id);
		return id;
	}

	/** The line at the foot while an action is under way: its words and a spinner. */
	function showWorking(words) {
		if (!words) return;
		clearTimeout(ui.resultTimer);
		resultWord.textContent = words;
		resultBox.setAttribute('data-tone', 'ok');
		resultBox.setAttribute('data-working', 'true');
		resultBox.setAttribute('aria-busy', 'true');
		resultBox.setAttribute('data-on', 'true');
	}

	/** The host's word on an action: a small tag at the window's foot, read out by a
	 *  screen reader, that fades after a while. Never an alert, never a focus change. */
	function showResult(ok, message) {
		if (!message) {
			// An answer with nothing to say (a file picker closed): the working line goes.
			if (resultBox.getAttribute('data-working') === 'true') resultBox.setAttribute('data-on', 'false');
			resultBox.removeAttribute('data-working');
			resultBox.removeAttribute('aria-busy');
			return;
		}
		resultBox.removeAttribute('data-working');
		resultBox.removeAttribute('aria-busy');
		resultWord.textContent = message;
		resultBox.setAttribute('data-tone', ok ? 'ok' : 'look');
		resultBox.setAttribute('data-on', 'true');
		var glyph = resultBox.querySelector('use');
		if (glyph) glyph.setAttribute('href', ok ? '#i-check' : '#i-note');
		clearTimeout(ui.resultTimer);
		ui.resultTimer = setTimeout(function () {
			resultBox.setAttribute('data-on', 'false');
		}, 8000);
	}

	function runNotice(keyName) {
		var n = (ui.view.notices || []).filter(function (x) {
			return x.key === keyName;
		})[0];
		if (!n || !n.action) return;
		var a = n.action;
		var paths = a.paths || [];
		switch (a.command) {
			case 'expand':
				toggleExpand(n.key);
				break;
			case 'dismissNotice':
				bridge.send('dismissNotice', { key: n.key });
				break;
			case 'checkOut':
				act('checkOut', { paths: paths, open: false });
				break;
			case 'checkIn':
			case 'undoCheckOut':
				act(a.command, { paths: paths });
				break;
			case 'launchFile':
				if (paths[0]) act('launchFile', { path: paths[0] });
				break;
			case 'showInFolder':
				if (paths[0]) bridge.send('showInFolder', { path: paths[0] });
				break;
			case 'openFile':
				var first = (n.items || []).filter(function (it) {
					return it.fileId;
				})[0];
				if (first) openFile(first.fileId, 'nt-act-' + n.key);
				break;
		}
	}

	function toggleExpand(keyName) {
		if (ui.expanded[keyName]) delete ui.expanded[keyName];
		else ui.expanded[keyName] = true;
		render();
	}

	/* ------------------------------------------------------- Long lists */

	/*
	 * A long list draws only the rows near the view: a spacer above them and one below
	 * (heights through CSSOM) hold the room of the rows left out, so the scroll bar and
	 * "N more files below" tell the truth. Every row is --vrow-h tall. The row Tab enters
	 * on, the focused row and the row Back returns to are always drawn too, with spacers
	 * between, so neither focus nor Back ever lands on nothing.
	 */
	var lists = {};

	function registerList(def) {
		def.active = clamp(ui.active[def.id] || 0, 0, Math.max(0, def.items.length - 1));
		lists[def.id] = def;
		return '<ul class="vlist' + (def.cls ? ' ' + def.cls : '') + '" id="' + def.id + '" aria-label="' + esc(def.label) + '" data-total="' + def.items.length + '"></ul>';
	}

	function rowHeight(ul) {
		return parseFloat(getComputedStyle(ul).getPropertyValue('--vrow-h')) || 64;
	}

	/** The list index a control key points at ("row-f-wheel-hub" is f-wheel-hub's row). */
	function indexOfKey(list, controlKey) {
		if (!controlKey) return -1;
		if (!list.keys) {
			list.keys = {};
			list.items.forEach(function (x, i) {
				list.keys[list.key(x)] = i;
			});
		}
		var k = String(controlKey).replace(/^[a-z]+-/, '');
		return k in list.keys ? list.keys[k] : -1;
	}

	function mountLists(blind) {
		Object.keys(lists).forEach(function (id) {
			mountList(lists[id], blind);
		});
	}

	/** Draws the rows a list needs now. `blind` draws where it was last, without
	 *  measuring anything (so a fresh page is its full height before any layout read). */
	function mountList(list, blind) {
		var ul = document.getElementById(list.id);
		if (!ul) return;
		var n = list.items.length;
		var h = (list.h = rowHeight(ul));
		var a;
		var b;
		if (blind) {
			var r = ui.vrange[list.id] || [0, 2 * OVERSCAN];
			a = clamp(r[0], 0, n);
			b = clamp(Math.max(r[1], a + 2 * OVERSCAN), 0, n);
		} else {
			var sc = list.scrollEl() || scroller;
			var sr = sc.getBoundingClientRect();
			var ur = ul.getBoundingClientRect();
			var first = Math.floor((sr.top - ur.top) / h);
			var last = Math.ceil((sr.bottom - ur.top) / h);
			a = clamp(first - OVERSCAN, 0, n);
			b = clamp(last + OVERSCAN, 0, n);
			if (b <= a) {
				// The list is out of view: keep a few rows at the edge nearest the view.
				if (first >= n) {
					a = Math.max(0, n - OVERSCAN);
					b = n;
				} else {
					a = 0;
					b = Math.min(n, OVERSCAN);
				}
			}
		}
		ui.vrange[list.id] = [a, b];
		var want = {};
		for (var i = a; i < b; i++) want[i] = true;
		want[list.active] = n > 0;
		ui.pin.forEach(function (k) {
			var at = indexOfKey(list, k);
			if (at >= 0) want[at] = true;
		});
		var idx = Object.keys(want)
			.filter(function (k) {
				return want[k];
			})
			.map(Number)
			.sort(function (x, y) {
				return x - y;
			});

		// Reuse the rows already drawn; parse the new ones in one go.
		var have = {};
		for (var c = ul.firstElementChild; c; c = c.nextElementSibling) if (c.hasAttribute('data-vkey')) have[c.getAttribute('data-vkey')] = c;
		var nodes = [];
		var fresh = [];
		idx.forEach(function (i) {
			var x = list.items[i];
			var k = list.key(x);
			if (have[k] && have[k].getAttribute('data-i') === String(i)) nodes.push(have[k]);
			else {
				nodes.push(null);
				fresh.push({ at: nodes.length - 1, html: list.row(x, i, i === list.active) });
			}
		});
		if (fresh.length) {
			var tpl = document.createElement('template');
			tpl.innerHTML = fresh
				.map(function (f) {
					return f.html;
				})
				.join('');
			var made = tpl.content.children;
			var arr = [];
			for (var m = 0; m < made.length; m++) arr.push(made[m]);
			fresh.forEach(function (f, j) {
				nodes[f.at] = arr[j];
			});
		}
		// Spacers between runs, and above and below.
		var out = [];
		var prev = -1;
		idx.forEach(function (i, j) {
			var gap = i - prev - 1;
			if (gap > 0 || (j === 0 && i === 0)) out.push(spacer(gap * h));
			out.push(nodes[j]);
			prev = i;
		});
		out.push(spacer((n - prev - 1) * h));
		if (!idx.length) out = [spacer(n * h)];

		var keep = new Set(out);
		Array.prototype.slice.call(ul.children).forEach(function (el) {
			if (!keep.has(el)) ul.removeChild(el);
		});
		var ref = ul.firstChild;
		out.forEach(function (node) {
			if (node === ref) ref = ref.nextSibling;
			else ul.insertBefore(node, ref);
		});
	}

	function spacer(px) {
		var li = document.createElement('li');
		li.className = 'vspacer';
		li.setAttribute('aria-hidden', 'true');
		li.style.height = Math.max(0, px) + 'px';
		return li;
	}

	/** Moves the row Tab enters the list on. Only drawn rows carry tabindex. */
	function setActive(list, i) {
		var ul = document.getElementById(list.id);
		if (!ul || i === list.active) return;
		var roves = function (at, t) {
			var li = ul.querySelector('li[data-i="' + at + '"]');
			if (!li) return;
			var ctl = li.querySelectorAll('[data-rove]');
			for (var j = 0; j < ctl.length; j++) ctl[j].setAttribute('tabindex', t);
		};
		roves(list.active, '-1');
		list.active = i;
		ui.active[list.id] = i;
		roves(i, '0');
	}

	/** Arrow keys, Home, End and Page keys move between a list's rows, scrolling and
	 *  drawing as they go, and keep to the same kind of key (row, pick, Open). */
	function moveInList(list, to, role) {
		var ul = document.getElementById(list.id);
		var n = list.items.length;
		to = clamp(to, 0, n - 1);
		setActive(list, to);
		var sc = list.scrollEl() || scroller;
		var sr = sc.getBoundingClientRect();
		var ur = ul.getBoundingClientRect();
		var top = ur.top + to * list.h;
		var bottom = top + list.h;
		var bar = document.querySelector('.sel-bar');
		var shade = bar && sc.contains(bar) ? bar.getBoundingClientRect().height + 8 : 8;
		if (top < sr.top + shade) sc.scrollTop -= sr.top + shade - top;
		else if (bottom > sr.bottom - 8) sc.scrollTop += bottom - (sr.bottom - 8);
		ui.pin = [];
		mountList(list, false);
		var li = ul.querySelector('li[data-i="' + to + '"]');
		if (!li) return;
		var ctl = li.querySelector('[data-rove="' + role + '"]') || li.querySelector('[data-rove="row"]');
		if (ctl) ctl.focus({ preventScroll: true });
		updateCues();
	}

	/* ------------------------------------------------------ Scroll cues */

	/*
	 * A region with more below says so: a fade at its foot and a small tag counting the
	 * files still out of sight, counted from the data for a long list (most of its rows
	 * are not drawn). The wide Home's recessed column has its own; the window has one for
	 * everything else. Paint only, no pointer events, hidden from screen readers.
	 */
	function below(box) {
		if (box.scrollHeight <= box.clientHeight + 1) return null;
		if (box.scrollTop + box.clientHeight >= box.scrollHeight - 2) return null;
		var limit = box.getBoundingClientRect().bottom - 28;
		var n = 0;
		var files = true;
		var rows = box.querySelectorAll('.row, .attn-card, .history-list > li');
		for (var i = 0; i < rows.length; i++) {
			if (rows[i].closest('.vlist')) continue;
			if (rows[i].getBoundingClientRect().top >= limit) {
				n++;
				if (!rows[i].matches('.row')) files = false;
			}
		}
		Object.keys(lists).forEach(function (id) {
			var l = lists[id];
			var ul = document.getElementById(id);
			if (!ul || !l.h || (l.scrollEl() || scroller) !== box) return;
			var ur = ul.getBoundingClientRect();
			var k = clamp(Math.ceil((limit - ur.top) / l.h), 0, l.items.length);
			n += l.items.length - k;
			for (var j = k; j < l.items.length && files; j++) if (l.items[j].kind !== 'file' && l.items[j].kind !== 'mine') files = false;
		});
		return { n: n, files: files };
	}

	function setCue(el, more) {
		if (!el) return;
		el.setAttribute('data-on', more ? 'true' : 'false');
		var word = el.querySelector('.cue-word');
		var text = !more ? '' : more.n && more.files ? plural(more.n, 'more file', 'more files') + ' below' : 'More below';
		if (word.textContent !== text) word.textContent = text;
	}

	function updateCues() {
		var r = recessEl();
		setCue(document.getElementById('recess-cue'), r && getComputedStyle(r).overflowY !== 'visible' ? below(r) : null);
		setCue(windowCue, below(scroller));
	}

	var framed = false;
	function onScroll() {
		if (framed) return;
		framed = true;
		requestAnimationFrame(function () {
			framed = false;
			ui.pin = [activeKey(), ui.returnKey];
			mountLists(false);
			updateCues();
		});
	}

	/* --------------------------------------------------------- Navigation */

	function partialDetail(fileId) {
		var found = findRow(fileId);
		if (found) {
			return {
				fileId: fileId,
				name: found.row.name,
				path: found.row.path,
				project: found.project.name,
				folder: found.folder.path,
				status: found.row.status,
				checkout: found.row.checkout,
				releaseNotChecked: found.row.releaseNotChecked,
				canTakeBack: found.project.canTakeBack,
				history: null
			};
		}
		var mine = (ui.view.myFiles || []).filter(function (f) {
			return f.fileId === fileId;
		})[0];
		var path = mine ? mine.path : '';
		return {
			fileId: fileId,
			name: mine ? mine.name : 'File',
			path: path,
			project: path.split('/')[0] || '',
			folder: '',
			status: mine && mine.status ? mine.status : 'synced',
			checkout: mine ? mine.checkout : { state: 'available', label: 'Available', name: null, email: null, device: null, since: null },
			releaseNotChecked: false,
			canTakeBack: false,
			history: null
		};
	}

	function focusHeading() {
		var h = document.getElementById('detail-title');
		if (h) h.focus({ preventScroll: true });
	}

	/** Moves the view to a file's detail: the screen changes, scrolls to the top and the heading takes focus. */
	function openFile(fileId, fromKey) {
		if (ui.screen === 'home') {
			ui.homeScroll = scroller.scrollTop;
			ui.homeRecess = recessTop();
		}
		ui.returnKey = fromKey || null;
		ui.screen = 'detail';
		ui.detail = partialDetail(fileId);
		ui.waitingForDetail = true;
		bridge.send('openFile', { fileId: fileId });
		render();
		scroller.scrollTop = 0;
		focusHeading();
		updateCues();
	}

	function back() {
		ui.screen = 'home';
		ui.detail = null;
		render();
		scroller.scrollTop = ui.homeScroll;
		setRecessTop(ui.homeRecess);
		mountLists(false);
		var target = ui.returnKey ? document.querySelector('[data-key="' + sel(ui.returnKey) + '"]') : null;
		(target || document.querySelector('[data-key="hdr-vault"]')).focus({ preventScroll: true });
		if (target && target.scrollIntoView) target.scrollIntoView({ block: 'nearest' });
		updateCues();
	}

	/** Into a folder (or back up to one): the selection is for one folder at a time, and
	 *  the list starts at its top. */
	function goFolder(path, fromKey) {
		var place = browserPlace();
		ui.folders[place.project.id] = path;
		clearPicked();
		ui.active['vl-browser'] = 0;
		ui.vrange['vl-browser'] = null;
		render();
		var head = document.querySelector('.browser-head');
		var sc = homeScroller();
		if (head) {
			var top = head.getBoundingClientRect().top - sc.getBoundingClientRect().top;
			if (top < 0 || top > sc.clientHeight * 0.6) sc.scrollTop += top - 8;
		}
		mountList(lists['vl-browser'] || { items: [] }, false);
		var focus = document.querySelector('#vl-browser [data-rove="row"][tabindex="0"]') || document.querySelector('.crumb-here');
		if (fromKey && focus && focus.focus) focus.focus({ preventScroll: true });
		updateCues();
	}

	/* ------------------------------------------------------- Dropping files */

	function hasFiles(e) {
		var t = e.dataTransfer && e.dataTransfer.types;
		return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0;
	}

	function dropZoneOf(el) {
		return el && el.closest ? el.closest('[data-drop]') : null;
	}

	function paintDrag() {
		var zone = document.querySelector('[data-drop]');
		if (zone) zone.setAttribute('data-drag', ui.drag ? 'true' : 'false');
	}

	function setDrag(on) {
		if (ui.drag === on) return;
		ui.drag = on;
		paintDrag();
	}

	// A file dropped anywhere never opens in the window; one dropped on the open folder's
	// list is copied in by the host, which reads each File's path.
	document.addEventListener('dragover', function (e) {
		if (!hasFiles(e)) return;
		e.preventDefault();
		var ok = !!dropZoneOf(e.target);
		e.dataTransfer.dropEffect = ok ? 'copy' : 'none';
		setDrag(ok);
	});
	document.addEventListener('dragleave', function (e) {
		if (!dropZoneOf(e.relatedTarget)) setDrag(false);
	});
	document.addEventListener('drop', function (e) {
		if (!hasFiles(e)) return;
		e.preventDefault();
		setDrag(false);
		if (!dropZoneOf(e.target) || !ui.view) return;
		var files = e.dataTransfer.files;
		if (!files || !files.length) return;
		var place = browserPlace();
		bridge.sendWithFiles('dropFiles', { projectId: place.project.id, folder: place.folder }, files);
	});

	/* ------------------------------------------------------------ Events */

	document.addEventListener('click', function (e) {
		var el = e.target.closest ? e.target.closest('[data-action]') : null;
		// A chip in a row sits above the row's key so it keeps the arrow cursor (a tag is
		// never a button); a click on it is still a click on the row.
		if (!el && e.target.closest && e.target.closest('.row .chip')) el = e.target.closest('.row-main').querySelector('.row-hit');
		// Disabled, or busy with an action the host has not answered yet: a second press does nothing.
		if (!el || el.disabled || el.getAttribute('aria-busy') === 'true') return;
		var action = el.getAttribute('data-action');
		var path = el.getAttribute('data-path');
		var from = el.getAttribute('data-key');
		switch (action) {
			case 'openFile':
				openFile(el.getAttribute('data-file-id'), from);
				break;
			case 'back':
				back();
				break;
			case 'showInFolder':
				bridge.send('showInFolder', { path: path });
				break;
			case 'launch':
				act('launchFile', { path: path }, { key: from });
				break;
			case 'checkOut':
				act('checkOut', { paths: [path], open: false }, { key: from });
				break;
			case 'checkOutOpen':
				act('checkOut', { paths: [path], open: true }, { key: from });
				break;
			case 'checkIn':
				act('checkIn', { paths: [path] }, { key: from });
				break;
			case 'undoCheckOut':
				act('undoCheckOut', { paths: [path] }, { key: from });
				break;
			case 'askTakeBack':
				askTakeBack([findRow(el.getAttribute('data-file-id'))], from);
				break;
			case 'promptCheckOut':
				// Check out and reopen: the host checks it out, then opens it again here once
				// SolidWorks has closed it (until then it says to close it first).
				act('checkOut', { paths: [path], open: true }, { key: from });
				ui.promptGone = promptKey(ui.view.prompt);
				render();
				break;
			case 'promptLater':
				// Not now: this one question goes; the next time SolidWorks opens the file, it asks again.
				if (ui.view.prompt) bridge.send('dismissNotice', { key: promptKey(ui.view.prompt) });
				ui.promptGone = promptKey(ui.view.prompt);
				render();
				break;
			case 'select':
				togglePick(path, e.shiftKey);
				break;
			case 'selCheckOut':
			case 'selCheckIn':
			case 'selUndo':
				var paths = pickedRows().map(function (h) {
					return h.row.path;
				});
				if (action === 'selCheckOut') act('checkOut', { paths: paths, open: false }, { key: from });
				else if (action === 'selCheckIn') act('checkIn', { paths: paths }, { key: from });
				else act('undoCheckOut', { paths: paths }, { key: from });
				break;
			case 'askTakeBackPicked':
				askTakeBack(pickedRows(), from);
				break;
			case 'askForceAll':
				var at = browserPlace();
				askTakeBack(
					rowsUnder(at.pi, at.folder).map(function (r) {
						return { row: r, project: at.project };
					}),
					from
				);
				break;
			case 'selClear':
				clearPicked();
				render();
				var first = document.querySelector('#vl-browser [data-rove="sel"][tabindex="0"]') || document.querySelector('.crumb-here');
				if (first && first.focus) first.focus({ preventScroll: true });
				break;
			case 'folder':
				goFolder(el.getAttribute('data-folder') || '', from);
				break;
			case 'askNewFolder':
				askFolder('newFolder', from);
				break;
			case 'askRenameFolder':
				askFolder('renameFolder', from);
				break;
			case 'askDeleteFolder':
				askFolder('deleteFolder', from);
				break;
			case 'addFiles':
				var place = browserPlace();
				act('addFiles', { projectId: place.project.id, folder: place.folder }, { key: from });
				break;
			case 'askCheckOutAll':
				askFolder('checkOutAll', from);
				break;
			case 'askRenameFile':
				askRenameFile(path, from);
				break;
			case 'folderCheckIn':
				var here = browserPlace();
				act('checkIn', { paths: [folderPathOf(here.pi, here.folder)] }, { key: from });
				break;
			case 'mineCheckIn':
				act('checkIn', { paths: mineFolders(ui.view) }, { key: from });
				break;
			case 'askUndoMine':
				var undoCount = (ui.view.myFiles || []).filter(function (f) {
					return checkoutOf(f).state === 'mine';
				}).length;
				if (undoCount) openAsk('undoMine', { count: undoCount }, from);
				break;
			case 'askOk':
				askOk();
				break;
			case 'askCancel':
				ask.close();
				break;
			case 'notice':
				runNotice(el.getAttribute('data-notice'));
				break;
			case 'expand':
				toggleExpand(el.getAttribute('data-notice'));
				break;
			case 'openSettings':
				openSettings();
				break;
			case 'askReport':
				// Report a problem opens over Home; closing it comes back to the Settings key.
				sheet.close();
				openAsk('report', { kind: 'bug' }, 'hdr-settings');
				break;
			case 'askFeedback':
				// Send feedback opens over Home, from the account panel or from Settings.
				var fromSettings = sheet.open;
				if (fromSettings) sheet.close();
				openFeedback(fromSettings, fromSettings ? 'hdr-settings' : 'feedback');
				break;
			case 'addShot':
				takeShot();
				break;
			case 'removeShot':
				if (ui.ask && ui.ask.kind === 'feedback') {
					ui.ask.shot = null;
					paintShot();
					paintSendKey();
					var addKey = ask.querySelector('[data-key="ask-shot"]');
					if (addKey) addKey.focus();
				}
				break;
			case 'openMyFeedback':
				// From Settings, or from Send feedback (the note being written comes back with Back).
				if (sheet.open) {
					sheet.close();
					openMyFeedback('hdr-settings', null);
				} else if (ui.ask && ui.ask.kind === 'feedback') openMyFeedback(ui.ask.returnKey, feedbackDraft());
				break;
			case 'myFeedbackBack':
				if (ui.ask && ui.ask.kind === 'myFeedback' && ui.ask.ctx.draft) openFeedback(false, ui.ask.returnKey, ui.ask.ctx.draft);
				break;
			case 'reportKind':
				if (ui.ask && (ui.ask.kind === 'report' || ui.ask.kind === 'feedback')) {
					ui.ask.ctx.kind = el.getAttribute('data-value');
					Array.prototype.forEach.call(ask.querySelectorAll('[data-action="reportKind"]'), function (b) {
						b.setAttribute('aria-pressed', String(b === el));
					});
				}
				break;
			case 'openIncidents':
				bridge.send('openIncidents');
				break;
			case 'closeSettings':
				sheet.close();
				break;
			case 'toggleStart':
				saveSettings({ startAtSignIn: !ui.view.settings.startAtSignIn });
				break;
			case 'theme':
				// Worn at once: the host's answer only confirms it (0.3.1 waited for it, and
				// a view still carrying the old theme flipped it back for a moment).
				var picked = el.getAttribute('data-value');
				ui.themeWanted = picked;
				document.documentElement.setAttribute('data-theme', effectiveOf(picked, ui.view.effectiveTheme));
				Array.prototype.forEach.call(document.querySelectorAll('[data-action="theme"]'), function (b) {
					b.setAttribute('aria-pressed', String(b.getAttribute('data-value') === picked));
				});
				saveSettings({ theme: picked });
				break;
			case 'takeOverFolder':
				act('takeOverFolder', {}, { key: from, words: 'Checking this folder...' });
				break;
			case 'switchAccount':
				bridge.send('switchAccount');
				break;
			case 'useFolder':
				// A folder of the student's own, next to the one that is taken. Saving it is
				// the same as picking it in Settings; the host answers with a new view.
				saveSettings({ vaultRoot: path });
				break;
			case 'project':
				ui.projectId = el.getAttribute('data-project');
				clearPicked();
				render();
				break;
			case 'connect':
			case 'cancelConnect':
			case 'signOut':
			case 'pause':
			case 'resume':
			case 'openVault':
			case 'chooseVaultRoot':
				bridge.send(action);
				break;
		}
	});

	document.addEventListener('keydown', function (e) {
		var el = e.target;
		// Left and right arrows move between project tabs, as a tab strip should.
		if (el && el.getAttribute && el.getAttribute('role') === 'tab' && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
			var tabs = Array.prototype.slice.call(el.parentNode.querySelectorAll('[role="tab"]'));
			var t = tabs.indexOf(el) + (e.key === 'ArrowRight' ? 1 : -1);
			var next = tabs[(t + tabs.length) % tabs.length];
			ui.projectId = next.getAttribute('data-project');
			clearPicked();
			render();
			var again = document.querySelector('[data-key="' + sel(next.getAttribute('data-key')) + '"]');
			if (again) again.focus();
			e.preventDefault();
			return;
		}
		// Inside a long list: up, down, Home, End and the page keys move between rows.
		var role = el && el.getAttribute ? el.getAttribute('data-rove') : null;
		var ul = role ? el.closest('.vlist') : null;
		var list = ul ? lists[ul.id] : null;
		if (list) {
			var at = Number(el.closest('li[data-i]').getAttribute('data-i'));
			var page = Math.max(1, Math.floor((list.scrollEl() || scroller).clientHeight / list.h) - 1);
			var to = null;
			if (e.key === 'ArrowDown') to = at + 1;
			else if (e.key === 'ArrowUp') to = at - 1;
			else if (e.key === 'Home') to = 0;
			else if (e.key === 'End') to = list.items.length - 1;
			else if (e.key === 'PageDown') to = at + page;
			else if (e.key === 'PageUp') to = at - page;
			if (to !== null) {
				e.preventDefault();
				moveInList(list, to, role);
				return;
			}
		}
		// Ctrl+Enter sends a note or a report from anywhere in its dialog.
		if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && ask.open && ui.ask && (ui.ask.kind === 'feedback' || ui.ask.kind === 'report')) {
			e.preventDefault();
			askOk();
			return;
		}
		if (e.key === 'Enter' && el && el.id === 'ask-name') {
			e.preventDefault();
			askOk();
			return;
		}
		// Escape lets go of every picked file.
		if (e.key === 'Escape' && !sheet.open && !ask.open && Object.keys(ui.selected).length) {
			clearPicked();
			render();
		}
	});

	// Tab into a long list lands on the row it left from.
	document.addEventListener('focusin', function (e) {
		var el = e.target;
		var ul = el && el.closest ? el.closest('.vlist') : null;
		var li = ul ? el.closest('li[data-i]') : null;
		if (li && lists[ul.id]) setActive(lists[ul.id], Number(li.getAttribute('data-i')));
	});

	// Scrolling anywhere (the window, the recessed column, a notice's list) draws the rows
	// now in view and updates the "more below" tags.
	document.addEventListener('scroll', onScroll, { capture: true, passive: true });

	// A thumbnail that arrived shows over its glyph; one Windows has no picture for goes, and
	// the glyph stays. (Image events don't bubble: these listen on the way down.)
	document.addEventListener(
		'load',
		function (e) {
			var img = e.target;
			if (img && img.classList && img.classList.contains('thumb') && img.parentNode) img.parentNode.setAttribute('data-thumb', 'on');
		},
		true
	);
	document.addEventListener(
		'error',
		function (e) {
			var img = e.target;
			if (img && img.classList && img.classList.contains('thumb') && img.parentNode) img.parentNode.removeChild(img);
		},
		true
	);
	window.addEventListener('resize', onScroll);

	// A window the student can't see stops turning the gear.
	document.addEventListener('visibilitychange', function () {
		document.documentElement.setAttribute('data-hidden', document.hidden ? 'true' : 'false');
	});

	sheet.addEventListener('close', function () {
		var k = document.querySelector('[data-key="hdr-settings"]');
		if (k) k.focus();
	});
	ask.addEventListener('close', function () {
		var from = ui.ask && ui.ask.returnKey ? document.querySelector('[data-key="' + sel(ui.ask.returnKey) + '"]') : null;
		ui.ask = null;
		if (from) from.focus({ preventScroll: true });
	});
	// A click on the dim area around a sheet closes it.
	[sheet, ask].forEach(function (d) {
		d.addEventListener('click', function (e) {
			if (e.target !== d || feedbackBusy()) return;
			var r = d.getBoundingClientRect();
			if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) d.close();
		});
	});
	// A note on its way, or a picture being taken, keeps its dialog (Escape waits for it too).
	ask.addEventListener('cancel', function (e) {
		if (feedbackBusy()) e.preventDefault();
	});
	function feedbackBusy() {
		return !!(ui.ask && (ui.ask.sending || ui.ask.shooting));
	}
	// What was tried: how much of its 1,000 characters is used, near the limit.
	document.addEventListener('input', function (e) {
		if (e.target && e.target.id === 'ask-tried') paintTriedCount();
	});

	/* ------------------------------------------------------- Host messages */

	function markReady() {
		// A demo place still waiting for an answer it draws (routeAnswered calls this again).
		if (Object.keys(ui.waits).length) {
			ui.readyWanted = true;
			return;
		}
		if (ui.ready) return;
		ui.ready = true;
		requestAnimationFrame(function () {
			requestAnimationFrame(function () {
				document.body.classList.remove('preload');
				mountLists(false);
				updateCues();
				document.documentElement.setAttribute('data-ready', 'true');
			});
		});
	}

	/** Applies the demo's place once, after the first view: the screen, the project and
	 *  folder, picked files, an open notice list, a dialog, files held over the list, and
	 *  Home scrolled to the team's files. */
	function applyRoute() {
		if (ui.routed) return;
		ui.routed = true;
		var r = bridge.route();
		if (!r || ui.view.connection !== 'signedIn') return markReady();
		if (r.project) ui.projectId = r.project;
		var place = browserPlace();
		if (place && r.folder != null && place.pi.folders[r.folder]) ui.folders[place.project.id] = r.folder;
		place = browserPlace();
		if (place && r.select && r.select.length) {
			(place.pi.folders[place.folder].files || []).forEach(function (row) {
				if (row.fileId && r.select.indexOf(row.name) >= 0) setPicked(row.path, true);
			});
		}
		if (r.expand) ui.expanded[r.expand] = true;
		ui.drag = !!r.drag;
		render();
		if (r.screen === 'detail' && r.fileId) {
			ui.routeDialog = r;
			openFile(r.fileId, 'row-' + r.fileId);
			return; // ready when the detail arrives
		}
		if (r.at === 'browser') {
			// The open project's card at the top, under the selection bar when there is one.
			var part = document.querySelector('.browser-group');
			var sc = homeScroller();
			if (part) sc.scrollTop += part.getBoundingClientRect().top - sc.getBoundingClientRect().top;
			var bar = document.querySelector('.sel-bar');
			var card = document.getElementById('project-panel');
			if (bar && card) sc.scrollTop += card.getBoundingClientRect().top - bar.getBoundingClientRect().bottom - 12;
			ui.pin = [];
			mountLists(false);
		}
		if (r.screen === 'settings') {
			openSettings();
			if (!ui.myFeedback) waitFor('mine');
		}
		routeDialog(r);
		// A demo state drawn just after a key was pressed (the demo holds the answer).
		if (r.press) {
			var pressed = document.querySelector('[data-key="' + String(r.press).replace(/["\\]/g, '') + '"]');
			if (pressed) pressed.click();
		}
		markReady();
	}

	function routeDialog(r) {
		if (!r || !r.dialog) return;
		if (r.dialog === 'report') openAsk('report', { kind: 'bug' }, 'hdr-settings');
		if (r.dialog === 'feedback') {
			// words: typed in already; shot: a picture taken (offer: and sent, and refused).
			openFeedback(false, 'feedback', r.words ? { body: r.words } : null);
			if (!ui.myFeedback) waitFor('mine');
			if (r.shot) {
				ui.routeShot = r.shot;
				waitFor('shot');
				takeShot();
			}
		}
		if (r.dialog === 'myFeedback') {
			openMyFeedback('hdr-settings', null);
			if (!ui.myFeedback) waitFor('mine');
		}
		if (r.dialog === 'takeBack' && ui.detail) askTakeBack([findRow(ui.detail.fileId)], 'd-takeback');
		if (r.dialog === 'forceAll') {
			var here = browserPlace();
			if (here)
				askTakeBack(
					rowsUnder(here.pi, here.folder).map(function (row) {
						return { row: row, project: here.project };
					}),
					'fk-force'
				);
		}
		else if (r.dialog === 'newFolder' || r.dialog === 'renameFolder' || r.dialog === 'deleteFolder' || r.dialog === 'checkOutAll') askFolder(r.dialog, null);
		else if (r.dialog === 'renameFile') {
			// The first file in the open notice list that Armory doesn't have yet.
			var n = (ui.view.notices || []).filter(function (x) {
				return x.key === r.expand;
			})[0];
			var first = n ? (n.items || []).filter(function (it) {
				return !it.fileId;
			})[0] : null;
			if (first) askRenameFile(first.path, 'rename-ni:' + n.key + ':' + first.path);
		}
	}

	function onHost(message) {
		// While the host takes a picture of the window, nothing redraws it.
		if (holdForShot(message)) return;
		if (message.type === 'view') {
			var wasSignedIn = ui.view && ui.view.connection === 'signedIn';
			ui.view = message.view;
			if (!ui.view.activity) ui.view.activity = { line: null, upload: null, download: null, move: null, waiting: null, active: [] };
			ui.index = buildIndex(ui.view);
			// Not now lasts for that one question; a new one (another open) asks again.
			if (ui.promptGone && promptKey(ui.view.prompt) !== ui.promptGone) ui.promptGone = null;
			followRename();
			// Picked files that are gone from the view are let go.
			Object.keys(ui.selected).forEach(function (p) {
				if (!ui.index.byPath[p]) delete ui.selected[p];
			});
			if (ui.view.connection !== 'signedIn') {
				ui.screen = 'home';
				ui.detail = null;
			} else if (!wasSignedIn) {
				ui.screen = 'home';
			}
			render();
			applyRoute();
		} else if (message.type === 'fileDetail') {
			var d = message.detail;
			if (ui.screen === 'detail' && ui.detail && ui.detail.fileId === d.fileId) {
				ui.detail = d;
				render();
				if (ui.waitingForDetail) {
					ui.waitingForDetail = false;
					routeDialog(ui.routeDialog);
					ui.routeDialog = null;
					markReady();
				}
			}
		} else if (message.type === 'activity') {
			patchActivity(message.activity);
		} else if (message.type === 'actionResult') {
			delete ui.pending[message.requestId];
			endWorking(message.requestId);
			// Send feedback answers in its own dialog, which stays open until then.
			if (ui.ask && ui.ask.sending && ui.ask.sending === message.requestId) feedbackAnswered(message);
			else showResult(!!message.ok, message.message);
		} else if (message.type === 'windowShot') {
			shotTaken(message);
		} else if (message.type === 'myFeedback') {
			myFeedbackRead(message);
		}
	}
	bridge.onMessage(onHost);

	bridge.send('ready');
})();
