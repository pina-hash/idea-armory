// Renders one demo state on one screen to a PNG (for a quick look while working).
// Run: node tools/agent-ui/shot-one.mjs <state> <screen> <theme> <width> <height> <out.png> [scrollTo]
import { loadPlaywright, openPage } from './lib.mjs';
const [state, screen = 'home', theme = 'idea', w = '1280', h = '800', out = 'shot.png', scroll] = process.argv.slice(2);
const { chromium } = loadPlaywright();
const browser = await chromium.launch();
const { context, page } = await openPage(browser, { state, screen, size: { w: Number(w), h: Number(h) } }, theme);
if (scroll) {
	await page.evaluate((sel) => document.querySelector(sel)?.scrollIntoView(), scroll);
	await page.waitForTimeout(200);
}
await page.screenshot({ path: out, fullPage: false });
await context.close();
await browser.close();
