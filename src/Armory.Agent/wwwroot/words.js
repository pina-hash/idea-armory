/* The six words a student needs, and nothing more (ledger 0504).
 *
 * ONE VOCABULARY, APP AND WEBSITE. These are the only names for the ideas they stand for:
 * never lock, reserve, grab, sync, push, share, vault or "save to Armory" for them. The
 * website says the same six words with the same meanings in idea-app's
 * src/lib/armory/words.ts; change both together. tools/agent-ui/first-run-check.mjs holds
 * the list to six, every meaning to grade 5 or lower, and one icon beside each word.
 *
 * A word is shown where a student first meets it: the lesson card of that step carries its
 * line (an icon, the word, what it means), and Settings lists all six under Words to know. */
(function () {
	'use strict';
	var list = [
		{ id: 'project', word: 'Project', icon: 'folder', means: 'Your class or team, with its own files.' },
		{ id: 'checkOut', word: 'Check out', icon: 'pencil', means: 'Take a file so only you can change it.' },
		{ id: 'changed', word: 'Changed', icon: 'dot', means: 'You saved new work that your team does not have yet.' },
		{ id: 'checkIn', word: 'Check in', icon: 'upload', means: 'Give the file back so your team gets your change.' },
		{ id: 'available', word: 'Available', icon: 'check', means: 'Nobody has this file, so you can check it out.' },
		{ id: 'version', word: 'Version', icon: 'clock', means: 'Each check in is kept, so you can go back to it.' }
	];
	var byId = {};
	list.forEach(function (w) { byId[w.id] = w; });
	var ICONS = {
		folder: '<path d="M3 7h6l2 2h10v10H3z" />',
		pencil: '<path d="M4 20l4-1 11-11-3-3L5 16z" /><path d="M14 6l3 3" />',
		dot: '<circle cx="12" cy="12" r="5" />',
		upload: '<path d="M12 16V5" /><path d="M7 10l5-5 5 5" /><path d="M5 19h14" />',
		check: '<path d="M5 12l5 5 9-10" />',
		clock: '<circle cx="12" cy="12" r="8" /><path d="M12 8v4l3 2" />'
	};
	function esc(s) {
		return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; });
	}
	function iconSvg(name) {
		return '<svg class="word-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">' + (ICONS[name] || ICONS.dot) + '</svg>';
	}
	/** One word's line: the icon beside the word, then what it means. */
	function line(id) {
		var w = byId[id];
		if (!w) return '';
		return '<p class="word-line" data-word="' + esc(w.id) + '">' + iconSvg(w.icon) + '<span><strong>' + esc(w.word) + ':</strong> ' + esc(w.means) + '</span></p>';
	}
	/** Every word, for Settings. */
	function all() {
		return '<dl class="word-list">' + list.map(function (w) {
			return '<div class="word-row" data-word="' + esc(w.id) + '"><dt>' + iconSvg(w.icon) + '<span>' + esc(w.word) + '</span></dt><dd>' + esc(w.means) + '</dd></div>';
		}).join('') + '</dl>';
	}
	window.ArmoryWords = { list: list, byId: byId, line: line, all: all, icon: iconSvg };
})();
