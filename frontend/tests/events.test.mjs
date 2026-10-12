import test from 'node:test';
import assert from 'node:assert/strict';
import { validateEvent, validateShowtime, vietnamToUtc, formatVietnamTime, apiErrors, apiMessage, submissionGate, managementApi } from '../src/events/management.js';

test('event validation requires each field and respects database limits', () => {
  assert.deepEqual(Object.keys(validateEvent({ name: ' ', description: '', location: '' })), ['name', 'description', 'location']);
  assert.deepEqual(validateEvent({ name: 'Concert', description: 'Details', location: 'Venue' }), {});
  assert.deepEqual(Object.keys(validateEvent({ name: 'a'.repeat(201), description: 'Details', location: 'b'.repeat(301) })), ['name', 'location']);
});

test('Vietnam datetime-local converts to UTC irrespective of system timezone', () => {
  for (const tz of ['UTC', 'America/New_York', 'Asia/Tokyo']) {
    const previous = process.env.TZ;
    try { process.env.TZ = tz; assert.equal(vietnamToUtc('2030-10-10T19:00'), '2030-10-10T12:00:00.000Z'); }
    finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous; }
  }
  assert.throws(() => vietnamToUtc('bad'));
});

test('UTC display always uses Vietnam time including date rollover', () => {
  const text = formatVietnamTime('2030-10-10T20:00:00Z');
  assert.match(text, /03:00/);
  assert.match(text, /11\/10\/2030/);
});

test('showtime field validation rejects past, invalid calendar and inverted intervals', () => {
  const now = Date.parse('2030-10-10T12:00:00Z');
  assert.deepEqual(Object.keys(validateShowtime('2030-10-10T18:00', '', now)), ['startTime']);
  assert.deepEqual(Object.keys(validateShowtime('2030-10-10T19:01', '2030-10-10T18:00', now)), ['endTime']);
  assert.deepEqual(Object.keys(validateShowtime('2030-02-30T19:00', '', now)), ['startTime']);
  assert.deepEqual(validateShowtime('2030-10-10T19:01', '', now), {});
});

test('server field errors are assigned next to matching inputs', () => {
  assert.deepEqual(apiErrors({ response: { data: { errors: { Name: ['Required'], StartTime: ['Past'] } } } }), { name: 'Required', startTime: 'Past' });
});

test('authorization and missing resource errors remain distinguishable', () => {
  assert.match(apiMessage({ response: { status: 403 } }), /quyền/);
  assert.match(apiMessage({ response: { status: 401 } }), /hết hạn/);
  assert.match(apiMessage({ response: { status: 404 } }), /Không tìm thấy/);
});

test('same-tick submits are blocked; retry becomes possible after release', () => {
  const gate = submissionGate();
  assert.equal(gate.enter(), true);
  assert.equal(gate.enter(), false);
  gate.leave();
  assert.equal(gate.enter(), true);
});

test('management workflows call the protected backend routes with unchanged payloads', async () => {
  const calls = [];
  const client = Object.fromEntries(['get', 'post', 'put'].map(method => [method, async (...args) => { calls.push([method, ...args]); return { data: { id: 7 } }; }]));
  const api = managementApi(client);
  const event = { name: 'Concert', description: 'Details', location: 'Venue' };
  const slot = { startTime: vietnamToUtc('2030-10-10T19:00'), endTime: null };
  await api.getEvents(); await api.getEventById(7); await api.createEvent(event); await api.updateEvent(7, event);
  await api.getShowtimes(7); await api.createShowtime(7, slot);
  assert.deepEqual(calls, [['get', '/events/admin'], ['get', '/events/7'], ['post', '/events', event], ['put', '/events/7', event], ['get', '/events/7/showtimes'], ['post', '/events/7/showtimes', slot]]);
});

test('API rejects errors to caller rather than pretending to save', async () => {
  const failure = { response: { status: 403 } };
  const api = managementApi({ put: async () => { throw failure; } });
  await assert.rejects(api.updateEvent(7, {}), error => error === failure);
});
