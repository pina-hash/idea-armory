// Renders the Agent window's screens for review: every demo state on the screens that
// show it, in both themes (IDEA, Space White), at 1280x800 and 420x720, as viewport
// screenshots (not full page) into docs/agent/screens/<screen>-<state>-<theme>-<w>x<h>.png,
// plus docs/agent/screens/README.md listing every file. Stale PNGs are deleted first.
//
// Run: node tools/agent-ui/render-screens.mjs   (no server; pages load from file://)

import fs from 'node:fs';
import path from 'node:path';
import { loadPlaywright, combos, demoStates, THEMES, SIZES, SCREENS_DIR, shotName, openPage } from './lib.mjs';

const { chromium } = loadPlaywright();
const demo = demoStates();

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

let md = '# Agent window screens\n\n';
md += 'Rendered by `node tools/agent-ui/render-screens.mjs` from the demo states in\n';
md += '`src/Armory.Agent/wwwroot/demo/states.js`, in both themes at ' + SIZES.map((s) => `${s.w}x${s.h}`).join(' and ') + '.\n';
md += 'Each image is the window as it first opens (the viewport, not the whole scrolled page).\n';
md += 'File names are `<screen>-<state>-<theme>-<width>x<height>.png`. The demo clock is fixed at\n';
md += '`' + demo.now + '`, so the relative times hold still between runs.\n\n';
md += 'Home exists only after a computer is connected, so `signedOut` and `connecting` (and the\n';
md += 'other not-yet-connected states) appear on the Connect screen. File detail is shown where a\n';
md += "file's own page tells the story best: `lockedByOther`, `releaseNotChecked` and `conflict`.\n";
md += 'The Settings sheet is shown once per theme, over Home in the `synced` state.\n\n';
md += `${written.length} images.\n`;
for (const screen of ['connect', 'home', 'detail', 'settings']) {
	const rows = written.filter((w) => w.screen === screen);
	if (!rows.length) continue;
	md += `\n## ${SCREEN_TITLES[screen]}\n\n| File | State | What it shows | Theme | Size |\n|---|---|---|---|---|\n`;
	for (const r of rows) {
		md += `| [${r.file}](${r.file}) | \`${r.state}\` | ${demo.states[r.state].label} | ${THEME_TITLES[r.theme]} | ${r.size.w}x${r.size.h} |\n`;
	}
}
fs.writeFileSync(path.join(SCREENS_DIR, 'README.md'), md);

console.log(`SCREENS rendered=${written.length} removed_stale=${removed} dir=docs/agent/screens index=docs/agent/screens/README.md`);
