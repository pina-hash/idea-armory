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
assert.deepEqual(rows.map(r => r.name), ['Part-10', 'Part-2', 'Old', 'Unknown']);
console.log('PASS: dates sort both ways, ties use natural names, missing dates stay last, source rows stay unchanged.');
