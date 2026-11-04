/* First steps: Home's checklist for a student's first real edit (ledger 0504).
 *
 * The lesson (tutorial.js) does the job once on a practice part. This list is the same job
 * on the student's own work, and it ticks itself off from what the student really does,
 * never from a press on the list: a file checked out, opened, changed in SolidWorks,
 * checked in, and a look at who else has files. The line for the next step is lit, and so
 * is the one control in the window that does it (data-lit, a green ring), so there is
 * always exactly one thing to press. Show me scrolls that control into view.
 *
 * Ticks are kept per student on this computer (localStorage, like the lesson), so a
 * shared lab computer keeps each student's list apart. If the browser storage fails the
 * list still works for this session. Hide puts it away for good; Settings > Show me again
 * brings it back.
 *
 * app.js calls html(view) inside Home, light(view) after every draw, observe(view) for
 * every real view and sent(type, fields) for every real message to the host. Nothing here
 * sends anything to the host. */
(function () {
	'use strict';
	var PREFIX = 'armory.firstSteps.v1:';
	var LESSON = 'armory.tutorial.v1:';
	var STEPS = [
		{ id: 'practice', title: 'Try the practice', line: 'Do the short practice with a sample part.', key: 'Start', tip: 'Start the practice lesson again from the first card.' },
		{ id: 'checkOut', title: 'Check out your part', line: 'Find your part in your folder, then click Check out.', lit: '[data-key="d-checkout"], #vl-browser [data-action="checkOut"]' },
		{ id: 'open', title: 'Open it', line: 'Click Open, and it opens in SolidWorks.', lit: '[data-key="d-open"] [data-action="launch"], [data-key="d-open"][data-action="launch"], [data-part="mine"] [data-action="launch"]' },
		{ id: 'save', title: 'Change it and save', line: 'Change your part in SolidWorks and click Save.' },
		{ id: 'checkIn', title: 'Check it in', line: 'Close it in SolidWorks, then click Check in.', lit: '[data-key="d-checkin"], [data-part="mine"] [data-action="checkIn"]' },
		{ id: 'others', title: 'See who has what', line: 'Click Checked out by others to see who is working on what.', lit: '[data-action="searchOthers"]' }
	];
	var state = {}; // student -> { done: {id: true}, hidden: bool, pendingIn: [paths] }
	var current = '';
	var minePaths = [];

	function key(email) { return PREFIX + encodeURIComponent(email); }
	/** The demo page (a plain browser, the screenshot tools) shows the list only when asked
	 *  with ?firstSteps=1, as it starts the lesson only with ?tutorial=1. */
	function offInDemo() {
		try { return !!(window.ArmoryBridge && window.ArmoryBridge.isDemo) && new URLSearchParams(location.search).get('firstSteps') !== '1'; } catch (_) { return false; }
	}
	function load(email) {
		if (state[email]) return state[email];
		var s = { done: {}, hidden: false, pendingIn: [], celebrated: false };
		try { var raw = localStorage.getItem(key(email)); if (raw) s = Object.assign(s, JSON.parse(raw)); } catch (_) { /* this session only */ }
		state[email] = s;
		return s;
	}
	function save(email) {
		try { localStorage.setItem(key(email), JSON.stringify(state[email])); } catch (_) { /* this session only */ }
	}
	function studentOf(v) {
		return v && v.connection === 'signedIn' && v.account && v.account.email ? v.account.email.trim().toLowerCase() : '';
	}
	function lessonFinished(email) {
		if (hooks.finished) return hooks.finished(email);
		try { return localStorage.getItem(LESSON + encodeURIComponent(email)) === 'finished'; } catch (_) { return false; }
	}
	function tick(email, id) {
		var s = load(email);
		if (s.done[id]) return false;
		s.done[id] = true;
		save(email);
		return true;
	}
	function next(s) {
		for (var i = 0; i < STEPS.length; i++) if (!s.done[STEPS[i].id]) return STEPS[i];
		return null;
	}
	function esc(t) {
		return String(t).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; });
	}

	/** Ticks from the real view: the student's own files say what they have done. */
	function observe(v) {
		var email = studentOf(v);
		current = email;
		if (!email) return;
		var s = load(email);
		if (lessonFinished(email)) tick(email, 'practice');
		var mine = (v.myFiles || []).filter(function (f) { return f.checkout && f.checkout.state === 'mine'; });
		minePaths = mine.map(function (f) { return f.path; });
		if (mine.length) tick(email, 'checkOut');
		if (mine.some(function (f) { return f.status === 'changed'; })) { tick(email, 'save'); tick(email, 'open'); }
		if (s.pendingIn.length) {
			var still = s.pendingIn.filter(function (p) { return mine.some(function (f) { return f.path === p; }); });
			if (still.length < s.pendingIn.length) { tick(email, 'checkIn'); s.pendingIn = []; save(email); }
		}
	}
	/** Ticks from what the student pressed that reached the host. */
	function sent(type, fields) {
		if (!current) return;
		var s = load(current);
		// Opening a file someone else has, or one not checked out, is not this step.
		if (type === 'launchFile' && fields && minePaths.indexOf(fields.path) >= 0 && tick(current, 'open') && hooks.redraw) setTimeout(hooks.redraw, 0);
		if (type === 'checkIn' && fields && fields.paths && fields.paths.length) { s.pendingIn = fields.paths.slice(0, 50); save(current); }
	}

	function html(v, lessonActive) {
		var email = studentOf(v);
		if (!email || offInDemo()) return '';
		var s = load(email);
		if (s.hidden) return '';
		if (lessonFinished(email)) tick(email, 'practice');
		var n = next(s);
		var doneCount = STEPS.filter(function (x) { return s.done[x.id]; }).length;
		var out = '<section class="group top first-steps" id="first-steps" aria-labelledby="fs-label" data-part="first-steps">';
		out += '<h2 class="section-label" id="fs-label">First steps <span class="fs-count">' + doneCount + ' of ' + STEPS.length + '</span></h2>';
		if (!n) {
			out += '<p class="fs-done">' + check() + '<span>You did every step. Now you know Armory!</span></p>';
			out += '<div class="fs-keys"><button class="key" type="button" data-fs="hide" data-key="fs-hide" data-tip="Put this list away. Show me again in Settings brings it back.">Hide this list</button></div>';
			return out + '</section>';
		}
		out += '<ol class="fs-list">';
		STEPS.forEach(function (x, i) {
			var st = s.done[x.id] ? 'done' : x === n ? 'next' : 'todo';
			out += '<li class="fs-item" data-state="' + st + '" data-step="' + x.id + '"' + (st === 'next' ? ' aria-current="step"' : '') + '>';
			out += '<span class="fs-mark" aria-hidden="true">' + (st === 'done' ? check() : String(i + 1)) + '</span>';
			out += '<span class="fs-words"><span class="fs-title">' + (st === 'done' ? '<span class="visually-hidden">Done: </span>' : '') + esc(x.title) + '</span>';
			if (st === 'next') out += '<span class="fs-line">' + esc(x.line) + '</span>';
			out += '</span>';
			if (st === 'next' && x.id === 'practice')
				out += '<button class="key primary" type="button" data-fs="lesson" data-key="fs-lesson"' + (lessonActive ? '' : ' data-lit="first-steps"') + ' data-tip="' + esc(x.tip) + '">' + esc(x.key) + '</button>';
			else if (st === 'next' && x.lit)
				out += '<button class="key" type="button" data-fs="show" data-key="fs-show" data-tip="Find the control for this step and light it up.">Show me</button>';
			out += '</li>';
		});
		out += '</ol>';
		out += '<div class="fs-keys">';
		out += '<button class="key" type="button" data-fs="help" data-key="fs-help" data-tip="Tell Mr. Pina what went wrong, with a short record of what Armory was doing.">I’m stuck</button>';
		out += '<button class="key" type="button" data-fs="hide" data-key="fs-hide" data-tip="Put this list away. Show me again in Settings brings it back.">Hide this list</button>';
		out += '</div>';
		return out + '</section>';
	}
	function check() {
		return '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M5 12l5 5 9-10" /></svg>';
	}

	var litEl = null;
	/** Lights the one control that does the next step, if it is on this screen. */
	function light(v, lessonActive) {
		lastView = v; lastLesson = lessonActive;
		if (litEl && litEl.getAttribute('data-fs') !== 'lesson') litEl.removeAttribute('data-lit');
		litEl = null;
		var email = studentOf(v);
		if (!email || lessonActive || offInDemo()) return null;
		var s = load(email);
		var n = s.hidden ? null : next(s);
		if (!n || !n.lit) return null;
		var all = document.querySelectorAll(n.lit);
		for (var i = 0; i < all.length; i++) {
			var el = all[i];
			if (el.disabled || el.getAttribute('aria-disabled') === 'true' || !el.getClientRects().length || !inView(el)) continue;
			// Check in: the changed file first, when there is one.
			if (n.id === 'checkIn') {
				var row = el.closest('[data-path], li, .row');
				if (row && !/Changed/i.test(row.textContent) && i < all.length - 1) continue;
			}
			el.setAttribute('data-lit', 'first-steps');
			litEl = el;
			return el;
		}
		// Nothing on screen does it yet: Show me is what to press.
		var show = document.querySelector('[data-fs="show"]');
		if (show) { show.setAttribute('data-lit', 'first-steps'); litEl = show; }
		return null;
	}

	/** On screen now: inside the window and inside every scrolling box around it. */
	function inView(el) {
		var r = el.getBoundingClientRect();
		if (r.bottom <= 0 || r.top >= innerHeight || r.right <= 0 || r.left >= innerWidth) return false;
		for (var p = el.parentElement; p && p !== document.body; p = p.parentElement) {
			var o = getComputedStyle(p).overflowY;
			if (o !== 'auto' && o !== 'scroll') continue;
			var b = p.getBoundingClientRect();
			if (r.bottom <= b.top || r.top >= b.bottom) return false;
		}
		return true;
	}
	// A scroll can bring the step's control into view (or take it away): light again.
	var lastView = null, lastLesson = false, frame = 0;
	document.addEventListener('scroll', function () {
		if (frame || !lastView) return;
		frame = requestAnimationFrame(function () { frame = 0; light(lastView, lastLesson); });
	}, { capture: true, passive: true });

	var hooks = { lesson: null, help: null, redraw: null, finished: null };
	document.addEventListener('click', function (e) {
		var others = e.target.closest && e.target.closest('[data-action="searchOthers"]');
		if (others && current) { if (tick(current, 'others') && hooks.redraw) setTimeout(hooks.redraw, 0); }
		var el = e.target.closest ? e.target.closest('[data-fs]') : null;
		if (!el || !current) return;
		var what = el.getAttribute('data-fs');
		if (what === 'hide') { load(current).hidden = true; save(current); if (hooks.redraw) hooks.redraw(); }
		else if (what === 'lesson' && hooks.lesson) hooks.lesson();
		else if (what === 'help' && hooks.help) hooks.help();
		else if (what === 'show') {
			var target = litEl && litEl !== el ? litEl : null;
			if (!target) {
				// Bring the team's files into view, then light what is there.
				var n = load(current);
				var step = next(n);
				var first = step && step.lit ? document.querySelector(step.lit) : null;
				var browser = first || document.querySelector('[data-part="browser"]');
				if (browser) browser.scrollIntoView({ block: first ? 'center' : 'start', inline: 'nearest' });
				light(lastView, lastLesson);
				target = litEl && litEl !== el ? litEl : null;
			}
			if (target) { target.scrollIntoView({ block: 'center', inline: 'nearest' }); target.focus({ preventScroll: true }); }
		}
	});

	window.ArmoryFirstSteps = {
		steps: STEPS,
		html: html,
		light: light,
		observe: observe,
		sent: sent,
		/** Settings > Show me again: the list comes back, its ticks kept. */
		show: function () { if (current) { load(current).hidden = false; save(current); } },
		connect: function (h) { Object.assign(hooks, h); }
	};
})();
