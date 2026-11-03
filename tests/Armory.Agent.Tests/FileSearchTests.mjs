// node --test tests/Armory.Agent.Tests/FileSearchTests.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import search from '../../src/Armory.Agent/wwwroot/file-search.js';

const files = [
    { name: 'Plate.SLDPRT', path: 'Robot/Drive/Plate.SLDPRT', checkout: 'mine', changed: true },
    { name: 'Gear.SLDASM', path: 'Robot/Drive/deep/Gear.SLDASM', checkout: 'other', changed: true },
    { name: 'Plate.slddrw', path: 'Robot/Drawings/Plate.slddrw', checkout: 'myOtherComputer', changed: false },
    { name: 'notes.txt', path: 'Robot/Drive/notes.txt', checkout: 'available', changed: false },
    { name: '新しい部品.SLDPRT', path: 'Robot/日本語/新しい部品.SLDPRT', checkout: 'available', changed: false }
];

for (const [text, expected] of [
    ['', [0, 1, 2, 3, 4]], ['   ', [0, 1, 2, 3, 4]], ['lAtE', [0, 2]], ['rive/de', [1]],
    ['DRIVE\\DEEP', [1]], ['  gear  ', [1]], ['新しい', [4]], ['日本語', [4]], ['missing', []], ['*', []], ['[', []]
]) test(`Literal substring in names or paths: ${JSON.stringify(text)}`, () => {
    assert.deepEqual(search.create(files).filter({ text }), expected);
});

for (const [name, text] of [
    ['Écrou.SLDPRT', 'E\u0301CROU'], ['E\u0301crou.SLDPRT', 'éCROU'], ['Пластина.SLDPRT', 'ПЛАСТИНА'],
    ['Δοκιμή.SLDPRT', 'ΔΟΚΙΜΉ'], ['οδος.SLDPRT', 'ΟΔΟΣ'], ['部品🔩.SLDPRT', '🔩'], ['𐐨.SLDPRT', '𐐀'],
    ['Straße.txt', 'STRASSE'], ['ﬃ.txt', 'FFI'], ['i.txt', 'I']
]) test(`Unicode names: ${name}`, () => {
    assert.deepEqual(search.create([{ name, path: 'Robot/' + name }]).filter({ text }), [0]);
});

test('Names and paths are both searched, accents stay significant and repeated queries are independent', () => {
    const index = search.create([{ name: 'different.SLDPRT', path: 'Robot/Écrou/FILE.SLDPRT' }]);
    assert.deepEqual(index.filter({ text: 'file' }), [0]);
    assert.deepEqual(index.filter({ text: 'e\u0301crou' }), [0]);
    assert.deepEqual(index.filter({ text: 'ecrou' }), []);
    assert.deepEqual(index.filter({ text: 'different' }), [0]);
    assert.deepEqual(index.filter(), [0]);
});

for (const [checkout, expected] of [[0, [0, 1, 2, 3, 4]], [1, [0, 2]], [2, [1]], [3, [0, 1, 2]]])
    test(`Checkout choices union, including my other computer: ${checkout}`, () => {
        assert.deepEqual(search.create(files).filter({ checkout }), expected);
    });

test('Changed locally intersects text, checkout and file type', () => {
    const index = search.create(files);
    assert.deepEqual(index.filter({ changed: true }), [0, 1]);
    assert.deepEqual(index.filter({ text: 'plate', checkout: 1, changed: true, type: 1 }), [0]);
    assert.deepEqual(index.filter({ text: 'plate', checkout: 2, changed: true, type: 1 }), []);
    assert.deepEqual(index.filter({ text: 'plate', checkout: 1, changed: true, type: 3 }), []);
    assert.deepEqual(index.filter({ text: 'deep', checkout: 2, changed: true, type: 2 }), [1]);
});

for (const [type, expected] of [[0, [0, 1, 2, 3, 4]], [1, [0, 4]], [2, [1]], [3, [2]], [4, [3]]])
    test(`File type ${type} preserves input order`, () => assert.deepEqual(search.create(files).filter({ type }), expected));

test('Other means every type except SolidWorks parts, assemblies and drawings', () => {
    for (const [name, type] of [
        ['part.SLDPRT', 1], ['assembly.sldasm', 2], ['drawing.SlDdRw', 3], ['template.drwdot', 4],
        ['drawing.dxf', 4], ['model.step', 4], ['model.eprt', 4], ['.sldprt', 4], ['README', 4],
        ['trailing.', 4], ['thing.unknown', 4], ['folder.sldprt/inside', 4], ['a.b.SLDASM', 2],
        ['folder\\inside.SLDPRT', 1], ['', 4], [null, 4]
    ]) assert.equal(search.typeOf(name), type, String(name));
    assert.deepEqual(search.Types, { any: 0, part: 1, assembly: 2, drawing: 3, other: 4 });
    assert.deepEqual(search.Checkouts, { any: 0, me: 1, others: 2 });
});

test('Every combination on an empty folder has zero total and zero matches', () => {
    const index = search.create([]);
    assert.equal(index.count, 0);
    for (const text of ['', 'part']) for (const checkout of [0, 1, 2, 3]) for (const changed of [false, true]) for (const type of [0, 1, 2, 3, 4])
        assert.deepEqual(index.filter({ text, checkout, changed, type }), []);
});

test('Deep paths and duplicate names retain stable indexes in a snapshot independent of input mutation', () => {
    const path = 'Robot/' + Array.from({ length: 150 }, (_, i) => 'level' + i).join('/');
    const input = [{ name: 'same.SLDPRT', path: path + '/same.SLDPRT' }, { name: 'same.SLDPRT', path: 'Robot/other/same.SLDPRT' }];
    const index = search.create(input);
    input[0].name = 'edited.txt';
    input.length = 0;
    assert.equal(index.count, 2);
    assert.deepEqual(index.filter({ text: 'same' }), [0, 1]);
    assert.deepEqual(index.filter({ text: 'level149/same' }), [0]);
    assert.deepEqual(index.filter({ text: 'other' }), [1]);
    assert.deepEqual(index.filter({}), [0, 1]);
    const options = Object.freeze({ text: 'same' });
    const result = index.filter(options);
    result.length = 0;
    assert.deepEqual(index.filter(options), [0, 1]);
});

test('Frozen inputs, missing optional values, punctuation and literal paths work without mutation', () => {
    const entries = Object.freeze([
        Object.freeze({ name: 'gear[1].SLDPRT', path: 'Robot/A.B/gear[1].SLDPRT' }),
        Object.freeze({ name: 'other.txt', path: 'Robot/AxB/other.txt' }),
        Object.freeze({ name: null, path: null })
    ]);
    const index = search.create(entries);
    assert.deepEqual(index.filter({ text: '[1]' }), [0]);
    assert.deepEqual(index.filter({ text: 'A.B' }), [0]);
    assert.deepEqual(index.filter({ changed: true }), []);
    assert.deepEqual(index.filter({ checkout: 3 }), []);
    assert.deepEqual(index.filter(), [0, 1, 2]);
});

test('Every combination agrees with individually known memberships', () => {
    const matches = { plate: [0, 2], drive: [0, 1, 3], 日本語: [4], '': [0, 1, 2, 3, 4] };
    const checkouts = [[0, 1, 2, 3, 4], [0, 2], [1], [0, 1, 2]];
    const types = [[0, 1, 2, 3, 4], [0, 4], [1], [2], [3]];
    const index = search.create(files);
    for (const [text, names] of Object.entries(matches)) for (const checkout of [0, 1, 2, 3]) for (const changed of [false, true]) for (const type of [0, 1, 2, 3, 4]) {
        const expected = names.filter(i => checkouts[checkout].includes(i) && (!changed || [0, 1].includes(i)) && types[type].includes(i));
        assert.deepEqual(index.filter({ text, checkout, changed, type }), expected, JSON.stringify({ text, checkout, changed, type }));
    }
});

test('5,000 files filter within the 100 ms interactive budget', t => {
    const files = Array.from({ length: 5000 }, (_, i) => ({ name: `Plate-${i}.SLDPRT`,
        path: `Robot/Drive/Stage-${i % 10}/Deep/Plate-${i}.SLDPRT`, checkout: i % 2 === 0 ? 'mine' : 'other', changed: i % 5 === 0 }));
    const start = performance.now();
    const index = search.create(files);
    const prepareMs = performance.now() - start;
    const options = { text: 'stage-0/deep', checkout: 1, changed: true, type: 1 };
    const expected = Array.from({ length: 500 }, (_, i) => i * 10);
    for (let i = 0; i < 20; i++) assert.deepEqual(index.filter(options), expected);
    const samples = Array.from({ length: 101 }, () => {
        const start = performance.now();
        const matches = index.filter(options);
        const ms = performance.now() - start;
        assert.deepEqual(matches, expected);
        return ms;
    }).sort((a, b) => a - b);
    t.diagnostic(`5,000 files, 101 warm filters: median ${samples[50].toFixed(3)} ms, p95 ${samples[95].toFixed(3)} ms, max ${samples[100].toFixed(3)} ms; prepare ${prepareMs.toFixed(3)} ms; ${process.version}, ${process.platform}/${process.arch}.`);
    assert.ok(samples[95] < 100, `p95 ${samples[95]} ms exceeds 100 ms`);
    assert.equal(index.filter().length, 5000);
    assert.deepEqual(index.filter({ text: 'nothing here' }), []);
});
