// Run after the .NET golden tests: node tests/Armory.Agent.Tests/Previews/check-pictures.mjs
// Real generated PNGs, real page/CSP/eligibility, and a local stand-in for WebView2's host.
// This cannot establish that WebView2 itself raises WebResourceRequested on a lab PC.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadPlaywright, demoStates, WWWROOT } from '../../../tools/agent-ui/lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const files = fs.readdirSync(path.join(here, 'Fixtures')).filter(name => /\.(stl|step|stp|dxf|gcode|nc|tap|lbrn|lbrn2|svg)$/i.test(name));
assert.equal(files.length, 11);
const browser = await loadPlaywright().chromium.launch();
try {
    for (const theme of ['idea', 'spaceWhite']) for (const size of [{ width: 1280, height: 800 }, { width: 420, height: 720 }, { width: 320, height: 480 }]) {
        const demo = demoStates();
        const view = JSON.parse(JSON.stringify(demo.states.synced.view));
        const base = view.projects[0].folders[0].files[0];
        const rows = files.map((name, i) => ({ ...base, fileId: 'preview-' + i, path: 'Preview fixtures/' + name, name }));
        // Unavailable local files and unrequested CAD formats keep their glyph.
        rows.push({ ...base, fileId: 'absent', path: 'Preview fixtures/absent.svg', name: 'absent.svg', status: 'notOnThisComputer' });
        rows.push({ ...base, fileId: 'unsupported', path: 'Preview fixtures/other.iges', name: 'other.iges' });
        const project = { ...view.projects[0], name: 'Preview fixtures', folders: [{ path: '', name: 'Preview fixtures', fileCount: rows.length, files: rows }] };
        Object.assign(view, { myFiles: [], projects: [project], notices: [], prompt: null, effectiveTheme: theme });
        const context = await browser.newContext({ viewport: size, reducedMotion: 'reduce', colorScheme: theme === 'idea' ? 'dark' : 'light' });
        const asked = new Set(); const errors = [];
        await context.route('**/*', route => {
            const url = new URL(route.request().url());
            if (url.origin === 'https://armory-pictures.local' && url.pathname.startsWith('/thumb/')) {
                const name = path.basename(decodeURIComponent(url.pathname));
                asked.add(name);
                assert(files.includes(name), 'Unexpected picture ask: ' + name);
                return route.fulfill({ status: 200, contentType: 'image/png', body: fs.readFileSync(path.join(here, 'Golden', name + '.png')) });
            }
            if (url.origin === 'https://armory.local') {
                assert(!/^\/(thumb|shot)\//.test(url.pathname), 'Picture used the mapped page host');
                const file = path.resolve(WWWROOT, '.' + url.pathname);
                assert(file.startsWith(WWWROOT + path.sep));
                const contentType = file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html';
                return route.fulfill({ status: 200, contentType, body: fs.readFileSync(file) });
            }
            errors.push('Request left the two app hosts: ' + route.request().url());
            return route.abort();
        });
        await context.addInitScript(email => {
            localStorage.setItem('armory.tutorial.v1:' + encodeURIComponent(email.toLowerCase()), 'finished');
            const events = new EventTarget();
            window.__sent = [];
            window.chrome = { webview: {
                addEventListener: (name, fn) => events.addEventListener(name, fn),
                removeEventListener: (name, fn) => events.removeEventListener(name, fn),
                postMessage: message => window.__sent.push(message)
            } };
            window.__host = message => events.dispatchEvent(new MessageEvent('message', { data: message }));
        }, view.account.email);
        const page = await context.newPage();
        page.on('pageerror', e => errors.push(e.message));
        await page.goto('https://armory.local/index.html');
        await page.waitForFunction(() => window.__sent.some(m => m.type === 'ready'));
        await page.evaluate(view => window.__host({ type: 'view', view }), view);
        await page.waitForSelector('body[data-screen="home"]');
        for (const row of rows) {
            const element = page.locator('[data-vkey="' + row.fileId + '"]');
            await element.scrollIntoViewIfNeeded();
            if (row.fileId === 'absent' || row.fileId === 'unsupported') {
                assert.equal(await element.locator('img.thumb').count(), 0);
                continue;
            }
            // The established compact layout hides row glyphs and pictures below 560px.
            // File detail must still load eagerly there, even from a hidden row picture.
            if (size.width > 560) {
                await element.locator('.thumb-slot[data-thumb="on"]').waitFor();
                assert.equal(await element.locator('img.thumb').evaluate(img => img.naturalWidth), 192);
            } else assert.equal(await element.locator('.thumb-slot').evaluate(slot => getComputedStyle(slot).display), 'none');
            await element.locator('[data-action="openFile"]').first().click();
            const detail = { fileId: row.fileId, name: row.name, path: row.path, project: project.name, folder: '', status: row.status,
                checkout: row.checkout, releaseNotChecked: false, canTakeBack: false, history: [], savedRelease: null, newerThanPin: false };
            await page.evaluate(detail => window.__host({ type: 'fileDetail', detail }), detail);
            await page.locator('.detail-thumb[data-thumb="on"]').waitFor();
            assert.equal(await page.locator('.detail-thumb img').evaluate(img => img.naturalWidth), 192);
            await page.keyboard.press('Alt+ArrowLeft');
            await page.waitForSelector('body[data-screen="home"]');
        }
        assert.equal(asked.size, files.length);
        assert.deepEqual(errors, []);
        console.log(`PICTURES PASS ${theme} ${size.width}x${size.height}: ${files.length} ${size.width > 560 ? 'row/detail' : 'detail'} PNGs; absent/unsupported keep glyphs`);
        await context.close();
    }
} finally { await browser.close(); }
