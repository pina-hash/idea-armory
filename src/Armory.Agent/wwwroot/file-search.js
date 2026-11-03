/* Pure file-list decisions. No DOM, bridge, disk or network access. */
(function (root, factory) {
	'use strict';
	if (typeof module === 'object' && module.exports) module.exports = factory();
	else root.ArmoryFileSearch = factory();
})(typeof globalThis === 'object' ? globalThis : this, function () {
	'use strict';

	var Types = Object.freeze({ any: 0, part: 1, assembly: 2, drawing: 3, other: 4 });
	var Checkouts = Object.freeze({ any: 0, me: 1, others: 2 });

	// Canonically equivalent names match. Uppercase is independent of the computer's
	// locale and handles Greek sigma and Unicode case expansions consistently. Accents
	// stay significant; either slash is accepted when searching a folder path.
	function fold(text) {
		return String(text == null ? '' : text).normalize('NFC').replace(/\\/g, '/').toUpperCase();
	}

	function typeOf(name) {
		var leaf = String(name == null ? '' : name).split(/[\\/]/).pop();
		var dot = leaf.lastIndexOf('.');
		if (dot <= 0) return Types.other;
		switch (leaf.slice(dot).toLowerCase()) {
			case '.sldprt': return Types.part;
			case '.sldasm': return Types.assembly;
			case '.slddrw': return Types.drawing;
			default: return Types.other;
		}
	}

	// Prepare a snapshot once per inventory, not per keystroke. Callers supply files
	// in scope. Empty folders supply no files. The returned indexes preserve input order
	// and identity, including duplicate names; neither inputs nor options are changed.
	function create(files) {
		var prepared = files.map(function (f) {
			return { name: fold(f.name), path: fold(f.path), type: typeOf(f.name),
				me: f.checkout === 'mine' || f.checkout === 'myOtherComputer',
				others: f.checkout === 'other', changed: !!f.changed };
		});
		return Object.freeze({
			count: prepared.length,
			filter: function (options) {
				options = options || {};
				var text = fold(String(options.text || '').trim());
				var checkout = options.checkout || Checkouts.any;
				var type = options.type || Types.any;
				var result = [];
				prepared.forEach(function (f, i) {
					if (text && f.name.indexOf(text) < 0 && f.path.indexOf(text) < 0) return;
					// Checkout choices union. Text, checkout, changed and type intersect.
					if (checkout && !((checkout & Checkouts.me) && f.me || (checkout & Checkouts.others) && f.others)) return;
					if (options.changed && !f.changed || type && type !== f.type) return;
					result.push(i);
				});
				return result;
			}
		});
	}

	return Object.freeze({ create: create, typeOf: typeOf, Types: Types, Checkouts: Checkouts });
});
