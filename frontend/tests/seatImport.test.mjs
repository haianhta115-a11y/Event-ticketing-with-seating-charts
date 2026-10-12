import test from 'node:test';
import assert from 'node:assert/strict';
import { importChart, importMessage } from '../src/events/seatImport.js';

test('chart upload sends the file with the real multipart import route', async () => {
  const file = new File(['{"layoutName":"Fixture"}'], 'chart.json', { type: 'application/json' });
  let call;
  const result = await importChart({ post: async (...args) => { call = args; return { data: { success: true, seatCount: 1 } }; } }, 10, file);
  assert.equal(call[0], '/showtimes/10/seats/import');
  assert.equal(call[1].get('file').name, 'chart.json');
  assert.equal(result.seatCount, 1);
});

test('blocked replacement and field validation messages are visible to the organizer', () => {
  assert.equal(importMessage({ response: { status: 409, data: { message: 'Có ghế đang giữ.' } } }), 'Có ghế đang giữ.');
  assert.match(importMessage({ response: { status: 400, data: { errors: [{ message: 'Ghế A-1 trùng.' }] } } }), /A-1 trùng/);
  assert.match(importMessage({ response: { status: 400, data: { errors: ['seats[1] duplicates A-1'] } } }), /duplicates/);
  assert.match(importMessage({ response: { status: 403 } }), /quyền/);
});

test('persistence failures are propagated and never interpreted as success', async () => {
  const error = { response: { status: 500, data: { message: 'Dữ liệu trước đó được giữ nguyên.' } } };
  await assert.rejects(importChart({ post: async () => { throw error; } }, 10, new File(['{}'], 'chart.json')), e => e === error);
  assert.match(importMessage(error), /giữ nguyên/);
});
