// Checks wwwroot/moments.js (0.5.6, idea-app ledger 0521): the first-time help at a student's
// first check out and first "two people changed one file" shows only when that happens while
// Armory is open, once per student, and Got it keeps it away. Runs the real file in a sandbox
// with a stand-in localStorage; no browser. Exit 1 on any failure.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const source = readFileSync(fileURLToPath(new URL('../../src/Armory.Agent/wwwroot/moments.js', import.meta.url)), 'utf8');
const store = new Map();
const sandbox = { window: {}, localStorage: { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => void store.set(k, String(v)) } };
vm.runInNewContext(source, sandbox);
const M = sandbox.window.ArmoryMoments;
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
let failed = 0;
const check = (label, ok) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${label}`); if (!ok) failed++; };

const view = (email, mine = [], notices = []) => ({
	connection: 'signedIn', account: { email },
	myFiles: mine.map((id) => ({ fileId: id, path: 'P/' + id, checkout: { state: 'mine' } })),
	notices
});
const kept = (key, tone = 'look') => ({ kind: 'keptCopy', tone, key });

check('a file already checked out when the window opens shows nothing', M.observe(view('ana@x.net', ['a'])) === null);
check('a new check out shows the check out card', M.observe(view('ana@x.net', ['a', 'b'])) === 'checkout');
check('it stays until Got it', M.observe(view('ana@x.net', ['a', 'b'])) === 'checkout');
const html = M.html('checkout', esc, ' data-tip="t."');
check('the card has its title, three lines and one Got it key with a tip', /You checked out a file/.test(html) && (html.match(/<li>/g) || []).length === 3 && /data-action="momentDone"[^>]*data-tip="t\."/.test(html));
check('no jargon a student would read', !/\b(lock|unlock|conflict|sync|vault|reserve)\b/i.test(html.replace(/data-[a-z-]+="[^"]*"/g, '')));
M.done('checkout');
check('Got it hides it', M.observe(view('ana@x.net', ['a', 'b'])) === null);
check('and the next check out does not show it again', M.observe(view('ana@x.net', ['a', 'b', 'c'])) === null);
check('Got it is kept for this student', store.has('armory.moment.v1:checkout:ana%40x.net'));
check('a kept copy that needs a look shows the two-changes card', M.observe(view('ana@x.net', ['a'], [kept('k1')])) === 'twoChanges');
check('its words say what happened', /Someone else checked it in first/.test(M.html('twoChanges', esc)));
M.done('twoChanges');
check('a quiet kept copy (info) never shows it', (M.reset(), M.observe(view('ben@x.net')), M.observe(view('ben@x.net', [], [kept('k2', 'info')]))) === null);
check('another student on the same window starts fresh', (M.observe(view('cy@x.net')), M.observe(view('cy@x.net', ['z']))) === 'checkout');
check('signed out shows nothing', M.observe({ connection: 'signedOut', account: null, myFiles: [], notices: [] }) === null);
check('an unknown moment draws nothing', M.html('nope', esc) === '');
if (failed) { console.log(`${failed} failed`); process.exit(1); }
console.log('moments: all checks passed');
