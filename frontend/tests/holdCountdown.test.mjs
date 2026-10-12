import test from 'node:test';
import assert from 'node:assert/strict';
import { startHoldCountdown } from '../src/events/holdCountdown.js';

function fixture(offset = 0) {
  let now = 100000, callback, expirations = 0, clears = 0, schedules = 0;
  const ticks = [];
  const stop = startHoldCountdown({ expiresAtUtc: new Date(110000).toISOString(), serverTimeOffsetMs: offset,
    onTick: value => ticks.push(value), onExpired: () => expirations++ }, {
    now: () => now,
    setInterval: (fn, interval) => { assert.equal(interval, 1000); callback = fn; schedules++; return 7; },
    clearInterval: id => { assert.equal(id, 7); clears++; },
  });
  return { stop, tick: time => { now = time; callback(); }, ticks,
    state: () => ({ expirations, clears, schedules }) };
}

test('countdown uses the fixed server deadline, including client clock skew', () => {
  const timer = fixture(-300000);
  timer.tick(400000); timer.tick(401500);
  assert.deepEqual(timer.ticks, [10, 8]);
  assert.equal(timer.state().schedules, 1, 'positive ticks never recreate the interval');
  timer.stop();
});

test('deadline expiry clamps at zero, refreshes once and stops subsequent callbacks', () => {
  const timer = fixture();
  timer.tick(109001); timer.tick(111000); timer.tick(120000);
  assert.deepEqual(timer.ticks, [0]);
  assert.deepEqual(timer.state(), { expirations: 1, clears: 1, schedules: 1 });
});

test('release/unmount cleanup is idempotent and cannot expire a new session', () => {
  const timer = fixture();
  timer.tick(101000); timer.stop(); timer.stop(); timer.tick(120000);
  assert.deepEqual(timer.ticks, [9]);
  assert.deepEqual(timer.state(), { expirations: 0, clears: 1, schedules: 1 });
  const next = fixture(); next.tick(102000);
  assert.deepEqual(next.ticks, [8]); next.stop();
});
