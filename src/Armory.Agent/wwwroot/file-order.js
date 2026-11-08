/* Shared by the folder and search results. Unknown dates stay last in either direction. */
(function () {
	'use strict';
	var names = new Intl.Collator('en', { numeric: true, sensitivity: 'base' });
	window.ArmoryFileOrder = function (rows, order) {
		return rows.slice().sort(function (a, b) {
			if (order === 'newest' || order === 'oldest') {
				var x = Date.parse(a.updatedAt), y = Date.parse(b.updatedAt);
				if (isFinite(x) !== isFinite(y)) return isFinite(x) ? -1 : 1;
				if (isFinite(x) && x !== y) return order === 'newest' ? y - x : x - y;
			}
			return names.compare(a.name, b.name) || names.compare(a.path, b.path);
		});
	};
})();
