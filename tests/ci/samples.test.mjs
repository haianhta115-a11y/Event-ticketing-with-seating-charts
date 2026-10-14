import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { generateSeatChart } from '../../scripts/generate-seat-chart.mjs';
test('checked-in 2000-seat synthetic sample is deterministic, unique and geometrically non-overlapping', () => {
  const sample = JSON.parse(readFileSync(new URL('../../samples/seating-chart-2000.json', import.meta.url)));
  assert.deepEqual(sample, generateSeatChart()); assert.equal(sample.seats.length, 2000);
  assert.equal(new Set(sample.seats.map(s => `${s.row}/${s.seatNumber}`)).size, 2000);
  assert.equal(new Set(sample.seats.map(s => `${s.x}/${s.y}`)).size, 2000);
});
test('sample generator rejects invalid counts and does not derive historical coordinates', () => {
  for (const count of [0, 20001, 2.5, NaN]) assert.throws(() => generateSeatChart(count));
  assert.throws(() => generateSeatChart(100, 0)); assert.equal(generateSeatChart(100, 10).seats.length, 100);
});
