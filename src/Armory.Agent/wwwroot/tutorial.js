/* First-run guidance. The adapter renders the real window; practice never reaches the host. */
(function () {
	'use strict';
	// THE FIRST RUN IS A WORKED EXAMPLE (ledger 0504). The student does the whole job once
	// on a practice part, one short sentence a card: get it, open it, change it, save, give
	// it back, then see what it looks like when someone else has a file. The real project
	// comes after, on Home's First steps list (first-steps.js), which lights each real step.
	// The 0.5.5 lesson's nineteen cards taught search, filters, the Armory folder, Switch
	// student and Report a problem as well; none of those changes what a student does in a
	// first edit, so they left the lesson (Search and the filters stay one press away on
	// Home, Report a problem is on the First steps list, and the picker asks who is using a
	// shared computer by itself). Each card's topic is named so check-ui can hold the
	// lesson to the job rather than to a count. A card's word is its glossary line
	// (words.js), shown on the card where the student first meets that word.
	var steps = [
		{ id: 'welcome', topic: 'overview', title: 'Let\'s try Armory', practice: true, phase: 'available', place: 'home', target: '[data-part="browser"]', word: 'project', body: 'You will get a practice part, change it, and give it back.' },
		{ id: 'file', topic: 'find', title: 'Open the part', practice: true, phase: 'available', place: 'home', afterTarget: '.detail-side .display', target: '#vl-browser [data-action="openFile"]', click: '[data-action="openFile"]', action: 'openFile', word: 'available', body: 'Click Practice-Plate to see the part.' },
		{ id: 'checkout', topic: 'checkOut', title: 'Check it out', practice: true, phase: 'available', target: '[data-key="d-checkout"]', click: '[data-action="checkOut"]', action: 'checkOut', word: 'checkOut', body: 'Click Check out so you can change it.' },
		{ id: 'open', topic: 'launch', title: 'Open it', practice: true, phase: 'mine', target: '[data-key="d-open"]', click: '[data-action="launch"]', action: 'launchFile', body: 'Click Open, and your part opens in SolidWorks.' },
		{ id: 'save', topic: 'save', title: 'Change it and save', practice: true, phase: 'mine', target: '.detail-side .display', action: 'practiceSave', exercise: 'Make a practice change', word: 'changed', body: 'In SolidWorks you change it and click Save, so try it here.' },
		{ id: 'checkin', topic: 'checkIn', title: 'Check it in', practice: true, phase: 'changed', target: '[data-key="d-checkin"]', click: '[data-action="checkIn"]', action: 'checkIn', word: 'checkIn', body: 'Close it in SolidWorks, then click Check in.' },
		{ id: 'other', topic: 'other', title: 'See who has a file', practice: true, phase: 'other', target: '.detail-side .display', body: 'Ana has this part now, so you can look but not change it.' },
		{ id: 'finish', topic: 'realWork', title: 'Now do it for real', target: '#first-steps', body: 'The First steps list on Home lights up each step for you.' }
	];
	/** How the card sits (the three first-run candidates in the 0504 pull request):
	 *  coach, the card beside the lit control with progress dots (the default); quest, the
	 *  same card with every step listed and ticked, like a game's quest log; bar, one big
	 *  sentence docked across the window's foot. Only the default ships to students; the
	 *  others are kept for the comparison screenshots (?lesson=quest|bar in a browser). */
	var LAYOUTS = ['coach', 'quest', 'bar'];
	var layout = (function () {
		try { var q = new URLSearchParams(location.search).get('lesson'); return LAYOUTS.indexOf(q) >= 0 ? q : LAYOUTS[0]; } catch (_) { return LAYOUTS[0]; }
	})();
	window.ArmoryTutorial = { steps: steps, layouts: LAYOUTS, create: function (adapter) {
		var root = document.getElementById('tutorial'), card = root.querySelector('.tutorial-card'), spot = root.querySelector('.tutorial-spot');
		var index = -1, target = null, pressed = null, completed = {}, student = '', saved = null, frame = 0, inert = [], sessionSeen = {}, stopped = false;
		root.setAttribute('data-layout', layout);
		var prefix = 'armory.tutorial.v1:';
		function active() { return index >= 0; }
		function key(email) { return prefix + encodeURIComponent(email); }
		var sessionState = {};
		function remember(state) {
			sessionSeen[student] = true;
			sessionState[student] = state;
			try { localStorage.setItem(key(student), state); return true; } catch (_) { return false; }
		}
		function seen(email) {
			try { return sessionSeen[email] || !!localStorage.getItem(key(email)); } catch (_) { return !!sessionSeen[email]; }
		}
		function release() {
			inert.forEach(function (e) { e[0].inert = e[1]; }); inert = [];
			if (target) target.removeAttribute('data-tutorial-target');
			if (pressed) pressed.removeAttribute('data-lit'); pressed = null;
		}
		function isolate() {
			var keep = [target, root];
			function walk(node) {
				Array.prototype.forEach.call(node.children, function (child) {
					if (keep.indexOf(child) >= 0 || child.tagName === 'SCRIPT') return;
					if (keep.some(function (k) { return child.contains(k); })) walk(child);
					else { inert.push([child, child.inert]); child.inert = true; }
				});
			}
			walk(document.body);
		}
		function step() { return steps[index]; }
		function allowedControls() {
			var items = [];
			if (target && step().click) items = Array.prototype.filter.call(target.querySelectorAll('button, input, select, [tabindex]'), function (e) { return e.matches(step().click) && e.tabIndex >= 0; });
			if (target && (target.matches(step().click || 'input, textarea') || /^(INPUT|TEXTAREA)$/.test(target.tagName))) items.unshift(target);
			return items.concat(Array.prototype.filter.call(card.querySelectorAll('[tabindex="0"], button'), function (e) { return !e.disabled && !e.hidden; }));
		}
		function update() {
			card.querySelector('[data-tour="next"]').disabled = !!step().action && !completed[index] && !stopped;
			// One sentence a card: the body says what to do, the lit control shows where, and
			// Next wakes once it is done. The hint speaks only when there is nothing to press.
			card.querySelector('#tutorial-hint').textContent = stopped ? 'Nothing to click here yet, so click Next.' : '';
		}
		function place() {
			frame = 0;
			if (!active()) return;
			release();
			var found = document.querySelector(step().target);
			if (found && !found.checkVisibility({ visibilityProperty: true })) found = null;
			if (!found && completed[index] && step().afterTarget) found = document.querySelector(step().afterTarget);
			stopped = !found || !!(step().click && found && !found.matches(step().click) && !found.querySelector(step().click) && !completed[index]);
			// Empty accounts and projects still teach the rest of the lesson.
			target = found || document.querySelector('[data-part="browser"]') || document.getElementById('main');
			var parent = document.getElementById('ask').open ? document.getElementById('ask') : document.getElementById('settings').open ? document.getElementById('settings') : document.body;
			if (root.parentNode !== parent) parent.appendChild(root);
			target.setAttribute('data-tutorial-target', step().id);
			// THE NEXT ACTION IS ALWAYS LIT: the one control to press wears data-lit (a green
			// ring), or the exercise key, or Next itself once the step is done.
			var press = null;
			if (!completed[index] && !stopped && step().click) press = target.matches(step().click) ? target : target.querySelector(step().click);
			else if (!completed[index] && !stopped && step().exercise) press = card.querySelector('[data-tour="exercise"]');
			if (!press) press = card.querySelector('[data-tour="next"]');
			pressed = press; pressed.setAttribute('data-lit', 'lesson');
			update();
			isolate();
			var r = target.getBoundingClientRect(), w = window.innerWidth, h = window.innerHeight;
			var width = Math.min(344, w - 16);
			card.style.width = width + 'px';
			if (layout === 'bar') { width = w - 16; card.style.width = width + 'px'; }
			var left = layout === 'bar' ? 8 : r.right + 16 + width <= w ? r.right + 16 : r.left - width - 16 >= 0 ? r.left - width - 16 : Math.max(8, w - width - 8);
			var beside = layout !== 'bar' && (r.right + 16 + width <= w || r.left - width - 16 >= 0);
			card.style.left = left + 'px';
			card.style.top = (beside ? Math.max(8, Math.min(r.top, h - card.offsetHeight - 8)) : h - card.offsetHeight - 8) + 'px';
			var c = card.getBoundingClientRect();
			// Keep the real target above a docked card, also in nested scrolling columns.
			if (!beside && (r.bottom > c.top - 12 || r.top < 8)) {
				var sc = target.parentElement;
				while (sc && sc !== document.body) {
					if (sc.scrollHeight > sc.clientHeight && /auto|scroll/.test(getComputedStyle(sc).overflowY)) {
						sc.scrollTop += r.top - Math.max(8, (c.top - Math.min(r.height, 80)) / 2);
						r = target.getBoundingClientRect();
					}
					sc = sc.parentElement;
				}
			}
			var top = Math.max(4, r.top - 4), bottom = Math.min(h - 4, r.bottom + 4, beside ? h : c.top - 8);
			spot.style.left = Math.max(4, r.left - 4) + 'px';
			spot.style.top = top + 'px';
			spot.style.width = Math.max(1, Math.min(w - 4, r.right + 4) - Math.max(4, r.left - 4)) + 'px';
			spot.style.height = Math.max(1, bottom - top) + 'px';
		}
		function refresh() { if (active() && !frame) frame = requestAnimationFrame(place); }
		function enter(next) {
			release(); index = next;
			// Leave top-layer dialogs before the adapter closes or replaces them.
			document.body.appendChild(root);
			adapter.prepare(step());
			root.hidden = false;
			root.setAttribute('data-step', step().id);
			card.querySelector('#tutorial-title').textContent = step().title;
			card.querySelector('#tutorial-body').textContent = step().body;
			card.querySelector('#tutorial-progress').textContent = (step().practice ? 'Practice ' : 'Step ') + (index + 1) + ' of ' + steps.length;
			// Progress you can see: a dot a step (the quest layout lists each step by name).
			card.querySelector('#tutorial-dots').innerHTML = steps.map(function (s, i) {
				var state = i < index ? 'done' : i === index ? 'current' : 'todo';
				return '<li data-state="' + state + '"><span class="dot"></span><span class="quest-name">' + s.title.replace(/[&<>"]/g, '') + '</span></li>';
			}).join('');
			var word = card.querySelector('#tutorial-word');
			word.innerHTML = step().word && window.ArmoryWords ? window.ArmoryWords.line(step().word) : '';
			word.hidden = !word.innerHTML;
			card.querySelector('[data-tour="back"]').disabled = index === 0;
			card.querySelector('[data-tour="next"]').textContent = index === steps.length - 1 ? 'Finish' : 'Next';
			var exercise = card.querySelector('[data-tour="exercise"]');
			exercise.hidden = !step().exercise; exercise.textContent = step().exercise || '';
			card.scrollTop = 0;
			var t = document.querySelector(step().target);
			if (t) t.scrollIntoView({ block: 'center', inline: 'nearest' });
			place();
			card.querySelector('#tutorial-title').focus({ preventScroll: true });
		}
		function start() {
			if (active() || !adapter.student()) return;
			student = adapter.student(); saved = adapter.capture(); completed = {};
			remember('seen');
			document.body.setAttribute('data-tutorial', 'true');
			enter(0);
		}
		function stop(state, restore) {
			if (!active()) return;
			var persisted = state ? remember(state) : true;
			release(); index = -1; target = null;
			root.hidden = true; document.body.appendChild(root);
			document.body.removeAttribute('data-tutorial');
			adapter.stop(restore ? saved : null);
			if (!persisted) adapter.storageFailed();
		}
		function action(name) {
			if (!active() || step().action !== name) return;
			completed[index] = true; update(); refresh();
		}
		document.addEventListener('click', function (e) {
			if (!active()) return;
			var control = e.target.closest('[data-tour]');
			if (control && root.contains(control)) {
				e.preventDefault(); e.stopImmediatePropagation();
				if (control.disabled) return;
				var name = control.getAttribute('data-tour');
				if (name === 'skip') stop('skipped', true);
				else if (name === 'back' && index > 0) enter(index - 1);
				else if (name === 'next' && (!step().action || completed[index] || stopped)) {
					if (index === steps.length - 1) stop('finished', true); else enter(index + 1);
				} else if (name === 'exercise') { adapter.save(); action('practiceSave'); }
				return;
			}
			var hit = e.target.closest('[data-action]');
			if (!target || !target.contains(e.target) || !step().click || !hit || !hit.matches(step().click)) {
				// Text fields accept a click too.
				if (target === e.target && /^(INPUT|TEXTAREA)$/.test(target.tagName)) return;
				e.preventDefault(); e.stopImmediatePropagation(); return;
			}
			if (step().id === 'student') {
				e.preventDefault(); e.stopImmediatePropagation();
				adapter.previewStudent(); action('switchPreview'); return;
			}
			var name = step().action;
			setTimeout(function () { action(name); refresh(); }, 0);
		}, true);
		document.addEventListener('input', function (e) {
			if (active() && e.target === target && e.target.value.trim()) action(step().id === 'search' ? 'search' : 'reportWords');
		}, true);
		document.addEventListener('keydown', function (e) {
			if (!active()) return;
			if (e.key === 'Escape' && step().id === 'search' && target && target.value) return;
			if (e.key === 'Escape') { e.preventDefault(); e.stopImmediatePropagation(); stop('skipped', true); return; }
			if (e.target.id === 'tutorial-copy') {
				var copy = e.target, scroll = { ArrowDown: 40, ArrowUp: -40, PageDown: copy.clientHeight, PageUp: -copy.clientHeight };
				if (scroll[e.key] || e.key === 'Home' || e.key === 'End') {
					e.preventDefault(); e.stopImmediatePropagation();
					copy.scrollTop = e.key === 'Home' ? 0 : e.key === 'End' ? copy.scrollHeight : copy.scrollTop + scroll[e.key];
					return;
				}
			}
			if (e.key === 'Tab') {
				var controls = allowedControls(), at = controls.indexOf(document.activeElement);
				var next = controls[(at + (e.shiftKey ? -1 : 1) + controls.length) % controls.length];
				e.preventDefault(); e.stopImmediatePropagation(); if (next) next.focus();
			} else if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); e.stopImmediatePropagation(); }
		}, true);
		// No drag, context-menu action or stray programmatic focus can escape the lesson.
		['drop', 'dragover', 'contextmenu'].forEach(function (name) { document.addEventListener(name, function (e) { if (active()) { e.preventDefault(); e.stopImmediatePropagation(); } }, true); });
		document.addEventListener('focusin', function (e) {
			if (active() && !root.contains(e.target) && allowedControls().indexOf(e.target) < 0) card.querySelector('#tutorial-title').focus({ preventScroll: true });
		});
		window.addEventListener('resize', refresh);
		document.addEventListener('scroll', refresh, { capture: true, passive: true });
		return { active: active, start: start, refresh: refresh, action: action, owner: function () { return student; },
			/** True once this student finished the lesson, this session or before. */
			finished: function (email) {
				if (sessionState[email] === 'finished') return true;
				try { return localStorage.getItem(key(email)) === 'finished'; } catch (_) { return false; }
			},
			practice: function () { return active() && !!(step().practice || step().preview); },
			consider: function () {
				var current = adapter.student();
				if (active() && current !== student) stop(null, false);
				if (!active() && current && !seen(current) && adapter.auto()) start();
			},
			cancel: function () { stop(null, false); }
		};
	} };
})();
