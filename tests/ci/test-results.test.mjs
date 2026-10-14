import test from 'node:test';
import assert from 'node:assert/strict';
import { phases, verifyCounters } from './run-backend-tests.mjs';
const counters = patch => '<Counters ' + Object.entries({ total: 1, executed: 1, passed: 1, failed: 0, notExecuted: 0, error: 0, aborted: 0, ...patch }).map(([key, value]) => `${key}="${value}"`).join(' ') + ' />';
test('CI runs API, both browser workflows and latency sequentially, not zero-match green tests', () => {
  assert.equal(phases.length, 5); assert.equal(phases[0][2], 367);
  assert.equal(phases[1][0], 'concurrency'); assert.equal(phases[1][3], 10);
  assert.equal(verifyCounters(counters(), 1), 1);
});
for (const [name, patch] of Object.entries({ 'zero match': { total: 0, executed: 0, passed: 0 }, failed: { failed: 1, passed: 0 },
  skipped: { notExecuted: 1, executed: 0, passed: 0 }, error: { error: 1 }, aborted: { aborted: 1 } }))
  test(`TRX guard rejects ${name}`, () => assert.throws(() => verifyCounters(counters(patch), 1)));
test('missing counters and fewer than all required API cases fail closed', () => {
  assert.throws(() => verifyCounters('<TestRun/>', 1));
  assert.throws(() => verifyCounters(counters({ total: 366, executed: 366, passed: 366 }), phases[0][2]));
});
