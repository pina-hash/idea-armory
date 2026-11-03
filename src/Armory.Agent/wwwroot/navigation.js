/* Window navigation only. No browser history entries or file actions. */
(function () {
	'use strict';
	window.ArmoryNavigation = function (adapter) {
		var entries = [], at = -1, restoring = false;
		function before() {
			if (!restoring && adapter.allowed() && at >= 0) entries[at] = adapter.capture();
		}
		function after() {
			if (restoring || !adapter.allowed()) return;
			var next = adapter.capture();
			if (at >= 0 && adapter.same(entries[at], next)) { entries[at] = next; return; }
			entries.splice(at + 1);
			entries.push(next);
			at = entries.length - 1;
			if (entries.length > 100) { entries.shift(); at--; }
		}
		function go(direction) {
			if (!adapter.allowed() || at + direction < 0 || at + direction >= entries.length) return false;
			before();
			at += direction;
			restoring = true;
			try { adapter.restore(entries[at]); } finally { restoring = false; }
			return true;
		}
		function sideButton(e) { return e.button === 3 || e.button === 4; }
		document.addEventListener('mousedown', function (e) {
			if (sideButton(e)) e.preventDefault();
		}, true);
		document.addEventListener('mouseup', function (e) {
			if (!sideButton(e)) return;
			e.preventDefault();
			e.stopImmediatePropagation();
			go(e.button === 3 ? -1 : 1);
		}, true);
		document.addEventListener('auxclick', function (e) {
			if (sideButton(e)) { e.preventDefault(); e.stopImmediatePropagation(); }
		}, true);
		document.addEventListener('keydown', function (e) {
			if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.isComposing || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return;
			e.preventDefault();
			e.stopImmediatePropagation();
			go(e.key === 'ArrowLeft' ? -1 : 1);
		}, true);
		return { before: before, after: after, go: go, reset: function () { entries = []; at = -1; after(); } };
	};
})();
