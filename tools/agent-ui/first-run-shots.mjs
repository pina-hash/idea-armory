// Screenshots of the three first-run layouts (ledger 0504) for Mr. Pina to pick from: the
// same lesson card on the same step, as coach (the card beside the lit control, the
// default), quest (every step listed and ticked) and bar (one big sentence across the
// foot), plus Home's First steps list, in the IDEA theme at the lab window size and the
// small one. Written to docs/agent/screens/first-run/.
//
// Run: node tools/agent-ui/first-run-shots.mjs

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadPlaywright, demoStates, WWWROOT, ROOT } from './lib.mjs';

const OUT = path.join(ROOT, 'docs', 'agent', 'screens', 'first-run');
fs.mkdirSync(OUT, { recursive: true });
const { chromium } = loadPlaywright();
const browser = await chromium.launch();
const written = [];

async function open(size, theme, query, init) {
	const context = await browser.newContext({ viewport: { width: size.w, height: size.h }, deviceScaleFactor: 1, reducedMotion: 'reduce', colorScheme: theme === 'idea' ? 'dark' : 'light' });
	await context.addInitScript((pre) => {
		const events = new EventTarget(); window.__sent = [];
		window.chrome = { webview: { addEventListener: (...a) => events.addEventListener(...a), postMessage: (m) => window.__sent.push(m) } };
		window.__host = (m) => events.dispatchEvent(new MessageEvent('message', { data: m }));
		if (pre) localStorage.setItem('armory.tutorial.v1:' + encodeURIComponent('jordan.reyes@boscotech.edu'), 'finished');
	}, !!init);
	const page = await context.newPage();
	const u = pathToFileURL(path.join(WWWROOT, 'index.html'));
	u.search = query;
	await page.goto(u.href);
	await page.waitForFunction(() => window.__sent.length);
	const v = JSON.parse(JSON.stringify(demoStates().states.synced.view));
	v.effectiveTheme = theme;
	for (const p of v.projects) for (const f of p.folders) for (const r of f.files) if (r.checkout.state === 'mine') { r.checkout = { state: 'available', label: 'Available', name: null, email: null, device: null, since: null }; r.status = 'synced'; }
	v.myFiles = [];
	await page.evaluate((x) => window.__host({ type: 'view', view: x }), v);
	await page.waitForTimeout(300);
	return { context, page };
}

for (const size of [{ w: 1280, h: 800 }, { w: 420, h: 720 }]) {
	for (const layout of ['coach', 'quest', 'bar']) {
		const { context, page } = await open(size, 'idea', 'lesson=' + layout);
		await page.waitForSelector('#tutorial:not([hidden])');
		// Walk to card 3, Check it out.
		await page.click('[data-tour="next"]');
		await page.waitForFunction(() => document.querySelector('#tutorial').dataset.step === 'file');
		await page.click('[data-lit]');
		await page.waitForFunction(() => !document.querySelector('[data-tour="next"]').disabled);
		await page.click('[data-tour="next"]');
		await page.waitForFunction(() => document.querySelector('#tutorial').dataset.step === 'checkout');
		await page.waitForTimeout(250);
		const file = `lesson-${layout}-${size.w}x${size.h}.png`;
		await page.screenshot({ path: path.join(OUT, file) });
		written.push(file);
		await context.close();
	}
	const { context, page } = await open(size, 'idea', '', true);
	await page.waitForSelector('#first-steps');
	await page.locator('#first-steps').scrollIntoViewIfNeeded();
	await page.waitForTimeout(200);
	const file = `first-steps-${size.w}x${size.h}.png`;
	await page.screenshot({ path: path.join(OUT, file) });
	written.push(file);
	await context.close();
}
await browser.close();
console.log(`FIRST-RUN-SHOTS ${written.length} images in ${path.relative(ROOT, OUT)}: ${written.join(', ')}`);
