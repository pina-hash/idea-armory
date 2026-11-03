/* First-run guidance. The adapter renders the real window; practice never reaches the host. */
(function () {
	'use strict';
	var steps = [
		{ id: 'welcome', title: 'Your first few minutes', target: '[data-part="browser"]', body: 'Armory keeps your team\'s files together. We will find your work, then try a practice file. You can skip now and choose Show me again in Settings.' },
		{ id: 'project', title: 'Find your project', target: '[role="tablist"]', click: '[data-action="project"]', action: 'project', body: 'Choose your class or team project here. Each project has its own folders. If yours is missing, ask your teacher to add you.' },
		{ id: 'folder', title: 'Find your own folder', target: '.browser-body', click: '[data-action="folder"]', action: 'folder', body: 'Open your folder. In a class project, look for your last and first name, like Reyes_Ana. Save your work there. If you cannot find it, ask your teacher.' },
		{ afterTarget: '.detail-side .display', id: 'file', title: 'Try a practice file', practice: true, phase: 'available', place: 'home', target: '#vl-browser [data-action="openFile"]', click: '[data-action="openFile"]', action: 'openFile', body: 'This is a practice file inside the same file list. Open its details by choosing its name. All practice changes stay in this lesson.' },
		{ id: 'checkout', title: 'Check out before editing', practice: true, phase: 'available', target: '[data-key="d-checkout"]', click: '[data-action="checkOut"]', action: 'checkOut', body: 'Choose Check out. It reserves the file for you to change. Other people can still look at it, but cannot check it out until you check it in.' },
		{ id: 'mine', title: 'Checked out by you', practice: true, phase: 'mine', target: '.detail-side .display', body: 'Your name and the green mark mean you can edit this file on this computer. The words carry the meaning, even if you cannot see the color.' },
		{ id: 'open', title: 'Open from Armory', practice: true, phase: 'mine', target: '[data-key="d-open"]', click: '[data-action="launch"]', action: 'launchFile', body: 'Choose Open. For a real part, this starts SolidWorks. This practice only shows the result; it does not start another program. Later, save in SolidWorks before coming back here.' },
		{ id: 'save', title: 'Try a saved change', practice: true, phase: 'mine', target: '.detail-side .display', action: 'practiceSave', body: 'In SolidWorks, make your change and click Save. Try that here with Make a practice change. It only changes our practice file.', exercise: 'Make a practice change' },
		{ id: 'changed', title: 'Changed on this computer', practice: true, phase: 'changed', place: 'home', target: '#vl-browser .chip.lamp.amber', body: 'Changed means this copy differs from the last check in. Your teammates get the new shared version when you check it in. Saving and checking in are two separate steps.' },
		{ id: 'checkin', title: 'Share your finished work', practice: true, phase: 'changed', target: '[data-key="d-checkin"]', click: '[data-action="checkIn"]', action: 'checkIn', body: 'Save and close the file in SolidWorks, then choose Check in. Armory shares your changes and lets the next person edit. If the file is still open, it waits for you to close it.' },
		{ id: 'saved', title: 'Up to date', practice: true, phase: 'available', target: '.detail-side .display', body: 'Up to date means this copy matches the version shared in Armory. A normal saved row needs no extra badge. Available means nobody has it checked out.' },
		{ id: 'other', title: 'Two people want one file', practice: true, phase: 'other', target: '.detail-side .display', body: 'Ana has this practice file checked out. The first check out wins, so there is no Check out button for you now. Ask Ana to check it in when done. You can open it to look, read-only.' },
		{ id: 'search', title: 'Find a file fast', practice: true, phase: 'changed', place: 'home', target: '#file-search', action: 'search', body: 'Type Practice in Search files. It searches this folder and the folders inside it by file name or folder. Ctrl+F jumps here. Escape clears your words.' },
		{ id: 'filters', title: 'Narrow the list', practice: true, phase: 'changed', place: 'home', target: '[aria-label="Quick filters"]', click: '[data-action^="search"]', action: 'filter', body: 'Try a quick filter: checked out by me, by others, or changed here. File type below offers parts, assemblies, drawings, and other files. Reset filters brings the full list back.' },
		{ id: 'storage', title: 'Where your work goes', target: '#set-root-value', settings: true, body: 'This is the Armory folder on this computer. Each project lives inside it. Lab students share that folder. Work you leave waiting stays yours and goes to Armory under your sign-in when you return.' },
		{ afterTarget: '.picker-inner', preview: true, id: 'student', title: 'Check your name', target: '[data-key="switch-student"], [data-key="switch-account"]', click: '[data-action="showPicker"], [data-action="switchAccount"]', action: 'switchPreview', body: 'Before working, check who is using Armory. On a shared lab PC, choose Switch student, then your name. Add a student signs you in once. Try this button for a practice preview.' },
		{ afterTarget: '#ask-report', id: 'report', title: 'Something looks wrong?', target: '[data-key="set-report"]', settings: true, click: '[data-action="askReport"]', action: 'askReport', body: 'Choose Report a problem in Settings. Say what you expected and what happened. A report includes a short record of what Armory was doing, with file names, never file contents.' },
		{ id: 'words', title: 'Practice asking for help', target: '#ask-report', report: true, action: 'reportWords', body: 'Type a few practice words. In real use, Send saves your report and tries to send it. If offline, it waits. This lesson will discard your practice words without sending them.' },
		{ id: 'finish', title: 'Ready to try your own work', target: '[data-key="set-tutorial"]', settings: true, body: 'Find your project and folder. Check out, open, save, close, then check in. You can come back to Show me again any time. Ask your teacher if you are unsure.' }
	];
	window.ArmoryTutorial = { steps: steps, create: function (adapter) {
		var root = document.getElementById('tutorial'), card = root.querySelector('.tutorial-card'), spot = root.querySelector('.tutorial-spot');
		var index = -1, target = null, completed = {}, student = '', saved = null, frame = 0, inert = [], sessionSeen = {}, stopped = false;
		var prefix = 'armory.tutorial.v1:';
		function active() { return index >= 0; }
		function key(email) { return prefix + encodeURIComponent(email); }
		function remember(state) {
			sessionSeen[student] = true;
			try { localStorage.setItem(key(student), state); return true; } catch (_) { return false; }
		}
		function seen(email) {
			try { return sessionSeen[email] || !!localStorage.getItem(key(email)); } catch (_) { return !!sessionSeen[email]; }
		}
		function release() {
			inert.forEach(function (e) { e[0].inert = e[1]; }); inert = [];
			if (target) target.removeAttribute('data-tutorial-target');
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
			card.querySelector('#tutorial-hint').textContent = stopped ? 'Nothing to choose here yet. Ask your teacher, or continue the lesson.' : step().action && !completed[index] ? (step().exercise ? 'Try the practice change, then choose Next.' : 'Try the highlighted control, then choose Next.') : 'Choose Next when you are ready.';
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
			update();
			isolate();
			var r = target.getBoundingClientRect(), w = window.innerWidth, h = window.innerHeight;
			var width = Math.min(344, w - 16);
			card.style.width = width + 'px';
			var left = r.right + 16 + width <= w ? r.right + 16 : r.left - width - 16 >= 0 ? r.left - width - 16 : Math.max(8, w - width - 8);
			var beside = r.right + 16 + width <= w || r.left - width - 16 >= 0;
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
			card.querySelector('#tutorial-progress').textContent = 'Step ' + (index + 1) + ' of ' + steps.length + (step().practice || step().preview ? ' · Practice' : '');
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
