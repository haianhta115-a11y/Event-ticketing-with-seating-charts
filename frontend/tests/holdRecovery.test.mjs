import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHoldRecovery, serverClock, sessionFromServer } from '../src/events/holdRecovery.js';

const epoch = Date.parse('2030-10-10T12:00:00Z');
const iso = seconds => new Date(epoch + seconds * 1000).toISOString();
const session = (seconds = 360) => ({ performanceId: 10, userId: 501, serverTimeUtc: iso(0), heldUntilUtc: iso(seconds), remainingSeconds: seconds,
  heldSeats: [1, 2].map(id => ({ id, row: 'A', number: `${id}`, heldUntilUtc: iso(seconds) })) });
const chart = () => ({ serverTimeUtc: iso(0), seats: [1, 2, 3].map(id => ({ id, displayStatus: id === 3 ? 'HeldByOther' : 'HeldByMe', heldUntilUtc: iso(360), canSelect: false })) });
const rejected = status => Object.assign(new Error('request rejected'), { response: { status } });

function fixture(raw = session()) {
  let monotonic = 0, token = 'signed-user-A'; const calls = [], updates = [];
  const api = {
    async seats(id) { calls.push(['seats', id]); return chart(); },
    async session(id) { calls.push(['session', id]); return raw; },
    async hold(...args) { calls.push(['hold', ...args]); return { heldUntilUtc: raw.heldUntilUtc, serverTimeUtc: raw.serverTimeUtc }; },
  };
  const make = () => createHoldRecovery({ showId: 10, api, readToken: () => token, now: () => monotonic, onChange: s => updates.push(s) });
  const controller = make();
  return { api, controller, make, calls, updates, advance: ms => { monotonic += ms; }, token: value => { token = value; } };
}

test('reloading on a fresh controller recovers two server-owned seats and six minutes without POST', async () => {
  const f = fixture(); await f.controller.load(); f.controller.dispose();
  const reopened = f.make(); await reopened.load();
  assert.equal(reopened.snapshot().remainingSeconds, 360);
  assert.equal(reopened.snapshot().session.heldUntilUtc, iso(360));
  assert.deepEqual(reopened.snapshot().session.heldSeats.map(s => s.id), [1, 2]);
  assert.equal(reopened.snapshot().data.seats[2].displayStatus, 'HeldByOther');
  assert.equal(f.calls.some(c => c[0] === 'hold'), false);
});

test('old routed clock reproduces 60/660 seconds for +/-5m; server monotonic clock remains 360', () => {
  assert.equal(Math.floor((epoch + 360000 - (epoch + 300000)) / 1000), 60);
  assert.equal(Math.floor((epoch + 360000 - (epoch - 300000)) / 1000), 660);
  let monotonic = 0;
  const clock = serverClock(iso(0), 0, 0, () => monotonic);
  assert.equal(Math.ceil((epoch + 360000 - clock()) / 1000), 360);
  monotonic = 120000;
  assert.equal(Math.ceil((epoch + 360000 - clock()) / 1000), 240);
});

test('RTT midpoint accounts for response latency without changing the original deadline', () => {
  let monotonic = 1000;
  const clock = serverClock(iso(0), 0, 1000, () => monotonic);
  assert.equal(clock(), epoch + 500);
  const recovered = sessionFromServer(session(), 10, clock);
  assert.equal(recovered.deadline, epoch + 360000);
  monotonic = 2000;
  assert.equal(clock(), epoch + 1500);
});

test('expired recovery has no countdown and rereads availability rather than extending', async () => {
  const f = fixture(session(-1));
  f.api.seats = async id => { f.calls.push(['seats', id]); return { serverTimeUtc: iso(0), seats: [{ id: 1, displayStatus: 'Available', canSelect: true }] }; };
  await f.controller.load();
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.equal(f.controller.snapshot().data.seats[0].displayStatus, 'Available');
  assert.equal(f.calls.filter(c => c[0] === 'seats').length, 2);
  assert.equal(f.calls.some(c => c[0] === 'hold'), false);
});

test('404 is normal no-session; 401/403 clear own hold details and do not recover another user', async () => {
  for (const status of [404, 401, 403]) {
    const f = fixture(); f.api.session = async () => { throw rejected(status); };
    await f.controller.load();
    assert.equal(f.controller.snapshot().session, null);
    assert.equal(f.controller.snapshot().remainingSeconds, 0);
    if (status === 404) assert.equal(f.controller.snapshot().error, null);
    else assert.ok(f.controller.snapshot().error);
  }
});

test('logout discards recovery arriving after token changed and clears previously recovered state', async () => {
  const f = fixture(); await f.controller.load();
  let finish; f.api.session = () => new Promise(resolve => { finish = resolve; });
  const loading = f.controller.load(); await Promise.resolve();
  f.token(null); f.controller.tick();
  finish(session()); await loading; await Promise.resolve();
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.equal(f.controller.snapshot().data.seats.some(s => s.displayStatus === 'HeldByMe'), false);
});

test('user switch discards old response and resolves only the new claims-scoped my-hold', async () => {
  const f = fixture(); let finish;
  f.api.session = () => new Promise(resolve => { finish = resolve; });
  const old = f.controller.load(); await Promise.resolve();
  f.token('signed-user-B'); f.api.session = async () => { throw rejected(404); };
  await f.controller.load(); finish(session()); await old;
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.calls.every(c => c.length === 2), true, 'no userId override in recovery calls');
});

test('expiry after background time removes local session then refreshes once', async () => {
  const f = fixture(); await f.controller.load();
  f.api.session = async () => { throw rejected(404); };
  f.api.seats = async () => ({ serverTimeUtc: iso(400), seats: [{ id: 1, displayStatus: 'Available', canSelect: true }] });
  f.advance(400000); f.controller.tick();
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.controller.snapshot().data.seats[0].displayStatus, 'Available');
  const before = f.calls.length; f.controller.tick(); f.controller.tick();
  assert.equal(f.calls.length, before);
});

test('focus/visibility recovery recalibrates and never POSTs a renewal', async () => {
  const f = fixture(); await f.controller.load(); f.advance(240000);
  f.api.session = async () => ({ ...session(), serverTimeUtc: iso(240), remainingSeconds: 120 });
  await f.controller.load();
  assert.equal(f.controller.snapshot().remainingSeconds, 120);
  assert.equal(f.controller.snapshot().session.heldUntilUtc, iso(360));
  assert.equal(f.calls.some(c => c[0] === 'hold'), false);
});

test('invalid timestamps/wrong showtime/inconsistent deadlines never synthesize a session', async () => {
  for (const raw of [{ ...session(), performanceId: 11 }, { ...session(), heldUntilUtc: null },
    { ...session(), serverTimeUtc: null }, { ...session(), heldSeats: [{ id: 1, heldUntilUtc: iso(500) }] },
    { ...session(), heldSeats: [session().heldSeats[0], session().heldSeats[0]] }]) {
    const f = fixture(raw); await f.controller.load();
    assert.equal(f.controller.snapshot().session, null); assert.ok(f.controller.snapshot().error);
  }
});

test('conflict refreshes authoritative chart, preserves existing own deadline and shows server reason', async () => {
  const f = fixture(); await f.controller.load();
  f.api.hold = async () => { throw { response: { status: 409, data: { message: 'ghế vừa có người chọn' } } }; };
  await f.controller.hold(3);
  assert.equal(f.controller.snapshot().notice, 'ghế vừa có người chọn');
  assert.equal(f.controller.snapshot().session.heldUntilUtc, iso(360));
  assert.equal(f.controller.snapshot().data.seats[2].displayStatus, 'HeldByOther');
});

test('disposing a route ignores late requests and prevents late countdown updates', async () => {
  const f = fixture(); let finish;
  f.api.seats = () => new Promise(resolve => { finish = resolve; });
  const loading = f.controller.load(); const updates = f.updates.length;
  f.controller.dispose(); finish(chart()); await loading; f.controller.tick();
  assert.equal(f.updates.length, updates);
});

test('holding uses one explicit POST and recovery GETs without extending deadline', async () => {
  const f = fixture(); await f.controller.load(); await f.controller.hold(1);
  assert.deepEqual(f.calls.filter(c => c[0] === 'hold'), [['hold', 10, 1]]);
  assert.equal(f.controller.snapshot().session.heldUntilUtc, iso(360));
  const anonymous = fixture(); anonymous.token(null); await anonymous.controller.hold(1);
  assert.equal(anonymous.calls.some(c => c[0] === 'hold'), false);
  assert.equal(anonymous.controller.snapshot().session, null);
});

test('incomplete POST response cannot create a local ten-minute deadline', async () => {
  const f = fixture(); f.api.hold = async () => ({});
  f.api.session = async () => { throw rejected(404); };
  await f.controller.hold(1);
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.ok(f.controller.snapshot().notice);
});

test('uncertain recovery after network failure removes private hold state and requires refresh', async () => {
  const f = fixture(); await f.controller.load(); f.api.session = async () => { throw new Error('offline'); };
  await f.controller.load();
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().remainingSeconds, 0);
  assert.equal(f.controller.snapshot().data.seats.some(s => s.displayStatus === 'HeldByMe'), false);
  assert.ok(f.controller.snapshot().error);
});

test('logout during an outstanding hold discards its response and refreshes anonymous availability', async () => {
  const f = fixture(); await f.controller.load(); let finish;
  f.api.hold = () => new Promise(resolve => { finish = resolve; });
  const holding = f.controller.hold(1);
  f.token(null); f.controller.tick();
  finish({ heldUntilUtc: iso(360), serverTimeUtc: iso(0) }); await holding;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.controller.snapshot().session, null);
  assert.equal(f.controller.snapshot().pending, false);
  assert.equal(f.controller.snapshot().data.seats.some(s => s.displayStatus === 'HeldByMe'), false);
});

test('the real routed SeatMap is wired to recovery and has no Date.now deadline fallback', async () => {
  const source = await readFile(new URL('../src/pages/SeatMap.jsx', import.meta.url), 'utf8');
  const router = await readFile(new URL('../src/router.jsx', import.meta.url), 'utf8');
  assert.match(router, /path: 'shows\/:id\/seats',[\s\S]*?element: <SeatMap/);
  assert.match(source, /useHoldRecovery\(id\)/);
  assert.match(source, /data-testid="hold-session"/);
  assert.doesNotMatch(source, /Date\.now\(\)|currentTime|getShowtimeSeats\(id\)/);
});
