/* Help at the moment it is needed (0.5.7, idea-app ledger 0537): a short card on Home the first
 * time a student checks out a file, and the first time two people changed one file. Front-loaded
 * lessons did not raise how often people got this right; help at the first check out and the
 * first conflict does. One picture: check out, work, check in.
 *
 * A card shows only when it happens while Armory is open (a file becomes checked out to you, or a
 * new "your copy kept" card that needs a look appears), never for what was already there when the
 * window opened, so a returning student is not told again. Got it remembers it per student, in this
 * window's own storage (armory.moment.v1:<moment>:<email>). Grade 5 words, the six words of
 * words.js: Project, Check out, Changed, Check in, Available, Version. */
(function () {
	'use strict';
	var MOMENTS = {
		checkout: {
			title: 'You checked out a file',
			lines: [
				'Only you can save it now. Others can still look.',
				'When you finish, save it, close it, then Check in.',
				'Your team waits for it until you check it in.'
			]
		},
		twoChanges: {
			title: 'Two people changed one file',
			lines: [
				'Someone else checked it in first.',
				'Your change is safe. It is a version in the history.',
				'Next time, check out the file before you change it.'
			]
		}
	};
	var prefix = 'armory.moment.v1:';
	var seenNow = {};
	var before = null;
	var pending = null;

	function key(id, email) {
		return prefix + id + ':' + encodeURIComponent(email || '');
	}
	function seen(id, email) {
		if (seenNow[key(id, email)]) return true;
		try { return !!localStorage.getItem(key(id, email)); } catch (_) { return false; }
	}
	function emailOf(v) {
		return v && v.connection === 'signedIn' && v.account ? String(v.account.email || '').trim().toLowerCase() : '';
	}
	function mineIds(v) {
		var out = {};
		(v.myFiles || []).forEach(function (f) {
			if (f.checkout && f.checkout.state === 'mine') out[f.fileId || f.path] = true;
		});
		return out;
	}
	function conflictKeys(v) {
		var out = {};
		(v.notices || []).forEach(function (n) {
			if (n.kind === 'keptCopy' && n.tone === 'look') out[n.key] = true;
		});
		return out;
	}
	function added(now, was) {
		return Object.keys(now).some(function (k) { return !was[k]; });
	}

	window.ArmoryMoments = {
		moments: MOMENTS,
		/* Called with every view: notes what newly happened, and answers the card to show, if any. */
		observe: function (v) {
			var email = emailOf(v);
			if (!email) { before = null; pending = null; return null; }
			var now = { email: email, mine: mineIds(v), conflicts: conflictKeys(v) };
			if (before && before.email === email) {
				if (added(now.conflicts, before.conflicts) && !seen('twoChanges', email)) pending = 'twoChanges';
				else if (!pending && added(now.mine, before.mine) && !seen('checkout', email)) pending = 'checkout';
			} else pending = null;
			before = now;
			return pending;
		},
		html: function (id, esc, tip) {
			var m = MOMENTS[id];
			if (!m) return '';
			return (
				'<section class="group top moment-group" aria-labelledby="moment-title" data-part="moment" data-moment="' + esc(id) + '">' +
				'<div class="attn-card panel" data-tone="ok" data-kind="moment"><div class="attn-body">' +
				'<h2 class="attn-title" id="moment-title">' + esc(m.title) + '</h2>' +
				'<ul class="moment-lines">' + m.lines.map(function (l) { return '<li>' + esc(l) + '</li>'; }).join('') + '</ul>' +
				'<div class="attn-actions"><button class="key primary" type="button" data-action="momentDone" data-moment="' + esc(id) + '" data-key="moment-done"' + (tip || '') + '>Got it</button></div>' +
				'</div></div></section>'
			);
		},
		/* Got it: never again for this student on this computer. */
		done: function (id) {
			var email = before ? before.email : '';
			seenNow[key(id, email)] = true;
			try { localStorage.setItem(key(id, email), 'seen'); } catch (_) { /* this window only */ }
			if (pending === id) pending = null;
		},
		/* For tests and the demo: forget what this window saw. */
		reset: function () { before = null; pending = null; seenNow = {}; }
	};
})();
