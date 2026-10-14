import test from 'node:test';
import assert from 'node:assert/strict';
import { seatClick } from '../src/events/seatClick.js';
import { createHoldRecovery } from '../src/events/holdRecovery.js';
import { seatPresentation } from '../src/events/seatPresentation.js';

test('default click holds exactly once; explicit batch mode queues without partial holds', () => {
  const actions = [], args = { seat: { id: 7, displayStatus: 'Available' }, pending: false,
    focus: s => actions.push(['focus', s.id]), hold: id => actions.push(['hold', id]),
    release: id => actions.push(['release', id]), toggle: s => actions.push(['queue', s.id]) };
  seatClick({ ...args, batchMode: false }); assert.deepEqual(actions, [['focus', 7], ['hold', 7]]);
  actions.length = 0; seatClick({ ...args, batchMode: true }); assert.deepEqual(actions, [['focus', 7], ['queue', 7]]);
  actions.length = 0; seatClick({ ...args, pending: true }); assert.deepEqual(actions, []);
});
test('click own hold releases; sold, foreign held and closed-sale available cannot hold', () => {
  const actions = [], args = { pending: false, focus() {}, hold: id => actions.push(['hold', id]), release: id => actions.push(['release', id]), toggle() {} };
  seatClick({ ...args, seat: { id: 2, displayStatus: 'HeldByMe' } });
  for (const displayStatus of ['Sold', 'HeldByOther']) seatClick({ ...args, seat: { id: 2, displayStatus } });
  seatClick({ ...args, seat: { id: 3, displayStatus: 'Available', canSelect: false } });
  assert.deepEqual(actions, [['release', 2]]);
  assert.equal(seatPresentation({ displayStatus: 'Available', canSelect: false }).selectable, false);
});
test('release uses one network request and exact authoritative remaining deadline', async () => {
  const deadline = '2030-10-10T12:10:00Z', stamp = '2030-10-10T12:00:00Z';
  const own = id => ({ id, row: 'A', number: String(id), heldUntilUtc: deadline });
  const session = heldSeats => ({ performanceId: 10, userId: 501, heldUntilUtc: deadline, serverTimeUtc: stamp, heldSeats });
  const calls = [], api = {
    seats: async () => { calls.push('GET chart'); return { serverTimeUtc: stamp, seats: [1, 2].map(id => ({ id, displayStatus: 'HeldByMe' })) }; },
    session: async () => { calls.push('GET session'); return session([own(1), own(2)]); },
    release: async () => { calls.push('DELETE'); return { success: true, session: session([own(2)]), chart: { serverTimeUtc: stamp,
      seats: [{ id: 1, displayStatus: 'Available' }, { id: 2, displayStatus: 'HeldByMe' }] } }; }
  };
  const c = createHoldRecovery({ showId: 10, api, readToken: () => 'fixture', now: () => 0, onChange() {} });
  await c.load(); calls.length = 0; assert.equal(await c.release(1), true);
  assert.deepEqual(calls, ['DELETE']); assert.equal(c.snapshot().session.heldUntilUtc, deadline);
  assert.deepEqual(c.snapshot().session.heldSeats.map(s => s.id), [2]);
  assert.equal(c.snapshot().data.seats[0].displayStatus, 'Available');
});
test('restricted API response never renders a stale chart or private session', async () => {
  const c = createHoldRecovery({ showId: 10, api: { seats: async () => { throw { response: { status: 403, data: { errorCode: 'SEAT_MAP_NOT_PUBLIC' } } }; } },
    readToken: () => null, onChange() {} });
  await c.load(); assert.deepEqual(c.snapshot().data.seats, []); assert.equal(c.snapshot().session, null);
  assert.equal(c.snapshot().remainingSeconds, 0);
});
