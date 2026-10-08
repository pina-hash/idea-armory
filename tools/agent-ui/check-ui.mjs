// Checks the Agent window against its hard rules, on every rendered combination
// (screen, state, size) in both themes:
//
//   44px        every visible a, button, [role=button], input, select, [tabindex] (and
//               every Tab stop) is at least 44x44 px.
//   network     no request leaves file:// (every request is intercepted; http and https
//               are aborted and fail the run).
//   grids       no element or pseudo-element paints a grid: no repeating gradient and no
//               gradient tiled smaller than its box. List rows are reported on their own.
//               A planted grid must be detected first, or the sweep proves nothing.
//   decoration  no screws, hatch, rails or brackets on list rows, and no screws, hatch,
//               rails or engraving anywhere off the window's main plate (the Settings
//               sheet, a card, a list).
//   chips       a status chip never looks like a button: not a button itself, no pointer
//               cursor (its own, or a key's laid over it), no raised drop shadow, nothing
//               changes on hover.
//   overflow    no horizontal scroll, nothing cut off at the window's or scroller's edge,
//               no words spilling out of a key or chip.
//   hairline    the outer hairline of every control, well, card and housing holds 3:1
//               against the ground it sits on; so does the divider between two rows.
//   keyboard    Tab reaches every control, and each one shows a focus ring at least 2px
//               wide that holds 3:1 against its ground.
//   copy        the words a student sees never use jargon: lock, unlock, conflict, sync,
//               journal, side version, intent, RPC, hash, vault. Upload and download are
//               allowed (v2 decision D5: Mr. Pina's own words for what is moving).
//   offline     no file in wwwroot names a web address (the SVG namespace inside a data:
//               URI is the one exception: it is an identifier, never fetched), loads a
//               font with @font-face or anything with @import, or points url() anywhere
//               but data: or an in-page #id (%23id inside a data: URI).
//   flows       clicking (or pressing Enter on) a row moves the view to that file's
//               detail, scrolled to the top with its heading focused, and Back returns to
//               the row; a notice card opens and closes its list; a folder row goes into the
//               folder and a crumb back out; picking files (Shift for a range) and Check
//               out checks them out, says so in the quiet result line, and Escape lets go;
//               the 5,000-file folder keeps fewer than 150 rows drawn and reaches its last
//               file by scrolling and by the End key; each file row offers Check out or
//               Check in beside Open (Open in one column) and its pick key draws an empty
//               box; Check out all asks first, with the count, starting on Cancel; My
//               files lists only my check outs and "waiting to upload" is said once; an
//               unzip is one summary that lists no file twice and agrees with the ring; a
//               file that shares a name is renamed in the app (a taken name and a lost
//               extension refused); the check-out question says to reopen the file and its
//               key checks out and reopens; the Settings sheet, Pause (Resume is the green
//               primary, and nothing to pause while offline), Connect and the one-click
//               folder of your own do what they say.
//   logo        the IDEA gear turns (idea-gear-spin, 24s, linear, infinite) when motion is
//               allowed, holds still when the student asks for reduced motion, and is
//               fully painted in both.
//   bridge      inside a stand-in WebView2 host (no demo transport): the page says ready
//               first, renders Home, detail and Connect from host messages alone, wears
//               effectiveTheme, ignores a stray or unknown message, and every one of the 25
//               page-to-host types is sent by the control that should send it, carrying
//               exactly the fields BRIDGE.md gives it (an action's requestId included; a
//               drop goes with its files through postMessageWithAdditionalObjects). An
//               'activity' message keeps focus and scroll and patches only the panel and
//               the status line; Not now on the check-out question sends that question's
//               key and hides only it; a kept copy's tone follows HistoryEntryView.routine,
//               never its note; an 'actionResult' shows its words in the quiet line; a
//               view that arrives while the folder dialog is open keeps the typed name. In
//               a plain browser the theme comes from ?theme= (idea, spaceWhite or
//               space-white) or prefers-color-scheme.
//   shapes      every demo view and file detail has exactly the fields bridge.js documents
//               for each record, and only the words its unions allow (AgentViewContractTests
//               holds the host's C# records to the same typedefs, so the demo can't invent a
//               field the host never sends). A planted extra and missing field are caught.
//   em dash     no U+2014 anywhere in wwwroot (or in these tools, BRIDGE.md, the design
//               review, both screens indexes and docs/overnight).
//
// Exits non-zero on any failure. Run: node tools/agent-ui/check-ui.mjs

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadPlaywright, combos, comboName, openPage, demoStates, WWWROOT, ROOT, THEMES, SIZES, walkFiles } from './lib.mjs';

const { chromium } = loadPlaywright();
const problems = [];
const tally = {
	pages: 0,
	controls: 0,
	under44: 0,
	network: 0,
	grids: 0,
	rowGrids: 0,
	rowDecoration: 0,
	chipsLikeButtons: 0,
	overflow: 0,
	hairlines: 0,
	hairlineUnder3: 0,
	hairlineMin: Infinity,
	tabStops: 0,
	focusMissed: 0,
	ringFailures: 0,
	ringMin: Infinity,
	jargon: 0,
	flows: 0,
	flowFailures: 0,
	emDash: 0
};
function problem(kind, where, detail) {
	problems.push(`${kind.padEnd(10)} ${where}: ${detail}`);
}

/* ------------------------------------------------------------- Em dash */

const EM_DASH = String.fromCharCode(0x2014);
const overnight = path.join(ROOT, 'docs', 'overnight');
const scanned = [
	...walkFiles(WWWROOT),
	...walkFiles(path.join(ROOT, 'tools', 'agent-ui')),
	path.join(ROOT, 'docs', 'agent', 'screens', 'README.md'),
	path.join(ROOT, 'docs', 'agent', 'screens', 'v2', 'README.md'),
	path.join(ROOT, 'docs', 'agent', 'DESIGN-REVIEW.md'),
	path.join(ROOT, 'docs', 'agent', 'BRIDGE.md'),
	...(fs.existsSync(overnight) ? walkFiles(overnight) : [])
].filter((f) => fs.existsSync(f) && !/\.(png|jpe?g|gif|ico|webp)$/i.test(f));
// The v2 index must be there to be swept: a missing file would pass in silence.
for (const must of [path.join(ROOT, 'docs', 'agent', 'screens', 'v2', 'README.md'), path.join(ROOT, 'docs', 'agent', 'BRIDGE.md')])
	if (!fs.existsSync(must)) problem('em dash', path.relative(ROOT, must), 'missing, so it was never swept');
for (const file of scanned) {
	const lines = fs.readFileSync(file, 'utf8').split('\n');
	lines.forEach((line, i) => {
		if (line.includes(EM_DASH)) {
			tally.emDash++;
			problem('em dash', path.relative(ROOT, file) + ':' + (i + 1), line.trim().slice(0, 80));
		}
	});
}

/* ------------------------------------------------- Offline, statically */

// What the page ships must name no web address and load no font or stylesheet from
// anywhere. The one http string allowed is the SVG namespace, which a data: URI SVG must
// carry to parse and which no browser fetches.
const SVG_NS = 'http://www.w3.org/2000/svg';
function offlineProblems(text) {
	const found = [];
	const stripped = text.split(SVG_NS).join('');
	for (const m of stripped.matchAll(/(?:https?:)?\/\/[a-z0-9.-]+\.[a-z]{2,}[^\s'")]*/gi)) found.push('web address ' + m[0].slice(0, 60));
	for (const m of stripped.matchAll(/@font-face|@import/gi)) found.push(m[0]);
	for (const m of stripped.matchAll(/url\(\s*(['"]?)([^'")]*)/gi)) {
		const target = m[2].trim();
		// An in-page reference: #id, or %23id inside a data: URI SVG (its own gradients and clips).
		if (target && !/^(data:|#|%23)/i.test(target)) found.push('url(' + target.slice(0, 40) + ')');
	}
	return found;
}
tally.offline = 0;
// The scan must see a planted address, font and import before its silence means anything.
{
	const planted = offlineProblems(
		"@import url('x.css'); @font-face { src: url(https://fonts.example.com/a.woff2) } a { b: url(\"data:image/svg+xml,%3Csvg xmlns='" + SVG_NS + "'%3E\") }"
	);
	tally.offlinePlanted = planted.length;
	// Five: the import, its url(), the @font-face, its url() and the web address in it.
	if (planted.length < 5) problem('offline', 'planted', `the offline scan found ${planted.length} of 5 planted problems`);
}
for (const file of walkFiles(WWWROOT).filter((f) => /\.(html|css|js|mjs|json|svg)$/i.test(f))) {
	const lines = fs.readFileSync(file, 'utf8').split('\n');
	lines.forEach((line, i) => {
		for (const what of offlineProblems(line)) {
			tally.offline++;
			problem('offline', path.relative(ROOT, file) + ':' + (i + 1), what);
		}
	});
}

/* ------------------------------------------------------- Demo shapes */

// Every check below runs on the demo's views, so they must be the views the host sends:
// exactly the fields bridge.js documents for each record (a "@typedef {object} Name" with
// its "@property {Type} field" lines), and only the words a union typedef allows.
// AgentViewContractTests holds the host's C# records to the same typedefs.
function readTypedefs(text) {
	const objects = {};
	const unions = {};
	for (const block of text.matchAll(/\/\*\*([\s\S]*?)\*\//g)) {
		const body = block[1];
		const name = body.match(/@typedef\s*\{object\}\s*(\w+)/);
		if (name) objects[name[1]] = [...body.matchAll(/@property\s*\{([^}]*)\}\s*(\w+)/g)].map((m) => ({ field: m[2], type: m[1].trim() }));
		for (const u of body.matchAll(/@typedef\s*\{([^{}]*?)\}\s*(\w+)/g)) {
			const flat = u[1].replace(/\n\s*\*/g, ' ');
			const words = [...flat.matchAll(/'([^']*)'/g)].map((m) => m[1]);
			if (words.length && !flat.replace(/'[^']*'/g, '').replace(/[|\s]/g, '')) unions[u[2]] = words;
		}
	}
	return { objects, unions };
}
const TYPES = readTypedefs(fs.readFileSync(path.join(WWWROOT, 'bridge.js'), 'utf8'));
function shapeProblems(value, type, at, out) {
	const def = TYPES.objects[type];
	if (!def) return out.push(`${at}: bridge.js documents no ${type}`), out;
	if (!value || typeof value !== 'object' || Array.isArray(value)) return out.push(`${at}: not an object (${type})`), out;
	const want = def.map((d) => d.field).sort();
	const have = Object.keys(value).sort();
	if (want.join() !== have.join()) out.push(`${at} (${type}) has {${have.join(', ')}}, bridge.js documents {${want.join(', ')}}`);
	for (const d of def) {
		const v = value[d.field];
		if (v === undefined) continue; // said above
		const nullable = /\bnull\b/.test(d.type);
		if (v === null) {
			if (!nullable) out.push(`${at}.${d.field} is null, bridge.js says ${d.type}`);
			continue;
		}
		const bare = d.type.replace(/\|\s*null/, '').trim();
		if (TYPES.unions[bare] && !TYPES.unions[bare].includes(v)) out.push(`${at}.${d.field} is '${v}', not one of ${bare}`);
		const inner = bare.match(/^(\w+View)(\[\])?$/);
		if (!inner) continue;
		if (!inner[2]) shapeProblems(v, inner[1], `${at}.${d.field}`, out);
		else if (!Array.isArray(v)) out.push(`${at}.${d.field} is not a list`);
		else v.forEach((x, i) => shapeProblems(x, inner[1], `${at}.${d.field}[${i}]`, out));
	}
	return out;
}
tally.shapes = 0;
tally.shapeFailures = 0;
{
	const demo = demoStates();
	const clone = (x) => JSON.parse(JSON.stringify(x));
	for (const [name, st] of Object.entries(demo.states)) {
		const found = shapeProblems(clone(st.view), 'AgentView', name + '.view', []);
		if (st.screens.includes('detail')) {
			const v = st.view;
			let id = st.detailFileId;
			for (const p of v.projects) for (const f of p.folders) for (const r of f.files) if (!id && r.fileId) id = r.fileId;
			shapeProblems(clone(demo.detailFor(name, id, v)), 'FileDetailView', name + '.detail', found);
		}
		tally.shapes++;
		for (const f of found.slice(0, 5)) {
			tally.shapeFailures++;
			problem('shape', 'demo ' + name, f);
		}
		if (found.length > 5) problem('shape', 'demo ' + name, `... and ${found.length - 5} more`);
	}
	// Planted: a row with a field the host never sends and one the host does, left out,
	// and a status word FileStatus doesn't have.
	const planted = clone(demo.states.synced.view);
	const row = planted.projects[0].folders[0].files[0];
	row.holder = null;
	delete row.changed;
	row.status = 'editingByMe';
	tally.shapesPlanted = shapeProblems(planted, 'AgentView', 'planted', []).length;
	if (tally.shapesPlanted < 2) problem('shape', 'planted', `the shape check found ${tally.shapesPlanted} of 2 planted problems`);
}

/* ------------------------------------------------------ In-page probes */

// Words a student never reads or hears in this window. Kept here as source strings so the
// in-page probe and the self-test below use the same list. v2 (decision D5) lets upload
// and download through: "Uploading", "Downloading" and "Moving" are Mr. Pina's words for
// what is happening right now. Every other word stays banned.
const JARGON = [
	'\\block(s|ed|ing)?\\b',
	'\\bunlock',
	'\\bconflict(s|ed|ing)?\\b',
	'\\bsync(s|ed|ing)?\\b',
	'\\bjournal',
	'side[ -]version',
	'\\bintents?\\b',
	'\\bRPC\\b',
	'\\bhash(es)?\\b',
	'\\bvault'
];
// Each pattern must catch the word it is for.
for (const [re, sample] of JARGON.map((src, i) => [
	new RegExp(src, 'i'),
	['Lock held', 'Unlock it', 'Sync conflict', 'Syncing now', 'Journal entry', 'A side version', 'Intent sent', 'RPC failed', 'Hash mismatch', 'Open vault'][i]
])) {
	if (!re.test(sample)) problem('control', 'jargon list', `${re} misses "${sample}"`);
}
// And the list must let the v2 words through, and nothing wider than it means.
for (const fine of ['Uploading 3 of 9 files', 'Downloading 412 of 1,280 files', 'Moving 120 files to Gearbox', 'Checked out by Maria Lopez', 'Blocks of files'])
	for (const src of JARGON) if (new RegExp(src, 'i').test(fine)) problem('control', 'jargon list', `${src} wrongly catches "${fine}"`);

/** Runs in the page. Everything that can be read without moving the mouse or keyboard. */
function inspect(jargonSources) {
	const out = { controls: 0, small: [], grids: [], rowGrids: [], rowDecoration: [], chips: [], overflow: [], hairlines: [], jargon: [] };

	const visible = (el) => {
		const r = el.getBoundingClientRect();
		if (r.width === 0 && r.height === 0) return false;
		if (el.checkVisibility && !el.checkVisibility({ visibilityProperty: true })) return false;
		return true;
	};
	const describe = (el) => {
		const r = el.getBoundingClientRect();
		const text = (el.getAttribute('aria-label') || el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40);
		const cls = typeof el.className === 'string' && el.className ? '.' + el.className.trim().split(/\s+/).join('.') : '';
		return `<${el.tagName.toLowerCase()}${cls}> "${text}" ${r.width.toFixed(1)}x${r.height.toFixed(1)}`;
	};

	// 44px rule.
	const controls = [...document.querySelectorAll('a, button, [role=button], input, select, textarea, summary, [tabindex]')].filter(visible);
	out.controls = controls.length;
	for (const el of controls) {
		const r = el.getBoundingClientRect();
		if (r.width < 44 || r.height < 44) out.small.push(describe(el));
	}

	// Grid backgrounds: a repeating gradient, or a gradient tiled smaller than its box.
	const splitTop = (s) => {
		const parts = [];
		let depth = 0;
		let cur = '';
		for (const ch of s) {
			if (ch === '(') depth++;
			if (ch === ')') depth--;
			if (ch === ',' && depth === 0) {
				parts.push(cur.trim());
				cur = '';
			} else cur += ch;
		}
		if (cur.trim()) parts.push(cur.trim());
		return parts;
	};
	const toPx = (v, box) => {
		if (!v || v === 'auto' || v === 'cover' || v === 'contain') return box;
		if (v.endsWith('%')) return (parseFloat(v) / 100) * box;
		if (v.endsWith('px')) return parseFloat(v);
		return box;
	};
	const repeatPair = (v) => {
		const t = v.trim().split(/\s+/);
		if (t.length === 2) return t;
		if (t[0] === 'repeat-x') return ['repeat', 'no-repeat'];
		if (t[0] === 'repeat-y') return ['no-repeat', 'repeat'];
		return [t[0], t[0]];
	};
	const gridLayers = (el, pseudo) => {
		const cs = getComputedStyle(el, pseudo);
		const img = cs.backgroundImage;
		if (!img || img === 'none') return [];
		if (pseudo && cs.content === 'none') return [];
		const layers = splitTop(img);
		const sizes = splitTop(cs.backgroundSize);
		const repeats = splitTop(cs.backgroundRepeat);
		const r = el.getBoundingClientRect();
		const boxW = pseudo ? parseFloat(cs.width) || r.width : r.width;
		const boxH = pseudo ? parseFloat(cs.height) || r.height : r.height;
		const found = [];
		layers.forEach((layer, i) => {
			if (/repeating-(linear|radial|conic)-gradient/.test(layer)) {
				found.push(layer.slice(0, 60));
				return;
			}
			if (!/gradient\(/.test(layer)) return;
			const [rx, ry] = repeatPair(repeats[i % repeats.length]);
			const size = sizes[i % sizes.length].split(/\s+/);
			const tw = toPx(size[0], boxW);
			const th = toPx(size[1] || 'auto', boxH);
			const tilesX = rx !== 'no-repeat' && tw > 0 && tw < boxW - 0.5;
			const tilesY = ry !== 'no-repeat' && th > 0 && th < boxH - 0.5;
			if (tilesX || tilesY) found.push(`${layer.slice(0, 50)} tiled ${size.join(' ')}`);
		});
		return found;
	};
	for (const el of document.querySelectorAll('*')) {
		if (el.closest('svg')) continue;
		for (const pseudo of [null, '::before', '::after']) {
			for (const g of gridLayers(el, pseudo)) {
				const where = describe(el) + (pseudo || '');
				if (el.closest('.row')) out.rowGrids.push(where + ' ' + g);
				else out.grids.push(where + ' ' + g);
			}
		}
	}

	// Decoration stays on the main plate: nothing on a list row.
	for (const row of document.querySelectorAll('.row')) {
		for (const el of [row, ...row.querySelectorAll('*')]) {
			if (el.matches('.plate-slab, .plate-engrave, .plate-rail, .plate-recess, .plate-title, .group')) out.rowDecoration.push(describe(el));
			for (const pseudo of ['::before', '::after']) {
				const cs = getComputedStyle(el, pseudo);
				if (cs.content !== 'none' && cs.maskImage && cs.maskImage !== 'none') out.rowDecoration.push(describe(el) + pseudo + ' (hatch mask)');
			}
		}
	}

	// Off the main plate (the Settings sheet, a card, a list) there are no screws, hatch,
	// rails or engraving either: decoration belongs to the window's main plate only.
	for (const host of document.querySelectorAll('dialog[open], .panel, .list-well')) {
		if (!visible(host)) continue;
		for (const el of host.querySelectorAll('*')) {
			if (el.matches('.plate-slab, .plate-engrave, .plate-rail')) out.rowDecoration.push(describe(el) + ' (off the main plate)');
			if (el.matches('.plate-title') && ['::before', '::after'].some((ps) => getComputedStyle(el, ps).content !== 'none'))
				out.rowDecoration.push(describe(el) + ' hazard hatch (off the main plate)');
		}
	}

	// Chips are recessed tags, never keys.
	const shadows = (s) =>
		s === 'none'
			? []
			: splitTop(s).map((sh) => {
					const inset = /\binset\b/.test(sh);
					const nums = (sh.replace(/rgba?\([^)]*\)/g, '').match(/-?\d*\.?\d+px/g) || []).map(parseFloat);
					return { inset, x: nums[0] || 0, y: nums[1] || 0, blur: nums[2] || 0, spread: nums[3] || 0 };
			  });
	for (const c of document.querySelectorAll('.chip')) {
		if (!visible(c)) continue;
		const cs = getComputedStyle(c);
		const why = [];
		if (c.matches('a, button, [role=button], [tabindex]')) why.push('is itself a control');
		if (cs.cursor === 'pointer') why.push('pointer cursor');
		if (shadows(cs.boxShadow).some((s) => !s.inset && (s.blur > 0 || s.y > 1 || s.spread > 0))) why.push('raised drop shadow');
		if (cs.transform !== 'none') why.push('transformed');
		// What the mouse actually meets over the chip: a key laid over it (a whole-row click
		// target) must not show its pointer hand on the chip. A chip under the open
		// Settings sheet is out of the mouse's reach, so the sheet's own keys don't count.
		const b = c.getBoundingClientRect();
		const cx = b.x + b.width / 2;
		const cy = b.y + b.height / 2;
		const underSheet = !!document.querySelector('dialog[open]') && !c.closest('dialog');
		if (!underSheet && cx >= 0 && cy >= 0 && cx < innerWidth && cy < innerHeight) {
			const top = document.elementFromPoint(cx, cy);
			if (top && !c.contains(top) && getComputedStyle(top).cursor === 'pointer') why.push('covered by a pointer-cursor ' + top.tagName.toLowerCase());
		}
		if (why.length) out.chips.push(describe(c) + ': ' + why.join(', '));
	}

	// Overflow: no horizontal scroll, nothing cut at an edge, no words spilling out.
	const se = document.scrollingElement;
	if (se.scrollWidth > innerWidth + 0.5) out.overflow.push(`page scrolls sideways: ${se.scrollWidth} > ${innerWidth}`);
	const scroller = document.getElementById('scroller');
	const sr = scroller.getBoundingClientRect();
	const sLeft = sr.left + scroller.clientLeft;
	const sRight = sLeft + scroller.clientWidth;
	for (const el of document.querySelectorAll('body *')) {
		if (el.closest('svg') || el.closest('.sprite') || !visible(el)) continue;
		const cs = getComputedStyle(el);
		const r = el.getBoundingClientRect();
		if ((cs.overflowX === 'auto' || cs.overflowX === 'scroll') && el.scrollWidth > el.clientWidth + 1)
			out.overflow.push(describe(el) + ` scrolls sideways (${el.scrollWidth} > ${el.clientWidth})`);
		if (r.right > innerWidth + 0.5 || r.left < -0.5) out.overflow.push(describe(el) + ` leaves the window (${r.left.toFixed(1)}..${r.right.toFixed(1)})`);
		else if (scroller.contains(el) && (r.right > sRight + 0.5 || r.left < sLeft - 0.5))
			out.overflow.push(describe(el) + ` is cut at the scroller's edge (${r.left.toFixed(1)}..${r.right.toFixed(1)} vs ${sLeft.toFixed(1)}..${sRight.toFixed(1)})`);
		if (el.matches('.key, .chip, .pad, .switch, .well, .row-main, h1, h2, h3, p') && el.scrollWidth > el.clientWidth + 1)
			out.overflow.push(describe(el) + ` spills its words (${el.scrollWidth} > ${el.clientWidth})`);
	}

	// Hairlines: the outer edge of a composite border against the ground under it.
	const parse = (s) => {
		const m = s && s.match(/rgba?\(([^)]+)\)/);
		if (!m) return null;
		const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
		return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1];
	};
	const over = (top, under) => {
		const a = top[3];
		return [0, 1, 2].map((i) => top[i] * a + under[i] * (1 - a)).concat(1);
	};
	const lum = (c) => {
		const f = (v) => {
			v /= 255;
			return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
		};
		return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]);
	};
	const ratio = (a, b) => {
		const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p);
		return (x + 0.05) / (y + 0.05);
	};
	const groundOf = (el) => {
		const bodyBg = parse(getComputedStyle(document.body).backgroundColor);
		if (el.matches('dialog')) {
			const scrim = parse(getComputedStyle(el, '::backdrop').backgroundColor);
			return scrim ? over(scrim, bodyBg) : bodyBg;
		}
		let p = el.parentElement;
		while (p) {
			const c = parse(getComputedStyle(p).backgroundColor);
			if (c && c[3] >= 0.999) return c;
			p = p.parentElement;
		}
		return bodyBg;
	};
	const edgeSel = '.key, .switch, .well, .list-well, .panel, .display, .history-well, dialog.housing, .pad, .field, .track';
	for (const el of document.querySelectorAll(edgeSel)) {
		if (!visible(el) || el.matches(':disabled, [aria-disabled="true"]')) continue;
		const cs = getComputedStyle(el);
		const ground = groundOf(el);
		let colors;
		if (el.matches('.pad')) colors = [parse(cs.boxShadow)];
		else colors = ['Top', 'Right', 'Bottom', 'Left'].map((s) => parse(cs['border' + s + 'Color']));
		let worst = Infinity;
		for (const c of colors) if (c) worst = Math.min(worst, ratio(over(c, ground), ground));
		out.hairlines.push({ what: describe(el), ratio: worst });
	}
	for (const well of document.querySelectorAll('.list-well')) {
		if (!visible(well) || well.querySelectorAll('.row').length < 2) continue;
		const divider = getComputedStyle(well).getPropertyValue('--plate-divider').trim();
		const probe = document.createElement('span');
		probe.style.color = divider;
		well.appendChild(probe);
		const c = parse(getComputedStyle(probe).color);
		probe.remove();
		const ground = parse(getComputedStyle(well).backgroundColor);
		out.hairlines.push({ what: describe(well) + ' row divider', ratio: ratio(c, ground) });
	}

	// Copy: what a student reads or hears (visible text, labels, tooltips) stays plain.
	const words = [document.body.innerText, document.title];
	for (const el of document.querySelectorAll('[aria-label], [title]')) words.push(el.getAttribute('aria-label') || '', el.getAttribute('title') || '');
	const said = words.join('\n');
	for (const re of jargonSources.map((src) => new RegExp(src, 'i'))) {
		const m = said.match(re);
		if (m) out.jargon.push(`"${m[0]}" in "${said.slice(Math.max(0, m.index - 30), m.index + 30).replace(/\s+/g, ' ')}"`);
	}
	return out;
}

/** Runs in the page: what the keyboard focus is on right now. */
function focusInfo() {
	const el = document.activeElement;
	if (!el || el === document.body || el === document.documentElement) return null;
	const r = el.getBoundingClientRect();
	const cs = getComputedStyle(el);
	const parse = (s) => {
		const m = s && s.match(/rgba?\(([^)]+)\)/);
		if (!m) return null;
		const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
		return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1];
	};
	const lum = (c) => {
		const f = (v) => {
			v /= 255;
			return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
		};
		return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]);
	};
	let ground = null;
	for (let p = el.parentElement; p && !ground; p = p.parentElement) {
		const c = parse(getComputedStyle(p).backgroundColor);
		if (c && c[3] >= 0.999) ground = c;
	}
	ground = ground || parse(getComputedStyle(document.body).backgroundColor);
	const ring = parse(cs.outlineColor);
	const [hi, lo] = [lum(ring), lum(ground)].sort((a, b) => b - a);
	return {
		id: el.getAttribute('data-key') || el.id || el.tagName.toLowerCase() + ':' + (el.textContent || '').trim().slice(0, 20),
		tabindex: el.getAttribute('tabindex'),
		w: r.width,
		h: r.height,
		outlineStyle: cs.outlineStyle,
		outlineWidth: parseFloat(cs.outlineWidth),
		ringRatio: (hi + 0.05) / (lo + 0.05)
	};
}

/** Runs in the page: the controls Tab should reach. */
function expectedTabStops() {
	const dialog = document.querySelector('dialog[open]');
	const scope = dialog || document;
	return [...scope.querySelectorAll('a[href], button, input, select, textarea, [tabindex]')]
		.filter((el) => el.getAttribute('tabindex') !== '-1' && !el.disabled)
		.filter((el) => {
			const r = el.getBoundingClientRect();
			return (r.width > 0 || r.height > 0) && (!el.checkVisibility || el.checkVisibility({ visibilityProperty: true }));
		})
		.map((el) => el.getAttribute('data-key') || el.id || el.tagName.toLowerCase() + ':' + (el.textContent || '').trim().slice(0, 20));
}

/* --------------------------------------------------------------- Run */

const browser = await chromium.launch();
const list = combos();
let gridControlChecked = false;

for (const c of list) {
	for (const theme of THEMES) {
		const where = `${comboName(c)} ${theme}`;
		const leaks = [];
		const { context, page } = await openPage(browser, c, theme, {
			onContext: async (ctx) => {
				await ctx.route('**/*', (route) => {
					const url = route.request().url();
					if (/^https?:/i.test(url)) {
						leaks.push(url);
						return route.abort();
					}
					return route.continue();
				});
			},
			onPage: async (p) => {
				p.on('request', (req) => {
					const url = req.url();
					if (!/^(file|data|blob|about):/i.test(url) && !leaks.includes(url)) leaks.push(url);
				});
			}
		});
		tally.pages++;

		const r = await page.evaluate(inspect, JARGON);
		tally.controls += r.controls;
		for (const s of r.small) {
			tally.under44++;
			problem('44px', where, s);
		}
		for (const g of r.grids) {
			tally.grids++;
			problem('grid', where, g);
		}
		for (const g of r.rowGrids) {
			tally.rowGrids++;
			problem('row grid', where, g);
		}
		for (const d of r.rowDecoration) {
			tally.rowDecoration++;
			problem('row decor', where, d);
		}
		for (const ch of r.chips) {
			tally.chipsLikeButtons++;
			problem('chip', where, ch);
		}
		for (const o of r.overflow) {
			tally.overflow++;
			problem('overflow', where, o);
		}
		for (const j of r.jargon) {
			tally.jargon++;
			problem('copy', where, j);
		}
		for (const h of r.hairlines) {
			tally.hairlines++;
			tally.hairlineMin = Math.min(tally.hairlineMin, h.ratio);
			if (h.ratio < 3) {
				tally.hairlineUnder3++;
				problem('hairline', where, `${h.what} edge ${h.ratio.toFixed(2)}:1`);
			}
		}

		// A chip does nothing on hover: its paint and box stay put.
		const chips = await page.$$('.chip');
		for (const chip of chips.slice(0, 3)) {
			if (!(await chip.isVisible())) continue;
			const read = () => chip.evaluate((el) => {
				const cs = getComputedStyle(el);
				const b = el.getBoundingClientRect();
				return [cs.boxShadow, cs.transform, cs.backgroundImage, cs.cursor, b.x, b.y, b.width, b.height].join('|');
			});
			await chip.scrollIntoViewIfNeeded();
			const before = await read();
			await chip.hover({ force: true });
			const after = await read();
			if (before !== after) {
				tally.chipsLikeButtons++;
				problem('chip', where, 'changes on hover');
			}
		}
		await page.mouse.move(0, 0);

		// The grid sweep must see a planted grid before its silence means anything.
		if (!gridControlChecked && c.screen === 'home') {
			await page.evaluate(() => {
				const rows = document.querySelectorAll('.row');
				rows[0].style.backgroundImage =
					'linear-gradient(90deg, rgba(255,0,0,.4) 1px, transparent 1px), linear-gradient(rgba(255,0,0,.4) 1px, transparent 1px)';
				rows[0].style.backgroundSize = '8px 8px';
				rows[0].style.backgroundRepeat = 'repeat';
				rows[1].style.backgroundImage = 'repeating-linear-gradient(0deg, rgba(0,0,0,.3) 0 1px, transparent 1px 6px)';
			});
			const withPlant = await page.evaluate(inspect, JARGON);
			await page.evaluate(() => document.querySelectorAll('.row').forEach((row) => row.removeAttribute('style')));
			tally.gridControl = withPlant.rowGrids.length;
			if (tally.gridControl < 3) problem('grid', where, `planted grid layers were not all detected (found ${tally.gridControl} of 3)`);
			gridControlChecked = true;
		}

		// Keyboard: Tab reaches every control, each with a visible ring.
		const expected = new Set(await page.evaluate(expectedTabStops));
		const reached = new Set();
		const budget = expected.size * 2 + 6;
		for (let i = 0; i < budget && reached.size < expected.size; i++) {
			await page.keyboard.press('Tab');
			const f = await page.evaluate(focusInfo);
			if (!f) continue;
			if (f.tabindex === '-1') continue;
			if (!reached.has(f.id)) {
				reached.add(f.id);
				tally.tabStops++;
				if (f.w < 44 || f.h < 44) {
					tally.under44++;
					problem('44px', where, `tab stop ${f.id} is ${f.w.toFixed(1)}x${f.h.toFixed(1)}`);
				}
				tally.ringMin = Math.min(tally.ringMin, f.ringRatio);
				if (f.outlineStyle === 'none' || !(f.outlineWidth >= 2) || f.ringRatio < 3) {
					tally.ringFailures++;
					problem('focus', where, `${f.id} ring ${f.outlineStyle} ${f.outlineWidth}px ${f.ringRatio.toFixed(2)}:1`);
				}
			}
		}
		for (const id of expected) {
			if (!reached.has(id)) {
				tally.focusMissed++;
				problem('keyboard', where, `Tab never reached ${id}`);
			}
		}

		for (const url of leaks) {
			tally.network++;
			problem('network', where, url);
		}
		await context.close();
	}
}
/* ------------------------------------------------- Positive controls */

// Each detector must catch a planted defect, or its silence above proves nothing.
{
	const c = { screen: 'home', state: 'synced', size: SIZES[0] };
	const leaks = [];
	const { context, page } = await openPage(browser, c, 'idea', {
		bypassCSP: true,
		onContext: async (ctx) => {
			await ctx.route('**/*', (route) => {
				const url = route.request().url();
				if (/^https?:/i.test(url)) {
					leaks.push(url);
					return route.abort();
				}
				return route.continue();
			});
		}
	});
	await page.evaluate(() => {
		const main = document.querySelector('.main-col');
		main.insertAdjacentHTML('beforeend', '<button style="min-width:0;min-height:0;width:30px;height:30px;padding:0">x</button>');
		main.insertAdjacentHTML('beforeend', '<div style="width:3000px;height:4px"></div>');
		main.insertAdjacentHTML('beforeend', '<p>Lock held by Maria</p>');
		main.insertAdjacentHTML('beforeend', '<img alt="" src="https://example.invalid/planted.png">');
		document.querySelector('.chip').style.cursor = 'pointer';
		// A rail planted on a card, off the main plate.
		[...document.querySelectorAll('.panel')].find((p) => p.getClientRects().length).insertAdjacentHTML('beforeend', '<span class="plate-rail"></span>');
		// A row chip dropped back under the row's whole-row key, so the hand shows on it.
		document.querySelectorAll('.row .chip')[1].style.zIndex = 'auto';
		const key = document.querySelector('[data-key="hdr-settings"]');
		key.style.borderColor = getComputedStyle(document.body).backgroundColor;
	});
	await page.waitForTimeout(200);
	const r = await page.evaluate(inspect, JARGON);
	const caught = {
		'44px': r.small.length > 0,
		chip: r.chips.length > 0,
		'chip cover': r.chips.some((x) => /covered by a pointer-cursor/.test(x)),
		decoration: r.rowDecoration.some((x) => /off the main plate/.test(x)),
		overflow: r.overflow.length > 0,
		hairline: r.hairlines.some((h) => h.ratio < 3),
		copy: r.jargon.length > 0,
		network: leaks.length > 0
	};
	tally.controlsCaught = Object.values(caught).filter(Boolean).length;
	tally.controlsPlanted = Object.keys(caught).length;
	for (const [k, ok] of Object.entries(caught)) if (!ok) problem('control', 'planted defects', `the ${k} check missed a planted defect`);
	await context.close();
}

/* -------------------------------------------------------------- Flows */

async function flow(name, size, state, run, screen) {
	const c = { screen: state === 'signedOut' ? 'connect' : screen || 'home', state, size };
	const { context, page } = await openPage(browser, c, 'idea');
	const where = `flow ${name} ${size.w}x${size.h}`;
	tally.flows++;
	try {
		const fails = [];
		page.on('pageerror', (e) => fails.push('page error: ' + e.message));
		await run(page, (ok, what) => {
			if (!ok) fails.push(what);
		});
		for (const f of fails) {
			tally.flowFailures++;
			problem('flow', where, f);
		}
	} catch (e) {
		tally.flowFailures++;
		problem('flow', where, 'threw ' + String(e.message || e).split('\n')[0]);
	}
	await context.close();
}
const settle = (page) => page.evaluate(() => new Promise((r) => setTimeout(() => requestAnimationFrame(() => requestAnimationFrame(() => r())), 30)));
const where = (page) =>
	page.evaluate(() => ({
		screen: document.body.getAttribute('data-screen'),
		top: document.getElementById('scroller').scrollTop,
		focus: document.activeElement && (document.activeElement.id || document.activeElement.getAttribute('data-key')),
		title: (document.getElementById('detail-title') || {}).textContent || null,
		history: document.querySelectorAll('.history-list > li').length
	}));
const text = async (page, sel) => ((await page.textContent(sel).catch(() => '')) || '').replace(/\s+/g, ' ').trim();
// A row is clicked where its name is (a narrow row's tag may lie over its middle; a click
// on the tag opens the row too, but Playwright would wait for the key itself).
const clickRow = async (page, key) => {
	const sel = `[data-key="${key}"]`;
	await page.$eval(sel, (el) => el.scrollIntoView({ block: 'center' }));
	const box = await page.$eval(sel, (el) => {
		const r = el.getBoundingClientRect();
		return { w: r.width, h: r.height };
	});
	await page.click(sel, { position: { x: Math.round(box.w * 0.55), y: Math.min(16, Math.round(box.h / 3)) } });
};
const toBottom = (page) =>
	page.evaluate(() => {
		for (const id of ['recess-scroll', 'scroller']) {
			const e = document.getElementById(id);
			if (e) e.scrollTop = e.scrollHeight;
		}
	});

for (const size of SIZES) {
	await flow('row opens detail', size, 'checkedOutByOther', async (page, expect) => {
		await toBottom(page);
		await settle(page);
		await clickRow(page, 'row-f-wheel-hub');
		await page.waitForSelector('.history-list');
		let s = await where(page);
		expect(s.screen === 'detail', `screen is ${s.screen}, not detail`);
		expect(s.top === 0, `detail opened scrolled to ${s.top}, not the top`);
		expect(s.focus === 'detail-title', `focus is on ${s.focus}, not the heading`);
		expect(s.title === 'Wheel-Hub.SLDPRT', `heading says ${s.title}`);
		expect(s.history > 0, 'no history rows');
		const free = await page.$$eval('.detail-act button', (b) => b.map((x) => x.textContent.trim()));
		expect(free.join('|') === 'Open|Check out|Check out and open|Show in folder', `an available file offers ${free.join(', ')}`);
		expect(/Available\. ?Check it out to make changes\./.test(await text(page, '.who-panel')), 'Checked out does not say the file is available');
		await page.click('[data-key="back"]');
		await settle(page);
		s = await where(page);
		expect(s.screen === 'home', `Back went to ${s.screen}`);
		expect(s.focus === 'row-f-wheel-hub', `Back put focus on ${s.focus}, not the row that opened detail`);
		await page.focus('[data-key="row-mine:f-gearbox"]');
		await page.keyboard.press('Enter');
		await page.waitForSelector('.history-list');
		s = await where(page);
		expect(s.screen === 'detail' && s.title === 'Gearbox.SLDASM', `Enter on a row opened ${s.title}`);
		expect(s.focus === 'detail-title', `Enter left focus on ${s.focus}`);
		const mine = await page.$$eval('.detail-act button', (b) => b.map((x) => x.textContent.trim()));
		expect(mine.join('|') === 'Open|Check in|Undo check out|Show in folder', `a file I have checked out offers ${mine.join(', ')}`);
		await page.click('[data-key="back"]');
		await settle(page);
		await clickRow(page, 'row-f-plate-left');
		await page.waitForSelector('.history-list');
		const holder = await text(page, '.holder-line');
		expect(/Checked out by Maria Lopez on LAB-PC-07/.test(holder), `holder line says "${holder}"`);
		const email = await text(page, '.who-email');
		expect(email === 'maria.lopez@boscotech.edu', `Checked out gives no way to reach Maria (${email})`);
		const taken = await page.$$eval('.detail-act button', (b) => b.map((x) => x.textContent.trim()));
		expect(taken.join('|') === 'Open|Show in folder', `a file Maria has checked out offers ${taken.join(', ')}`);
		const page2 = await page.content();
		expect(!/double-click/i.test(page2), 'the page still tells the student to double-click');
	});
	await flow('a chip in a row opens the row', size, 'checkedOutByOther', async (page, expect) => {
		const chip = 'li.row:has([data-key="row-f-plate-left"]) .chip.who';
		const cursor = await page.$eval(chip, (c) => getComputedStyle(c).cursor);
		expect(cursor !== 'pointer', `the chip shows a ${cursor} cursor`);
		await page.click(chip);
		await page.waitForSelector('.history-list');
		const s = await where(page);
		expect(s.screen === 'detail' && s.title === 'Plate-Left.SLDPRT', `clicking the chip opened ${s.screen} ${s.title}`);
		expect(s.top === 0 && s.focus === 'detail-title', `top ${s.top}, focus ${s.focus}`);
	});
	await flow('every row says who has it checked out', size, 'checkedOutByOther', async (page, expect) => {
		const lines = await page.$$eval('#vl-browser .vrow:not(.folder-item)', (rows) =>
			rows.map((r) => [r.querySelector('.row-name').textContent, (r.querySelector('.chip.who') || r.querySelector('.row-avail') || {}).textContent || ''])
		);
		expect(lines.length >= 5, `only ${lines.length} file rows drawn`);
		for (const [name, mark] of lines) expect(/^(?:[A-Z]{1,2})?(Checked out by .+|Available)$/.test(mark.trim()), `${name} shows "${mark}" for who has it`);
		const plate = lines.find((l) => l[0] === 'Plate-Left.SLDPRT');
		expect(plate && /Checked out by Maria Lopez on LAB-PC-07$/.test(plate[1]), 'Plate-Left does not say Maria has it');
		const gearbox = lines.find((l) => l[0] === 'Gearbox.SLDASM');
		expect(gearbox && /Checked out by you$/.test(gearbox[1]), 'Gearbox does not say I have it');
		const avail = await page.$$eval('.row-avail', (a) => a.length);
		const chips = await page.$$eval('.chip.who', (c) => c.filter((x) => /Available/.test(x.textContent)).length);
		expect(avail > 0 && chips === 0, 'Available is a chip, not plain meta text');
	});
	await flow('notice list opens and closes', size, 'groupedNotices', async (page, expect) => {
		const k = '[data-key="nt-expand-nameShared"]';
		expect((await page.getAttribute(k, 'aria-expanded')) === 'false', 'the list starts open');
		await page.click(k);
		await settle(page);
		expect((await page.getAttribute(k, 'aria-expanded')) === 'true', 'the list did not open');
		const rows = await page.$$eval('.attn-card[data-kind="nameShared"] .vrow', (r) => r.length);
		expect(rows > 0 && rows <= 14, `the open list draws ${rows} rows`);
		const f = await page.evaluate(() => document.activeElement.getAttribute('data-key'));
		expect(f === 'nt-expand-nameShared', `opening the list moved focus to ${f}`);
		await page.click(k);
		await settle(page);
		expect(!(await page.$('.attn-card[data-kind="nameShared"] .vrow')), 'the list did not close');
		const kinds = await page.$$eval('.attn-card', (c) => c.map((x) => x.getAttribute('data-kind')));
		expect(new Set(kinds).size === kinds.length, 'two cards of one kind: ' + kinds.join(', '));
		const said = await page.evaluate(() => document.body.innerText);
		expect(!/SolidWorks year not checked/i.test(said), 'SolidWorks year not checked shows on Home');
	});
	await flow('folders: in, deeper, and back out', size, 'synced', async (page, expect) => {
		await page.click('[data-key="row-dir:Drivetrain"]');
		await settle(page);
		expect((await text(page, '.crumb-here')) === 'Drivetrain', `went into ${await text(page, '.crumb-here')}`);
		expect(!!(await page.$('[data-key="row-f-wheel-hub"]')), 'Drivetrain does not list Wheel-Hub');
		await page.click('[data-key="row-dir:Drivetrain/Gearbox"]');
		await settle(page);
		expect(/^Robot 2027\s*›\s*Drivetrain\s*›\s*Gearbox$/.test(await text(page, '.crumbs')), `the crumbs say ${await text(page, '.crumbs')}`);
		await page.click('[data-key="crumb-"]');
		await settle(page);
		expect((await text(page, '.crumb-here')) === 'Robot 2027', `the top crumb went to ${await text(page, '.crumb-here')}`);
		const keys = await page.$$eval('.folder-keys button', (b) => b.map((x) => x.getAttribute('data-key')));
		expect(!keys.includes('fk-rename') && !keys.includes('fk-delete'), 'the project folder offers Rename or Delete');
	});
	await flow('select, check out, let go', size, 'checkedOutByOther', async (page, expect) => {
		await page.click('[data-key="sel-f-plate-right"]');
		await settle(page);
		await page.click('[data-key="sel-f-wheel-hub"]', { modifiers: ['Shift'] });
		await settle(page);
		expect((await text(page, '.sel-count')) === '2 selected', `the bar says ${await text(page, '.sel-count')}`);
		const on = await page.$$eval('.sel-key[aria-checked="true"]', (k) => k.map((x) => x.getAttribute('data-key')));
		expect(on.join() === 'sel-f-plate-right,sel-f-wheel-hub', `selected ${on.join(', ')}`);
		await page.click('[data-key="sel-out"]');
		await settle(page);
		expect((await text(page, '#result-word')) === 'Checked out 2 files.', `the result line says "${await text(page, '#result-word')}"`);
		expect((await page.getAttribute('#result', 'data-on')) === 'true', 'the result line did not show');
		expect(/Checked out by you$/.test(await text(page, 'li.row:has([data-key="row-f-wheel-hub"]) .chip.who')), 'Wheel-Hub does not say I have it now');
		await page.keyboard.press('Escape');
		await settle(page);
		expect(!(await page.$('.sel-bar')), 'Escape did not let go of the selection');
	});
	await flow('folder dialog: refuses a bad name, renames, follows', size, 'checkedOutByOther', async (page, expect) => {
		await page.click('[data-key="fk-rename"]');
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('ask').open), 'Rename folder did not ask');
		expect((await page.inputValue('#ask-name')) === 'Drivetrain', 'the field does not start with the name');
		await page.fill('#ask-name', 'Drive/train');
		await page.click('[data-key="ask-ok"]');
		await settle(page);
		expect(/can't use/.test(await text(page, '#ask-error')), `a slash was not refused: "${await text(page, '#ask-error')}"`);
		await page.fill('#ask-name', 'Chassis');
		await page.keyboard.press('Enter');
		await settle(page);
		expect(!(await page.evaluate(() => document.getElementById('ask').open)), 'Enter did not rename');
		expect((await text(page, '#result-word')) === 'Renamed Drivetrain to Chassis.', `the result says "${await text(page, '#result-word')}"`);
		expect((await text(page, '.crumb-here')) === 'Chassis', `after the rename the list shows ${await text(page, '.crumb-here')}`);
		await page.click('[data-key="fk-delete"]');
		await settle(page);
		const words = await text(page, '#ask-words');
		expect(/the 8 files in it/.test(words) && /history of every file is kept/.test(words), `Delete folder says "${words}"`);
		await page.keyboard.press('Escape');
		await settle(page);
		const f = await page.evaluate(() => document.activeElement.getAttribute('data-key'));
		expect(f === 'fk-delete', `closing the dialog put focus on ${f}`);
	});
	await flow('5,000 files stay windowed', size, 'bigProject', async (page, expect) => {
		const drawn = () => page.$$eval('.vrow', (r) => r.length);
		let n = await drawn();
		expect(n < 150, `${n} rows are drawn for 5,000 files`);
		expect((await page.getAttribute('#vl-browser', 'data-total')) === '5000', 'the list does not know its 5,000 files');
		expect(/^4,9\d\d more files below$/.test(await text(page, '#recess-cue .cue-word, #window-cue .cue-word')) || size.w < 761, `the cue says "${await text(page, '#recess-cue .cue-word')}"`);
		await toBottom(page);
		await settle(page);
		await settle(page);
		const last = await page.$eval('#vl-browser', (ul) => {
			const rows = ul.querySelectorAll('.vrow');
			return rows.length ? rows[rows.length - 1].getAttribute('data-i') : null;
		});
		expect(last === '4999', `scrolled to the bottom, the last row drawn is ${last}`);
		n = await drawn();
		expect(n < 150, `${n} rows are drawn at the bottom`);
		await page.focus('#vl-browser [data-rove="row"][tabindex="0"]');
		await page.keyboard.press('Home');
		await settle(page);
		const at = () => page.evaluate(() => (document.activeElement.closest('li[data-i]') || { getAttribute: () => null }).getAttribute('data-i'));
		expect((await at()) === '0', `Home went to row ${await at()}`);
		await page.keyboard.press('ArrowDown');
		await page.keyboard.press('ArrowDown');
		expect((await at()) === '2', `two downs went to row ${await at()}`);
		await page.keyboard.press('End');
		await settle(page);
		expect((await at()) === '4999', `End went to row ${await at()}`);
		const vis = await page.evaluate(() => {
			const r = document.activeElement.getBoundingClientRect();
			return r.top >= 0 && r.bottom <= innerHeight;
		});
		expect(vis, 'the last row is focused but out of sight');
		n = await drawn();
		expect(n < 150, `${n} rows are drawn after End`);
		const tabbable = await page.$$eval('#vl-browser [data-rove]:not([tabindex="-1"])', (k) => new Set(k.map((x) => x.closest('li').getAttribute('data-i'))).size);
		expect(tabbable === 1, `${tabbable} rows are in the Tab order, not one`);
	});
	await flow('a row checks out and checks in', size, 'checkedOutByOther', async (page, expect) => {
		const keys = await page.$$eval('#vl-browser .vrow:not(.folder-item)', (rows) =>
			rows.map((r) => [r.querySelector('.row-name').textContent, [...r.querySelectorAll('.row-extra .key')].map((k) => k.getAttribute('aria-label')).join('|')])
		);
		const of = (n) => (keys.find((k) => k[0] === n) || [n, 'no row'])[1];
		expect(of('Wheel-Hub.SLDPRT') === 'Check out Wheel-Hub.SLDPRT|Open Wheel-Hub.SLDPRT', `an available row offers ${of('Wheel-Hub.SLDPRT')}`);
		expect(of('Gearbox.SLDASM') === 'Check in Gearbox.SLDASM|Open Gearbox.SLDASM', `my row offers ${of('Gearbox.SLDASM')}`);
		expect(of('Plate-Left.SLDPRT') === 'Open Plate-Left.SLDPRT', `Maria's row offers ${of('Plate-Left.SLDPRT')}`);
		const opens = await page.$$eval('#vl-browser .vrow:not(.folder-item) [data-rove="open"]', (k) => [...new Set(k.map((x) => Math.round(x.getBoundingClientRect().left)))]);
		expect(opens.length === 1, `Open is not one column down the list (left edges ${opens.join(', ')})`);
		// The pick key draws its empty box before anything is picked.
		const box = await page.$eval('[data-key="sel-f-wheel-hub"] .sel-box', (el) => {
			const cs = getComputedStyle(el);
			return [cs.borderTopStyle, parseFloat(cs.borderTopWidth), Math.round(el.getBoundingClientRect().width)].join(' ');
		});
		expect(box === 'solid 2 20', `the empty pick box is "${box}"`);
		await page.click('[data-key="state-f-wheel-hub"]');
		await settle(page);
		expect((await text(page, '#result-word')) === 'Checked out Wheel-Hub.SLDPRT.', `Check out said "${await text(page, '#result-word')}"`);
		expect(/Checked out by you$/.test(await text(page, 'li.row:has([data-key="row-f-wheel-hub"]) .chip.who')), 'Wheel-Hub does not say I have it');
		expect((await page.getAttribute('[data-key="state-f-wheel-hub"]', 'aria-label')) === 'Check in Wheel-Hub.SLDPRT', 'the row key did not turn into Check in');
		await page.click('[data-key="state-f-wheel-hub"]');
		await settle(page);
		expect((await text(page, '#result-word')) === 'Checked in Wheel-Hub.SLDPRT.', `Check in said "${await text(page, '#result-word')}"`);
	});
	await flow('check out all asks first', size, 'synced', async (page, expect) => {
		await page.click('[data-key="fk-out"]');
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('ask').open), 'Check out all did not ask');
		const words = (await text(page, '#ask-words')).replace(/\u00a0/g, ' ');
		expect(
			words === 'Check out 16 files in Robot 2027 and its folders? Nobody else can save them until you check them in. 1 other file is checked out by someone else, and stays with them.',
			`Check out all asks "${words}"`
		);
		const f = await page.evaluate(() => document.activeElement.getAttribute('data-key'));
		expect(f === 'ask-cancel', `the question starts on ${f}, not Cancel`);
		expect(!(await page.$eval('[data-key="ask-ok"]', (b) => b.classList.contains('primary'))), 'Check out all is the primary key');
		await page.keyboard.press('Enter');
		await settle(page);
		expect(!(await page.evaluate(() => document.getElementById('ask').open)), 'Enter on Cancel did not close the question');
		expect((await page.getAttribute('#result', 'data-on')) !== 'true', 'Cancel checked files out');
		await page.click('[data-key="fk-out"]');
		await settle(page);
		await page.click('[data-key="ask-ok"]');
		await settle(page);
		expect(/^Checked out 16 of 18 files\. Alex Kim has 1 of them checked out\.$/.test(await text(page, '#result-word')), `the answer says "${await text(page, '#result-word')}"`);
	});
	await flow('My files is my check outs; waiting is said once', size, 'offlineWaiting', async (page, expect) => {
		const names = (await page.$$eval('#vl-mine .row-name', (r) => r.map((x) => x.textContent))).sort();
		expect(names.join() === 'Gearbox.SLDASM,Plate-Right.SLDPRT', `My files lists ${names.join(', ')}`);
		const said = (await page.evaluate(() => document.body.innerText)).match(/waiting to upload/gi) || [];
		expect(said.length === 1, `"waiting to upload" is said ${said.length} times`);
	});
	await flow('one import summary, no file listed twice', size, 'importSummary', async (page, expect) => {
		const mine = await page.$$eval('#vl-mine .row-name', (r) => r.map((x) => x.textContent));
		expect(mine.join() === 'Gearbox.SLDASM', `My files lists ${mine.join(', ')}`);
		const cards = await page.$$eval('.attn-card', (c) => c.map((x) => x.getAttribute('data-kind')));
		expect(cards.join() === 'import,nameShared', `the cards are ${cards.join(', ')}`);
		expect(!(await page.$('[data-key="nt-expand-import"]')), 'the import summary lists the files the name card lists');
		const title = (await text(page, '.attn-card[data-kind="import"] .attn-title')).replace(/\u00a0/g, ' ');
		expect(title === 'Added 4,987 of 5,000 files to Robot 2027 \u203a CopyDesignTemp', `the summary says "${title}"`);
		const ring = (await text(page, '.gauge-words')).replace(/\u00a0/g, ' ');
		expect(ring === 'All 5,010 team files are up to date on LAB-PC-14.', `the ring says "${ring}"`);
	});
	await flow('rename a file that shares a name, in the app', size, 'groupedNotices', async (page, expect) => {
		await page.click('[data-key="nt-expand-nameShared"]');
		await settle(page);
		const item = 'ni:nameShared:Robot 2027/CopyDesignTemp/Bracket.SLDPRT';
		const hint = await page.getAttribute(`[data-key="row-${item}"]`, 'title');
		expect(hint === 'See the Bracket.SLDPRT in Robot 2027 \u203a Intake', `the row goes to "${hint}"`);
		await page.click(`[data-key="rename-${item}"]`);
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('ask').open), 'Rename did not ask');
		const picked = await page.evaluate(() => {
			const f = document.getElementById('ask-name');
			return f.value.slice(f.selectionStart, f.selectionEnd);
		});
		expect(picked === 'Bracket', `the field picks "${picked}", not the name before its .SLDPRT`);
		await page.fill('#ask-name', 'Plate-Left.SLDPRT');
		await page.click('[data-key="ask-ok"]');
		await settle(page);
		expect(/already has a file named Plate-Left\.SLDPRT/.test(await text(page, '#ask-error')), `a taken name was not refused: "${await text(page, '#ask-error')}"`);
		await page.fill('#ask-name', 'Bracket-Intake');
		await page.click('[data-key="ask-ok"]');
		await settle(page);
		expect(/Keep \.SLDPRT at the end/.test(await text(page, '#ask-error')), `a lost .SLDPRT was not refused: "${await text(page, '#ask-error')}"`);
		await page.fill('#ask-name', 'Bracket-Intake.SLDPRT');
		await page.keyboard.press('Enter');
		await settle(page);
		expect(!(await page.evaluate(() => document.getElementById('ask').open)), 'Enter did not rename');
		expect((await text(page, '#result-word')) === 'Renamed Bracket.SLDPRT to Bracket-Intake.SLDPRT.', `the answer says "${await text(page, '#result-word')}"`);
		const title = (await text(page, '.attn-card[data-kind="nameShared"] .attn-title')).replace(/\u00a0/g, ' ');
		expect(title === '13 files share a name with other files in this project', `the card now says "${title}"`);
	});
	await flow('the check-out question', size, 'checkoutPrompt', async (page, expect) => {
		const words = (await text(page, '.prompt-card .attn-detail')).replace(/\u00a0/g, ' ');
		expect(words === 'SolidWorks opened it read-only. Check it out, then close it in SolidWorks and open it again here to save changes.', `the question says "${words}"`);
		const keys = await page.$$eval('.prompt-card button', (b) => b.map((x) => x.textContent.trim()));
		expect(keys.join('|') === 'Check out and reopen|Not now', `the question offers ${keys.join(', ')}`);
		await page.click('[data-key="prompt-checkout"]');
		await settle(page);
		expect(!(await page.$('.prompt-card')), 'the question stayed after Check out and reopen');
		expect(/^Checked out Plate-Left\.SLDPRT\. Close Plate-Left\.SLDPRT in SolidWorks first/.test(await text(page, '#result-word')), `the answer says "${await text(page, '#result-word')}"`);
	});
	await flow('report a problem', size, 'synced', async (page, expect) => {
		await page.click('[data-key="hdr-settings"]');
		await settle(page);
		await page.click('[data-key="set-report"]');
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('ask').open && !document.getElementById('settings').open), 'Report a problem did not open over Home');
		expect((await page.evaluate(() => document.activeElement && document.activeElement.id)) === 'ask-report', 'the words field does not have focus');
		expect((await page.getAttribute('[data-key="ask-kind-bug"]', 'aria-pressed')) === 'true', 'a report does not start as a bug');
		await page.click('[data-key="ask-kind-other"]');
		expect((await page.getAttribute('[data-key="ask-kind-other"]', 'aria-pressed')) === 'true' && (await page.getAttribute('[data-key="ask-kind-bug"]', 'aria-pressed')) === 'false', 'Other did not become the kind');
		await page.fill('#ask-report', 'The ring stayed at 0 after I signed in.');
		await page.click('[data-key="ask-ok"]');
		await settle(page);
		expect(!(await page.evaluate(() => document.getElementById('ask').open)), 'Send left the dialog open');
		expect((await text(page, '#result-word')) === 'Sent. Thank you for telling us.', `the answer says "${await text(page, '#result-word')}"`);
		expect((await page.evaluate(() => document.activeElement && document.activeElement.getAttribute('data-key'))) === 'hdr-settings', 'focus did not come back to Settings');
	});
	await flow('settings sheet', size, 'synced', async (page, expect) => {
		await page.click('[data-key="hdr-settings"]');
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('settings').open), 'Settings did not open');
		// A theme pad says its name and, under it, what it is ("Dark", "Light"); the name is the setting.
		const keys = await page.$$eval('#settings button', (b) => b.map((x) => (x.querySelector('.seg-name') || x).textContent.trim()));
		expect(keys.join('|') === 'Done|Change|On|Match Windows|IDEA|Space White|Report a problem|Send feedback|Open incidents folder', `sheet holds ${keys.join(', ')}`);
		await page.click('[data-key="set-theme-spaceWhite"]');
		await settle(page);
		expect((await page.getAttribute('html', 'data-theme')) === 'spaceWhite', 'Space White did not apply');
		expect((await page.getAttribute('[data-key="set-theme-spaceWhite"]', 'aria-pressed')) === 'true', 'Space White key is not on');
		await page.click('[data-key="set-theme-idea"]');
		await settle(page);
		expect((await page.getAttribute('html', 'data-theme')) === 'idea', 'IDEA did not apply');
		await page.click('[data-key="set-start"]');
		await settle(page);
		expect((await page.getAttribute('[data-key="set-start"]', 'aria-pressed')) === 'false', 'Start at sign-in did not turn off');
		expect((await page.textContent('#set-start-word')).trim() === 'Off', 'the switch does not say Off');
		await page.click('[data-key="set-root"]');
		await settle(page);
		expect(/School/.test(await page.textContent('#set-root-value')), 'Change did not pick a folder');
		await page.keyboard.press('Escape');
		await settle(page);
		expect(!(await page.evaluate(() => document.getElementById('settings').open)), 'Escape did not close the sheet');
		const f = await page.evaluate(() => document.activeElement.getAttribute('data-key'));
		expect(f === 'hdr-settings', `closing put focus on ${f}`);
	});
	await flow('settings from detail is over Home', size, 'synced', async (page, expect) => {
		await page.click('[data-key="row-mine:f-gearbox"]');
		await page.waitForSelector('.history-list');
		await page.click('[data-key="hdr-settings"]');
		await settle(page);
		const s = await where(page);
		expect(s.screen === 'home', `the sheet opened over ${s.screen}`);
		expect(await page.evaluate(() => document.getElementById('settings').open), 'Settings did not open');
	});
	await flow('pause and resume', size, 'synced', async (page, expect) => {
		await page.click('[data-key="sync-toggle"]');
		await settle(page);
		expect(/Paused/i.test(await page.textContent('.status-group .screen')), 'Pause did not show Paused');
		expect(/Resume/i.test(await page.textContent('[data-key="sync-toggle"]')), 'no Resume key');
		expect(await page.$eval('[data-key="sync-toggle"]', (b) => b.classList.contains('primary')), 'Resume is not the green primary');
		await page.click('[data-key="sync-toggle"]');
		await settle(page);
		expect(/All saved/i.test(await page.textContent('.status-group .screen')), 'Resume did not come back');
	});
	await flow('nothing to pause while offline', size, 'offlineWaiting', async (page, expect) => {
		expect(!(await page.$('[data-key="sync-toggle"]')), 'Pause is offered while offline');
		expect(/Offline/i.test(await page.textContent('.status-group .screen')), 'the status does not say Offline');
		expect(/waiting to upload/.test(await text(page, '#act-panel')), 'waiting files are not in Right now');
	});
	await flow('a folder of my own', size, 'vaultOwnedByOther', async (page, expect) => {
		const label = (await page.textContent('[data-key="cn-own"]')).trim();
		expect(/C:\\IDEA\\Armory-jordan/.test(label), `the one-click folder key says "${label}"`);
		await page.click('[data-key="cn-own"]');
		await settle(page);
		const s = await where(page);
		expect(s.screen === 'home', `using my own folder went to ${s.screen}, not Home`);
	});
	await flow('connect', size, 'signedOut', async (page, expect) => {
		const buttons = await page.$$eval('main button', (b) => b.map((x) => x.textContent.trim()));
		expect(buttons.join('|') === 'Connect this computer', `Connect shows ${buttons.join(', ')}`);
		await page.click('[data-key="cn-connect"]');
		await settle(page);
		expect((await page.getAttribute('.step[data-step="current"]', 'aria-current')) === 'step', 'no current step');
		expect(/Sign in/.test(await page.textContent('.step[data-step="current"]')), 'step 2 is not current');
		await page.click('[data-key="cn-cancel"]');
		await settle(page);
		expect(!!(await page.$('[data-key="cn-connect"]')), 'Cancel did not go back');
	});
}

/* --------------------------------------------------------------- Logo */

// The IDEA gear turns like ideabosco.com's (AnimatedLogo.svelte): idea-gear-spin, 24s,
// linear, infinite, only when motion is allowed; still under reduced motion; both layers
// painted either way; the gear 46.95% of the mark's width. A planted change of speed must
// be caught first, or the probe proves nothing.
tally.logo = 0;
tally.logoFailures = 0;
async function logoProbe(state, sel, motion, plant) {
	const c = { screen: state === 'signedOut' ? 'connect' : 'home', state, size: SIZES[0] };
	const { context, page } = await openPage(browser, c, 'idea', { reducedMotion: motion, bypassCSP: !!plant });
	if (plant) await page.addStyleTag({ content: plant });
	const r = await page.$eval(sel, (el) => {
		const g = getComputedStyle(el, '::before');
		const p = getComputedStyle(el, '::after');
		return {
			name: g.animationName,
			dur: g.animationDuration,
			timing: g.animationTimingFunction,
			count: g.animationIterationCount,
			gearOpacity: g.opacity,
			plateOpacity: p.opacity,
			gearShown: g.content !== 'none' && g.display !== 'none' && g.visibility !== 'hidden',
			plateShown: p.content !== 'none' && p.display !== 'none' && p.visibility !== 'hidden',
			gearArt: /url\(/.test(g.backgroundImage),
			plateArt: /url\(/.test(p.backgroundImage),
			share: parseFloat(g.width) / el.getBoundingClientRect().width
		};
	});
	await context.close();
	const found = [];
	if (motion === 'no-preference') {
		if (r.name !== 'idea-gear-spin' || r.dur !== '24s' || r.timing !== 'linear' || r.count !== 'infinite') found.push(`the gear turns as ${r.name} ${r.dur} ${r.timing} ${r.count}, not idea-gear-spin 24s linear infinite`);
	} else if (r.name !== 'none') found.push(`the gear turns (${r.name}) under reduced motion`);
	if (r.gearOpacity !== '1' || r.plateOpacity !== '1' || !r.gearShown || !r.plateShown || !r.gearArt || !r.plateArt) found.push('a layer of the mark is hidden or unpainted');
	if (Math.abs(r.share - 0.4695) > 0.005) found.push(`the gear is ${r.share.toFixed(4)} of the mark's width, not 0.4695`);
	return found;
}
for (const motion of ['no-preference', 'reduce']) {
	for (const [state, sel] of [
		['synced', '.plate-header .wordmark'],
		['signedOut', '.wordmark-hero']
	]) {
		tally.logo++;
		for (const f of await logoProbe(state, sel, motion, null)) {
			tally.logoFailures++;
			problem('logo', `${state} ${motion}`, f);
		}
	}
}
{
	const caught = await logoProbe('synced', '.plate-header .wordmark', 'no-preference', '.wordmark::before { animation-duration: 3s !important; }');
	tally.logoPlanted = caught.length ? 1 : 0;
	if (!caught.length) problem('logo', 'planted', 'a gear planted at 3s a turn was not caught');
}

/* ------------------------------------------------------------- Bridge */

// The page as WebView2 hosts it: a stand-in window.chrome.webview records every message
// the page posts (and the files a drop sends with postMessageWithAdditionalObjects) and
// delivers the host's. Each page-to-host type must be sent, by the control that should
// send it, with exactly the fields BRIDGE.md lists; an action carries its requestId.
const ACT = ['requestId'];
const CONTRACT = {
	ready: [],
	connect: [],
	cancelConnect: [],
	signOut: [],
	pause: [],
	resume: [],
	openVault: [],
	openFile: ['fileId'],
	launchFile: ['path', ...ACT],
	showInFolder: ['path'],
	checkOut: ['paths', 'open', ...ACT],
	checkIn: ['paths', ...ACT],
	undoCheckOut: ['paths', ...ACT],
	takeBack: ['fileId', ...ACT],
	createFolder: ['projectId', 'parent', 'name', ...ACT],
	renameFolder: ['projectId', 'folder', 'newName', ...ACT],
	deleteFolder: ['projectId', 'folder', ...ACT],
	renameFile: ['path', 'newName', ...ACT],
	addFiles: ['projectId', 'folder', ...ACT],
	dropFiles: ['projectId', 'folder', ...ACT],
	dismissNotice: ['key'],
	saveSettings: ['vaultRoot', 'startAtSignIn', 'theme'],
	chooseVaultRoot: [],
	reportProblem: ['kind', 'body', ...ACT],
	openIncidents: [],
	sendFeedback: ['kind', 'body', ...ACT]
};
tally.bridgeFailures = 0;
tally.bridgeTypes = 0;
{
	const demo = demoStates();
	const fails = [];
	const expect = (ok, what) => {
		if (!ok) fails.push(what);
	};
	const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, colorScheme: 'dark', reducedMotion: 'reduce', timezoneId: 'America/Los_Angeles' });
	const requests = [];
	context.on('request', (r) => requests.push(r.url()));
	await context.addInitScript(() => {
		const t = new EventTarget();
		window.__sent = [];
		window.chrome = {
			webview: {
				addEventListener: (n, f) => t.addEventListener(n, f),
				removeEventListener: (n, f) => t.removeEventListener(n, f),
				postMessage: (m) => window.__sent.push(JSON.parse(JSON.stringify(m))),
				postMessageWithAdditionalObjects: (m, objects) =>
					window.__sent.push(Object.assign(JSON.parse(JSON.stringify(m)), { __files: Array.prototype.map.call(objects, (f) => f.name) }))
			}
		};
		window.__host = (m) => t.dispatchEvent(new MessageEvent('message', { data: m }));
	});
	const page = await context.newPage();
	page.on('pageerror', (e) => fails.push('page error: ' + e.message));
	try {
		await page.goto(pathToFileURL(path.join(WWWROOT, 'index.html')).href);
		await page.waitForTimeout(150);
		const take = () => page.evaluate(() => window.__sent.splice(0));
		const host = async (m) => {
			await page.evaluate((x) => window.__host(x), m);
			await page.waitForTimeout(60);
		};
		const view = (name, patch) => Object.assign(JSON.parse(JSON.stringify(demo.states[name].view)), patch || {});
		const all = [];
		const click = async (sel, opts) => {
			await page.click(sel, opts);
			await page.waitForTimeout(40);
			const m = await take();
			all.push(...m);
			return m[0] || {};
		};
		const first = await take();
		all.push(...first);
		expect(first.length === 1 && first[0].type === 'ready', 'the first message was not ready: ' + JSON.stringify(first));
		expect(!requests.some((u) => /demo\/states\.js/.test(u)), 'the demo states loaded inside WebView2');
		const lists = await page.evaluate(() => window.ArmoryBridge.PAGE_TO_HOST.slice());
		expect(lists.slice().sort().join() === Object.keys(CONTRACT).sort().join(), 'bridge.js PAGE_TO_HOST is not the list this check knows: ' + lists.join(', '));

		const GEARBOX = 'Robot 2027/Drivetrain/Gearbox.SLDASM';
		const HUB = 'Robot 2027/Drivetrain/Wheel-Hub.SLDPRT';
		await host({ type: 'view', view: view('synced', { effectiveTheme: 'spaceWhite' }) });
		expect((await page.getAttribute('html', 'data-theme')) === 'spaceWhite', 'effectiveTheme spaceWhite was not worn');
		expect((await page.getAttribute('body', 'data-screen')) === 'home', 'a signed-in view did not show Home');
		let m = await click('[data-key="hdr-vault"]');
		expect(m.type === 'openVault', 'Open Armory folder sent ' + JSON.stringify(m));
		m = await click('[data-key="sync-toggle"]');
		expect(m.type === 'pause', 'Pause sent ' + JSON.stringify(m));
		m = await click('[data-key="open-mine:f-gearbox"]');
		expect(m.type === 'launchFile' && m.path === GEARBOX && !!m.requestId, 'a My files Open key sent ' + JSON.stringify(m));
		m = await click('[data-key="in-mine:f-gearbox"]');
		expect(m.type === 'checkIn' && m.paths.join() === GEARBOX, 'a My files Check in key sent ' + JSON.stringify(m));

		// Into Drivetrain: select a file, Check out; select mine, Undo check out.
		await click('[data-key="row-dir:Drivetrain"]');
		expect((await page.textContent('.crumb-here')).trim() === 'Drivetrain', 'a folder row did not open the folder');
		await click('[data-key="sel-f-wheel-hub"]');
		m = await click('[data-key="sel-out"]');
		expect(m.type === 'checkOut' && m.paths.join() === HUB && m.open === false, 'select then Check out sent ' + JSON.stringify(m));
		await click('[data-key="sel-clear"]');
		await click('[data-key="sel-f-gearbox"]');
		m = await click('[data-key="sel-undo"]');
		expect(m.type === 'undoCheckOut' && m.paths.join() === GEARBOX, 'Undo check out sent ' + JSON.stringify(m));
		await click('[data-key="sel-clear"]');
		// Check out all asks first, with the count, and sends nothing until the answer.
		m = await click('[data-key="fk-out"]');
		expect(!m.type && (await page.evaluate(() => document.getElementById('ask').open)), 'Check out all did not ask first: ' + JSON.stringify(m));
		expect(/^Check out \d+ files in Drivetrain and its folders\?/.test((await page.textContent('#ask-words')).replace(/\u00a0/g, ' ')), 'Check out all asked: ' + (await page.textContent('#ask-words')));
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'checkOut' && m.paths.join() === 'Robot 2027/Drivetrain' && m.open === false, 'Check out all sent ' + JSON.stringify(m));
		m = await click('[data-key="fk-in"]');
		expect(m.type === 'checkIn' && m.paths.join() === 'Robot 2027/Drivetrain', 'Check in all sent ' + JSON.stringify(m));
		// Each row's one state key: Check out when nobody has it, Check in when I have it.
		m = await click('[data-key="state-f-wheel-hub"]');
		expect(m.type === 'checkOut' && m.paths.join() === HUB && m.open === false, 'a row\'s Check out sent ' + JSON.stringify(m));
		m = await click('[data-key="state-f-gearbox"]');
		expect(m.type === 'checkIn' && m.paths.join() === GEARBOX, 'a row\'s Check in sent ' + JSON.stringify(m));

		// The folder dialog: a host view while typing keeps the typed name.
		await click('[data-key="fk-new"]');
		await page.fill('#ask-name', 'Brackets');
		await host({ type: 'view', view: view('synced') });
		expect((await page.inputValue('#ask-name')) === 'Brackets' && (await page.evaluate(() => document.getElementById('ask').open)), 'a host view lost the folder name being typed');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'createFolder' && m.projectId === 'proj-robot-2027' && m.parent === 'Drivetrain' && m.name === 'Brackets', 'New folder sent ' + JSON.stringify(m));
		await click('[data-key="fk-rename"]');
		await page.fill('#ask-name', 'Chassis');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'renameFolder' && m.folder === 'Drivetrain' && m.newName === 'Chassis', 'Rename folder sent ' + JSON.stringify(m));
		await click('[data-key="fk-delete"]');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'deleteFolder' && m.folder === 'Drivetrain', 'Delete folder sent ' + JSON.stringify(m));
		m = await click('[data-key="fk-add"]');
		expect(m.type === 'addFiles' && m.folder === 'Drivetrain', 'Add files sent ' + JSON.stringify(m));

		// Files dropped from File Explorer go with the message, as WebView2 objects.
		const dropped = await page.evaluate(() => {
			const zone = document.querySelector('[data-drop]');
			const dt = new DataTransfer();
			dt.items.add(new File(['x'], 'Bracket-2.SLDPRT'));
			dt.items.add(new File(['y'], 'Bracket-3.SLDPRT'));
			zone.dispatchEvent(new DragEvent('dragover', { dataTransfer: dt, bubbles: true, cancelable: true }));
			const painted = zone.getAttribute('data-drag');
			zone.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
			return { painted, after: zone.getAttribute('data-drag') };
		});
		expect(dropped.painted === 'true' && dropped.after === 'false', 'holding files over the list did not paint it: ' + JSON.stringify(dropped));
		m = (await take())[0] || {};
		all.push(m);
		expect(m.type === 'dropFiles' && m.folder === 'Drivetrain' && (m.__files || []).join() === 'Bracket-2.SLDPRT,Bracket-3.SLDPRT', 'a drop sent ' + JSON.stringify(m));

		// An 'activity' message patches the panel and the status line, and nothing else.
		await page.evaluate(() => {
			const r = document.getElementById('recess-scroll');
			r.scrollTop = 140;
		});
		await page.focus('[data-key="row-f-plate-left"]');
		const before = await page.evaluate(() => {
			window.__row = document.activeElement;
			const r = document.getElementById('recess-scroll');
			return { top: r.scrollTop, y: document.activeElement.getBoundingClientRect().top };
		});
		const busy = JSON.parse(JSON.stringify(demo.states.transferring.view.activity));
		await host({ type: 'activity', activity: busy });
		let after = await page.evaluate(() => ({
			same: document.activeElement === window.__row,
			top: document.getElementById('recess-scroll').scrollTop,
			y: document.activeElement.getBoundingClientRect().top,
			line: document.getElementById('sync-line').textContent,
			shown: !document.getElementById('activity-group').hidden,
			bars: document.querySelectorAll('#act-panel .track').length
		}));
		expect(after.same, 'an activity message moved focus');
		expect(Math.abs(after.y - before.y) < 1, `an activity message moved the focused row on screen (${before.y} to ${after.y})`);
		expect(after.shown && after.bars === 9, `Right now did not show its 3 directions and 6 files (${after.bars} tracks)`);
		expect(after.line.replace(/\u00a0/g, ' ') === busy.line, `the status line says "${after.line}", not the activity line`);
		const steady = { top: after.top, y: after.y };
		busy.download.filesDone = 640;
		busy.download.bytesDone += 200000000;
		busy.active[0].bytesDone = busy.active[0].bytesTotal;
		await host({ type: 'activity', activity: busy });
		after = await page.evaluate(() => ({
			same: document.activeElement === window.__row,
			top: document.getElementById('recess-scroll').scrollTop,
			y: document.activeElement.getBoundingClientRect().top,
			count: document.querySelector('.act-dir[data-direction="download"] .act-count').textContent,
			full: document.querySelector('.act-file .track-fill').style.width
		}));
		expect(after.same && after.top === steady.top && after.y === steady.y, 'a second activity message moved focus or scroll: ' + JSON.stringify(after));
		expect(after.count.replace(/\u00a0/g, ' ') === '640 of 1,280 files' && after.full === '100%', 'the panel did not patch its numbers: ' + JSON.stringify(after));

		// An actionResult is a quiet line, never an alert or a focus change.
		await host({ type: 'actionResult', requestId: 'r9', ok: false, message: 'Close Plate-Left.SLDPRT in SolidWorks first.' });
		const said = await page.evaluate(() => ({ words: document.getElementById('result-word').textContent, on: document.getElementById('result').getAttribute('data-on'), same: document.activeElement === window.__row }));
		expect(said.words === 'Close Plate-Left.SLDPRT in SolidWorks first.' && said.on === 'true' && said.same, 'an actionResult did not show quietly: ' + JSON.stringify(said));

		// File detail from host messages alone.
		m = await click('[data-key="row-f-wheel-hub"]');
		expect(m.type === 'openFile' && m.fileId === 'f-wheel-hub', 'a row sent ' + JSON.stringify(m));
		expect((await page.getAttribute('body', 'data-screen')) === 'detail', 'a row did not move the view to detail');
		await host({ type: 'fileDetail', detail: demo.detailFor('synced', 'f-wheel-hub') });
		expect((await page.$$('.history-list > li')).length === 5, 'fileDetail did not render its history');
		// A kept copy's tone comes from HistoryEntryView.routine, never from its note: a routine
		// one (saved while checked out, an earlier save) reads as a plain save; any other is marked.
		const toned = demo.detailFor('synced', 'f-wheel-hub');
		const older = toned.history[1];
		toned.history.splice(
			1,
			0,
			Object.assign({}, older, { id: 'k-news', kind: 'keptCopy', note: 'Saved while checked out', routine: false, isCurrent: false }),
			Object.assign({}, older, { id: 'k-routine', kind: 'keptCopy', note: 'An earlier save, kept', routine: true, isCurrent: false })
		);
		await host({ type: 'fileDetail', detail: toned });
		const tones = await page.$$eval('.history-list > li', (li) => li.map((x) => x.getAttribute('data-copy') === 'true'));
		expect(tones.length === 7 && tones[1] === true && tones[2] === false, 'a kept copy\'s tone did not follow routine: ' + JSON.stringify(tones));
		await host({ type: 'fileDetail', detail: demo.detailFor('synced', 'f-wheel-hub') });
		m = await click('[data-key="d-checkout"]');
		expect(m.type === 'checkOut' && m.paths.join() === HUB && m.open === false, 'detail Check out sent ' + JSON.stringify(m));
		// v0.2.1: the instant it is pressed, the key is busy (and takes no second press), the line
		// at the foot says what is under way, and its answer by requestId puts both back.
		let working = await page.evaluate(() => ({
			busy: document.querySelector('[data-key="d-checkout"]').getAttribute('aria-busy'),
			spin: !!document.querySelector('[data-key="d-checkout"] .spin'),
			line: document.getElementById('result-word').textContent,
			on: document.getElementById('result').getAttribute('data-on'),
			working: document.getElementById('result').getAttribute('data-working')
		}));
		expect(
			working.busy === 'true' && working.spin && working.line === 'Checking out Wheel-Hub.SLDPRT...' && working.on === 'true' && working.working === 'true',
			'a pressed key did not show it was working: ' + JSON.stringify(working)
		);
		const again = await click('[data-key="d-checkout"]', { force: true });
		expect(!again.type, 'a busy key sent a second ' + JSON.stringify(again));
		await host({ type: 'actionResult', requestId: m.requestId, ok: true, message: 'Checked out Wheel-Hub.SLDPRT.' });
		working = await page.evaluate(() => ({
			busy: document.querySelector('[data-key="d-checkout"]').getAttribute('aria-busy'),
			line: document.getElementById('result-word').textContent,
			working: document.getElementById('result').getAttribute('data-working')
		}));
		expect(working.busy === null && working.line === 'Checked out Wheel-Hub.SLDPRT.' && working.working === null, 'an answer did not end the working state: ' + JSON.stringify(working));
		m = await click('[data-key="d-checkout-open"]');
		expect(m.type === 'checkOut' && m.open === true, 'Check out and open sent ' + JSON.stringify(m));
		m = await click('[data-key="d-open"]');
		expect(m.type === 'launchFile' && m.path === HUB, 'detail Open sent ' + JSON.stringify(m));
		m = await click('[data-key="show-in-folder"]');
		expect(m.type === 'showInFolder' && m.path === HUB, 'detail Show in folder sent ' + JSON.stringify(m));
		await host({ type: 'fileDetail', detail: demo.detailFor('synced', 'f-bracket') });
		expect((await page.textContent('#detail-title')).trim() === 'Wheel-Hub.SLDPRT', 'a fileDetail for another file replaced the open one');
		await host({ type: 'view', view: view('synced') });
		expect((await page.getAttribute('body', 'data-screen')) === 'detail', 'a new view threw the open detail away');
		expect((await page.getAttribute('html', 'data-theme')) === 'idea', 'effectiveTheme idea was not worn');
		await click('[data-key="back"]');

		// Take back, as a CAD lead, after the question.
		await host({ type: 'view', view: view('takeBack') });
		m = await click('[data-key="row-f-plate-left"]');
		await host({ type: 'fileDetail', detail: demo.detailFor('takeBack', 'f-plate-left') });
		expect((await page.textContent('[data-key="d-takeback"]')).trim() === 'Force check in', 'the detail key is not Force check in');
		await click('[data-key="d-takeback"]');
		const forceWords = await page.textContent('#ask-words');
		expect(/Maria Lopez has it checked out now/.test(forceWords) && /Any changes Maria hasn't checked in are kept as Maria's own copy/.test(forceWords), 'Force check in does not say who has it and what happens to Maria\'s changes: ' + forceWords);
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'takeBack' && m.fileId === 'f-plate-left', 'Force check in sent ' + JSON.stringify(m));
		await click('[data-key="back"]');

		// A notice's action.
		await host({ type: 'view', view: view('importSummary') });
		m = await click('[data-key="nt-act-import"]');
		expect(m.type === 'dismissNotice' && m.key === 'import', 'the import summary\'s Done sent ' + JSON.stringify(m));

		// A file that shares a name is renamed from its notice, in the app.
		await host({ type: 'view', view: view('groupedNotices') });
		await click('[data-key="nt-expand-nameShared"]');
		const SHARED = 'Robot 2027/CopyDesignTemp/Bracket.SLDPRT';
		await click(`[data-key="rename-ni:nameShared:${SHARED}"]`);
		await page.fill('#ask-name', 'Bracket-Intake.SLDPRT');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'renameFile' && m.path === SHARED && m.newName === 'Bracket-Intake.SLDPRT', 'Rename sent ' + JSON.stringify(m));

		// The check-out question: Not now sends its own key back and hides that one question;
		// the same question stays hidden, the next open of the file asks again.
		const asked = view('checkoutPrompt');
		await host({ type: 'view', view: asked });
		m = await click('[data-key="prompt-later"]');
		expect(m.type === 'dismissNotice' && m.key === asked.prompt.key, 'Not now sent ' + JSON.stringify(m));
		expect(!(await page.$('.prompt-card')), 'Not now left the question up');
		await host({ type: 'view', view: view('checkoutPrompt') });
		expect(!(await page.$('.prompt-card')), 'the question Not now answered came back');
		const reopened = view('checkoutPrompt');
		reopened.prompt.key = 'prompt:' + reopened.prompt.path + ':2026-10-01T22:45:00.000Z';
		await host({ type: 'view', view: reopened });
		expect(!!(await page.$('.prompt-card')), 'opening the file again did not ask again');
		m = await click('[data-key="prompt-checkout"]');
		expect(m.type === 'checkOut' && m.open === true && m.paths.join() === reopened.prompt.path, 'Check out and reopen sent ' + JSON.stringify(m));

		await click('[data-key="hdr-settings"]');
		m = await click('[data-key="set-root"]');
		expect(m.type === 'chooseVaultRoot', 'Change sent ' + JSON.stringify(m));
		m = await click('[data-key="set-start"]');
		expect(m.type === 'saveSettings' && m.startAtSignIn === false && m.theme === 'system' && m.vaultRoot === 'C:\\IDEA\\Armory', 'the switch sent ' + JSON.stringify(m));
		m = await click('[data-key="set-theme-spaceWhite"]');
		expect(m.type === 'saveSettings' && m.theme === 'spaceWhite' && m.startAtSignIn === true, 'a theme pad sent ' + JSON.stringify(m));
		m = await click('[data-key="set-incidents"]');
		expect(m.type === 'openIncidents', 'Open incidents folder sent ' + JSON.stringify(m));
		// Report a problem: nothing goes until Send, and empty words are refused in the page.
		m = await click('[data-key="set-report"]');
		expect(!m.type && (await page.evaluate(() => document.getElementById('ask').open)), 'Report a problem did not open its dialog: ' + JSON.stringify(m));
		m = await click('[data-key="ask-ok"]');
		expect(!m.type && /Write a few words/.test(await page.textContent('#ask-error')), 'an empty report was sent: ' + JSON.stringify(m));
		await click('[data-key="ask-kind-idea"]');
		await page.fill('#ask-report', 'A button to check in every file I have.');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'reportProblem' && m.kind === 'idea' && m.body === 'A button to check in every file I have.', 'Send sent ' + JSON.stringify(m));
		await host({ type: 'actionResult', requestId: m.requestId, ok: true, message: 'Saved. It will be sent when the website is ready.' });
		expect((await page.textContent('#result-word')) === 'Saved. It will be sent when the website is ready.', 'the report\'s answer was not shown');
		// Send feedback (v0.3), from Settings and from the header: a note on its own.
		await click('[data-key="hdr-settings"]');
		m = await click('[data-key="set-feedback"]');
		expect(!m.type && (await page.evaluate(() => document.getElementById('ask').open)), 'Send feedback (Settings) did not open its dialog: ' + JSON.stringify(m));
		await page.keyboard.press('Escape');
		m = await click('[data-key="feedback"]');
		expect(!m.type && (await page.evaluate(() => document.getElementById('ask').open)), 'Send feedback did not open its dialog: ' + JSON.stringify(m));
		m = await click('[data-key="ask-ok"]');
		expect(!m.type && /Write a few words/.test(await page.textContent('#ask-error')), 'empty feedback was sent: ' + JSON.stringify(m));
		await page.fill('#ask-report', 'Show who is online on the team page.');
		m = await click('[data-key="ask-ok"]');
		expect(m.type === 'sendFeedback' && m.kind === 'idea' && m.body === 'Show who is online on the team page.', 'Send feedback sent ' + JSON.stringify(m));
		await host({ type: 'actionResult', requestId: m.requestId, ok: true, message: 'Sent. Thank you for the feedback.' });
		expect((await page.textContent('#result-word')) === 'Sent. Thank you for the feedback.', 'the feedback\'s answer was not shown');
		await click('[data-key="hdr-settings"]');
		await page.keyboard.press('Escape');
		m = await click('[data-key="signout"]');
		expect(m.type === 'signOut', 'Sign out sent ' + JSON.stringify(m));
		// How many files wait is said once: by Right now when it says so, else by the status.
		await host({ type: 'view', view: view('offlineWaiting') });
		expect(!(await page.$('.status-group .sync-detail')), 'the status repeats the waiting files Right now already counts');
		const quiet = view('offlineWaiting');
		quiet.sync.detail = null;
		quiet.activity.waiting = null;
		await host({ type: 'view', view: quiet });
		const pending = ((await page.textContent('.status-group .sync-detail').catch(() => '')) || '').replace(/\s+/g, ' ').trim();
		expect(pending === '3 files are waiting to upload.', `with no detail sentence, pendingCount 3 read "${pending}"`);
		await host({ type: 'view', view: view('pausedWaiting') });
		m = await click('[data-key="sync-toggle"]');
		expect(m.type === 'resume', 'Resume sent ' + JSON.stringify(m));
		await host({ type: 'view', view: view('signedOut') });
		expect((await page.getAttribute('body', 'data-screen')) === 'connect', 'a signed-out view did not show Connect');
		m = await click('[data-key="cn-connect"]');
		expect(m.type === 'connect', 'Connect sent ' + JSON.stringify(m));
		await host({ type: 'view', view: view('connecting') });
		m = await click('[data-key="cn-cancel"]');
		expect(m.type === 'cancelConnect', 'Cancel sent ' + JSON.stringify(m));
		await host({ type: 'view', view: view('vaultOwnedByOther') });
		m = await click('[data-key="cn-choose"]');
		expect(m.type === 'chooseVaultRoot', 'Choose another folder sent ' + JSON.stringify(m));
		m = await click('[data-key="cn-own"]');
		expect(m.type === 'saveSettings', 'Use my own folder sent ' + JSON.stringify(m));
		await host({ type: 'bogus' });
		expect((await page.getAttribute('body', 'data-screen')) === 'connect', 'an unknown host message changed the screen');

		for (const x of all) {
			const want = CONTRACT[x.type];
			if (!want) {
				fails.push('sent a type BRIDGE.md does not have: ' + x.type);
				continue;
			}
			const keys = Object.keys(x)
				.filter((k) => k !== 'type' && k !== '__files')
				.sort();
			if (keys.join() !== [...want].sort().join()) fails.push(`${x.type} carried {${keys.join(', ')}}, BRIDGE.md gives {${want.join(', ')}}`);
		}
		const types = new Set(all.map((x) => x.type));
		tally.bridgeTypes = [...types].filter((t) => CONTRACT[t]).length;
		for (const t of Object.keys(CONTRACT)) if (!types.has(t)) fails.push('never sent ' + t);
		expect(!requests.some((u) => /^https?:/i.test(u)), 'a request left file://');
	} catch (e) {
		fails.push('threw ' + String(e.message || e).split('\n')[0]);
	}
	await context.close();
	for (const f of fails) {
		tally.bridgeFailures++;
		problem('bridge', 'stand-in WebView2 host', f);
	}
}

// In a plain browser the demo picks the theme from ?theme=, else prefers-color-scheme.
for (const [scheme, query, want] of [
	['light', '', 'spaceWhite'],
	['dark', '', 'idea'],
	['dark', 'theme=spaceWhite', 'spaceWhite'],
	['dark', 'theme=space-white', 'spaceWhite'],
	['light', 'theme=idea', 'idea']
]) {
	const context = await browser.newContext({ viewport: { width: 420, height: 720 }, colorScheme: scheme });
	const page = await context.newPage();
	await page.goto(pathToFileURL(path.join(WWWROOT, 'index.html')).href + '?state=synced' + (query ? '&' + query : ''));
	await page.waitForSelector('html[data-ready="true"]', { timeout: 15000 });
	const got = await page.getAttribute('html', 'data-theme');
	if (got !== want) {
		tally.bridgeFailures++;
		problem('theme', `${scheme} ${query || '(no ?theme)'}`, `wears ${got}, not ${want}`);
	}
	await context.close();
}

await browser.close();

const fmt = (n) => (Number.isFinite(n) ? n.toFixed(2) : 'n/a');
for (const p of problems.slice(0, 80)) console.log(p);
if (problems.length > 80) console.log(`... and ${problems.length - 80} more`);
console.log(
	`CHECK-UI pages=${tally.pages} controls=${tally.controls} under44=${tally.under44} network=${tally.network} ` +
		`grids=${tally.grids} rowGrids=${tally.rowGrids} plantedGridLayersFound=${tally.gridControl ?? 0}/3 rowDecoration=${tally.rowDecoration} ` +
		`chipsLikeButtons=${tally.chipsLikeButtons} overflow=${tally.overflow} hairlines=${tally.hairlines} hairlineMin=${fmt(tally.hairlineMin)} ` +
		`hairlineUnder3=${tally.hairlineUnder3} tabStops=${tally.tabStops} focusMissed=${tally.focusMissed} ringMin=${fmt(tally.ringMin)} ` +
		`ringFailures=${tally.ringFailures} jargon=${tally.jargon} offline=${tally.offline} plantedOfflineFound=${tally.offlinePlanted}/5 flows=${tally.flows} flowFailures=${tally.flowFailures} logo=${tally.logo} logoFailures=${tally.logoFailures} plantedLogoFound=${tally.logoPlanted}/1 bridgeTypes=${tally.bridgeTypes}/${Object.keys(CONTRACT).length} bridgeFailures=${tally.bridgeFailures} plantedDefectsCaught=${tally.controlsCaught}/${tally.controlsPlanted} shapes=${tally.shapes} shapeFailures=${tally.shapeFailures} plantedShapesFound=${Math.min(tally.shapesPlanted, 2)}/2 emDash=${tally.emDash} files=${scanned.length}`
);
console.log(problems.length ? `CHECK-UI FAIL problems=${problems.length}` : 'CHECK-UI PASS');
process.exit(problems.length ? 1 : 0);
