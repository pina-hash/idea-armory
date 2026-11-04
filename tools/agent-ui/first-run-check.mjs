// The first run's words, checked without a browser (ledger 0504): the lesson, the six
// glossary words, Home's First steps list, the plain help cards and the save-blocked card
// the engine writes. Every instruction grades 5 or lower (reading-level.mjs), says one
// thing in one short sentence, and uses the one vocabulary (words.js). The help cards are
// held to the real report lines they answer, and plain words pass through them unchanged.
//
// Run: node tools/agent-ui/first-run-check.mjs   (exits 1 on any miss)

import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { WWWROOT, ROOT } from './lib.mjs';
import { grade, gradeOfString, sentences, words, selfTest, BAR } from './reading-level.mjs';

const SENTENCE_WORDS = 15;
const fails = [];
const tally = { strings: 0, worst: -99, worstText: '' };
const fail = (m) => fails.push(m);

function load(file) {
	const window = { location: { search: '' } };
	const sandbox = { window, document: { addEventListener() {}, querySelector() { return null; }, querySelectorAll() { return []; } }, location: window.location, URLSearchParams, localStorage: { getItem() { return null; }, setItem() {} }, setTimeout };
	vm.runInNewContext(fs.readFileSync(path.join(WWWROOT, file), 'utf8'), sandbox, { filename: file });
	return window;
}
/** One instruction: a sentence or two, each short, all at grade 5 or lower. */
function instruction(where, text, maxSentences = 1) {
	tally.strings++;
	const g = grade(text);
	if (g !== null && g > tally.worst) { tally.worst = g; tally.worstText = where + ': ' + text; }
	const ss = sentences(text);
	if (gradeOfString(text) !== null && gradeOfString(text) > BAR) fail(`${where}: grade ${gradeOfString(text)} over ${BAR}: "${text}"`);
	if (ss.length > maxSentences) fail(`${where}: ${ss.length} sentences, over ${maxSentences}: "${text}"`);
	for (const s of ss) if (words(s).length > SENTENCE_WORDS) fail(`${where}: a ${words(s).length}-word sentence: "${s}"`);
	if (/\u2014/.test(text)) fail(`${where}: an em dash: "${text}"`);
}

for (const f of selfTest()) fail('reading-level: ' + f);

// The lesson.
const lesson = load('tutorial.js').ArmoryTutorial;
const job = ['find', 'checkOut', 'launch', 'save', 'checkIn', 'other', 'realWork'];
if (lesson.steps.length > 10) fail('the lesson has ' + lesson.steps.length + ' cards, over ten');
for (const t of job) if (!lesson.steps.some((s) => s.topic === t)) fail('the lesson lost the ' + t + ' step');
const glossary = load('words.js').ArmoryWords;
for (const s of lesson.steps) {
	instruction('lesson ' + s.id, s.body);
	if (words(s.title).length > 6) fail(`lesson ${s.id}: a title of ${words(s.title).length} words`);
	if (s.word && !glossary.byId[s.word]) fail(`lesson ${s.id}: names a word the glossary does not have (${s.word})`);
}

// The six words: one line each, where each is first met.
if (glossary.list.length > 6) fail('the glossary has ' + glossary.list.length + ' words, over six');
for (const w of glossary.list) {
	instruction('word ' + w.word, w.means);
	if (!w.icon) fail('word ' + w.word + ' has no icon beside it');
}
const met = new Set(lesson.steps.map((s) => s.word).filter(Boolean));
for (const w of glossary.list) if (!met.has(w.id) && w.id !== 'version') fail('the word ' + w.word + ' is never shown where it is first met');
if (!/ArmoryWords\.line\('version'\)/.test(fs.readFileSync(path.join(WWWROOT, 'app.js'), 'utf8'))) fail('the word Version is not shown on the file page, where versions are first met');

// First steps.
const first = load('first-steps.js').ArmoryFirstSteps;
for (const s of first.steps) {
	instruction('first steps ' + s.id, s.line);
	if (s.tip) instruction('first steps ' + s.id + ' tip', s.tip);
}

// Help cards, and the real lines they answer (the Armory reports up to 2026-11-04).
const help = load('help.js').ArmoryHelp;
for (const c of [...help.cards, help.generic]) {
	instruction('help ' + c.id + ' (what happened)', c.what);
	instruction('help ' + c.id + ' (what to do)', c.next);
	instruction('help ' + c.id, c.what + ' ' + c.next, 2);
}
const REAL = [
	['Armory.Client.ArmoryRpcException: armory_my_projects failed with 521.', 'offline'],
	['Armory.Client.ArmoryRpcException: armory_list_changes failed with 521.', 'offline'],
	['System.Text.Json.JsonReaderException: Expected end of string, but instead reached end of data. LineNumber: 0 | BytePositionInLine: 641983.', 'cutOff'],
	['System.Net.Http.HttpRequestException: No such host is known. (abc.supabase.co:443)', 'offline'],
	['The SSL connection could not be established, see inner exception.', 'offline'],
	['A task was canceled.', 'offline'],
	["The process cannot access the file 'C:\\IDEA\\Armory\\Robot 2027\\Plate.SLDPRT' because it is being used by another process.", 'inUse'],
	["Access to the path 'C:\\IDEA\\Armory\\Class\\Reyes_Ana' is denied.", 'notYours'],
	['PT403 {"reason":"not_your_folder"}', 'notYours'],
	['There is not enough space on the disk.', 'diskFull'],
	['JWT expired', 'signIn'],
	['Object reference not set to an instance of an object.', 'generic'],
	['System.InvalidOperationException: Sequence contains no elements', 'generic']
];
for (const [line, want] of REAL) {
	const c = help.card(line);
	if (!c || c.id !== want) fail(`help: "${line.slice(0, 70)}" got ${c ? c.id : 'no card'}, not ${want}`);
}
// Words that are already plain go through untouched, numbers and all.
for (const plain of ['Checked out Plate.SLDPRT.', 'Downloading 412 of 1,280 files, 2.1 GB left, about 3 min', 'That sign-in didn\u2019t finish. Check that you\u2019re online, then try again.', 'Checked out 3 of 5 files. Maria Lopez has 2 of them checked out.', 'Your files show up in C:\\IDEA\\Armory.'])
	if (help.say(plain) !== plain) fail(`help rewrote plain words: "${plain}" became "${help.say(plain)}"`);

// The card the engine writes when SolidWorks cannot save (SaveBlockedDetector.cs).
const cs = fs.readFileSync(path.join(ROOT, 'src', 'Armory.Agent.Engine', 'SaveBlockedDetector.cs'), 'utf8');
const words_ = [...cs.matchAll(/(?:SaveBlockedReason\.\w+|_) => \$?"((?:[^"{]|\{[^}]*\})+)"/g)].map((m) => m[1].replace(/\{p\.Who \?\? "([^"]+)"\}/, '$1'));
const nexts = [...cs.matchAll(/var next = protection\.CanCheckOut \? "([^"]+)"\s*:\s*"([^"]+)"/g)].flatMap((m) => [m[1], m[2]]);
if (words_.length < 5 || nexts.length !== 2) fail('could not read the save-blocked words from SaveBlockedDetector.cs');
for (const w of words_) for (const n of nexts) instruction('save-blocked', w + ' ' + n, 2);

// One vocabulary: no second name for checking out, checking in or the folder.
const BANNED = /\b(lock(?:ed|s)?|unlock|reserve[sd]?|grab|sync(?:ed|ing)?|push(?:ed)?|vault)\b/i;
const copy = [
	...lesson.steps.flatMap((s) => [s.title, s.body]),
	...glossary.list.flatMap((w) => [w.word, w.means]),
	...first.steps.flatMap((s) => [s.title, s.line, s.tip || '']),
	...help.cards.flatMap((c) => [c.what, c.next]),
	...words_,
	...nexts
];
for (const t of copy) if (BANNED.test(t)) fail(`a second word for an Armory idea: "${t}"`);

console.log(`FIRST-RUN strings=${tally.strings} worstGrade=${tally.worst} (${tally.worstText}) lessonCards=${lesson.steps.length} words=${glossary.list.length} helpCards=${help.cards.length + 1} realLines=${REAL.length}`);
for (const f of fails) console.log('  FAIL ' + f);
console.log(fails.length ? 'FIRST-RUN FAIL' : 'FIRST-RUN PASS');
process.exit(fails.length ? 1 : 0);
