// Shared helpers for the Agent window tools (render-screens, bbox-diff, check-ui).
//
// The render matrix comes from the page's own demo data: every state in
// src/Armory.Agent/wwwroot/demo/states.js lists the screens that show it best, and each
// one is rendered in both themes at both window sizes. No server: pages load from file://.

import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const WWWROOT = path.join(ROOT, 'src', 'Armory.Agent', 'wwwroot');
// v2 renders go to their own folder, so the 64 v1 images beside it stay as they were.
export const SCREENS_DIR = path.join(ROOT, 'docs', 'agent', 'screens', 'v2');
export const THEMES = ['idea', 'spaceWhite'];
export const SIZES = [
	{ w: 1280, h: 800 },
	{ w: 420, h: 720 }
];

/** Playwright, from wherever this machine keeps it. */
export function loadPlaywright() {
	const require = createRequire(import.meta.url);
	const candidates = [
		'playwright',
		process.env.PLAYWRIGHT_MODULE,
		'/opt/node22/lib/node_modules/playwright',
		'/opt/node-tools/node_modules/playwright'
	].filter(Boolean);
	for (const c of candidates) {
		try {
			return require(c);
		} catch {
			// try the next place
		}
	}
	throw new Error('Playwright was not found. Set NODE_PATH (or PLAYWRIGHT_MODULE) to where it is installed.');
}

/** The demo data, read the same way the page reads it (once per run). */
let demoCache = null;
export function demoStates() {
	if (demoCache) return demoCache;
	const src = fs.readFileSync(path.join(WWWROOT, 'demo', 'states.js'), 'utf8');
	const sandbox = { window: {} };
	vm.runInNewContext(src, sandbox, { filename: 'demo/states.js' });
	demoCache = sandbox.window.ArmoryDemoStates;
	return demoCache;
}

/** Every (screen, state, size) the tools render, in a stable order. */
export function combos() {
	const demo = demoStates();
	const order = ['connect', 'picker', 'home', 'detail', 'settings'];
	const list = [];
	for (const screen of order)
		for (const [state, s] of Object.entries(demo.states))
			if (s.screens.includes(screen)) for (const size of SIZES) list.push({ screen, state, size });
	return list;
}

export function comboName(c) {
	return `${c.screen}-${c.state}-${c.size.w}x${c.size.h}`;
}

export function shotName(c, theme) {
	return `${c.screen}-${c.state}-${theme}-${c.size.w}x${c.size.h}.png`;
}

/** The page for one combination: the state, the theme, the screen, and the state's own
 *  page-only place (project, folder, select, expand, dialog, drag) from demo/states.js. */
export function pageUrl(c, theme) {
	const u = pathToFileURL(path.join(WWWROOT, 'index.html'));
	const s = demoStates().states[c.state] || {};
	u.search = new URLSearchParams({ state: c.state, theme, screen: c.screen, ...(s.params || {}) }).toString();
	return u.href;
}

/**
 * Opens one combination in one theme and waits until the page says it is settled
 * (html[data-ready="true"]: first view rendered, route applied, detail loaded).
 */
export async function openPage(browser, c, theme, options = {}) {
	const context = await browser.newContext({
		viewport: { width: c.size.w, height: c.size.h },
		deviceScaleFactor: 1,
		reducedMotion: options.reducedMotion || 'reduce',
		timezoneId: 'America/Los_Angeles',
		locale: 'en-US',
		colorScheme: theme === 'spaceWhite' ? 'light' : 'dark',
		bypassCSP: !!options.bypassCSP
	});
	if (options.onContext) await options.onContext(context);
	const page = await context.newPage();
	if (options.onPage) await options.onPage(page);
	await page.goto(options.url || pageUrl(c, theme));
	await page.waitForSelector('html[data-ready="true"]', { timeout: 15000 });
	await page.evaluate(() => document.fonts.ready.then(() => true));
	return { context, page };
}

/** Every file under a folder, recursively. */
export function walkFiles(dir) {
	const out = [];
	for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
		const p = path.join(dir, entry.name);
		if (entry.isDirectory()) out.push(...walkFiles(p));
		else out.push(p);
	}
	return out;
}
