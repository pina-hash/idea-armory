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
//   copy        the words a student sees never use jargon: lock, conflict, sync, journal,
//               side version, intent, RPC, hash, vault, upload, download.
//   offline     no file in wwwroot names a web address (the SVG namespace inside a data:
//               URI is the one exception: it is an identifier, never fetched), loads a
//               font with @font-face or anything with @import, or points url() anywhere
//               but data: or an in-page #id (%23id inside a data: URI).
//   flows       clicking (or pressing Enter on) a row moves the view to that file's
//               detail, scrolled to the top with its heading focused, and Back returns to
//               the row; a Needs-you card's key opens its file; the Settings sheet, Pause
//               (Resume is the green primary, and nothing to pause while offline),
//               Connect and the one-click folder of your own do what they say.
//   bridge      inside a stand-in WebView2 host (no demo transport): the page says ready
//               first, renders Home, detail and Connect from host messages alone, wears
//               effectiveTheme, ignores a stray or unknown message, and every one of the 11
//               page-to-host types is sent by the control that should send it, carrying
//               exactly the fields BRIDGE.md gives it. In a plain browser the theme comes
//               from ?theme= (idea, spaceWhite or space-white) or prefers-color-scheme.
//   em dash     no U+2014 anywhere in wwwroot (or in these tools, the screens index and the
//               design review).
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
const scanned = [
	...walkFiles(WWWROOT),
	...walkFiles(path.join(ROOT, 'tools', 'agent-ui')),
	path.join(ROOT, 'docs', 'agent', 'screens', 'README.md'),
	path.join(ROOT, 'docs', 'agent', 'DESIGN-REVIEW.md')
].filter((f) => fs.existsSync(f) && !/\.(png|jpe?g|gif|ico|webp)$/i.test(f));
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

/* ------------------------------------------------------ In-page probes */

// Words a student never reads or hears in this window. Kept here as source strings so the
// in-page probe and the self-test below use the same list.
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
	'\\bvault',
	'\\bupload',
	'\\bdownload'
];
// Each pattern must catch the word it is for.
for (const [re, sample] of JARGON.map((src, i) => [
	new RegExp(src, 'i'),
	['Lock held', 'Unlock it', 'Sync conflict', 'Syncing now', 'Journal entry', 'A side version', 'Intent sent', 'RPC failed', 'Hash mismatch', 'Open vault', 'Upload failed', 'Download it'][i]
])) {
	if (!re.test(sample)) problem('control', 'jargon list', `${re} misses "${sample}"`);
}

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
	const edgeSel = '.key, .switch, .well, .list-well, .panel, .display, .history-well, dialog.housing, .pad';
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
		document.querySelector('.panel').insertAdjacentHTML('beforeend', '<span class="plate-rail"></span>');
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

async function flow(name, size, state, run) {
	const c = { screen: state === 'signedOut' ? 'connect' : 'home', state, size };
	const { context, page } = await openPage(browser, c, 'idea');
	const where = `flow ${name} ${size.w}x${size.h}`;
	tally.flows++;
	try {
		const fails = [];
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
const settle = (page) => page.evaluate(() => new Promise((r) => setTimeout(() => requestAnimationFrame(() => r()), 30)));
const where = (page) =>
	page.evaluate(() => ({
		screen: document.body.getAttribute('data-screen'),
		top: document.getElementById('scroller').scrollTop,
		focus: document.activeElement && (document.activeElement.id || document.activeElement.getAttribute('data-key')),
		title: (document.getElementById('detail-title') || {}).textContent || null,
		history: document.querySelectorAll('.history-list > li').length
	}));

for (const size of SIZES) {
	await flow('row opens detail', size, 'lockedByOther', async (page, expect) => {
		await page.evaluate(() => (document.getElementById('scroller').scrollTop = 99999));
		await page.click('[data-key="row-f-wheel-hub"]');
		await page.waitForSelector('.history-list');
		let s = await where(page);
		expect(s.screen === 'detail', `screen is ${s.screen}, not detail`);
		expect(s.top === 0, `detail opened scrolled to ${s.top}, not the top`);
		expect(s.focus === 'detail-title', `focus is on ${s.focus}, not the heading`);
		expect(s.title === 'Wheel-Hub.SLDPRT', `heading says ${s.title}`);
		expect(s.history > 0, 'no history rows');
		await page.click('[data-key="back"]');
		await settle(page);
		s = await where(page);
		expect(s.screen === 'home', `Back went to ${s.screen}`);
		expect(s.focus === 'row-f-wheel-hub', `Back put focus on ${s.focus}, not the row that opened detail`);
		await page.focus('[data-key="mine-f-plate-left"]');
		await page.keyboard.press('Enter');
		await page.waitForSelector('.history-list');
		s = await where(page);
		expect(s.screen === 'detail' && s.title === 'Plate-Left.SLDPRT', `Enter on a row opened ${s.title}`);
		expect(s.focus === 'detail-title', `Enter left focus on ${s.focus}`);
		const holder = await page.textContent('.holder-line');
		expect(/Maria Lopez is editing this/.test(holder), `holder line says "${holder}"`);
		const email = await page.textContent('.who-email').catch(() => null);
		expect(email === 'maria.lopez@boscotech.edu', `Who's editing gives no way to reach Maria (${email})`);
	});
	await flow('a chip in a row opens the row', size, 'lockedByOther', async (page, expect) => {
		const chip = 'li.row:has([data-key="row-f-plate-left"]) .chip.who';
		const cursor = await page.$eval(chip, (c) => getComputedStyle(c).cursor);
		expect(cursor !== 'pointer', `the chip shows a ${cursor} cursor`);
		await page.click(chip);
		await page.waitForSelector('.history-list');
		const s = await where(page);
		expect(s.screen === 'detail' && s.title === 'Plate-Left.SLDPRT', `clicking the chip opened ${s.screen} ${s.title}`);
		expect(s.top === 0 && s.focus === 'detail-title', `top ${s.top}, focus ${s.focus}`);
	});
	await flow('needs-you card opens detail', size, 'conflict', async (page, expect) => {
		await page.click('[data-key="attn-open-0"]');
		await page.waitForSelector('.history-list');
		const s = await where(page);
		expect(s.screen === 'detail' && s.title === 'Plate-Left.SLDPRT', `opened ${s.title}`);
		expect(s.top === 0 && s.focus === 'detail-title', `top ${s.top}, focus ${s.focus}`);
	});
	await flow('settings sheet', size, 'synced', async (page, expect) => {
		await page.click('[data-key="hdr-settings"]');
		await settle(page);
		expect(await page.evaluate(() => document.getElementById('settings').open), 'Settings did not open');
		// A theme pad says its name and, under it, what it is ("Dark", "Light"); the name is the setting.
		const keys = await page.$$eval('#settings button', (b) => b.map((x) => (x.querySelector('.seg-name') || x).textContent.trim()));
		expect(keys.join('|') === 'Done|Change|On|Match Windows|IDEA|Space White', `sheet holds ${keys.join(', ')}`);
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
		await page.click('[data-key="mine-f-gearbox"]');
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
	await flow('nothing to pause while offline', size, 'offline', async (page, expect) => {
		expect(!(await page.$('[data-key="sync-toggle"]')), 'Pause is offered while offline');
		expect(/Offline/i.test(await page.textContent('.status-group .screen')), 'the status does not say Offline');
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

/* ------------------------------------------------------------- Bridge */

// The page as WebView2 hosts it: a stand-in window.chrome.webview records every message
// the page posts and delivers the host's. Each page-to-host type must be sent, by the
// control that should send it, with exactly the fields BRIDGE.md lists.
const CONTRACT = {
	ready: [],
	connect: [],
	cancelConnect: [],
	signOut: [],
	pause: [],
	resume: [],
	openVault: [],
	openFile: ['fileId'],
	showInFolder: ['path'],
	saveSettings: ['vaultRoot', 'startAtSignIn', 'theme'],
	chooseVaultRoot: []
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
				postMessage: (m) => window.__sent.push(JSON.parse(JSON.stringify(m)))
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
		const click = async (sel) => {
			await page.click(sel);
			await page.waitForTimeout(40);
			const m = await take();
			all.push(...m);
			return m[0] || {};
		};
		const first = await take();
		all.push(...first);
		expect(first.length === 1 && first[0].type === 'ready', 'the first message was not ready: ' + JSON.stringify(first));
		expect(!requests.some((u) => /demo\/states\.js/.test(u)), 'the demo states loaded inside WebView2');

		await host({ type: 'view', view: view('synced', { effectiveTheme: 'spaceWhite' }) });
		expect((await page.getAttribute('html', 'data-theme')) === 'spaceWhite', 'effectiveTheme spaceWhite was not worn');
		expect((await page.getAttribute('body', 'data-screen')) === 'home', 'a signed-in view did not show Home');
		let m = await click('[data-key="hdr-vault"]');
		expect(m.type === 'openVault', 'Open Armory folder sent ' + JSON.stringify(m));
		m = await click('[data-key="sync-toggle"]');
		expect(m.type === 'pause', 'Pause sent ' + JSON.stringify(m));
		m = await click('[data-key="mine-folder-f-gearbox"]');
		expect(m.type === 'showInFolder' && m.path === 'Robot 2027/Drivetrain/Gearbox.SLDASM', 'a My files folder key sent ' + JSON.stringify(m));
		m = await click('[data-key="row-f-wheel-hub"]');
		expect(m.type === 'openFile' && m.fileId === 'f-wheel-hub', 'a row sent ' + JSON.stringify(m));
		expect((await page.getAttribute('body', 'data-screen')) === 'detail', 'a row did not move the view to detail');
		await host({ type: 'fileDetail', detail: demo.detailFor('synced', 'f-wheel-hub') });
		expect((await page.$$('.history-list > li')).length === 5, 'fileDetail did not render its history');
		m = await click('[data-key="show-in-folder"]');
		expect(m.type === 'showInFolder' && m.path === 'Robot 2027/Drivetrain/Wheel-Hub.SLDPRT', 'detail Show in folder sent ' + JSON.stringify(m));
		await host({ type: 'fileDetail', detail: demo.detailFor('synced', 'f-bracket') });
		expect((await page.textContent('#detail-title')).trim() === 'Wheel-Hub.SLDPRT', 'a fileDetail for another file replaced the open one');
		await host({ type: 'view', view: view('synced') });
		expect((await page.getAttribute('body', 'data-screen')) === 'detail', 'a new view threw the open detail away');
		expect((await page.getAttribute('html', 'data-theme')) === 'idea', 'effectiveTheme idea was not worn');
		await click('[data-key="back"]');
		await click('[data-key="hdr-settings"]');
		m = await click('[data-key="set-root"]');
		expect(m.type === 'chooseVaultRoot', 'Change sent ' + JSON.stringify(m));
		m = await click('[data-key="set-start"]');
		expect(m.type === 'saveSettings' && m.startAtSignIn === false && m.theme === 'system' && m.vaultRoot === 'C:\\IDEA\\Armory', 'the switch sent ' + JSON.stringify(m));
		m = await click('[data-key="set-theme-spaceWhite"]');
		expect(m.type === 'saveSettings' && m.theme === 'spaceWhite' && m.startAtSignIn === true, 'a theme pad sent ' + JSON.stringify(m));
		await page.keyboard.press('Escape');
		m = await click('[data-key="signout"]');
		expect(m.type === 'signOut', 'Sign out sent ' + JSON.stringify(m));
		const quiet = view('offline');
		quiet.sync.detail = null;
		await host({ type: 'view', view: quiet });
		const said = ((await page.textContent('.status-group .sync-detail').catch(() => '')) || '').replace(/\s+/g, ' ').trim();
		expect(said === '3 changes are waiting to send.', `with no detail sentence, pendingCount 3 read "${said}"`);
		await host({ type: 'view', view: view('paused') });
		m = await click('[data-key="sync-toggle"]');
		expect(m.type === 'resume', 'Resume sent ' + JSON.stringify(m));
		await host({ type: 'view', view: view('conflict') });
		m = await click('[data-key="attn-open-0"]');
		expect(m.type === 'openFile' && m.fileId === 'f-plate-left', 'a Needs-you key sent ' + JSON.stringify(m));
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
			const keys = Object.keys(x).filter((k) => k !== 'type').sort();
			if (keys.join() !== [...want].sort().join()) fails.push(`${x.type} carried {${keys.join(', ')}}, BRIDGE.md gives {${want.join(', ')}}`);
		}
		const types = new Set(all.map((x) => x.type));
		tally.bridgeTypes = types.size;
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
		`ringFailures=${tally.ringFailures} jargon=${tally.jargon} offline=${tally.offline} plantedOfflineFound=${tally.offlinePlanted}/5 flows=${tally.flows} flowFailures=${tally.flowFailures} bridgeTypes=${tally.bridgeTypes}/11 bridgeFailures=${tally.bridgeFailures} plantedDefectsCaught=${tally.controlsCaught}/${tally.controlsPlanted} emDash=${tally.emDash} files=${scanned.length}`
);
console.log(problems.length ? `CHECK-UI FAIL problems=${problems.length}` : 'CHECK-UI PASS');
process.exit(problems.length ? 1 : 0);
