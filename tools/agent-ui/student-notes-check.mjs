// Real shipped page, both themes and sizes, with a stand-in WebView host. All URLs
// are intercepted locally, including pictures. No student/team CAD is needed.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { loadPlaywright, demoStates, WWWROOT, ROOT } from './lib.mjs';
const demo = demoStates();
const base = structuredClone(demo.states.synced.view);
const detail = structuredClone(demo.detailFor('synced', 'f-wheel-hub'));
const png = fs.readFileSync(path.join(ROOT, 'tests/Armory.Agent.Tests/Previews/Golden/bracket-ascii.stl.png'));
const browser = await loadPlaywright().chromium.launch();
let cases = 0;
try {
    for (const theme of ['idea', 'spaceWhite']) for (const size of [{ width: 1280, height: 800 }, { width: 420, height: 720 }]) {
        const context = await browser.newContext({ viewport: size, reducedMotion: 'reduce' });
        const requests = [], errors = [];
        await context.route('**/*', route => {
            const u = new URL(route.request().url());
            if (u.hostname === 'armory-pictures.local') return route.fulfill({ contentType: 'image/png', body: png });
            if (u.hostname !== 'armory.local') { requests.push(u.href); return route.abort(); }
            const file = path.resolve(WWWROOT, '.' + decodeURIComponent(u.pathname));
            if (!file.startsWith(WWWROOT + path.sep) || !fs.existsSync(file)) return route.fulfill({ status: 404, body: '' });
            return route.fulfill({ contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html', body: fs.readFileSync(file) });
        });
        const page = await context.newPage();
        page.on('pageerror', error => errors.push(String(error)));
        const view = structuredClone(base);
        view.settings.theme = view.effectiveTheme = theme;
        const p = view.projects.find(p => !p.archived);
        p.canTakeBack = true; p.role = 'mentor';
        const prototype = p.folders.flatMap(f => f.files)[0];
        const row = (i) => ({ ...prototype, fileId: 'file-' + i, path: p.name + '/Part-' + String(i).padStart(4, '0') + '.step', name: 'Part-' + String(i).padStart(4, '0') + '.step', status: 'synced', changed: false,
            checkout: { ...prototype.checkout, state: 'available', label: 'Available' }, updatedAt: new Date(1700000000000 + i * 1000).toISOString() });
        p.folders = [{ path: '', name: p.name, fileCount: 5000, files: Array.from({ length: 5000 }, (_, i) => row(i)) },
            { path: 'Empty', name: 'Empty', fileCount: 0, files: [] }, { path: '日本語', name: '日本語', fileCount: 0, files: [] }];
        view.projects = [p]; view.myFiles = []; view.notices = [];
        await page.addInitScript(({ view, detail }) => {
            window.studentView = view; window.sent = [];
            localStorage.setItem('armory.tutorial.v1:' + encodeURIComponent(view.account.email.trim().toLowerCase()), 'finished');
            window.chrome = { webview: {
                addEventListener: (_, fn) => { window.deliver = (m) => fn({ data: m }); },
                postMessage: m => {
                    window.sent.push(m);
                    if (m.type === 'ready') window.deliver({ type: 'view', view: window.studentView });
                    if (m.type === 'openFile') {
                        const row = view.projects[0].folders.flatMap(f => f.files).find(r => r.fileId === m.fileId);
                        window.deliver({ type: 'fileDetail', detail: { ...detail, ...row } });
                    }
                }
            } };
        }, { view, detail });
        await page.goto('https://armory.local/index.html');
        await page.waitForSelector('html[data-ready="true"]');
        assert.match(await page.locator('[data-part="upload-help"]').innerText(), /Add files.*choose your part/);
        await page.locator('[data-key="fk-add"]').click();
        assert.equal(await page.evaluate(() => window.sent.at(-1).type), 'addFiles');
        const add = await page.evaluate(() => window.sent.at(-1));
        await page.evaluate(m => window.deliver({ type: 'actionResult', requestId: m.requestId, ok: false, message: '' }), add);
        await page.locator('[data-key="row-file-0"]').dragTo(page.locator('#vl-browser [data-move-folder="Empty"]'));
        const mouseMove = await page.evaluate(() => window.sent.at(-1));
        assert.deepEqual([mouseMove.type, mouseMove.fileId, mouseMove.folder], ['moveFile', 'file-0', 'Empty']);
        await page.evaluate(m => window.deliver({ type: 'actionResult', requestId: m.requestId, ok: false, message: 'Close the part first.' }), mouseMove);
        await page.locator('[data-key="layout-tiles"]').click();
        await page.waitForSelector('#vl-browser.file-tiles .thumb-slot[data-thumb="on"]');
        assert.equal(await page.locator('#vl-browser').getAttribute('data-total'), '5002');
        assert.ok(await page.locator('#vl-browser > .vrow').count() < 100);
        assert.deepEqual(await page.locator('#vl-browser .key').evaluateAll(keys => keys.filter(k => { const r = k.getBoundingClientRect(); return r.width < 43.9 || r.height < 43.9; }).map(k => k.getAttribute('data-key'))), []);
        await page.selectOption('#file-order', 'newest');
        if (process.env.ARMORY_UI_SHOTS) await page.screenshot({ path: path.join(process.env.ARMORY_UI_SHOTS, 'tiles-' + theme + '-' + size.width + '.png') });
        assert.equal(await page.locator('#vl-browser .row-name').nth(2).textContent(), 'Part-4999.step');
        await page.locator('#vl-browser [data-rove="row"]').first().focus();
        await page.keyboard.press('End');
        assert.match(await page.locator(':focus').getAttribute('data-key'), /row-file-0$/);
        await page.keyboard.press('Home');
        await page.locator('[data-key="compact-folders"]').click();
        await page.locator('#vl-browser [data-rove="row"]').first().scrollIntoViewIfNeeded();
        if (process.env.ARMORY_UI_SHOTS) await page.screenshot({ path: path.join(process.env.ARMORY_UI_SHOTS, 'cells-' + theme + '-' + size.width + '.png') });
        assert.equal(await page.locator('#vl-folders').getAttribute('data-total'), '2');
        assert.equal(await page.locator('#vl-browser').getAttribute('data-total'), '5000');
        assert.match(await page.locator('#vl-folders').innerText(), /Empty/);
        const beforeForeign = await page.evaluate(() => window.sent.length);
        await page.evaluate(() => {
            const dt = new DataTransfer(); dt.setData('application/x-armory-file', 'file-4999');
            document.querySelector('#vl-folders [data-move-folder="Empty"]').dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
        });
        assert.equal(await page.evaluate(() => window.sent.length), beforeForeign);
        // A synthetic DataTransfer checks the browser's actual drag listeners, including
        // file identity, same-project target and its highlighted feedback before the drop.
        await page.evaluate(() => {
            const source = document.querySelector('#vl-browser [data-drag-file]');
            const target = document.querySelector('#vl-folders [data-move-folder="Empty"]');
            const dt = new DataTransfer();
            source.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
            target.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
            if (target.getAttribute('data-move-over') !== 'true') throw new Error('no drop feedback');
            target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
        });
        const move = await page.evaluate(() => window.sent.at(-1));
        assert.deepEqual([move.type, move.fileId, move.projectId, move.folder], ['moveFile', 'file-4999', p.id, 'Empty']);
        await page.evaluate(m => window.deliver({ type: 'actionResult', requestId: m.requestId, ok: false, message: 'Close this part in SolidWorks first.' }), move);
        assert.match(await page.locator('#result-word').innerText(), /Close this part/);
        await page.locator('[data-key="row-file-4999"]').click();
        await page.selectOption('#move-folder', 'Empty');
        await page.locator('[data-key="d-move"]').click();
        assert.equal(await page.evaluate(() => window.sent.at(-1).folder), 'Empty');
        await page.evaluate(() => window.deliver({ type: 'actionResult', requestId: window.sent.at(-1).requestId, ok: true, message: 'Moved the part. Its history stays with it.' }));
        assert.equal(await page.locator('body').getAttribute('data-screen'), 'detail');
        assert.ok(await page.locator('#move-folder').count());
        assert.deepEqual(errors, []); assert.deepEqual(requests, []);
        await page.keyboard.press('Alt+ArrowLeft');
        await page.locator('[data-key="layout-list"]').click();
        // A student sees every folder too, with the admin-only control removed.
        await page.evaluate(() => { window.studentView.projects[0].canTakeBack = false; window.deliver({ type: 'view', view: window.studentView }); });
        assert.equal(await page.locator('[data-key="compact-folders"]').count(), 0);
        assert.equal(await page.locator('#vl-browser').getAttribute('data-total'), '5002');
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
        assert.equal(overflow, false);
        await page.locator('[data-key="layout-tiles"]').click();
        await page.reload(); await page.waitForSelector('html[data-ready="true"]');
        assert.equal(await page.locator('#vl-browser').evaluate(e => e.classList.contains('file-tiles')), true);
        await page.evaluate(() => {
            const next = structuredClone(window.studentView); next.account.email = 'another@students.test';
            localStorage.setItem('armory.tutorial.v1:' + encodeURIComponent(next.account.email), 'finished');
            window.deliver({ type: 'view', view: next });
        });
        assert.equal(await page.locator('#vl-browser').evaluate(e => e.classList.contains('file-tiles')), false);
        await context.close(); cases++;
        console.log(`Student notes passed: ${theme}, ${size.width}x${size.height}`);
    }
} finally { await browser.close(); }
console.log(`STUDENT-NOTES PASS ${cases} theme/size flows`);
