import test from 'node:test';
import assert from 'node:assert/strict';
import { parseLcov, verifyChangedCoverage, resolveCoverageBase } from './coverage.mjs';
const file = 'frontend/src/events/a.js';
const record = hits => `SF:frontend\\src\\events\\a.js\nLF:10\nLH:${hits}\nFNF:2\nFNH:2\nend_of_record\n`;
test('initial branch push needs an approved earlier SHA, never current commit or missing baseline', () => {
  const zero = '0'.repeat(40), head = 'a'.repeat(40), base = 'b'.repeat(40);
  assert.throws(() => resolveCoverageBase(zero, undefined, head));
  assert.throws(() => resolveCoverageBase(zero, head, head));
  assert.throws(() => resolveCoverageBase(undefined, base, head));
  assert.equal(resolveCoverageBase(zero, base, head), base);
  assert.equal(resolveCoverageBase(base, undefined, head), base);
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
