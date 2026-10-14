import test from 'node:test';
import assert from 'node:assert/strict';
import { salesApi, salesDisabledReason } from '../src/events/sales.js';

test('sales API preserves publish/unpublish and status request contracts', async () => {
  const requests = [];
  const client = { async put(...args) { requests.push(args); return { data: { status: args[1]?.newStatus || 'Published' } }; } };
  const api = salesApi(client);
  await api.publishEvent(10); await api.unpublishEvent(10);
  assert.deepEqual(await api.updateShowtimeSalesStatus(20, 'OnSale'), { status: 'OnSale' });
  assert.deepEqual(requests, [['/events/10/publish'], ['/events/10/unpublish'], ['/showtimes/20/status', { newStatus: 'OnSale' }]]);
});
test('open and reopen require published parent and a map; close remains available', () => {
  assert.match(salesDisabledReason({ status: 'Draft' }, { status: 'Draft', hasSeatMap: true }), /xuất bản/);
  assert.match(salesDisabledReason({ status: 'Published' }, { status: 'Closed', hasSeatMap: false }), /sơ đồ/);
  assert.equal(salesDisabledReason({ status: 'Published' }, { status: 'Closed', hasSeatMap: true }), '');
  assert.equal(salesDisabledReason({ status: 'Draft' }, { status: 'OnSale', hasSeatMap: false }), '');
});
