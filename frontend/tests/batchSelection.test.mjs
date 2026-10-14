import test from 'node:test';
import assert from 'node:assert/strict';
import { toggleBatch, batchHoldApi } from '../src/events/batchSelection.js';
import { createHoldRecovery } from '../src/events/holdRecovery.js';

test('multi selection toggles without duplicates and excludes unavailable seats', () => {
  let ids = [];
  for (let id = 1; id <= 5; id++) ids = toggleBatch(ids, { id, displayStatus: 'Available', canSelect: true });
  assert.deepEqual(ids, [1, 2, 3, 4, 5]);
  assert.deepEqual(toggleBatch(ids, { id: 3, displayStatus: 'Available' }), [1, 2, 4, 5]);
  assert.deepEqual(toggleBatch(ids, { id: 6, displayStatus: 'HeldByOther' }), ids);
});
test('one batch request carries only seat IDs, never a spoofable owner', async () => {
  const calls = [];
  const data = await batchHoldApi({ post: async (...args) => { calls.push(args); return { data: { success: true } }; } }, 10, [1, 2, 3]);
  assert.deepEqual(calls, [['/showtimes/10/seats/hold', { seatIds: [1, 2, 3] }]]);
  assert.equal(data.success, true);
});
test('batch conflict displays only requested rejected IDs and refreshes server-owned session', async () => {
  const sent = [], deadline = '2030-10-10T12:10:00Z';
  const api = {
    seats: async () => ({ serverTimeUtc: '2030-10-10T12:00:00Z', seats: [1, 2, 3].map(id => ({ id, displayStatus: id === 3 ? 'HeldByOther' : 'Available' })) }),
    session: async () => ({ performanceId: 10, userId: 501, serverTimeUtc: '2030-10-10T12:00:00Z', heldUntilUtc: deadline,
      heldSeats: [{ id: 1, row: 'A', number: '1', heldUntilUtc: deadline }] }),
    batch: async (...args) => { sent.push(args); throw { response: { status: 409, data: { message: 'ghế vừa có người chọn', rejectedSeatIds: [3, 999] } } }; },
  };
  const controller = createHoldRecovery({ showId: 10, api, readToken: () => 'signed fixture', now: () => 0, onChange: () => {} });
  await controller.load(); assert.equal(await controller.hold([2, 3]), false);
  assert.deepEqual(sent, [[10, [2, 3]]]);
  assert.match(controller.snapshot().notice, /Ghế bị từ chối: 3/);
  assert.doesNotMatch(controller.snapshot().notice, /999/);
  assert.equal(controller.snapshot().session.heldUntilUtc, deadline);
  assert.equal(controller.snapshot().data.seats[2].displayStatus, 'HeldByOther');
});
