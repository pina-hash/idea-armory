/*
 * app.js: the Armory Agent window.
 *
 * Renders everything from the AgentView the host sends (docs/agent/BRIDGE.md) and talks
 * back only through window.ArmoryBridge. Three screens: Connect, Home and File detail,
 * plus the Settings sheet over Home. The words are for students who have never used a
 * vault: plain sentences, no jargon.
 */
(function () {
	'use strict';

	var bridge = window.ArmoryBridge;
	var main = document.getElementById('main');
	var headerKeys = document.getElementById('header-keys');
	var scroller = document.getElementById('scroller');
	var sheet = document.getElementById('settings');

	/** Page state the host does not own. */
	var ui = {
		/** @type {import('./bridge.js').AgentView | null} */ view: null,
		screen: 'home', // 'home' | 'detail' (Connect is chosen by the view)
		/** @type {any} */ detail: null,
		projectId: null,
		returnKey: null, // the row that opened detail, so Back can put focus there again
		homeScroll: 0,
		homeRecess: 0, // the recessed column's own scroll, in a wide window
		routed: false,
		ready: false,
		waitingForDetail: false
	};

	/* ------------------------------------------------------------- Words */

	/**
	 * Short status words for chips (the words carry the meaning; a lamp only adds light).
	 * A saved file gets no chip: its line already says "Saved by". `same` lists the
	 * sentences that only repeat the chip, so a note beside the chip can leave them out
	 * and say the next step instead.
	 */
	var STATUS = {
		synced: { chip: '', lamp: '' },
		syncing: { chip: 'Updating', lamp: 'cyan', same: ['updating', 'updating now'] },
		waitingToSend: { chip: 'Waiting to send', lamp: 'amber', same: ['waiting to send'] },
		editingByMe: { chip: "You're editing", lamp: 'gold', same: ["you're editing this", 'you are editing this'] },
		editingByOther: { chip: 'Being edited', lamp: 'gold' },
		newerWaiting: { chip: 'Newer version', lamp: 'cyan', same: ['a newer version is waiting'] },
		conflict: { chip: 'Own copy kept', lamp: 'amber', same: ['kept as your own copy'] },
		refused: { chip: "Can't send", lamp: 'red', same: ["can't send this one", "can't send this"] },
		notOnThisComputer: { chip: 'Not here yet', lamp: '', same: ['not on this computer yet'] }
	};

	/** A Needs-you row's glyph. Its title says what happened, so it carries no chip. */
	var ATTENTION = {
		newerWaiting: 'newer',
		sideVersion: 'copy',
		lockBroken: 'copy',
		refused: 'cant',
		nameTaken: 'cant',
		releaseNotChecked: 'question'
	};

	var SYNC = {
		synced: { readout: 'All saved', tone: 'ok' },
		syncing: { readout: 'Updating', tone: 'busy' },
		offline: { readout: 'Offline', tone: 'warn' },
		paused: { readout: 'Paused', tone: 'off' },
		attention: { readout: 'Needs you', tone: 'warn' }
	};

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

	function plural(n, one, many) {
		return n + ' ' + (n === 1 ? one : many);
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
		return parts.filter(Boolean).join(' \u00b7\u00a0');
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

	function size(bytes) {
		if (bytes < 1024) return plural(bytes, 'byte', 'bytes');
		if (bytes < 1024 * 1024) return Math.round(bytes / 1024) + ' KB';
		return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
	}

	/** "Robot 2027 › Drivetrain" for "Robot 2027/Drivetrain/Gearbox.SLDASM". */
	function whereIs(path) {
		var parts = String(path || '').split('/');
		parts.pop();
		return parts.join(' › ');
	}

	function firstName(name) {
		var n = String(name || '').trim();
		if (/^(Mr|Mrs|Ms|Dr|Mx)\.?\s/i.test(n)) return n;
		return n.split(/\s+/)[0] || n;
	}

	function kindOf(name) {
		var ext = String(name).split('.').pop().toUpperCase();
		if (ext === 'SLDPRT') return 'part';
		if (ext === 'SLDASM') return 'asm';
		if (ext === 'SLDDRW') return 'drw';
		return 'file';
	}

	function chip(words, lamp) {
		return '<span class="chip' + (lamp ? ' lamp ' + lamp : '') + '">' + esc(words) + '</span>';
	}

	function statusChip(status, releaseNotChecked) {
		if (status === 'synced' && releaseNotChecked) return chip('Version not checked', 'gold');
		var s = STATUS[status] || { chip: status, lamp: '' };
		return s.chip ? chip(s.chip, s.lamp) : '';
	}

	/** A note without the sentences that only repeat its chip: "Waiting to send." beside
	 *  a Waiting to send chip says nothing new. Null when nothing is left. */
	function noteBesideChip(note, status) {
		if (!note) return null;
		var same = (STATUS[status] && STATUS[status].same) || [];
		var kept = String(note)
			.split(/(?<=[.!?])\s+/)
			.filter(function (sentence) {
				var plain = sentence.trim().replace(/[.!?]+$/, '').toLowerCase();
				return same.indexOf(plain) < 0;
			});
		return kept.length ? kept.join(' ') : null;
	}

	/** "2 things", "1 file": a count in words, for the right end of a label row. */
	function count(n, one, many) {
		return '<span class="count">' + esc(plural(n, one, many)) + '</span>';
	}

	function findRow(view, fileId) {
		if (!view || !fileId) return null;
		for (var i = 0; i < view.projects.length; i++) {
			var p = view.projects[i];
			for (var j = 0; j < p.folders.length; j++) {
				var f = p.folders[j];
				for (var k = 0; k < f.files.length; k++) if (f.files[k].fileId === fileId) return { row: f.files[k], project: p, folder: f };
			}
		}
		return null;
	}

	/** What a FileRow's second line says. */
	function rowMeta(row) {
		var h = row.holder;
		// The chip says the state; this line says who and when. "You're editing this"
		// beside a You're editing chip would say it twice, so that row shows its last save.
		if (h && h.isMe && h.isMyOtherComputer) return "You're editing this on " + h.device;
		if (h && !h.isMe) return h.name + ' is editing this';
		if (row.status === 'notOnThisComputer') return 'Coming to this computer soon';
		if (row.updatedBy) return metaLine(['Saved by ' + row.updatedBy, row.updatedAt ? agoWhole(row.updatedAt) : '']);
		return '';
	}

	/* --------------------------------------------------------- Rendering */

	function activeKey() {
		var el = document.activeElement;
		return el && el.getAttribute ? el.getAttribute('data-key') : null;
	}

	function restoreFocus(key) {
		if (!key) return;
		var el = document.querySelector('[data-key="' + key.replace(/"/g, '\\"') + '"]');
		if (el && el !== document.activeElement) el.focus({ preventScroll: true });
	}

	function render() {
		var v = ui.view;
		if (!v) return;
		document.documentElement.setAttribute('data-theme', v.effectiveTheme === 'spaceWhite' ? 'spaceWhite' : 'idea');
		var screen = v.connection === 'signedIn' ? ui.screen : 'connect';
		var focus = activeKey();
		// In a wide window Home's lists scroll inside the recessed column, and a new view
		// must not throw the student back to the top of them.
		var keepRecess = document.body.getAttribute('data-screen') === 'home' && screen === 'home' ? recessTop() : null;
		document.body.setAttribute('data-screen', screen);
		headerKeys.innerHTML = screen === 'connect' ? '' : headerHtml();
		if (screen === 'connect') main.innerHTML = connectHtml(v);
		else if (screen === 'detail') main.innerHTML = detailHtml(v);
		else main.innerHTML = homeHtml(v);
		if (keepRecess !== null) setRecessTop(keepRecess);
		if (sheet.open) {
			if (screen === 'connect') sheet.close();
			else sheet.innerHTML = settingsHtml(v);
		}
		document.title = screen === 'detail' && ui.detail ? ui.detail.name + ' · Armory' : 'Armory';
		restoreFocus(focus);
	}

	function headerHtml() {
		return (
			'<button class="key" type="button" data-action="openVault" data-key="hdr-vault" title="Open the Armory folder in File Explorer">' +
			icon('folder') +
			'<span>Open folder</span></button>' +
			'<button class="key" type="button" data-action="openSettings" data-key="hdr-settings" aria-haspopup="dialog">' +
			icon('settings') +
			'<span>Settings</span></button>'
		);
	}

	/* ---- Connect ---- */

	function connectHtml(v) {
		var phase = v.connect.phase;
		var html = '';
		// The title bar is the screen's one heading; the card under it goes straight to
		// what to do.
		if (v.connection === 'vaultOwnedByOther') {
			html += '<h1 class="plate-title">Choose a folder</h1>';
			html += '<div class="connect"><div class="panel connect-card">';
			html += '<p class="lead">' + esc(v.connect.message || 'The Armory folder on this computer already holds files for someone else.') + '</p>';
			if (v.account) html += '<p class="connect-where">You are signed in as ' + esc(v.account.email) + '.</p>';
			html += '<div class="connect-actions">';
			html += '<button class="key primary" type="button" data-action="chooseVaultRoot" data-key="cn-choose">Choose a different folder</button>';
			html += '<button class="key" type="button" data-action="signOut" data-key="cn-signout">' + icon('signout') + '<span>Sign out</span></button>';
			html += '</div></div></div>';
			return html;
		}

		var waiting = v.connection === 'connecting' && phase === 'waitingForBrowser';
		var finishing = v.connection === 'connecting' && phase === 'finishing';
		var failed = phase === 'failed';
		var current = finishing ? 3 : waiting ? 2 : 1;

		html += '<h1 class="plate-title">Connect this computer</h1>';
		html += '<div class="connect"><div class="panel connect-card">';
		html += '<p class="lead">You only do this once. After that, Armory saves your work for the team and keeps everyone\'s files up to date by itself.</p>';
		html += '<ol class="steps">';
		html += step(1, current, failed ? 'Click <strong>Try again</strong> below.' : 'Click the button below.');
		html += step(2, current, 'Sign in with your school Google account in the browser that opens.');
		html += step(3, current, 'Come back here. Your files show up in <strong>' + esc(v.vaultRoot) + '</strong>.');
		html += '</ol>';
		if (v.connect.message || waiting || finishing) {
			var msg = v.connect.message || (waiting ? 'Finish signing in in your browser. This window updates by itself.' : 'Almost done.');
			html += '<p class="connect-msg" role="status"' + (failed ? ' data-tone="bad"' : '') + '>' + esc(msg) + '</p>';
		}
		html += '<div class="connect-actions">';
		if (waiting) html += '<button class="key" type="button" data-action="cancelConnect" data-key="cn-cancel">Cancel</button>';
		else if (!finishing)
			html +=
				'<button class="key primary" type="button" data-action="connect" data-key="cn-connect">' +
				(failed ? 'Try again' : 'Connect this computer') +
				'</button>';
		html += '</div>';
		html += '</div></div>';
		return html;
	}

	function step(n, current, text) {
		var state = n < current ? 'done' : n === current ? 'current' : 'todo';
		var num = state === 'done' ? icon('check') + '<span class="visually-hidden">Done:</span>' : String(n);
		return (
			'<li class="step" data-step="' + state + '"' + (state === 'current' ? ' aria-current="step"' : '') + '>' +
			'<span class="step-num">' + num + '</span><span class="step-text">' + text + '</span></li>'
		);
	}

	/* ---- Home ---- */

	/*
	 * Home: the status display and this computer's account on the left, and the recessed
	 * column on the right holding one card per list. In a wide window the column scrolls
	 * inside its own frame, so the status stays in sight; in a narrow one the window
	 * scrolls and the parts stack in reading order. Nothing is drawn empty: a list with
	 * nothing in it is left out (Needs you) or becomes one printed line.
	 */
	function homeHtml(v) {
		return (
			'<div class="home">' +
			'<svg class="plate-engrave" viewBox="0 0 120 22" preserveAspectRatio="none" aria-hidden="true" focusable="false">' +
			'<path class="e-dk" d="M0 17.5 H62 L76 3.5 H120" /><path class="e-lt" d="M0 18.5 H62.4 L76.4 4.5 H120" /></svg>' +
			statusHtml(v) +
			'<div class="main-col plate-recess"><div class="recess-scroll" id="recess-scroll">' +
			needsHtml(v) +
			myFilesHtml(v) +
			projectsHtml(v) +
			'</div></div>' +
			accountHtml(v) +
			'</div>'
		);
	}

	function recessTop() {
		var r = document.getElementById('recess-scroll');
		return r ? r.scrollTop : 0;
	}

	function setRecessTop(top) {
		var r = document.getElementById('recess-scroll');
		if (r) r.scrollTop = top;
	}

	/** The display holds only the readout and its words; Pause sits under it, centered. */
	function statusHtml(v) {
		var s = SYNC[v.sync.state] || SYNC.synced;
		var paused = v.sync.state === 'paused';
		return (
			'<section class="group top status-group" aria-labelledby="status-label">' +
			'<h2 class="section-label" id="status-label">Status</h2>' +
			'<div class="display" data-tone="' + s.tone + '">' +
			'<p class="screen" role="status">' + esc(s.readout) + '</p>' +
			'<p class="sync-line">' + esc(glue(v.sync.line)) + '</p>' +
			(v.sync.detail ? '<p class="sync-detail">' + esc(glue(v.sync.detail)) + '</p>' : '') +
			'</div>' +
			(paused
				? '<button class="key status-key" type="button" data-action="resume" data-key="sync-toggle">' + icon('play') + '<span>Resume</span></button>'
				: '<button class="key status-key" type="button" data-action="pause" data-key="sync-toggle" title="Stop sending and getting files for now">' + icon('pause') + '<span>Pause</span></button>') +
			'</section>'
		);
	}

	/** Who is signed in on this computer, housed in a card: a label over each value. */
	function accountHtml(v) {
		var a = v.account;
		return (
			'<section class="group top account-group" aria-labelledby="acct-label">' +
			'<h2 class="section-label" id="acct-label">This computer</h2>' +
			(a
				? '<div class="panel acct-panel"><dl class="acct">' +
				  '<div><dt class="label">Signed in as</dt><dd class="acct-email">' + esc(a.email) + '</dd></div>' +
				  '<div><dt class="label">Computer</dt><dd>' + esc(a.deviceName) + '</dd></div>' +
				  '</dl></div>'
				: '') +
			'<button class="key" type="button" data-action="signOut" data-key="signout">' + icon('signout') + '<span>Sign out</span></button>' +
			'</section>'
		);
	}

	function needsHtml(v) {
		var items = v.needsMe || [];
		// Nothing needs the student: the group is not drawn at all. The status display
		// already says so, and the group appears the moment something does.
		if (!items.length) return '';
		var html = '<section class="group top" aria-labelledby="needs-label">';
		html += '<h2 class="section-label" id="needs-label"><span>Needs you</span>' + count(items.length, 'thing', 'things') + '</h2>';
		html += '<ul class="list-well">';
		items.forEach(function (a, i) {
			html += attentionRow(a, i);
		});
		html += '</ul>';
		return html + '</section>';
	}

	/** A Needs-you row: a glyph for the kind, the title, what to do, then which file and when. */
	function attentionRow(a, i) {
		var body =
			icon(ATTENTION[a.kind] || 'note', 'row-icon') +
			'<span class="row-body">' +
			'<span class="row-name">' + esc(glue(a.title)) + '</span>' +
			'<span class="row-detail">' + esc(glue(a.detail)) + '</span>' +
			'<span class="row-meta">' + esc(metaLine([a.name, whereIs(a.path), a.at ? agoWhole(a.at) : ''])) + '</span>' +
			'</span>';
		var key = 'attn-' + i + '-' + (a.fileId || a.path);
		if (a.fileId) {
			return (
				'<li class="row"><button class="row-main attn" type="button" data-action="openFile" data-file-id="' + esc(a.fileId) + '" data-key="' + esc(key) + '">' +
				body + icon('chev-right', 'row-go') + '</button></li>'
			);
		}
		return (
			'<li class="row"><div class="row-main attn has-key">' + body +
			'<button class="key row-key" type="button" data-action="showInFolder" data-path="' + esc(a.path) + '" data-key="' + esc(key) + '">' +
			icon('folder') + '<span>Show in folder</span></button></div></li>'
		);
	}

	function myFilesHtml(v) {
		var files = v.myFiles || [];
		var html = '<section class="group top" aria-labelledby="mine-label">';
		html += '<h2 class="section-label" id="mine-label"><span>My files</span>' + (files.length ? count(files.length, 'file', 'files') : '') + '</h2>';
		if (!files.length) {
			html += '<p class="group-help">Files you edit show up here until they\'re saved to Armory. Open a file from ' + esc(v.vaultRoot) + ' in SolidWorks to start.</p>';
			return html + '</section>';
		}
		html += '<ul class="list-well">';
		files.forEach(function (f) {
			var note = noteBesideChip(f.note, f.status);
			var body =
				icon(kindOf(f.name), 'row-icon') +
				'<span class="row-body">' + rowHead(f.name, statusChip(f.status, false)) +
				(note ? '<span class="row-note">' + esc(glue(note)) + '</span>' : '') +
				'<span class="row-meta">' + esc(whereIs(f.path)) + '</span>' +
				'</span>';
			if (f.fileId) {
				html +=
					'<li class="row"><button class="row-main file" type="button" data-action="openFile" data-file-id="' + esc(f.fileId) + '" data-key="mine-' + esc(f.fileId) + '">' +
					body + icon('chev-right', 'row-go') + '</button></li>';
			} else {
				html += '<li class="row"><div class="row-main file">' + body + '</div></li>';
			}
		});
		html += '</ul>';
		return html + '</section>';
	}

	/** A row's title line: the name as typed, and its chip (if any) at the line's right end. */
	function rowHead(name, chipHtml) {
		return '<span class="row-head"><span class="row-name">' + esc(name) + '</span>' + chipHtml + '</span>';
	}

	/** Projects: the label row carries the project pads; under it, one card per project
	 *  with each folder as a header row inside it. */
	function projectsHtml(v) {
		var projects = v.projects || [];
		var html = '<section class="group top" aria-labelledby="proj-label">';
		if (!projects.length) {
			html += '<h2 class="section-label" id="proj-label">Projects</h2>';
			html += '<p class="group-help">You\'re not in any projects yet. Ask your teacher or CAD lead to add you.</p>';
			return html + '</section>';
		}
		var current = projects.filter(function (p) {
			return p.id === ui.projectId;
		})[0] || projects[0];
		ui.projectId = current.id;
		html += '<div class="group-head"><h2 class="section-label" id="proj-label">Projects</h2>';
		if (projects.length > 1) {
			html += '<div class="pads" role="tablist" aria-labelledby="proj-label">';
			projects.forEach(function (p) {
				var on = p.id === current.id;
				html +=
					'<button class="pad project-pad" type="button" role="tab" id="tab-' + esc(p.id) + '" aria-selected="' + on + '" aria-controls="project-panel"' +
					' data-action="project" data-project="' + esc(p.id) + '" data-key="tab-' + esc(p.id) + '">' + esc(p.name) + '</button>';
			});
			html += '</div>';
		} else {
			html += '<p class="project-name">' + esc(current.name) + '</p>';
		}
		html += '</div>';
		html += '<div class="list-well project-card" id="project-panel"' + (projects.length > 1 ? ' role="tabpanel" aria-labelledby="tab-' + esc(current.id) + '"' : '') + '>';
		current.folders.forEach(function (folder, fi) {
			if (!folder.files.length) return;
			var label = folder.path === '' ? 'Main folder' : folder.name || folder.path;
			var headId = 'fh-' + esc(current.id) + '-' + fi;
			html += '<section class="folder" aria-labelledby="' + headId + '">';
			html +=
				'<h3 class="folder-row" id="' + headId + '">' + icon('folder') + '<span class="folder-name">' + esc(label) + '</span>' +
				'<span class="folder-count">' + plural(folder.files.length, 'file', 'files') + '</span></h3>';
			html += '<ul class="folder-list">';
			folder.files.forEach(function (row) {
				var meta = rowMeta(row);
				html +=
					'<li class="row"><button class="row-main file" type="button" data-action="openFile" data-file-id="' + esc(row.fileId) + '" data-key="row-' + esc(row.fileId) + '">' +
					icon(kindOf(row.name), 'row-icon') +
					'<span class="row-body">' + rowHead(row.name, statusChip(row.status, row.releaseNotChecked)) +
					(meta ? '<span class="row-meta">' + esc(meta) + '</span>' : '') + '</span>' +
					icon('chev-right', 'row-go') +
					'</button></li>';
			});
			html += '</ul></section>';
		});
		html += '</div>';
		return html + '</section>';
	}

	/* ---- File detail ---- */

	/** The detail with the newest status and holder from the view laid over it. */
	function currentDetail(v) {
		var d = ui.detail;
		var found = findRow(v, d.fileId);
		if (!found) return d;
		var out = {};
		for (var k in d) out[k] = d[k];
		out.status = found.row.status;
		out.holder = found.row.holder;
		out.releaseNotChecked = found.row.releaseNotChecked;
		return out;
	}

	function holderWords(d) {
		var h = d.holder;
		if (h && h.isMe && !h.isMyOtherComputer) {
			return {
				readout: "You're editing",
				tone: 'hold',
				line: "You're editing this",
				meta: 'On this computer for ' + lasting(h.since) + '.',
				help: h.savedToArmory ? 'Your latest save is in Armory. Each time you save in SolidWorks, Armory sends it to the team.' : 'Your newest changes haven\'t been sent yet. They send by themselves.'
			};
		}
		if (h && h.isMe) {
			return {
				readout: 'Open elsewhere',
				tone: 'hold',
				line: "You're editing this on " + h.device,
				meta: 'For ' + lasting(h.since) + '.',
				help: 'Close it on ' + h.device + ' to edit it on this computer.'
			};
		}
		if (h) {
			var who = firstName(h.name);
			return {
				readout: 'Being edited',
				tone: 'hold',
				line: h.name + ' is editing this',
				meta: 'On ' + h.device + ' for ' + lasting(h.since) + '.' + (h.savedToArmory ? '' : ' ' + who + '\'s newest changes aren\'t in Armory yet.'),
				help: 'You can open it to look. To make changes, wait until ' + who + ' closes it, or ask ' + who + '.'
			};
		}
		switch (d.status) {
			case 'conflict':
				return {
					readout: 'Own copy kept',
					tone: 'warn',
					line: 'Your changes were kept as your own copy',
					meta: 'Someone else saved first. Nothing was lost.',
					help: 'Your copy is listed in the history. Ask your CAD lead which one to keep.'
				};
			case 'newerWaiting':
				return { readout: 'Newer version', tone: 'busy', line: 'A newer version is waiting', meta: null, help: 'Close it in SolidWorks to get the newer version.' };
			case 'refused':
				return { readout: "Can't send", tone: 'bad', line: "Armory can't send your changes", meta: null, help: 'Your changes are safe on this computer. See Needs you on Home for what to do.' };
			case 'waitingToSend':
				return { readout: 'Waiting to send', tone: 'warn', line: 'Saved on this computer', meta: null, help: 'It goes to Armory as soon as this computer is back online.' };
			case 'syncing':
				return { readout: 'Updating', tone: 'busy', line: 'Updating now', meta: null, help: 'Armory is moving the newest version. You can keep working.' };
			case 'notOnThisComputer':
				return { readout: 'Not here yet', tone: 'off', line: 'Not on this computer yet', meta: null, help: 'Armory is getting it. It shows up in the folder soon.' };
			default:
				return {
					readout: 'Free to edit',
					tone: 'ok',
					line: 'Nobody is editing this',
					meta: null,
					help: 'Open it in SolidWorks to start. Armory saves it for the team each time you save.'
				};
		}
	}

	function detailHtml(v) {
		var d = currentDetail(v);
		var w = holderWords(d);
		var html = '<article class="detail" aria-labelledby="detail-title">';
		html += '<div class="detail-top"><button class="key" type="button" data-action="back" data-key="back" aria-label="Back to Home">' + icon('chev-left') + '<span>Back</span></button></div>';
		html += '<div class="detail-title">';
		html += '<h1 id="detail-title" tabindex="-1" data-key="detail-title">' + esc(d.name) + '</h1>';
		// No chip here: the display below is this file's status, said once.
		html += '<p class="detail-path">' + esc(whereIs(d.path)) + '</p>';
		html += '</div>';
		html += '<div class="detail-grid">';

		html += '<div class="detail-side">';
		html += '<section class="display" data-tone="' + w.tone + '" aria-labelledby="holder-line">';
		html += '<p class="screen">' + esc(w.readout) + '</p>';
		html += '<h2 class="holder-line" id="holder-line">' + esc(w.line) + '</h2>';
		if (w.meta) html += '<p class="holder-meta">' + esc(w.meta) + '</p>';
		html += '<p class="holder-help">' + esc(w.help) + '</p>';
		html += '</section>';
		// The display holds words only; its one action sits under it, centered.
		html +=
			'<button class="key primary side-key" type="button" data-action="showInFolder" data-path="' + esc(d.path) + '" data-key="show-in-folder">' +
			icon('folder') + '<span>Show in folder</span></button>';
		if (d.releaseNotChecked) {
			html +=
				'<section class="panel note-panel" aria-labelledby="rnc-title"><h3 id="rnc-title">' + icon('note') + '<span>SolidWorks version not checked</span></h3>' +
				'<p>Armory saved this file but couldn\'t tell which SolidWorks made it. If it won\'t open on a lab computer, open it in the team\'s SolidWorks version and save it again.</p></section>';
		}
		html += '</div>';

		html += '<section class="detail-main" aria-labelledby="history-label">';
		html += '<div class="history-well"><h2 class="history-head" id="history-label"><span>History</span>' + (d.history ? count(d.history.length, 'save', 'saves') : '') + '</h2>';
		if (!d.history) {
			html += '<p class="hist-loading history-list-pad">Getting the history…</p>';
		} else {
			html += '<ol class="history-list">';
			d.history.forEach(function (e) {
				// A plain save is told by who and when, the part a student scans for; the
				// event word drops to the meta line. Anything else (a kept copy) is its own title.
				var routine = e.kind === 'version' && (e.note === 'Saved' || e.note === 'Added to Armory');
				var when = '<time datetime="' + esc(e.at) + '" title="' + esc(fullTime(e.at)) + '">' + esc(agoWhole(e.at)) + '</time>';
				var chips = '';
				if (e.isCurrent) chips += chip('Current', 'green');
				if (e.kind === 'sideVersion' && !/own copy/i.test(e.note)) chips += chip('Own copy', 'amber');
				if (e.releaseNotChecked) chips += chip('Version not checked', 'gold');
				html +=
					'<li><div class="hist-top"><span class="hist-title">' + (routine ? esc(e.author) + ' · ' + when : esc(e.note)) + '</span>' +
					(chips ? '<span class="hist-chips">' + chips + '</span>' : '') + '</div>' +
					'<div class="hist-meta">' + (routine ? esc(e.note) : esc(e.author) + ' · ' + when) + ' · ' + esc(size(e.bytes)) + '</div></li>';
			});
			html += '</ol>';
		}
		html += '</div></section>';
		html += '</div></article>';
		return html;
	}

	/* ---- Settings sheet ---- */

	function settingsHtml(v) {
		var s = v.settings;
		var themes = [
			['system', 'Match Windows'],
			['idea', 'IDEA'],
			['spaceWhite', 'Space White']
		];
		var html = '<div class="sheet-head"><h2 class="sheet-title" id="settings-title">Settings</h2>';
		html += '<button class="key" type="button" data-action="closeSettings" data-key="set-done">Done</button></div>';

		html += '<section class="setting" aria-labelledby="set-root-label">';
		html += '<h3 class="section-label" id="set-root-label">Where your files are kept</h3>';
		html += '<div class="setting-row"><div class="well path-well" id="set-root-value">' + esc(s.vaultRoot) + '</div>';
		html += '<button class="key" type="button" data-action="chooseVaultRoot" data-key="set-root" aria-describedby="set-root-label set-root-value">Change</button></div>';
		html += '<p class="setting-help">Armory keeps a copy of your team\'s files in this folder. Most people never change it.</p>';
		html += '</section>';

		html += '<section class="setting" aria-labelledby="set-start-label">';
		html += '<h3 class="section-label" id="set-start-label">Start Armory when I sign in</h3>';
		html +=
			'<button class="switch" type="button" data-action="toggleStart" data-key="set-start" aria-pressed="' + !!s.startAtSignIn + '" aria-labelledby="set-start-label set-start-word">' +
			'<span class="ts-glyph" aria-hidden="true"></span><span class="ts-word" id="set-start-word">' + (s.startAtSignIn ? 'On' : 'Off') + '</span></button>';
		html += '<p class="setting-help">When this is on, Armory opens by itself when you sign in to Windows, so your saves always reach the team.</p>';
		html += '</section>';

		html += '<section class="setting" aria-labelledby="set-theme-label">';
		html += '<h3 class="section-label" id="set-theme-label">Theme</h3>';
		html += '<div class="theme-keys" role="group" aria-labelledby="set-theme-label">';
		themes.forEach(function (t) {
			html +=
				'<button class="key ring" type="button" data-action="theme" data-value="' + t[0] + '" data-key="set-theme-' + t[0] + '" aria-pressed="' + (s.theme === t[0]) + '">' +
				esc(t[1]) + '</button>';
		});
		html += '</div></section>';
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
		}
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

	/* --------------------------------------------------------- Navigation */

	function partialDetail(fileId) {
		var found = findRow(ui.view, fileId);
		if (found) {
			return {
				fileId: fileId,
				name: found.row.name,
				path: found.row.path,
				project: found.project.name,
				folder: found.folder.path,
				status: found.row.status,
				holder: found.row.holder,
				releaseNotChecked: found.row.releaseNotChecked,
				history: null
			};
		}
		var mine = (ui.view.myFiles || []).concat(ui.view.needsMe || []).filter(function (f) {
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
			holder: null,
			releaseNotChecked: false,
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
	}

	function back() {
		ui.screen = 'home';
		ui.detail = null;
		render();
		scroller.scrollTop = ui.homeScroll;
		setRecessTop(ui.homeRecess);
		var target = ui.returnKey ? document.querySelector('[data-key="' + ui.returnKey + '"]') : null;
		(target || document.querySelector('[data-key="hdr-vault"]')).focus({ preventScroll: true });
		if (target && target.scrollIntoView) target.scrollIntoView({ block: 'nearest' });
	}

	/* ------------------------------------------------------------ Events */

	document.addEventListener('click', function (e) {
		var el = e.target.closest ? e.target.closest('[data-action]') : null;
		if (!el || el.disabled) return;
		var action = el.getAttribute('data-action');
		switch (action) {
			case 'openFile':
				openFile(el.getAttribute('data-file-id'), el.getAttribute('data-key'));
				break;
			case 'back':
				back();
				break;
			case 'showInFolder':
				bridge.send('showInFolder', { path: el.getAttribute('data-path') });
				break;
			case 'openSettings':
				openSettings();
				break;
			case 'closeSettings':
				sheet.close();
				break;
			case 'toggleStart':
				saveSettings({ startAtSignIn: !ui.view.settings.startAtSignIn });
				break;
			case 'theme':
				saveSettings({ theme: el.getAttribute('data-value') });
				break;
			case 'project':
				ui.projectId = el.getAttribute('data-project');
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

	// Left and right arrows move between project tabs, as a tab strip should.
	document.addEventListener('keydown', function (e) {
		var el = e.target;
		if (!el || el.getAttribute('role') !== 'tab' || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return;
		var tabs = Array.prototype.slice.call(el.parentNode.querySelectorAll('[role="tab"]'));
		var i = tabs.indexOf(el) + (e.key === 'ArrowRight' ? 1 : -1);
		var next = tabs[(i + tabs.length) % tabs.length];
		ui.projectId = next.getAttribute('data-project');
		render();
		var again = document.querySelector('[data-key="' + next.getAttribute('data-key') + '"]');
		if (again) again.focus();
		e.preventDefault();
	});

	sheet.addEventListener('close', function () {
		var key = document.querySelector('[data-key="hdr-settings"]');
		if (key) key.focus();
	});
	// A click on the dim area around the sheet closes it.
	sheet.addEventListener('click', function (e) {
		if (e.target !== sheet) return;
		var r = sheet.getBoundingClientRect();
		if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) sheet.close();
	});

	/* ------------------------------------------------------- Host messages */

	function markReady() {
		if (ui.ready) return;
		ui.ready = true;
		requestAnimationFrame(function () {
			requestAnimationFrame(function () {
				document.body.classList.remove('preload');
				document.documentElement.setAttribute('data-ready', 'true');
			});
		});
	}

	/** Applies the demo's ?screen= once, after the first view. */
	function applyRoute() {
		if (ui.routed) return;
		ui.routed = true;
		var r = bridge.route();
		if (!r || ui.view.connection !== 'signedIn') return markReady();
		if (r.screen === 'detail' && r.fileId) {
			openFile(r.fileId, 'row-' + r.fileId);
			return; // ready when the detail arrives
		}
		if (r.screen === 'settings') openSettings();
		markReady();
	}

	bridge.onMessage(function (message) {
		if (message.type === 'view') {
			var wasSignedIn = ui.view && ui.view.connection === 'signedIn';
			ui.view = message.view;
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
					markReady();
				}
			}
		}
	});

	bridge.send('ready');
})();
