import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const context = { window: {} };
vm.runInNewContext(fs.readFileSync(new URL('../../src/Armory.Agent/wwwroot/file-order.js', import.meta.url), 'utf8'), context);
const rows = [
  { name: 'Part-10', path: 'A/Part-10', updatedAt: '2026-11-06T10:00:00Z' },
  { name: 'Part-2', path: 'A/Part-2', updatedAt: '2026-11-06T10:00:00Z' },
  { name: 'Old', path: 'A/Old', updatedAt: '2026-11-01T10:00:00Z' },
  { name: 'Unknown', path: 'A/Unknown', updatedAt: null }
];
const order = mode => Array.from(context.window.ArmoryFileOrder(rows, mode), r => r.name);
assert.deepEqual(order('newest'), ['Part-2', 'Part-10', 'Old', 'Unknown']);
assert.deepEqual(order('oldest'), ['Old', 'Part-2', 'Part-10', 'Unknown']);
assert.deepEqual(order('name'), ['Old', 'Part-2', 'Part-10', 'Unknown']);
assert.deepEqual(order('nameDesc'), ['Unknown', 'Part-10', 'Part-2', 'Old']);
assert.deepEqual(rows.map(r => r.name), ['Part-10', 'Part-2', 'Old', 'Unknown']);
// 0.6.1: by file type, with the caller's group for each row, names in order inside a group.
const typed = [
  { name: 'Wheel.SLDASM', path: 'A/Wheel.SLDASM' }, { name: 'Notes.txt', path: 'A/Notes.txt' },
  { name: 'Axle-10.SLDPRT', path: 'A/Axle-10.SLDPRT' }, { name: 'Axle-2.SLDPRT', path: 'A/Axle-2.SLDPRT' },
  { name: 'Plate.SLDDRW', path: 'A/Plate.SLDDRW' }, { name: 'Bracket.stl', path: 'A/Bracket.stl' }
];
const group = r => ({ SLDPRT: '1', SLDASM: '2', SLDDRW: '3', stl: '4Mesh', txt: '4Text' })[r.name.split('.').pop()];
assert.deepEqual(Array.from(context.window.ArmoryFileOrder(typed, 'type', group), r => r.name),
  ['Axle-2.SLDPRT', 'Axle-10.SLDPRT', 'Wheel.SLDASM', 'Plate.SLDDRW', 'Bracket.stl', 'Notes.txt']);
// Without a group, File type is Name.
assert.deepEqual(Array.from(context.window.ArmoryFileOrder(typed, 'type'), r => r.name),
  Array.from(context.window.ArmoryFileOrder(typed, 'name'), r => r.name));
console.log('PASS: dates sort both ways, names both ways, file types group, ties use natural names, missing dates stay last, source rows stay unchanged.');
