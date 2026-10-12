import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { openEvents } from '../src/events/publicEvents.js';
import { getEventStatusInfo, getShowtimeStatusInfo } from '../src/constants/eventStatus.js';

test('Home feed uses anonymous sales endpoint and preserves nearest showtime contract', async () => {
  const calls = [];
  const payload = { items: [{ eventId: 561, nearestShowtime: { id: 673 }, imageUrl: null }], nextCursor: null };
  const result = await openEvents({ get: async url => { calls.push(url); return { data: payload }; } });
  assert.deepEqual(calls, ['/events/open']);
  assert.equal(result, payload); assert.equal(result.items[0].nearestShowtime.id, 673);
});

test('public feed errors propagate; no fallback to management events', async () => {
  const calls = []; const failure = new Error('unavailable');
  await assert.rejects(openEvents({ get: async url => { calls.push(url); throw failure; } }), e => e === failure);
  assert.deepEqual(calls, ['/events/open']);
});

test('Published is publication, only OnSale showtimes receive a selling label', () => {
  assert.equal(getEventStatusInfo('Draft').label, 'Bản nháp');
  assert.equal(getEventStatusInfo('Published').label, 'Đã xuất bản');
  assert.equal(getShowtimeStatusInfo('OnSale').label, 'Đang mở bán');
  assert.equal(getShowtimeStatusInfo('Closed').label, 'Đóng bán');
});

test('Home integration retains Vietnamese text and uses showtime ID instead of event ID for booking', async () => {
  const source = await readFile(new URL('../src/pages/Home.jsx', import.meta.url), 'utf8');
  assert.match(source, /await getOpenEvents\(\)/); assert.doesNotMatch(source, /await getEvents\(\)/);
  assert.match(source, /Đang tải sự kiện/); assert.match(source, /Hiện chưa có sự kiện nào mở bán/);
  assert.match(source, /Đặt vé ngay/); assert.match(source, /Chưa có ảnh/);
  assert.doesNotMatch(source, /\uFFFD/);
  assert.match(source, /event.nearestShowtime.id/); assert.doesNotMatch(source, /nearestShowtime\?\.id \|\| event.id/);
});
