// Run with the same Playwright setup as tools/agent-ui/check-ui.mjs:
// node tests/Armory.Agent.Tests/FileSearchPageTests.mjs
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { loadPlaywright, openPage, pageUrl, demoStates, WWWROOT } from '../../tools/agent-ui/lib.mjs';

const fixture = structuredClone(demoStates().states.synced.view);
const { chromium } = loadPlaywright();
// Serve only the shipped page over loopback. This also works on managed browsers
// that disallow file URLs, and exercises the production page's self-only CSP.
const server = http.createServer((request, response) => {
    const file = path.resolve(WWWROOT, '.' + decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
    if (!file.startsWith(WWWROOT + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
        response.writeHead(404).end();
        return;
    }
    response.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
    fs.createReadStream(file).pipe(response);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = (combo, theme) => `http://127.0.0.1:${server.address().port}/index.html${new URL(pageUrl(combo, theme)).search}`;
const browser = await chromium.launch({ headless: true, executablePath: process.env.ARMORY_CHROMIUM || undefined })
    .catch(error => { server.close(); throw error; });
try {
    // Both themes and window sizes: text, focus, debouncing, quick filters, type, empty
    // state, matching-only selection, redraws, activity, IME, subfolders and keyboard.
    for (const theme of ['idea', 'spaceWhite']) for (const size of [{ w: 1280, h: 800 }, { w: 420, h: 720 }]) {
        const errors = [];
        const externalRequests = [];
        const combo = { state: 'synced', screen: 'home', size };
        const { context, page } = await openPage(browser, combo, theme, {
            url: url(combo, theme),
            onContext: context => context.on('request', request => {
                if (new URL(request.url()).hostname !== '127.0.0.1') externalRequests.push(request.url());
            }),
            onPage: async page => {
                page.setDefaultTimeout(10000);
                page.setDefaultNavigationTimeout(10000);
                page.on('pageerror', e => errors.push(String(e)));
                await page.addInitScript(() => {
                    window.searchHostMessages = [];
                    window.chrome = { webview: {
                        postMessage: m => { window.searchHostMessages.push(m); if (m.type === 'ready') window.searchHost({ data: { type: 'view', view: window.searchView } }); },
                        addEventListener: (type, callback) => { window.searchHost = callback; }
                    } };
                });
                await page.addInitScript(v => {
                    window.searchView = v;
                    const p = v.projects.find(p => !p.archived);
                    const base = p.folders.flatMap(f => f.files)[0];
                    const row = (name, state, changed, folder = '') => ({ ...base, fileId: name, name,
                        path: p.name + '/' + (folder ? folder + '/' : '') + name,
                        checkout: { ...base.checkout, state }, changed, status: changed ? 'changed' : 'synced' });
                    p.folders = [
                        { path: '', name: p.name, fileCount: 3, files: [row('Plate.SLDPRT', 'mine', true), row('Drive.SLDASM', 'other', true), row('notes.txt', 'available', false)] },
                        { path: 'Deep/日本語', name: '日本語', fileCount: 2, files: [row('Écrou.SLDPRT', 'myOtherComputer', false, 'Deep/日本語'), row('Plate.slddrw', 'other', false, 'Deep/日本語')] },
                        { path: 'Empty', name: 'Empty', fileCount: 0, files: [] }
                    ];
                    v.myFiles = [];
                    v.notices = [];
                }, { ...fixture, effectiveTheme: theme, settings: { ...fixture.settings, theme } });
            }
        });
        const input = page.locator('#file-search');
        const count = page.locator('#file-search-count');
        console.log(`Checking browser search: ${theme}, ${size.w}x${size.h}.`);
        assert.equal(await page.locator('html').getAttribute('data-theme'), theme);
        const waitCount = async text => { await count.filter({ hasText: text }).waitFor(); assert.equal(await count.textContent(), text); };
        await page.keyboard.press('Control+f');
        assert.equal(await input.evaluate(e => e === document.activeElement), true);
        await waitCount('5 of 5 files');
        await input.fill('pLaTe');
        // Input changes alone do not synchronously filter.
        assert.equal(await count.textContent(), '5 of 5 files');
        await waitCount('2 of 5 files');
        assert.equal(await input.evaluate(e => e === document.activeElement), true);
        assert.equal(await input.inputValue(), 'pLaTe');
        if (process.env.ARMORY_SEARCH_SHOTS) await page.screenshot({ path: path.join(process.env.ARMORY_SEARCH_SHOTS, `search-${theme}-${size.w}.png`) });
        await page.getByRole('checkbox', { name: 'Select all matching files', exact: true }).click();
        assert.equal(await page.locator('#vl-browser [role=checkbox][aria-checked=true]').count(), 2);
        // A host change removes selection when a row stops matching, too.
        await page.evaluate(() => {
            const file = window.searchView.projects[0].folders[0].files[0];
            window.searchOriginalFile = structuredClone(file);
            file.name = 'Renamed.SLDPRT';
            file.path = file.path.replace('Plate.SLDPRT', file.name);
            window.searchHost({ data: { type: 'view', view: window.searchView } });
        });
        await waitCount('1 of 5 files');
        assert.equal(await page.locator('#vl-browser [role=checkbox][aria-checked=true]').count(), 1);
        await page.evaluate(() => {
            window.searchView.projects[0].folders[0].files[0] = window.searchOriginalFile;
            window.searchHost({ data: { type: 'view', view: window.searchView } });
        });
        // The next query drops selections rather than leaving hidden files selected.
        await input.fill('日本語');
        await waitCount('2 of 5 files');
        await page.waitForFunction(() => !document.querySelector('#vl-browser [role=checkbox][aria-checked=true]'));
        assert.match(await page.locator('#vl-browser').innerText(), /Écrou|Plate/);
        await input.hover();
        await page.locator('#tip[data-on=true]').waitFor();
        await page.keyboard.press('Escape');
        await waitCount('5 of 5 files');
        assert.equal(await input.inputValue(), '');
        await page.getByRole('button', { name: 'Checked out by me', exact: true }).click();
        await waitCount('2 of 5 files');
        await page.getByRole('button', { name: 'Checked out by others', exact: true }).click();
        await waitCount('4 of 5 files');
        await page.getByRole('button', { name: 'Changed locally', exact: true }).click();
        await waitCount('2 of 5 files');
        await page.locator('#file-search-type').selectOption('1');
        await waitCount('1 of 5 files');
        await input.fill('does not exist');
        await waitCount('0 of 5 files');
        assert.match(await page.locator('.browser-empty').innerText(), /Clear the search.*quick filter.*All types/);
        // A fresh inventory and activity do not discard the query, type or focus.
        await input.focus();
        await page.evaluate(() => {
            window.searchView.sync.detail = 'A newer view';
            window.searchHost({ data: { type: 'view', view: window.searchView } });
            window.searchHost({ data: { type: 'activity', activity: window.searchView.activity } });
        });
        assert.equal(await input.inputValue(), 'does not exist');
        assert.equal(await input.evaluate(e => e === document.activeElement), true);
        assert.equal(await page.locator('#file-search-type').inputValue(), '1');
        await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
        await waitCount('5 of 5 files');
        assert.equal(await page.locator('#file-search-type').inputValue(), '0');
        // IME composition waits for its completed text.
        await input.evaluate(e => {
            e.value = '日本語';
            e.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true }));
        });
        await page.waitForTimeout(220);
        assert.equal(await count.textContent(), '5 of 5 files');
        await input.dispatchEvent('compositionend');
        await waitCount('2 of 5 files');
        await page.getByRole('button', { name: 'Reset filters', exact: true }).click();
        await page.locator('[data-action=folder][data-folder=Empty]').click();
        await waitCount('0 of 0 files');
        assert.match(await page.locator('.browser-empty').innerText(), /This folder is empty/);
        await input.fill('Plate');
        await waitCount('0 of 0 files');
        await page.waitForFunction(() => document.querySelector('.browser-empty').textContent.includes('No files match'));
        const controls = page.locator('[data-part=file-search] button, [data-part=file-search] input, [data-part=file-search] select');
        for (const control of await controls.all()) {
            const box = await control.boundingBox();
            assert.ok(box.width >= 44 && box.height >= 44, '44px search control');
        }
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'No horizontal overflow');
        await page.evaluate(() => {
            window.searchView.connection = 'signedOut';
            window.searchHost({ data: { type: 'view', view: window.searchView } });
        });
        await page.locator('body[data-screen=connect]').waitFor();
        await page.evaluate(() => {
            window.searchView.connection = 'signedIn';
            window.searchHost({ data: { type: 'view', view: window.searchView } });
        });
        await input.waitFor();
        assert.equal(await input.inputValue(), '', 'Sign out drops the previous search');
        assert.equal(await page.locator('#file-search-type').inputValue(), '0');
        assert.deepEqual(errors, []);
        assert.deepEqual(externalRequests, [], 'No request leaves the local page');
        console.log(`Browser search flows passed: ${theme}, ${size.w}x${size.h}.`);
        await context.close();
    }

    const big = { state: 'bigProject', screen: 'home', size: { w: 1280, h: 800 } };
    const { context, page } = await openPage(browser, big, 'idea', { url: url(big, 'idea') });
    page.setDefaultTimeout(10000);
    await page.locator('#file-search').fill('CopyDesignTemp');
    await page.waitForFunction(() => document.querySelector('#file-search-count').textContent === '5,000 of 5,000 files');
    await page.locator('#vl-browser').scrollIntoViewIfNeeded();
    const measure = async () => page.evaluate(() => {
        const entries = Array.from({ length: 5000 }, (_, i) => ({ name: `Plate-${i}.SLDPRT`, path: `Robot/Drive/Stage-${i % 10}/Deep/Plate-${i}.SLDPRT`, checkout: i % 2 === 0 ? 'mine' : 'other', changed: i % 5 === 0 }));
        const start = performance.now();
        const index = window.ArmoryFileSearch.create(entries);
        const prepareMs = performance.now() - start;
        const options = { text: 'stage-0/deep', checkout: 1, changed: true, type: 1 };
        for (let i = 0; i < 20; i++) index.filter(options);
        const samples = Array.from({ length: 101 }, () => {
            const start = performance.now();
            const hits = index.filter(options);
            if (hits.length !== 500) throw new Error('Wrong benchmark results');
            return performance.now() - start;
        }).sort((a, b) => a - b);
        return { medianMs: samples[50], p95Ms: samples[95], maxMs: samples[100], prepareMs };
    });
    const perf = await measure();
    const rowCount = await page.locator('#vl-browser li[data-i]').count();
    assert.ok(rowCount > 0 && rowCount < 150, 'Virtualization preserved with matching rows drawn');
    const first = page.locator('#vl-browser [data-rove=row][tabindex="0"]');
    await first.focus();
    await page.keyboard.press('End');
    await page.waitForFunction(() => document.activeElement.closest('li[data-i]')?.getAttribute('data-i') === '4999');
    console.log('Browser 5,000-file timing (normal CPU): ' + JSON.stringify(perf));
    const session = await context.newCDPSession(page);
    await session.send('Emulation.setCPUThrottlingRate', { rate: 4 });
    const slow = await measure();
    assert.ok(slow.p95Ms < 100, '5,000-file filter stays within 100 ms at 4x CPU slowdown');
    const queryMs = await page.evaluate(() => new Promise((resolve, reject) => {
        const count = document.querySelector('#file-search-count');
        const input = document.querySelector('#file-search');
        const start = performance.now();
        const observer = new MutationObserver(() => {
            if (count.textContent !== '0 of 5,000 files') return;
            observer.disconnect();
            clearTimeout(timer);
            requestAnimationFrame(() => requestAnimationFrame(() => resolve(performance.now() - start)));
        });
        const timer = setTimeout(() => { observer.disconnect(); reject(new Error('UI filter did not finish')); }, 4000);
        observer.observe(count, { childList: true, characterData: true, subtree: true });
        input.value = 'no matching files';
        input.dispatchEvent(new Event('input', { bubbles: true }));
    }));
    assert.ok(queryMs < 1000, `The 4x CPU UI filter, debounce and paint took ${queryMs} ms`);
    await context.close();
    console.log('Browser at 4x CPU slowdown: ' + JSON.stringify(slow));
    console.log(`Browser at 4x CPU slowdown, input through debounce and paint: ${queryMs.toFixed(1)} ms.`);
} finally {
    await browser.close();
    server.close();
}
