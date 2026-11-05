// The cold-user test: a first-time student with no help, from a fresh Armory window to a
// part checked out, opened, changed, saved and checked back in, and knowing who else is
// editing what. It drives the real window (wwwroot/index.html) against a local stand-in for
// the Windows host, the school site and SolidWorks (StandIn below), so nothing leaves this
// machine, and it fails when the run misses the bar:
//
//   time      the whole first run, website to checked in, models under 10 minutes.
//   reading   every instruction the student reads grades 5 or lower (reading-level.mjs).
//   short     no instruction sentence runs past SENTENCE_WORDS words, and no instruction
//             shows more than BLOCK_SENTENCES sentences at once.
//   stuck     at every moment there is one lit next action, or the robot gives up.
//   help      a save SolidWorks refuses (the file was never checked out, the November 3,
//             2026 lab report) shows a plain card that says what happened and one thing to
//             do, and that one thing works.
//
// THE ROBOT IS A COLD USER, NOT A TESTER. It knows no selector for a feature. It reads what
// a lesson card, a checklist line or a help card tells it and presses the control that is
// lit (data-lit, or the lesson's spotlight). Where nothing is lit it reads the whole window
// once and presses the first control whose words match its task, the way a student scans a
// screen they have never seen. Each press is one step.
//
// THE CLOCK IS A MODEL, AND EVERY NUMBER IN IT IS HERE. A headless page answers in
// milliseconds, so wall time says nothing about a student. Seconds are:
//   reading   words read / READING_WPM. 115 words a minute is the 25th percentile of
//             grade 5 oral reading fluency (Hasbrouck and Tindal, 2017 norms, spring), so a
//             slow reader, not an average one.
//   pressing  KLM_PRESS seconds per press: Card, Moran and Newell's keystroke-level model,
//             think (1.35 s) + point (1.1 s) + click (0.2 s).
//   typing    KLM_KEY seconds per character (0.28 s, an average non-typist).
//   outside   OUTSIDE: the website, the download, Windows' installer warning, the browser
//             sign-in and SolidWorks itself, as fixed estimates for a lab computer. They are
//             the same before and after, so the difference between two runs is all Armory.
// The wall time of the scripted run is printed beside the model, and is not the bar.
//
// Run: node tools/agent-ui/cold-user.mjs [--size 1280x800] [--json evidence/cold-user.json]
// Prints COLD-USER pass=<bool> steps=<n> seconds=<n> worstGrade=<n> ... and exits 1 on a miss.

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadPlaywright, demoStates, WWWROOT, ROOT } from './lib.mjs';
import { gradeOfString, gradeOfScreen, sentences, words, BAR } from './reading-level.mjs';

export const READING_WPM = 115;
export const KLM_PRESS = 2.65;
export const KLM_KEY = 0.28;
export const BUDGET_SECONDS = 600;
export const SENTENCE_WORDS = 15;
export const BLOCK_SENTENCES = 2;
export const OUTSIDE = [
	{ id: 'website', seconds: 60, what: 'read ideabosco.com/armory/start and press Get the Windows app' },
	{ id: 'download', seconds: 20, what: 'the installer downloads (about 100 MB on the school network)' },
	{ id: 'install', seconds: 45, what: 'open it, More info, Run anyway, it installs and opens' },
	{ id: 'signIn', seconds: 45, what: 'sign in with the school Google account in the browser' },
	{ id: 'solidworksOpen', seconds: 60, what: 'SolidWorks starts and opens the part on a lab computer' },
	{ id: 'change', seconds: 60, what: 'make one small change to the part' },
	{ id: 'save', seconds: 5, what: 'press Save in SolidWorks' },
	{ id: 'close', seconds: 5, what: 'close the part in SolidWorks' }
];

const args = process.argv.slice(2);
const opt = (name, fallback) => {
	const i = args.indexOf('--' + name);
	return i >= 0 ? args[i + 1] : fallback;
};

/** The Windows host, the school site and SolidWorks, in memory. It answers the page the way
 *  the engine does for the handful of messages a first run sends, from the demo data. */
class StandIn {
	constructor(demo, send) {
		this.demo = demo;
		this.send = send;
		this.view = clone(demo.states.signedOut.view);
		this.timers = [];
		this.blockedWords = savedBlockedWords();
	}
	post() {
		this.send({ type: 'view', view: clone(this.view) });
	}
	answer(m, ok, message) {
		if (m.requestId) this.send({ type: 'actionResult', requestId: m.requestId, ok, message, offer: null });
	}
	rows(fn) {
		for (const p of this.view.projects || []) for (const f of p.folders) for (const r of f.files) fn(r, p);
	}
	byPath(p) {
		let hit = null;
		this.rows((r) => { if (r.path === p) hit = r; });
		return hit;
	}
	refreshMine() {
		const mine = [];
		this.rows((r, p) => { if (r.checkout.state === 'mine') mine.push({ fileId: r.fileId, path: r.path, name: r.name, project: p.name, status: r.status, note: null, checkout: r.checkout }); });
		this.view.myFiles = mine;
	}
	handle(m) {
		const demo = this.demo;
		switch (m.type) {
			case 'ready':
				this.post();
				break;
			case 'connect':
				this.view.connection = 'connecting';
				this.view.connect = { phase: 'waitingForBrowser', message: demo.states.connecting.view.connect.message };
				this.post();
				this.timers.push(setTimeout(() => { this.view = clone(demo.states.synced.view); this.view.myFiles = []; this.clearMine(); this.post(); }, 400));
				break;
			case 'openFile': {
				this.send({ type: 'fileDetail', detail: demo.detailFor('synced', m.fileId, this.view) });
				break;
			}
			case 'launchFile': {
				const row = this.byPath(m.path);
				this.answer(m, true, 'Opening ' + m.path.split('/').pop() + '.');
				if (!row) break;
				this.launched = m.path;
				// SolidWorks: the student changes the part and presses Save.
				if (row.checkout.state === 'mine') {
					row.status = 'changed';
					row.changed = true;
					this.refreshMine();
				} else {
					const reason = this.blockedWords.notCheckedOut;
					this.view.notices = [{ key: 'blocked:' + row.path, kind: 'saveBlocked', tone: 'look', title: this.blockedWords.title.replace('{name}', row.name),
						detail: reason + ' ' + this.blockedWords.nextCheckOut, count: 1,
						action: { label: this.blockedWords.checkOutLabel, command: 'saveBlockedAction', paths: ['blocked:' + row.path] },
						items: [{ fileId: row.fileId, path: row.path, name: row.name, detail: reason }] }];
					this.view.sync = Object.assign({}, this.view.sync, { state: 'attention' });
				}
				setTimeout(() => this.post(), 50);
				break;
			}
			case 'saveBlockedAction': {
				const n = (this.view.notices || []).find((x) => x.key === m.key);
				const row = n && this.byPath(n.items[0].path);
				if (row) { row.checkout = demo.checkoutMine(); this.refreshMine(); }
				this.view.notices = (this.view.notices || []).filter((x) => x.key !== m.key);
				this.view.sync = clone(demo.states.synced.view.sync);
				this.post();
				this.answer(m, !!row, row ? 'Checked out ' + row.name + '.' : 'That save question is no longer current. Try saving in SolidWorks again.');
				break;
			}
			case 'checkOut': {
				let got = 0;
				for (const p of m.paths || []) { const r = this.byPath(p); if (r && r.checkout.state === 'available') { r.checkout = demo.checkoutMine(); got++; } }
				this.refreshMine();
				this.post();
				this.answer(m, got > 0, got ? 'Checked out ' + (m.paths.length === 1 ? m.paths[0].split('/').pop() : got + ' files') + '.' : 'Those files are already checked out.');
				break;
			}
			case 'checkIn': {
				let n = 0;
				for (const p of m.paths || []) {
					const r = this.byPath(p);
					if (r && r.checkout.state === 'mine') { r.checkout = demo.checkoutAvailable(); r.changed = false; r.status = 'synced'; n++; this.checkedIn = (this.checkedIn || 0) + 1; }
				}
				this.refreshMine();
				this.post();
				this.answer(m, n > 0, n ? 'Checked in ' + (m.paths.length === 1 ? m.paths[0].split('/').pop() : n + ' files') + '.' : 'Nothing there is checked out by you.');
				break;
			}
			default:
				this.answer(m, true, 'Done.');
		}
	}
	/** A cold start: nothing is checked out by this student yet. */
	clearMine() {
		this.rows((r) => { if (r.checkout.state === 'mine') { r.checkout = this.demo.checkoutAvailable(); r.changed = false; r.status = 'synced'; } });
		this.refreshMine();
	}
	stop() { this.timers.forEach(clearTimeout); }
}

/** The save-blocked words the engine writes, read out of its source so the stand-in shows
 *  exactly what a student would. */
function savedBlockedWords() {
	const src = fs.readFileSync(path.join(ROOT, 'src', 'Armory.Agent.Engine', 'SaveBlockedDetector.cs'), 'utf8');
	const pick = (re, what) => { const m = src.match(re); if (!m) throw new Error('cold-user: could not read ' + what + ' from SaveBlockedDetector.cs'); return m[1]; };
	return {
		notCheckedOut: pick(/SaveBlockedReason\.NotCheckedOut => "([^"]+)"/, 'the not-checked-out reason'),
		nextCheckOut: pick(/CanCheckOut \? "([^"]+)"/, 'the check out next step'),
		checkOutLabel: pick(/new\(protection\.CanCheckOut \? "([^"]+)"/, 'the check out label'),
		title: pick(/NoticeTones\.Look, \$"([^"]+)"/, 'the card title').replace('{name}', '{name}')
	};
}

function clone(x) { return JSON.parse(JSON.stringify(x)); }

export async function runColdUser({ size = { w: 1280, h: 800 }, theme = 'idea' } = {}) {
	const { chromium } = loadPlaywright();
	const demo = demoStates();
	const browser = await chromium.launch();
	const context = await browser.newContext({ viewport: { width: size.w, height: size.h }, reducedMotion: 'reduce', colorScheme: theme === 'idea' ? 'dark' : 'light' });
	await context.addInitScript(() => {
		const events = new EventTarget();
		window.chrome = { webview: { addEventListener: (...a) => events.addEventListener(...a), postMessage: (m) => window.__toHost(m), postMessageWithAdditionalObjects: (m) => window.__toHost(m) } };
		window.__host = (m) => events.dispatchEvent(new MessageEvent('message', { data: m }));
	});
	const page = await context.newPage();
	const errors = [];
	page.on('pageerror', (e) => errors.push(e.message));
	let standIn = null;
	await page.exposeFunction('__toHost', (m) => { if (process.env.COLD_DEBUG) console.log('host<-', m.type, JSON.stringify(m.paths || m.path || '')); if (standIn) standIn.handle(m); });
	standIn = new StandIn(demo, (m) => { page.evaluate((msg) => window.__host(msg), m).catch(() => {}); });

	const log = [];
	const read = []; // every instruction block the student read
	const tally = { steps: 0, words: 0, typed: 0, scans: 0 };
	const fails = [];
	const t0 = Date.now();

	const settle = async () => { await page.waitForTimeout(120); };
	const visibleText = (sel) => page.evaluate((s) => {
		const out = [];
		const root = document.querySelector(s) || document.body;
		const walk = (el) => {
			if (!(el instanceof Element)) return;
			const cs = getComputedStyle(el);
			if (cs.display === 'none' || cs.visibility === 'hidden' || el.getAttribute('aria-hidden') === 'true' || el.hidden) return;
			for (const n of el.childNodes) {
				if (n.nodeType === 3) {
					const t = n.textContent.replace(/\s+/g, ' ').trim();
					if (!t) continue;
					const r = el.getBoundingClientRect();
					if (r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth && r.width > 0) out.push(t);
				} else walk(n);
			}
		};
		walk(root);
		return out;
	}, sel);

	/** The student reads a block of instruction: its words cost time and it must read easily. */
	function readBlock(where, strings) {
		const text = strings.join(' ').replace(/\s+/g, ' ').trim();
		if (!text) return;
		const n = words(text).length;
		tally.words += n;
		// Sentences are the prose a student reads: strings that end in . ! or ?, each read on
		// its own. A heading, a label or a key's word has no end mark and is not a sentence.
		const ss = strings.filter((s) => /[.!?]['"”’)]?$/.test(s.trim())).flatMap((s) => sentences(s));
		read.push({
			where,
			text,
			words: n,
			grade: gradeOfScreen(strings),
			worstString: Math.max(-99, ...strings.map((s) => gradeOfString(s) ?? -99)),
			sentences: ss.length,
			longestSentence: Math.max(0, ...ss.map((s) => words(s).length))
		});
	}
	/** No guidance on screen: the student reads the whole window once. */
	async function scan(where) {
		tally.scans++;
		const strings = await visibleText('body');
		const text = strings.join(' ');
		const n = words(text).length;
		tally.words += n;
		read.push({ where: where + ' (scan)', text: text.slice(0, 400) + (text.length > 400 ? '...' : ''), words: n, grade: gradeOfScreen(strings), scan: true });
	}
	async function press(locator, what) {
		// Hold the very element the student is looking at: a scroll can move the light, and
		// a click must land on what was read, not on whatever is lit a moment later.
		const handle = locator.asElement ? locator : await locator.elementHandle();
		// A student clicks what they can see where they see it, and scrolls only when it is not
		// on screen. (Playwright's own click scrolls first, which can push a control under the
		// lesson card, which then scrolls it back: a fight no person has.)
		const spot = () => handle.evaluate((e) => {
			const r = e.getBoundingClientRect();
			const x = r.left + r.width / 2, y = r.top + Math.min(r.height / 2, 20);
			const hit = document.elementFromPoint(x, y);
			return r.width > 0 && y > 0 && y < innerHeight && x > 0 && x < innerWidth && hit && (hit === e || e.contains(hit)) ? { x, y } : null;
		});
		let at = await spot();
		if (!at) { await handle.scrollIntoViewIfNeeded().catch(() => {}); await settle(); at = await spot(); }
		if (at) await page.mouse.click(at.x, at.y);
		else await handle.click({ timeout: 8000 });
		tally.steps++;
		log.push('press ' + what);
		await settle();
	}
	async function type(locator, text, what) {
		await locator.click();
		await page.keyboard.type(text);
		tally.steps++;
		tally.typed += text.length;
		log.push('type ' + what);
		await settle();
	}
	const lit = () => page.locator('[data-lit]:visible').first();

	try {
		await page.goto(pathToFileURL(path.join(WWWROOT, 'index.html')).href);
		await page.waitForSelector('body[data-screen="connect"]', { timeout: 10000 });
		await settle();

		// 1. The Connect screen.
		readBlock('connect', await visibleText('#main'));
		await press(page.locator('#main .key.primary').first(), 'Connect this computer');
		await page.waitForSelector('body[data-screen="home"]', { timeout: 10000 });
		await settle();

		// 2. The first run, if the window offers one. The robot reads each card and does what
		//    it says: the lit control, the exercise key, or a word in the lit box.
		let guard = 0;
		while (await page.locator('#tutorial:not([hidden])').count() && guard++ < 40) {
			const step = await page.getAttribute('#tutorial', 'data-step');
			readBlock('lesson ' + step, await visibleText('.tutorial-card'));
			const next = page.locator('[data-tour="next"]');
			if (await next.isDisabled()) {
				const exercise = page.locator('[data-tour="exercise"]:visible');
				if (await exercise.count()) await press(exercise, 'lesson exercise ' + step);
				else if (await lit().count()) await press(lit(), 'lit control ' + step);
				else {
					const target = page.locator('[data-tutorial-target]');
					const tag = await target.evaluate((e) => (e.matches('[data-action]') ? 'ACTION' : e.tagName));
					if (/INPUT|TEXTAREA/.test(tag)) await type(target, 'Practice', 'lesson box ' + step);
					else if (tag === 'ACTION') await press(target, 'spotlit control ' + step);
					else {
						// Nothing says which control: try the spotlit ones in order, as a student
						// would, each try a step, until the lesson moves on (at most five).
						const controls = target.locator('button:visible, input:visible, [role="button"]:visible, [data-action]:visible');
						const n = Math.min(5, await controls.count());
						if (!n) throw new Error('stuck in the lesson at ' + step + ': nothing to press');
						for (let i = 0; i < n; i++) {
							const control = page.locator('[data-tutorial-target]').locator('button:visible, input:visible, [role="button"]:visible, [data-action]:visible').nth(i);
							if (!(await control.count())) break;
							const box = await control.evaluate((e) => e.tagName);
							if (box === 'INPUT') await type(control, 'Practice', 'lesson box ' + step);
							else await press(control, 'spotlit control ' + step + ' (try ' + (i + 1) + ')');
							if (!(await page.locator('[data-tour="next"]').isDisabled())) break;
						}
					}
				}
				await page.waitForFunction(() => !document.querySelector('[data-tour="next"]').disabled, null, { timeout: 4000 }).catch(() => { throw new Error('stuck in the lesson at ' + step + ': Next never woke'); });
			}
			await press(page.locator('[data-tour="next"]'), 'Next (' + step + ')');
			await settle();
		}
		if (guard >= 40) throw new Error('the lesson never ended');

		// 3. The real work. Each task: follow the checklist line that is lit, or scan and look.
		async function task(id, words_, fallback) {
			const line = page.locator('#first-steps [data-state="next"]');
			if (await line.count()) {
				await line.scrollIntoViewIfNeeded().catch(() => {});
				readBlock('checklist ' + id, await visibleText('#first-steps [data-state="next"]'));
				if (!(await lit().count())) {
					const show = line.locator('[data-fs="show"]');
					if (await show.count()) await press(show, 'Show me (' + id + ')');
				}
				// Show me can be what is lit: it brings the step's control into view and lights it.
				for (let i = 0; i < 3 && (await lit().count()); i++) {
					const handle = await lit().elementHandle();
					const isShow = await handle.evaluate((e) => e.dataset.fs === 'show');
					await press(handle, isShow ? 'Show me (lit, ' + id + ')' : 'lit ' + id);
					if (!isShow) return;
				}
			}
			await scan(id);
			const hit = await fallback();
			if (!hit || !(await hit.count())) throw new Error('stuck: nothing on screen does "' + words_ + '"');
			return press(hit.first(), words_);
		}
		const wordKey = (re, scope = '#main') => page.locator(scope + ' button:visible').filter({ hasText: re });

		await task('checkOut', 'Check out', async () => wordKey(/^\s*Check out\s*$/i, '#vl-browser'));
		await page.waitForFunction(() => document.querySelector('#main') && /Checked out by you/i.test(document.querySelector('#main').textContent), null, { timeout: 4000 });
		await task('open', 'Open', async () => wordKey(/^\s*Open\s*$/i, '[data-part="mine"], #main'));
		// SolidWorks opens; the student changes the part and presses Save (the stand-in marks it changed).
		await page.waitForFunction(() => /Changed/.test(document.querySelector('#main [data-part="mine"]')?.textContent || ''), null, { timeout: 4000 }).catch(() => { throw new Error('the part never showed Changed after SolidWorks saved it'); });
		await settle();
		await task('checkIn', 'Check in', async () => wordKey(/^\s*Check in\s*$/i, '[data-part="mine"], #main'));
		await page.waitForFunction(() => !/Checked out by you/i.test(document.querySelector('#main [data-part="mine"]')?.textContent || ''), null, { timeout: 4000 }).catch(() => {});
		if (!standIn.checkedIn) fails.push('the part was never checked in');

		// 4. Who else is editing what.
		await task('others', 'Checked out by others', async () => wordKey(/Checked out by others/i));
		await settle();
		const who = (t) => /^Checked out by (?!(?:you|me|others)\b)\S/i.test(t);
		let others = await visibleText('#main');
		if (!others.find(who)) {
			// A small window: the student scrolls down to the list once.
			await page.locator('#vl-browser').first().scrollIntoViewIfNeeded().catch(() => {});
			await page.mouse.wheel(0, 200);
			tally.steps++;
			log.push('scroll to the list');
			await settle();
			others = await visibleText('#main');
		}
		const someone = others.find(who);
		if (!someone) fails.push('after following the window, no line names who else has a file checked out');
		else readBlock('who else', [someone]);

		// 5. Help: a save SolidWorks refuses. The student opens a file without checking it out.
		const file = page.locator('#vl-browser button:visible').filter({ hasText: /^\s*Open\s*$/i }).first();
		if (await file.count()) {
			await press(file, 'Open (without checking out)');
			await page.waitForFunction(() => document.querySelector('[data-notice^="blocked:"], [data-key^="nt-act-blocked:"]'), null, { timeout: 4000 });
			const card = page.locator('[data-key^="nt-act-blocked:"]').first().locator('xpath=ancestor::li[1]');
			const cardText = await card.evaluate((e) => e.innerText.split('\n').map((s) => s.trim()).filter(Boolean)).catch(() => []);
			readBlock('help: save refused', cardText);
			const primaries = await card.locator('button.primary:visible').count();
			if (primaries !== 1) fails.push('the refused-save card has ' + primaries + ' primary actions, not exactly one');
			await press(page.locator('[data-key^="nt-act-blocked:"]').first(), 'the help card\'s one action');
			await page.waitForFunction(() => !document.querySelector('[data-key^="nt-act-blocked:"]'), null, { timeout: 4000 }).catch(() => fails.push('the help card\'s action did not clear it'));
		} else fails.push('no file to try the refused save on');
	} catch (e) {
		fails.push(String(e && e.message ? e.message : e).split('\n')[0]);
	}
	for (const e of errors) fails.push('page error: ' + e);
	standIn.stop();
	const wall = (Date.now() - t0) / 1000;
	await context.close();
	await browser.close();

	const outside = OUTSIDE.reduce((n, o) => n + o.seconds, 0);
	const readingSeconds = (tally.words / READING_WPM) * 60;
	const seconds = Math.round(outside + readingSeconds + tally.steps * KLM_PRESS + tally.typed * KLM_KEY);
	const instructions = read.filter((r) => !r.scan);
	const worstGrade = Math.max(...instructions.map((r) => Math.max(r.grade ?? -99, r.worstString ?? -99)));
	const longestSentence = Math.max(0, ...instructions.map((r) => r.longestSentence || 0));
	const mostSentences = Math.max(0, ...instructions.filter((r) => !/^connect$/.test(r.where)).map((r) => r.sentences || 0));
	if (seconds > BUDGET_SECONDS) fails.push(`the first run models ${seconds} s, over the ${BUDGET_SECONDS} s bar`);
	if (worstGrade > BAR) fails.push(`an instruction reads at grade ${worstGrade}, over grade ${BAR}: ` + JSON.stringify(instructions.reduce((a, b) => (Math.max(b.grade ?? -99, b.worstString ?? -99) > Math.max(a.grade ?? -99, a.worstString ?? -99) ? b : a)).text.slice(0, 160)));
	if (longestSentence > SENTENCE_WORDS) fails.push(`an instruction sentence runs ${longestSentence} words, over ${SENTENCE_WORDS}`);
	if (mostSentences > BLOCK_SENTENCES) fails.push(`an instruction shows ${mostSentences} sentences at once, over ${BLOCK_SENTENCES}`);
	return {
		pass: fails.length === 0,
		size,
		steps: tally.steps,
		seconds,
		minutes: Math.round((seconds / 60) * 10) / 10,
		parts: { outside, reading: Math.round(readingSeconds), pressing: Math.round(tally.steps * KLM_PRESS), typing: Math.round(tally.typed * KLM_KEY) },
		wordsRead: tally.words,
		scans: tally.scans,
		lessonCards: read.filter((r) => r.where.startsWith('lesson')).length,
		worstGrade,
		longestSentence,
		mostSentences,
		wallSeconds: Math.round(wall * 10) / 10,
		fails,
		log,
		read
	};
}

if (import.meta.url === `file://${process.argv[1]}`) {
	const [w, h] = String(opt('size', '1280x800')).split('x').map(Number);
	const r = await runColdUser({ size: { w, h }, theme: opt('theme', 'idea') });
	const out = opt('json', path.join('evidence', 'cold-user.json'));
	fs.mkdirSync(path.dirname(out), { recursive: true });
	fs.writeFileSync(out, JSON.stringify(r, null, 2));
	console.log(
		`COLD-USER pass=${r.pass} size=${w}x${h} steps=${r.steps} seconds=${r.seconds} (${r.minutes} min: outside ${r.parts.outside}, reading ${r.parts.reading}, pressing ${r.parts.pressing}, typing ${r.parts.typing}) ` +
			`wordsRead=${r.wordsRead} scans=${r.scans} lessonCards=${r.lessonCards} worstGrade=${r.worstGrade} longestSentence=${r.longestSentence} mostSentences=${r.mostSentences} wall=${r.wallSeconds}s`
	);
	for (const f of r.fails) console.log('  FAIL ' + f);
	process.exit(r.pass ? 0 : 1);
}
