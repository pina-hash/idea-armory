// One geometry for both themes, proved: for every rendered combination (screen, state,
// size) this loads the page in IDEA and in Space White at the same size, reads
// getBoundingClientRect() of every element (x, y, width, height rounded to 0.01 px) keyed
// by its DOM path, and counts the boxes that differ and the elements present in one theme
// but not the other. A theme may change color and material, never a length
// (IDEA_INTERFACE_STANDARDS section 14a), so the count must be zero.
//
// The diff also proves it can see a difference: a planted control adds one length to the
// Space White theme block on one page and must report differing boxes.
//
// Exits non-zero if any box differs, any element is missing, or the planted control
// sees nothing. Summary: "BBOX states=N comparisons=M differing=0".
//
// Run: node tools/agent-ui/bbox-diff.mjs   (no server; pages load from file://)

import { loadPlaywright, combos, comboName, openPage } from './lib.mjs';

const { chromium } = loadPlaywright();

/** Runs in the page: every element's box, keyed by a stable DOM path. */
function readBoxes() {
	const out = {};
	const scroller = document.getElementById('scroller');
	if (scroller) scroller.scrollTop = 0;
	window.scrollTo(0, 0);
	const round = (v) => (Math.round(v * 100) / 100).toFixed(2);
	const walk = (el, p) => {
		const r = el.getBoundingClientRect();
		const cs = getComputedStyle(el);
		// A boxless element is recorded as boxless, never as a zero rect at the origin.
		const boxless = cs.display === 'none' || (r.width === 0 && r.height === 0);
		out[p] = boxless ? 'boxless' : [r.x, r.y, r.width, r.height].map(round).join(',');
		let i = 0;
		for (const c of el.children) walk(c, p + '>' + c.tagName.toLowerCase() + ':' + i++);
	};
	walk(document.documentElement, 'html');
	// The scrolled extents count too: a theme must not make a page longer or wider.
	const se = document.scrollingElement;
	out['@document-scroll'] = se.scrollWidth + 'x' + se.scrollHeight;
	if (scroller) out['@scroller-scroll'] = scroller.scrollWidth + 'x' + scroller.scrollHeight;
	return out;
}

function compare(a, b) {
	let differing = 0;
	const onlyA = [];
	const onlyB = [];
	const examples = [];
	for (const [k, v] of Object.entries(a)) {
		if (!(k in b)) onlyA.push(k);
		else if (b[k] !== v) {
			differing++;
			if (examples.length < 5) examples.push(`${k}: idea=${v} spaceWhite=${b[k]}`);
		}
	}
	for (const k of Object.keys(b)) if (!(k in a)) onlyB.push(k);
	return { elements: Object.keys(a).length, differing, onlyA, onlyB, examples };
}

const browser = await chromium.launch();
const list = combos();
const states = new Set(list.map((c) => c.state));
let totalDiffering = 0;
let totalMissing = 0;
let totalElements = 0;

for (const c of list) {
	const boxes = {};
	for (const theme of ['idea', 'spaceWhite']) {
		const { context, page } = await openPage(browser, c, theme);
		const actual = await page.evaluate(() => document.documentElement.getAttribute('data-theme'));
		if (actual !== theme) throw new Error(`${comboName(c)}: asked for ${theme}, page wears ${actual}`);
		boxes[theme] = await page.evaluate(readBoxes);
		await context.close();
	}
	const r = compare(boxes.idea, boxes.spaceWhite);
	const missing = r.onlyA.length + r.onlyB.length;
	totalDiffering += r.differing;
	totalMissing += missing;
	totalElements += r.elements;
	console.log(`${comboName(c).padEnd(36)} elements=${r.elements} differing=${r.differing} missing=${missing}`);
	for (const e of r.examples) console.log('    ' + e);
	for (const k of r.onlyA.slice(0, 5)) console.log('    only in IDEA: ' + k);
	for (const k of r.onlyB.slice(0, 5)) console.log('    only in Space White: ' + k);
}

// The positive control: a length planted in the Space White block must show up.
const planted = list.find((c) => c.screen === 'home' && c.size.w === 1280) || list[0];
const plantedBoxes = {};
for (const theme of ['idea', 'spaceWhite']) {
	const { context, page } = await openPage(browser, planted, theme, { bypassCSP: true });
	if (theme === 'spaceWhite') {
		await page.addStyleTag({
			content: ":root[data-theme='spaceWhite'] { --plate-r-control: 3px; --plate-chip-h: 26px; --plate-label-size: 0.9rem; }"
		});
		await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
	}
	plantedBoxes[theme] = await page.evaluate(readBoxes);
	await context.close();
}
await browser.close();
const control = compare(plantedBoxes.idea, plantedBoxes.spaceWhite);

console.log(`BBOX planted control (${comboName(planted)}, one length in Space White): differing=${control.differing} (must be above 0)`);
console.log(`BBOX states=${states.size} comparisons=${list.length} differing=${totalDiffering} missing=${totalMissing} elements=${totalElements}`);

if (totalDiffering > 0 || totalMissing > 0 || control.differing === 0) process.exit(1);
