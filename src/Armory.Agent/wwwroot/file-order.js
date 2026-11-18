/* Shared by the folder and search results. Unknown dates stay last in either direction.
 * 0.6.1: also Name (Z to A), and File type: the caller's group key for each row (parts,
 * then assemblies, then drawings, then the rest by kind), by name inside each group. */
(function () {
	'use strict';
	var names = new Intl.Collator('en', { numeric: true, sensitivity: 'base' });
	window.ArmoryFileOrder = function (rows, order, groupOf) {
		var byName = function (a, b) { return names.compare(a.name, b.name) || names.compare(a.path, b.path); };
		return rows.slice().sort(function (a, b) {
			if (order === 'newest' || order === 'oldest') {
				var x = Date.parse(a.updatedAt), y = Date.parse(b.updatedAt);
				if (isFinite(x) !== isFinite(y)) return isFinite(x) ? -1 : 1;
				if (isFinite(x) && x !== y) return order === 'newest' ? y - x : x - y;
			}
			if (order === 'type' && groupOf) {
				var g = names.compare(groupOf(a), groupOf(b));
				if (g) return g;
			}
			return order === 'nameDesc' ? -byName(a, b) : byName(a, b);
		});
	};
})();
