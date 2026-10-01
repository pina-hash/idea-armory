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
	var windowCue = document.getElementById('window-cue');

	/** Page state the host does not own. */
	var ui = {
		/** @type {import('./bridge.js').AgentView | null} */ view: null,
		screen: 'home', // 'home' | 'detail' (Connect is chosen by the view)
		/** @type {any} */ detail: null,
		projectId: null,
		returnKey: null, // the control that opened detail, so Back can put focus there again
		homeScroll: 0,
		homeRecess: 0, // the recessed column's own scroll, in a wide window
		routed: false,
		ready: false,
		waitingForDetail: false
	};

	/* ------------------------------------------------------------- Words */

	/*
	 * ONE SEVERITY SCALE, everywhere a state is shown (the status display, Needs you, the
	 * chips, File detail): ok is green, look is amber, bad (blocked) is red, off is gray.
	 * The word always carries the meaning; the color only adds to it.
	 */
	var LAMP = { ok: 'green', look: 'amber', bad: 'red', off: '' };

	/**
	 * Short status words for chips. A saved file gets no chip: its line already says
	 * "Saved by". `same` lists the sentences that only repeat the chip, so a note beside
	 * the chip can leave them out and say the next step instead. Editing chips are built
	 * by whoChip(), with the person's initials.
	 */
	var STATUS = {
		synced: { chip: '', tone: '' },
		syncing: { chip: 'Updating', tone: 'ok', same: ['updating', 'updating now'] },
		waitingToSend: { chip: 'Waiting to send', tone: 'look', same: ['waiting to send'] },
		editingByMe: { chip: "You're editing", tone: 'ok', same: ["you're editing this", 'you are editing this'] },
		editingByOther: { chip: 'Being edited', tone: 'look' },
		newerWaiting: { chip: 'Newer version waiting', tone: 'look', same: ['a newer version is waiting'] },
		conflict: { chip: 'Your copy kept', tone: 'look', same: ['kept as your own copy'] },
		refused: { chip: "Can't send", tone: 'bad', same: ["can't send this one", "can't send this"] },
		notOnThisComputer: { chip: 'Not here yet', tone: 'off', same: ['not on this computer yet'] }
	};

	/**
	 * A Needs-you card: its glyph, its severity, and the labeled actions it offers. The
	 * title says what happened, so the card carries no status chip.
	 */
	var ATTENTION = {
		newerWaiting: { glyph: 'newer', tone: 'look', open: 'See the file', folder: false },
		sideVersion: { glyph: 'copy', tone: 'look', open: 'See both copies', folder: true },
		lockBroken: { glyph: 'copy', tone: 'look', open: 'See both copies', folder: true },
		refused: { glyph: 'cant', tone: 'bad', open: 'See the file', folder: true, folderFirst: true },
		nameTaken: { glyph: 'cant', tone: 'bad', open: null, folder: true, folderFirst: true },
		releaseNotChecked: { glyph: 'question', tone: 'look', open: 'See the file', folder: true }
	};

	var SYNC = {
		synced: { readout: 'All saved', tone: 'ok' },
		syncing: { readout: 'Updating', tone: 'ok' },
		offline: { readout: 'Offline', tone: 'look' },
		paused: { readout: 'Paused', tone: 'off' },
		attention: { readout: 'Needs you', tone: 'look' }
	};

	var KIND_WORD = { part: 'Part', asm: 'Assembly', drw: 'Drawing', file: 'File' };

	/** Files that are in Armory and current on this computer. */
	var UP_TO_DATE = { synced: true, editingByMe: true, editingByOther: true };

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

	function size(bytes) {
		if (bytes < 1024) return plural(bytes, 'byte', 'bytes');
		if (bytes < 1024 * 1024) return Math.round(bytes / 1024) + ' KB';
		return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
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
		for (var i = 0; v && i < v.projects.length; i++)
			for (var j = 0; j < v.projects[i].folders.length; j++)
				for (var k = 0; k < v.projects[i].folders[j].files.length; k++) {
					var h = v.projects[i].folders[j].files[k].holder;
					if (h && h.isMe) return h.name;
				}
		return v && v.account ? nameFromEmail(v.account.email) : '';
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

	/** Who is editing: their initials in a disc, then the words. Green when it is you,
	 *  amber when it is someone else (you can look, not save). */
	function whoChip(holderName, isMe, device) {
		var words = isMe ? (device ? "You're editing on " + device : "You're editing") : firstName(holderName) + ' is editing';
		return (
			'<span class="chip who" data-tone="' + (isMe ? 'ok' : 'look') + '"><span class="avatar" aria-hidden="true">' + esc(initials(holderName)) + '</span>' +
			esc(words) + '</span>'
		);
	}

	function statusChip(row, meName) {
		var h = row.holder;
		if (row.status === 'editingByMe' || (h && h.isMe && (row.status === 'synced' || row.status === 'editingByOther')))
			return whoChip((h && h.name) || meName, true, h && h.isMyOtherComputer ? h.device : null);
		if (row.status === 'editingByOther' && h) return whoChip(h.name, false);
		var s = STATUS[row.status] || { chip: row.status, tone: '' };
		var out = s.chip ? chip(s.chip, s.tone) : '';
		if (row.releaseNotChecked) out += chip('Version not checked', 'look');
		return out;
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

	/** What a team file row's meta line says: who and when. */
	function rowMeta(row) {
		var h = row.holder;
		if (h && !h.isMe && row.status === 'editingByOther') return 'Since ' + agoWhole(h.since) + ' on ' + h.device;
		if (row.status === 'notOnThisComputer') return 'Coming to this computer soon';
		if (row.updatedBy) return metaLine(['Saved by ' + row.updatedBy, row.updatedAt ? agoWhole(row.updatedAt) : '']);
		return '';
	}

	/** The worst severity among the things that need the student. */
	function needsTone(items) {
		var tone = '';
		(items || []).forEach(function (a) {
			var t = (ATTENTION[a.kind] || {}).tone || 'look';
			if (t === 'bad' || !tone) tone = t;
		});
		return tone || 'look';
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
		headerKeys.innerHTML = screen === 'connect' ? '' : headerHtml(v);
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
		updateCues();
	}

	/** The header's two keys. In a narrow window they keep only their icons; the words
	 *  stay for screen readers and the tooltip says where each one goes. */
	function headerHtml(v) {
		return (
			'<button class="key hdr-key" type="button" data-action="openVault" data-key="hdr-vault" title="Open ' + esc(v.vaultRoot) + ' in File Explorer">' +
			icon('folder') +
			'<span class="key-word">Open Armory folder</span></button>' +
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
			html += '<p class="lead">You only do this once. After that, Armory saves your work for the team and keeps everyone\'s files up to date by itself.</p>';
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
			html +=
				'<button class="key primary" type="button" data-action="connect" data-key="cn-connect">' +
				(failed ? 'Try again' : 'Connect this computer') +
				'</button>';
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
		var num = state === 'done' ? icon('check') + '<span class="visually-hidden">Done:</span>' : String(n);
		return (
			'<li class="step panel" data-step="' + state + '"' + (state === 'current' ? ' aria-current="step"' : '') + '>' +
			'<span class="step-num">' + num + '</span><span class="step-text">' + text + '</span></li>'
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
			'<p class="lead"><span class="mono-inline">' + esc(v.vaultRoot) + '</span> already has ' + (ownerName ? esc(ownerName) + '\'s' : 'someone else\'s') +
			' files in it. Use a folder of your own, so your files and theirs don\'t get mixed up.</p>';
		if (!owner && msg) html += '<p class="connect-where">' + esc(msg) + '</p>';
		html += '<dl class="accounts">';
		if (owner) html += '<div><dt class="label">This folder belongs to</dt><dd class="mono-plate">' + esc(owner) + '</dd></div>';
		if (me) html += '<div><dt class="label">You\'re signed in as</dt><dd class="mono-plate">' + esc(me) + '</dd></div>';
		html += '</dl>';
		html += '<div class="connect-actions">';
		html +=
			'<button class="key primary" type="button" data-action="useFolder" data-path="' + esc(mine) + '" data-key="cn-own">Use <span class="key-path">' + esc(mine) + '</span></button>';
		html += '<button class="key" type="button" data-action="chooseVaultRoot" data-key="cn-choose">' + icon('folder') + '<span>Choose another folder</span></button>';
		html += '</div>';
		html +=
			'<p class="connect-foot">Not sure? Ask your teacher. Not you? ' +
			'<button class="textlink" type="button" data-action="signOut" data-key="cn-signout">Sign out</button></p>';
		html += '</div></div>';
		return html;
	}

	/* ---- Home ---- */

	/*
	 * Home: the status display, how much is up to date, and this computer's account on
	 * the left; the recessed column on the right holding Needs you, My files (what you're
	 * editing and what isn't in Armory yet) and every team file. In a wide window the
	 * column scrolls inside its own frame, so the status stays in sight; in a narrow one
	 * the window scrolls and the parts stack in reading order, the status shrunk to one
	 * lit strip.
	 */
	function homeHtml(v) {
		return (
			titleBar('h1', 'Home') +
			'<div class="home">' +
			'<svg class="plate-engrave" viewBox="0 0 120 22" preserveAspectRatio="none" aria-hidden="true" focusable="false">' +
			'<path class="e-dk" d="M0 17.5 H62 L76 3.5 H120" /><path class="e-lt" d="M0 18.5 H62.4 L76.4 4.5 H120" /></svg>' +
			statusHtml(v) +
			'<div class="main-col plate-recess"><div class="recess-scroll" id="recess-scroll">' +
			needsHtml(v) +
			myFilesHtml(v) +
			projectsHtml(v) +
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

	function recessTop() {
		var r = document.getElementById('recess-scroll');
		return r ? r.scrollTop : 0;
	}

	function setRecessTop(top) {
		var r = document.getElementById('recess-scroll');
		if (r) r.scrollTop = top;
	}

	/** The display holds the readout and its words. Its one key sits under it: Pause
	 *  sending (gone while offline, when there is nothing to pause), or Resume as the
	 *  screen's green primary while paused. */
	function statusHtml(v) {
		var s = SYNC[v.sync.state] || SYNC.synced;
		var tone = s.tone;
		var readout = s.readout;
		if (v.sync.state === 'attention') {
			tone = needsTone(v.needsMe);
			if (tone === 'bad' && (v.needsMe || []).every(function (a) { return (ATTENTION[a.kind] || {}).tone === 'bad'; })) readout = "Can't send";
		}
		// The host's sentence comes first; when it sends none, the count of changes still
		// on this computer says what is waiting (pendingCount), so nothing goes unsaid.
		var detail = v.sync.detail || (v.sync.pendingCount > 0 ? plural(v.sync.pendingCount, 'change is', 'changes are') + ' waiting to send.' : null);
		var key = '';
		if (v.sync.state === 'paused')
			key = '<button class="key primary status-key" type="button" data-action="resume" data-key="sync-toggle">' + icon('play') + '<span class="key-word">Resume sending</span></button>';
		else if (v.sync.state !== 'offline')
			key =
				'<button class="key status-key" type="button" data-action="pause" data-key="sync-toggle" title="Stop sending and getting files for now">' +
				icon('pause') + '<span class="key-word">Pause sending</span></button>';
		return (
			'<section class="group top status-group' + (key ? '' : ' no-key') + '" aria-labelledby="status-label">' +
			'<h2 class="section-label" id="status-label">Status</h2>' +
			'<div class="display" data-tone="' + tone + '">' +
			'<p class="screen lcd" role="status"><span>' + esc(readout) + '</span></p>' +
			'<p class="sync-line">' + esc(glue(v.sync.line)) + '</p>' +
			(detail ? '<p class="sync-detail">' + esc(glue(detail)) + '</p>' : '') +
			'</div>' +
			key +
			'</section>'
		);
	}

	/** How many team files are current on this computer, as a ring. */
	function upToDate(v) {
		var total = 0;
		var good = 0;
		(v.projects || []).forEach(function (p) {
			p.folders.forEach(function (f) {
				f.files.forEach(function (r) {
					total++;
					if (UP_TO_DATE[r.status]) good++;
				});
			});
		});
		(v.myFiles || []).forEach(function (f) {
			if (!f.fileId) total++; // a new file that isn't in Armory yet
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
		var words = (all ? 'All ' + c.total : c.good + ' of ' + c.total) + ' team files are up to date on ' + where + '.';
		return (
			'<div class="gauge">' +
			'<div class="ring" data-tone="' + tone + '">' +
			'<svg viewBox="0 0 100 100" aria-hidden="true" focusable="false">' +
			'<circle class="ring-track" cx="50" cy="50" r="41" />' +
			'<circle class="ring-arc" cx="50" cy="50" r="41" pathLength="100" stroke-dasharray="' + pct + ' 100" transform="rotate(-90 50 50)" /></svg>' +
			'<span class="ring-glass" aria-hidden="true"><span class="ring-value">' + c.good + '</span><span class="ring-of">of ' + c.total + '</span></span>' +
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
			'<button class="textlink acct-signout" type="button" data-action="signOut" data-key="signout">' + icon('signout') + '<span>Sign out of Armory</span></button>' +
			'</div></section>'
		);
	}

	/** Needs you: under its own lit strip ("2 things to look at" in amber, or "to fix" in
	 *  red when something can't be sent; the status display already says "Needs you"),
	 *  one card per thing, each with a colored edge, its glyph and labeled actions. */
	function needsHtml(v) {
		var items = v.needsMe || [];
		// Nothing needs the student: the group is not drawn at all. The status display
		// already says so, and the group appears the moment something does.
		if (!items.length) return '';
		var tone = needsTone(items);
		var html = '<section class="group top needs-group" aria-labelledby="needs-label">';
		html +=
			'<h2 class="needs-strip lcd" id="needs-label" data-tone="' + tone + '">' + icon(tone === 'bad' ? 'cant' : 'note', 'lcd-icon') +
			'<span>' + esc(plural(items.length, 'thing', 'things') + (tone === 'bad' ? ' to fix' : ' to look at')) + '</span></h2>';
		html += '<ul class="attn-list">';
		items.forEach(function (a, i) {
			html += attentionCard(a, i);
		});
		html += '</ul>';
		return html + '</section>';
	}

	function attentionCard(a, i) {
		var k = ATTENTION[a.kind] || { glyph: 'note', tone: 'look', open: 'See the file', folder: true };
		var id = 'attn-' + i;
		var keys = [];
		if (k.open && a.fileId)
			keys.push(
				'<button class="key" type="button" data-action="openFile" data-file-id="' + esc(a.fileId) + '" data-key="attn-open-' + i + '" aria-describedby="' + id + '-t">' +
				'<span>' + esc(k.open) + '</span>' + icon('chev-right') + '</button>'
			);
		if (k.folder || !a.fileId) {
			var folderKey =
				'<button class="key" type="button" data-action="showInFolder" data-path="' + esc(a.path) + '" data-key="attn-folder-' + i + '" aria-describedby="' + id + '-t">' +
				icon('folder') + '<span>Show in folder</span></button>';
			if (k.folderFirst) keys.unshift(folderKey);
			else keys.push(folderKey);
		}
		return (
			'<li class="attn-card panel" data-tone="' + k.tone + '">' +
			'<span class="attn-glyph">' + icon(k.glyph) + '</span>' +
			'<div class="attn-body">' +
			'<h3 class="attn-title" id="' + id + '-t">' + esc(glue(a.title)) + '</h3>' +
			'<p class="attn-detail">' + esc(glue(a.detail)) + '</p>' +
			'<p class="attn-meta">' + kindChip(a.name) + '<span class="meta-text">' + esc(metaLine([a.name, whereIs(a.path), a.at ? agoWhole(a.at) : ''])) + '</span></p>' +
			'<div class="attn-actions">' + keys.join('') + '</div>' +
			'</div></li>'
		);
	}

	/** A file row: the whole row is the click target (an invisible key laid over it),
	 *  with any extra key above it. Every row ends in a glyph that says where a click
	 *  goes: a chevron to the file's page, or a folder when it has no page yet. Chips
	 *  always sit on the line under the name. */
	function fileRow(o) {
		var hit =
			'<button class="row-hit" type="button" data-action="' + o.action + '"' +
			(o.fileId ? ' data-file-id="' + esc(o.fileId) + '"' : '') +
			(o.path ? ' data-path="' + esc(o.path) + '"' : '') +
			' data-key="' + esc(o.key) + '" aria-labelledby="' + o.nameId + '"' + (o.hint ? ' title="' + esc(o.hint) + '"' : '') + '></button>';
		return (
			'<li class="row"><div class="row-main' + (o.extra ? ' has-extra' : '') + '">' +
			hit +
			icon(kindOf(o.name), 'row-icon') +
			'<span class="row-body">' +
			'<span class="row-name" id="' + o.nameId + '">' + esc(o.name) + '</span>' +
			'<span class="row-line">' + kindChip(o.name) + (o.chips || '') + (o.meta ? '<span class="row-meta">' + esc(o.meta) + '</span>' : '') + '</span>' +
			(o.note ? '<span class="row-note">' + esc(glue(o.note)) + '</span>' : '') +
			'</span>' +
			(o.extra || '') +
			icon(o.action === 'openFile' ? 'chev-right' : 'folder-go', 'row-go') +
			'</div></li>'
		);
	}

	function myFilesHtml(v) {
		var files = v.myFiles || [];
		var me = myName(v);
		var html = '<section class="group top" aria-labelledby="mine-label">';
		html += '<h2 class="section-label" id="mine-label"><span>My files</span>' + (files.length ? count(files.length, 'file', 'files') : '') + '</h2>';
		if (!files.length) {
			html += '<div class="empty-tile">' + icon('asm', 'empty-glyph') + '<div class="empty-words">';
			html += '<p>Nothing right now. Files you open and change in SolidWorks show up here until they\'re saved to Armory.</p>';
			html += '<p class="empty-where">Your team\'s files are in <span class="mono-plate">' + esc(v.vaultRoot) + '</span></p>';
			html +=
				'<button class="key" type="button" data-action="openVault" data-key="empty-vault">' + icon('folder') + '<span>Open Armory folder</span></button>';
			html += '</div></div>';
			return html + '</section>';
		}
		html += '<ul class="list-well">';
		files.forEach(function (f, i) {
			var found = findRow(v, f.fileId);
			var row = found ? found.row : { status: f.status, holder: null, releaseNotChecked: false };
			var chips = statusChip({ status: f.status, holder: row.holder, releaseNotChecked: false }, me);
			var extra = f.fileId
				? '<button class="key row-key" type="button" data-action="showInFolder" data-path="' + esc(f.path) + '" data-key="mine-folder-' + esc(f.fileId) + '" title="Show in folder, then double-click it to open it in SolidWorks" aria-label="Show ' + esc(f.name) + ' in folder">' +
				  icon('folder') + '<span class="key-word">Show in folder</span></button>'
				: '';
			html += fileRow({
				action: f.fileId ? 'openFile' : 'showInFolder',
				fileId: f.fileId,
				path: f.fileId ? null : f.path,
				key: f.fileId ? 'mine-' + f.fileId : 'mine-new-' + i,
				hint: f.fileId ? null : 'Show in folder',
				nameId: 'mine-n-' + i,
				name: f.name,
				chips: chips,
				meta: whereIs(f.path),
				note: noteBesideChip(f.note, f.status),
				extra: extra
			});
		});
		html += '</ul>';
		return html + '</section>';
	}

	/** Team files: two-line project tabs (the name, then how many files) at the top left,
	 *  then one card per project with each folder labeled inside it. */
	function projectsHtml(v) {
		var projects = v.projects || [];
		var me = myName(v);
		var html = '<section class="group top" aria-labelledby="proj-label">';
		html += '<h2 class="section-label" id="proj-label">Team files</h2>';
		if (!projects.length) {
			html += '<p class="group-help">You\'re not in any projects yet. Ask your teacher or CAD lead to add you.</p>';
			return html + '</section>';
		}
		var current = projects.filter(function (p) {
			return p.id === ui.projectId;
		})[0] || projects[0];
		ui.projectId = current.id;
		var fileCount = function (p) {
			return p.folders.reduce(function (n, f) {
				return n + f.files.length;
			}, 0);
		};
		html += '<div class="pads" role="tablist" aria-labelledby="proj-label">';
		projects.forEach(function (p) {
			var on = p.id === current.id;
			html +=
				'<button class="pad project-pad" type="button" role="tab" id="tab-' + esc(p.id) + '" aria-selected="' + on + '" aria-controls="project-panel"' +
				' data-action="project" data-project="' + esc(p.id) + '" data-key="tab-' + esc(p.id) + '">' +
				'<span class="pad-name">' + esc(p.name) + '</span><span class="pad-sub">' + esc(plural(fileCount(p), 'file', 'files')) + '</span></button>';
		});
		html += '</div>';
		html += '<div class="list-well project-card" id="project-panel" role="tabpanel" aria-labelledby="tab-' + esc(current.id) + '">';
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
				html += fileRow({
					action: 'openFile',
					fileId: row.fileId,
					key: 'row-' + row.fileId,
					nameId: 'n-' + row.fileId,
					name: row.name,
					chips: statusChip(row, me),
					meta: rowMeta(row)
				});
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

	/** What the file's display says (readout, tone, a line and a sentence), and what to
	 *  do after Show in folder. */
	function detailWords(d) {
		var h = d.holder;
		var w;
		if (h && h.isMe && !h.isMyOtherComputer) {
			w = {
				readout: "You're editing",
				tone: 'ok',
				line: "You're editing this",
				meta: h.savedToArmory ? 'Your latest save is in Armory. Each time you save in SolidWorks, Armory sends it to the team.' : 'Your newest changes haven\'t been sent yet. They send by themselves.',
				act: "It's open in SolidWorks on this computer. Show in folder finds the file if you closed it."
			};
		} else if (h && h.isMe) {
			w = {
				readout: 'Open on ' + h.device,
				tone: 'look',
				line: "You're editing this on " + h.device,
				meta: 'Close it on ' + h.device + ' to edit it on this computer.',
				act: 'Double-click it there to open it and look.'
			};
		} else if (h) {
			var who = firstName(h.name);
			w = {
				readout: who + ' is editing',
				tone: 'look',
				line: h.name + ' is editing this',
				meta: h.savedToArmory ? who + '\'s latest save is in Armory.' : who + '\'s newest changes aren\'t in Armory yet.',
				act: 'Double-click it there to open it and look. To make changes, wait until ' + who + ' closes it, or ask ' + who + '.'
			};
		} else {
			switch (d.status) {
				case 'conflict':
					w = {
						readout: 'Your copy kept',
						tone: 'look',
						line: 'Your changes were kept as your own copy',
						meta: 'Someone else saved first. Nothing was lost. Your copy is marked in the history.',
						act: 'Ask your CAD lead which one to keep. Double-click the file there to open the team\'s version.'
					};
					break;
				case 'newerWaiting':
					w = { readout: 'Newer version waiting', tone: 'look', line: 'A newer version is waiting', meta: 'Close it in SolidWorks to get the newer version.', act: 'Double-click it there to open it in SolidWorks.' };
					break;
				case 'refused':
					w = { readout: "Can't send", tone: 'bad', line: "Armory can't send your changes", meta: 'Your changes are safe on this computer. Needs you on Home says how to fix it.', act: 'Double-click it there to open it in SolidWorks and fix it.' };
					break;
				case 'waitingToSend':
					w = { readout: 'Waiting to send', tone: 'look', line: 'Saved on this computer', meta: 'It goes to Armory as soon as this computer is back online.', act: 'Double-click it there to keep working in SolidWorks.' };
					break;
				case 'syncing':
					w = { readout: 'Updating', tone: 'ok', line: 'Updating now', meta: 'Armory is moving the newest version. You can keep working.', act: 'Double-click it there to open it in SolidWorks.' };
					break;
				case 'notOnThisComputer':
					w = { readout: 'Not here yet', tone: 'off', line: 'Not on this computer yet', meta: 'Armory is getting it. It shows up in the folder soon.', act: null };
					break;
				default:
					w = {
						readout: 'Free to edit',
						tone: 'ok',
						line: 'You can open this and make changes',
						meta: 'Armory saves it for the team each time you save in SolidWorks.',
						act: 'Double-click it there to open it in SolidWorks.'
					};
			}
		}
		// One scale: a file whose version couldn't be checked is never shown as all clear.
		if (d.releaseNotChecked && w.tone === 'ok') {
			w.readout = 'Version not checked';
			w.tone = 'look';
		}
		return w;
	}

	function detailHtml(v) {
		var d = currentDetail(v);
		var w = detailWords(d);
		var me = myName(v);
		var html = '<article class="detail" aria-labelledby="detail-title">';
		html += titleBar(
			'h1',
			d.name,
			' id="detail-title" tabindex="-1" data-key="detail-title"',
			'<button class="key back-key" type="button" data-action="back" data-key="back" aria-label="Back to Home" title="Back to Home">' + icon('chev-left') + '</button>'
		);
		html += '<p class="title-sub">' + kindChip(d.name) + '<span class="meta-text">' + esc(whereIs(d.path)) + '</span></p>';
		html += '<div class="detail-grid">';

		html += '<div class="detail-side">';
		html += '<section class="display" data-tone="' + w.tone + '" aria-labelledby="holder-line">';
		html += '<p class="screen lcd"><span>' + esc(w.readout) + '</span></p>';
		html += '<h2 class="holder-line" id="holder-line">' + esc(w.line) + '</h2>';
		// When a warning follows, it is the sentence that matters; the display keeps to its line.
		if (w.meta && !d.releaseNotChecked) html += '<p class="holder-meta">' + esc(w.meta) + '</p>';
		html += '</section>';
		// A warning sits above the action, with an amber edge, so it is read first.
		if (d.releaseNotChecked) {
			html +=
				'<section class="panel note-panel" data-tone="look" aria-labelledby="rnc-title"><h3 id="rnc-title">' + icon('question') + '<span>SolidWorks version not checked</span></h3>' +
				'<p>Armory saved this file but couldn\'t tell which SolidWorks made it. If it won\'t open on a lab computer, open it in the team\'s SolidWorks version and save it again.</p></section>';
		}
		if (w.act) {
			html += '<div class="detail-act">';
			html +=
				'<button class="key primary side-key" type="button" data-action="showInFolder" data-path="' + esc(d.path) + '" data-key="show-in-folder">' +
				icon('folder') + '<span>Show in folder</span></button>';
			html += '<p class="act-help">' + esc(w.act) + '</p>';
			html += '</div>';
		}
		html += whoHtml(d);
		html += '</div>';

		html += '<section class="detail-main plate-recess" aria-labelledby="history-label"><div class="history-group brackets">';
		html += '<h2 class="section-label history-head" id="history-label"><span>History</span>' + (d.history ? count(d.history.length, 'save', 'saves') : '') + '</h2>';
		if (!d.history) {
			html += '<p class="hist-loading">Getting the history…</p>';
		} else {
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

	/** Who's editing, as a person: their initials, their name, and where and since when. */
	function whoHtml(d) {
		var h = d.holder;
		var html = '<section class="group top who-group" aria-labelledby="who-label"><h2 class="section-label" id="who-label">Who\'s editing</h2>';
		html += '<div class="panel who-panel">';
		if (h) {
			html +=
				'<span class="avatar big" data-tone="' + (h.isMe ? 'ok' : 'look') + '" aria-hidden="true">' + esc(initials(h.name)) + '</span>' +
				'<div class="who-words"><p class="who-name">' + esc(h.isMe ? h.name + ' (you)' : h.name) + '</p>' +
				'<p class="who-where">' + esc(metaLine([h.device, 'for ' + lasting(h.since)])) + '</p>' +
				// Someone else's school email, so the student knows how to ask them.
				(!h.isMe && h.email ? '<p class="who-where who-email">' + esc(h.email) + '</p>' : '') +
				'</div>';
		} else {
			html +=
				'<span class="avatar big" data-tone="off" aria-hidden="true">' + icon('check') + '</span>' +
				'<div class="who-words"><p class="who-name">Nobody right now</p><p class="who-where">The first person to open it in SolidWorks gets to edit it.</p></div>';
		}
		return html + '</div></section>';
	}

	/** A history entry: who and when is the title (what a student scans for). A kept copy
	 *  is its own title and, when it is the student's, is marked YOUR COPY on a tinted row.
	 *  The size is in the tooltip, not the line. */
	function historyEntry(e, me) {
		var routine = e.kind === 'version' && (e.note === 'Saved' || e.note === 'Added to Armory');
		var when = '<time datetime="' + esc(e.at) + '" title="' + esc(fullTime(e.at)) + '">' + esc(agoWhole(e.at)) + '</time>';
		var mine = e.kind === 'sideVersion' && me && (e.author.toLowerCase() === me.toLowerCase() || firstName(e.author).toLowerCase() === firstName(me).toLowerCase());
		var chips = '';
		if (e.isCurrent) chips += chip('Current', 'ok');
		if (e.kind === 'sideVersion') chips += chip(mine ? 'Your copy' : firstName(e.author) + '\'s copy', 'look');
		if (e.releaseNotChecked) chips += chip('Version not checked', 'look');
		return (
			'<li class="hist"' + (mine ? ' data-mine="true"' : '') + (e.kind === 'sideVersion' ? ' data-copy="true"' : '') + ' title="' + esc(size(e.bytes)) + '">' +
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
		html += '<p class="setting-help">When this is on, Armory opens by itself when you sign in to Windows, so your saves always reach the team.</p>';
		html += '</section>';

		html += '<section class="setting" aria-labelledby="set-theme-label">';
		html += '<h3 class="section-label" id="set-theme-label">Theme</h3>';
		html += '<div class="segmented" role="group" aria-labelledby="set-theme-label">';
		themes.forEach(function (t) {
			html +=
				'<button class="pad seg" type="button" data-action="theme" data-value="' + t[0] + '" data-key="set-theme-' + t[0] + '" aria-pressed="' + (s.theme === t[0]) + '">' +
				'<span class="swatch" data-swatch="' + t[0] + '" aria-hidden="true"></span>' +
				'<span class="seg-words"><span class="seg-name">' + esc(t[1]) + '</span><span class="seg-sub">' + esc(t[2]) + '</span></span></button>';
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

	/* ------------------------------------------------------ Scroll cues */

	/*
	 * A region with more below says so: a fade at its foot and a small tag counting the
	 * files still out of sight. The wide Home's recessed column has its own; the window
	 * has one for everything else. Paint only, no pointer events, and the tag is hidden
	 * from screen readers (they read the list itself).
	 */
	function below(box) {
		if (box.scrollHeight <= box.clientHeight + 1) return null;
		if (box.scrollTop + box.clientHeight >= box.scrollHeight - 2) return null;
		var limit = box.getBoundingClientRect().bottom - 28;
		var n = 0;
		var rows = box.querySelectorAll('.row, .attn-card, .history-list > li');
		var files = true;
		for (var i = 0; i < rows.length; i++) {
			if (rows[i].getBoundingClientRect().top >= limit) {
				n++;
				if (!rows[i].matches('.row')) files = false;
			}
		}
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
		var r = document.getElementById('recess-scroll');
		setCue(document.getElementById('recess-cue'), r && getComputedStyle(r).overflowY !== 'visible' ? below(r) : null);
		setCue(windowCue, below(scroller));
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
		updateCues();
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
		updateCues();
	}

	/* ------------------------------------------------------------ Events */

	document.addEventListener('click', function (e) {
		var el = e.target.closest ? e.target.closest('[data-action]') : null;
		// A chip in a row sits above the row's key so it keeps the arrow cursor (a tag is
		// never a button); a click on it is still a click on the row.
		if (!el && e.target.closest && e.target.closest('.row .chip')) el = e.target.closest('.row-main').querySelector('.row-hit');
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
			case 'useFolder':
				// A folder of the student's own, next to the one that is taken. Saving it is
				// the same as picking it in Settings; the host answers with a new view.
				saveSettings({ vaultRoot: el.getAttribute('data-path') });
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

	// Scrolling anywhere (the window or the recessed column) updates the "more below" tags.
	document.addEventListener('scroll', updateCues, { capture: true, passive: true });
	window.addEventListener('resize', updateCues);

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
				updateCues();
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
