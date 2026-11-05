// Reading level of the words a student reads, with no dependency.
//
// Flesch-Kincaid grade level: 0.39 * (words / sentences) + 11.8 * (syllables / words) - 15.59.
// The bar for Armory's first run, glossary, checklist and help screens is grade 5 or lower
// (Mr. Pina, 2026-11-04: "The dumbest person should be able to understand how Armory works
// pretty easily, pretty early on").
//
// How it counts, so a reader can argue with the number:
//   sentences  split after . ! or ? (a string with no end mark is one sentence).
//   words      runs of letters, digits and apostrophes. "C:\IDEA\Armory" is one word.
//   syllables  vowel groups (a e i o u y), less a silent final e, an -es or -ed that adds no
//              sound, at least one per word. The self-test below holds it to a word list.
//   names      the few names a student sees on every screen (Armory, SolidWorks, Windows,
//              IDEA, Google) count as one syllable each, the way a reading-level list treats a
//              word the reader already knows. NAMES is the whole list and is printed with
//              every report. A file name (Robot-2027.SLDASM) counts the same way: it is a
//              label the student matches against their own part, not a word they sound out.
//   short      Flesch-Kincaid means nothing on a two-word label ("Check out" alone scores
//              grade 2.3, "Available" alone scores 32). A string under SHORT_WORDS words is
//              still counted, but only inside the screen it sits on (gradeOfScreen), never
//              on its own.
//
// Run `node tools/agent-ui/reading-level.mjs` for the self-test, or
// `node tools/agent-ui/reading-level.mjs "Some words."` to grade one string.

export const NAMES = ['armory', 'solidworks', 'windows', 'idea', 'google', 'sldprt', 'sldasm', 'slddrw'];
export const SHORT_WORDS = 4;
export const BAR = 5;

/** @param {unknown} text @returns {string[]} */
export function words(text) {
	return String(text).match(/[A-Za-z0-9][A-Za-z0-9'’:\\.\-]*[A-Za-z0-9]|[A-Za-z0-9]/g) || [];
}

/** @param {unknown} text @returns {string[]} */
export function sentences(text) {
	const parts = String(text)
		.replace(/\s+/g, ' ')
		.split(/(?<=[.!?])\s+(?=[A-Z0-9"“(])/)
		.map((s) => s.trim())
		.filter((s) => words(s).length);
	return parts.length ? parts : [];
}

/** @param {unknown} word @returns {number} */
export function syllables(word) {
	let w = String(word).toLowerCase().replace(/’/g, "'");
	if (/^[a-z0-9_\-]+\.(?:sld[a-z]+|step|stp|dxf|pdf|stl|txt)$/.test(w)) return 1;
	// A path or address is read as its parts.
	if (/[\\.:\-]/.test(w.replace(/\.$/, ''))) return w.split(/[\\.:\-]+/).filter(Boolean).reduce((/** @type {number} */ n, /** @type {string} */ part) => n + syllables(part), 0) || 1;
	w = w.replace(/'s$|'t$|'re$|'ll$|'ve$|'d$/, '').replace(/[^a-z0-9]/g, '');
	if (!w) return 1;
	if (NAMES.includes(w)) return 1;
	if (/^[0-9]+$/.test(w)) return Math.max(1, Math.ceil(w.length / 2));
	if (w.length <= 3) return 1;
	let v = w;
	// "-le" after a consonant is its own syllable (table, little), so its e stays. An -es after
	// s, x, z, ch, sh, g or c is heard (changes, boxes), and so is -ed after t or d (added).
	if (!/[^aeiouy]le$/.test(v) && !/(?:[sxzgc]|ch|sh)es$/.test(v) && !/[td]ed$/.test(v))
		v = v.replace(/(?:[^aeiouy]es|[^aeiouy]ed|[^aeiouy]e)$/, (m) => m[0]);
	v = v.replace(/^y/, '');
	const groups = v.match(/[aeiouy]+/g);
	return Math.max(1, groups ? groups.length : 1);
}

/** @param {unknown} text */
export function stats(text) {
	const s = sentences(text);
	const ws = words(text);
	const syl = ws.reduce((n, w) => n + syllables(w), 0);
	return { sentences: Math.max(1, s.length), words: ws.length, syllables: syl };
}

/** Flesch-Kincaid grade of running text; null when there are no words. */
/** @param {unknown} text @returns {number | null} */
export function grade(text) {
	const st = stats(text);
	if (!st.words) return null;
	return round(0.39 * (st.words / st.sentences) + 11.8 * (st.syllables / st.words) - 15.59);
}

/** The grade of one string read on its own, or null when it is too short to mean anything. */
/** @param {unknown} text @returns {number | null} */
export function gradeOfString(text) {
	return words(text).length < SHORT_WORDS ? null : grade(text);
}

/** The grade of everything on one screen, labels included, read as one passage. Each
 *  string with no end mark is treated as one sentence, as a reader takes it. */
/** @param {unknown[]} strings @returns {number | null} */
export function gradeOfScreen(strings) {
	const passage = strings
		.map((s) => String(s).replace(/\s+/g, ' ').trim())
		.filter((s) => words(s).length)
		.map((s) => (/[.!?]$/.test(s) ? s : s + '.'))
		.join(' ');
	return grade(passage);
}

/** Every string at or under the bar, or the ones over it with their grade. */
/** @param {string[]} strings @param {number} [bar] */
export function over(strings, bar = BAR) {
	return strings
		.map((text) => ({ text, grade: gradeOfString(text) }))
		.filter((r) => r.grade !== null && r.grade > bar);
}

/** @param {number} n */
function round(n) {
	return Math.round(n * 10) / 10;
}

// The self-test: a counter that drifts says so before it grades anything.
const KNOWN = {
	cat: 1, file: 1, files: 1, saved: 1, save: 1, change: 1, changed: 1, practice: 2, folder: 2, open: 2,
	project: 2, teacher: 2, version: 2, computer: 3, available: 4, little: 2, table: 2, checked: 1,
	everyone: 3, again: 2, simple: 2, people: 2, armory: 1, solidworks: 1, upload: 2, teammates: 2
};
/** @returns {string[]} */
export function selfTest() {
	/** @type {string[]} */
	const fails = [];
	for (const [w, n] of Object.entries(KNOWN)) if (syllables(w) !== n) fails.push(`${w}: counted ${syllables(w)}, expected ${n}`);
	// A first-grade passage stays low; a college sentence scores high.
	const easy = grade('The cat sat on the mat. The dog ran to the cat.');
	const hard = grade('Institutional accountability necessitates comprehensive documentation of organizational decision-making procedures.');
	if (easy === null || !(easy < 2)) fails.push('an easy passage graded ' + easy);
	if (hard === null || !(hard > 14)) fails.push('a hard sentence graded ' + hard);
	if (gradeOfString('Available') !== null) fails.push('a one-word label was graded on its own');
	if (sentences('One. Two words here! Three?').length !== 3) fails.push('sentence split');
	return fails;
}

if (import.meta.url === `file://${process.argv[1]}`) {
	const fails = selfTest();
	if (process.argv[2]) console.log(`grade ${grade(process.argv.slice(2).join(' '))} for: ${process.argv.slice(2).join(' ')}`);
	console.log(fails.length ? 'READING-LEVEL self-test FAILED\n' + fails.join('\n') : `READING-LEVEL self-test ok (names counted as one syllable: ${NAMES.join(', ')})`);
	process.exit(fails.length ? 1 : 0);
}
