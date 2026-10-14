import test from 'node:test';
import assert from 'node:assert/strict';
import { parseLcov, verifyChangedCoverage, resolveCoverageBase, parseApprovedInitialBaseline } from './coverage.mjs';
import fs from 'node:fs';
const file = 'frontend/src/events/a.js';
const record = hits => `SF:frontend\\src\\events\\a.js\nLF:10\nLH:${hits}\nFNF:2\nFNH:2\nend_of_record\n`;
test('initial branch push needs an approved earlier SHA, never current commit or missing baseline', () => {
  const zero = '0'.repeat(40), head = 'a'.repeat(40), base = 'b'.repeat(40);
  assert.throws(() => resolveCoverageBase(zero, undefined, head));
  assert.throws(() => resolveCoverageBase(zero, head, head));
  assert.throws(() => resolveCoverageBase(undefined, base, head));
  assert.throws(() => resolveCoverageBase(head, undefined, head));
  for (const invalid of ['', 'bad', 'f'.repeat(39)]) assert.throws(() => resolveCoverageBase(invalid, base, head));
  assert.throws(() => resolveCoverageBase(zero, zero, head));
  assert.equal(resolveCoverageBase(zero, base, head), base);
  assert.equal(resolveCoverageBase(base, undefined, head), base);
});

test('initial baseline metadata must contain an explicit SHA, measured scope and methodology', () => {
  for (const text of ['', '{}', '{', JSON.stringify({ sha: '0'.repeat(40), scope: 'frontend-executable-js', method: 'measure' }),
    JSON.stringify({ sha: 'b'.repeat(40), scope: 'all-code', method: 'measure' }),
    JSON.stringify({ sha: 'b'.repeat(40), scope: 'frontend-executable-js' })])
    assert.throws(() => parseApprovedInitialBaseline(text));
});

test('reviewed initial baseline is the measured earlier Delivery 02 develop commit', () => {
  const baseline = parseApprovedInitialBaseline(fs.readFileSync(new URL('./coverage-baseline.json', import.meta.url), 'utf8'));
  assert.equal(baseline, 'b41f67def33d78356c05b8ab9d3acb3b3f82aeaf');
  assert.equal(resolveCoverageBase('0'.repeat(40), baseline, '92c9d21cf9067c015535997ca0f7d9b12c823f15'), baseline);
});
test('changed JS comparison accepts preserved coverage and reports unmeasured JSX separately', () => {
  const result = verifyChangedCoverage(parseLcov(record(9)), parseLcov(record(10)), [file, 'frontend/src/pages/A.jsx']);
  assert.equal(result.measured.length, 1); assert.deepEqual(result.unmeasured, ['frontend/src/pages/A.jsx']);
});
test('coverage gate fails real regressions, missing artifacts and empty measurement', () => {
  assert.throws(() => verifyChangedCoverage(parseLcov(record(10)), parseLcov(record(9)), [file]), /regressed/);
  assert.throws(() => verifyChangedCoverage(new Map(), new Map(), [file]), /Missing/);
  assert.throws(() => verifyChangedCoverage(new Map(), new Map(), ['a.jsx']), /No changed/);
  assert.throws(() => verifyChangedCoverage(new Map(), parseLcov(record(8)), [file]), /regressed/);
});
