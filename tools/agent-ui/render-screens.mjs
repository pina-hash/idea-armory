// Renders the Agent window's v2 screens for review: every demo state on the screens that
// show it, in both themes (IDEA, Space White), at 1280x800 and 420x720, as viewport
// screenshots (not full page) into docs/agent/screens/v2/<screen>-<state>-<theme>-<w>x<h>.png,
// plus docs/agent/screens/v2/README.md listing every file. Stale PNGs in v2 are deleted
// first; the 64 v1 images one folder up are never touched.
//
// Run: node tools/agent-ui/render-screens.mjs   (no server; pages load from file://)

import fs from 'node:fs';
import path from 'node:path';
import { loadPlaywright, combos, demoStates, THEMES, SIZES, SCREENS_DIR, ROOT, shotName, openPage } from './lib.mjs';

const { chromium } = loadPlaywright();
const demo = demoStates();

// Only ever the v2 folder: deleting in docs/agent/screens itself would lose the v1 set.
if (path.basename(SCREENS_DIR) !== 'v2') throw new Error('render-screens writes only into docs/agent/screens/v2, not ' + SCREENS_DIR);
fs.mkdirSync(SCREENS_DIR, { recursive: true });
let removed = 0;
for (const f of fs.readdirSync(SCREENS_DIR)) {
	if (f.toLowerCase().endsWith('.png')) {
		fs.unlinkSync(path.join(SCREENS_DIR, f));
		removed++;
	}
}

const browser = await chromium.launch();
const written = [];
for (const c of combos()) {
	for (const theme of THEMES) {
		const { context, page } = await openPage(browser, c, theme);
		const file = shotName(c, theme);
		await page.screenshot({ path: path.join(SCREENS_DIR, file), fullPage: false });
		written.push({ file, theme, ...c });
		await context.close();
	}
}
await browser.close();

const SCREEN_TITLES = {
	connect: 'Connect (first run)',
	home: 'Home',
	detail: 'File detail',
	settings: 'Settings sheet over Home'
};
const THEME_TITLES = { idea: 'IDEA', spaceWhite: 'Space White' };
const where = (s) => {
	const p = demo.states[s].params || {};
	const bits = [];
	if (p.project) bits.push('project `' + p.project + '`');
	if (p.folder) bits.push('folder `' + p.folder + '`');
	if (p.select) bits.push('selected `' + p.select + '`');
	if (p.expand) bits.push('open list `' + p.expand + '`');
	if (p.dialog) bits.push('dialog `' + p.dialog + '`');
	if (p.drag) bits.push('files held over the list');
	if (p.at) bits.push('scrolled to the team files');
	if (p.result) bits.push('an action\'s answer at the foot');
	return bits.join(', ');
};

let md = '# Agent window screens, v2\n\n';
md += 'Rendered by `node tools/agent-ui/render-screens.mjs` from the demo states in\n';
md += '`src/Armory.Agent/wwwroot/demo/states.js`, in both themes at ' + SIZES.map((s) => `${s.w}x${s.h}`).join(' and ') + '.\n';
md += 'Each image is the window as it first opens (the viewport, not the whole scrolled page),\n';
md += 'with any page-only place the state names (a folder, selected files, an open notice list,\n';
md += 'a dialog, files held over the list) applied from the query string, so no click is needed.\n';
md += 'File names are `<screen>-<state>-<theme>-<width>x<height>.png`. The demo clock is fixed at\n';
md += '`' + demo.now + '`, so the relative times hold still between runs. The v1 screens are one\n';
md += 'folder up, in `docs/agent/screens/`, unchanged.\n\n';
md += 'Home exists only after a computer is connected, so `signedOut`, `connecting`, `connectFailed`\n';
md += 'and `vaultOwnedByOther` appear on the Connect screen. File detail is shown where a file\'s own\n';
md += 'page tells the story best. The Settings sheet is shown once per theme, over Home in `synced`.\n\n';
md += `${written.length} images.\n`;
for (const screen of ['connect', 'home', 'detail', 'settings']) {
	const rows = written.filter((w) => w.screen === screen);
	if (!rows.length) continue;
	md += `\n## ${SCREEN_TITLES[screen]}\n\n| File | State | What it shows | Opened at | Theme | Size |\n|---|---|---|---|---|---|\n`;
	for (const r of rows) {
		md += `| [${r.file}](${r.file}) | \`${r.state}\` | ${demo.states[r.state].label} | ${where(r.state)} | ${THEME_TITLES[r.theme]} | ${r.size.w}x${r.size.h} |\n`;
	}
}
fs.writeFileSync(path.join(SCREENS_DIR, 'README.md'), md);

const rel = path.relative(ROOT, SCREENS_DIR).split(path.sep).join('/');
console.log(`SCREENS rendered=${written.length} removed_stale=${removed} dir=${rel} index=${rel}/README.md`);
