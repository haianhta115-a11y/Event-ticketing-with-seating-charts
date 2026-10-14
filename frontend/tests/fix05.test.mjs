import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHoldRecovery } from '../src/events/holdRecovery.js';
import { createPublicFeed, openEvents } from '../src/events/publicEvents.js';

test('the routed SeatMap exposes releasing server-owned holds, not just pending picks', async () => {
  const screen = await readFile(new URL('../src/pages/SeatMap.jsx', import.meta.url), 'utf8');
  const hook = await readFile(new URL('../src/events/useHoldRecovery.js', import.meta.url), 'utf8');
  assert.match(screen, /confirmRelease/);
  assert.match(hook, /release: releaseSeat/);
});

test('Home renders the nearest showtime and observes the pagination sentinel', async () => {
  const screen = await readFile(new URL('../src/pages/Home.jsx', import.meta.url), 'utf8');
  assert.match(screen, /formatVietnamTime\(event.nearestShowtime.startTime\)/);
  assert.match(screen, /IntersectionObserver/);
  assert.match(screen, /nextCursor/);
});

test('pointer-up selection is not overwritten by a synthesized touch click; keyboard selection remains', async () => {
  const screen = await readFile(new URL('../src/pages/SeatMap.jsx', import.meta.url), 'utf8');
  assert.match(screen, /onClick=\{event => \{ if \(event.detail === 0\) select\(\); \}\}/);
  assert.match(screen, /onKeyDown=.*event.key === 'Enter'/);
  assert.match(screen, /const handlePointerUp = \(e\) => \{[\s\S]*?if \(!activePointers.current.has\(e.pointerId\)\) return;/);
  assert.match(screen, /seats.some\(s => !Number.isFinite\(s.x\) \|\| !Number.isFinite\(s.y\)\)/);
});

const stamp = '2030-10-10T12:00:00Z', deadline = '2030-10-10T12:06:00Z';
function recoveryFixture() {
  let token = 'buyer', ids = [1, 2, 3]; const calls = [];
  const api = {
    seats: async () => ({ serverTimeUtc: stamp, hasSeatMap: true, seats: [1, 2, 3, 4].map(id =>
      ({ id, displayStatus: ids.includes(id) ? 'HeldByMe' : 'Available', heldUntilUtc: ids.includes(id) ? deadline : null })) }),
    session: async () => {
      if (!ids.length) throw Object.assign(new Error('No hold'), { response: { status: 404 } });
      return { performanceId: 10, userId: 101, serverTimeUtc: stamp, heldUntilUtc: deadline,
        heldSeats: ids.map(id => ({ id, heldUntilUtc: deadline })) };
    },
    release: async (show, id) => { calls.push([show, id]); ids = ids.filter(value => value !== id); return { success: true }; },
  };
  const controller = createHoldRecovery({ showId: 10, api, readToken: () => token, now: () => 0, onChange() {} });
  return { controller, api, calls, logout: () => { token = null; } };
}

test('release preserves remaining holds and deadline; last release removes countdown', async () => {
  const f = recoveryFixture(); await f.controller.load();
  assert.equal(await f.controller.release(1), true);
  assert.deepEqual(f.controller.snapshot().session.heldSeats.map(s => s.id), [2, 3]);
  assert.equal(f.controller.snapshot().session.heldUntilUtc, deadline);
  assert.equal(f.controller.snapshot().data.seats[0].displayStatus, 'Available');
  await f.controller.release(2); await f.controller.release(3);
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.deepEqual(f.calls, [[10, 1], [10, 2], [10, 3]]);
});

test('recovery refuses releasing a seat outside its authoritative session', async () => {
  const f = recoveryFixture(); await f.controller.load();
  assert.equal(await f.controller.release(4), false);
  assert.equal(f.calls.length, 0);
});

test('failed release never guesses availability or changes the server deadline', async () => {
  const f = recoveryFixture(); await f.controller.load();
  f.api.release = async () => { throw Object.assign(new Error('Forbidden'), { response: { status: 403 } }); };
  assert.equal(await f.controller.release(1), false);
  assert.equal(f.controller.snapshot().data.seats[0].displayStatus, 'HeldByMe');
  assert.equal(f.controller.snapshot().session.heldUntilUtc, deadline);
  assert.match(f.controller.snapshot().notice, /quyền/);
});

test('logout while release is pending cannot restore the old private session', async () => {
  const f = recoveryFixture(); await f.controller.load(); let finish;
  f.api.release = () => new Promise(resolve => { finish = resolve; });
  const operation = f.controller.release(1); f.logout(); finish({ success: true }); await operation;
  await f.controller.load();
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.equal(f.controller.snapshot().data.seats.some(s => s.displayStatus === 'HeldByMe'), false);
});

test('cursor requests use encoded Axios params, append uniquely, and stop at the last page', async () => {
  const calls = [];
  await openEvents({ get: async (...args) => { calls.push(args); return { data: {} }; } }, 'cursor+/=');
  assert.deepEqual(calls, [['/events/open', { params: { cursor: 'cursor+/=' } }]]);
  const pages = [];
  const feed = createPublicFeed({ onChange() {}, page: async cursor => {
    pages.push(cursor); return cursor ? { items: [{ eventId: 2 }, { eventId: 3 }], nextCursor: null }
      : { items: [{ eventId: 1 }, { eventId: 2 }], nextCursor: 'next' };
  } });
  await feed.loadNext(); await feed.loadNext(); await feed.loadNext();
  assert.deepEqual(feed.snapshot().items.map(e => e.eventId), [1, 2, 3]);
  assert.deepEqual(pages, [null, 'next']);
});

test('pagination guards simultaneous requests and ignores results after unmount', async () => {
  let finish, calls = 0;
  const feed = createPublicFeed({ onChange() {}, page: () => { calls++; return new Promise(resolve => { finish = resolve; }); } });
  const first = feed.loadNext(); await feed.loadNext(); assert.equal(calls, 1);
  feed.dispose(); finish({ items: [{ eventId: 1 }], nextCursor: null }); await first;
  assert.deepEqual(feed.snapshot().items, []);
});

test('failed next page keeps previous cards and retries the same cursor, never management data', async () => {
  let calls = 0; const cursors = [];
  const feed = createPublicFeed({ onChange() {}, page: async cursor => {
    cursors.push(cursor); calls++;
    if (calls === 1) return { items: [{ eventId: 1 }], nextCursor: 'next' };
    if (calls === 2) throw new Error('offline');
    return { items: [{ eventId: 2 }], nextCursor: null };
  } });
  await feed.loadNext(); await feed.loadNext();
  assert.deepEqual(feed.snapshot().items.map(e => e.eventId), [1]); assert.equal(feed.snapshot().error, 'offline');
  await feed.loadNext(); assert.deepEqual(cursors, [null, 'next', 'next']);
  assert.deepEqual(feed.snapshot().items.map(e => e.eventId), [1, 2]);
});

test('missing showtime reports a resource error, while no-session 404 is not an error', async () => {
  const f = recoveryFixture(); f.api.seats = async () => { throw Object.assign(new Error('Missing'), { response: { status: 404 } }); };
  await f.controller.load(); assert.equal(f.controller.snapshot().error, 'Suất diễn không tồn tại.');
  assert.equal(f.controller.snapshot().session, null);
});

test('network-uncertain and unauthorized releases never mark a held seat available locally', async () => {
  for (const status of [undefined, 401]) {
    const f = recoveryFixture(); await f.controller.load();
    f.api.release = async () => { throw Object.assign(new Error('uncertain'), { response: { status } }); };
    assert.equal(await f.controller.release(1), false);
    assert.equal(f.controller.snapshot().data.seats[0].displayStatus, 'HeldByMe');
    assert.equal(f.controller.snapshot().session.heldUntilUtc, deadline);
  }
});
