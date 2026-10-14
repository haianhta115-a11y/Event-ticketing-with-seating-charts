import test from 'node:test';
import assert from 'node:assert/strict';
import { getSession, hasAnyRole, clearSession } from '../src/auth/session.js';

const storage = () => {
  const values = new Map();
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
};
const token = claims => `e30.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.signature`;

test('anonymous, malformed, missing-exp and expired sessions are denied', () => {
  const store = storage();
  assert.equal(getSession(store, 1000), null);
  for (const value of ['broken', token({ role: 'Admin' }), token({ exp: 1, role: 'Admin' })]) {
    store.setItem('token', value);
    assert.equal(getSession(store, 1000), null);
  }
});

test('Organizer sees event management and cannot see accounting', () => {
  const store = storage();
  store.setItem('token', token({ exp: 100, role: 'Organizer' }));
  const session = getSession(store, 1000);
  assert.equal(hasAnyRole(['Admin', 'Organizer'], session), true);
  assert.equal(hasAnyRole(['Admin', 'Accountant'], session), false);
});

test('multi-role JWT is supported without trusting stored role', () => {
  const store = storage();
  store.setItem('role', 'Admin');
  store.setItem('token', token({ exp: 100, 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': ['Buyer', 'Organizer'] }));
  const session = getSession(store, 1000);
  assert.equal(hasAnyRole('Organizer', session), true);
  assert.equal(hasAnyRole('Buyer', session), true);
  assert.equal(hasAnyRole('Admin', session), false);
});

test('logout removes credentials and stale identity', () => {
  const store = storage();
  for (const key of ['token', 'role', 'userId']) store.setItem(key, 'old');
  clearSession(store);
  for (const key of ['token', 'role', 'userId']) assert.equal(store.getItem(key), null);
});

globalThis.localStorage = storage();
const redirects = [];
globalThis.window = { location: { pathname: '/events', search: '?page=2', assign: url => redirects.push(url) } };
const { default: axiosClient } = await import('../src/api/axiosClient.js');

test('protected API 401 clears session and redirects to login with returnUrl', async () => {
  localStorage.setItem('token', 'old'); localStorage.setItem('userId', '101');
  await assert.rejects(axiosClient.get('/events/admin', { adapter: config => Promise.reject({ config, response: { status: 401 } }) }));
  assert.equal(localStorage.getItem('token'), null);
  assert.equal(localStorage.getItem('userId'), null);
  assert.equal(redirects.at(-1), '/login?returnUrl=%2Fevents%3Fpage%3D2');
});

test('403 and failed login do not force logout or redirect', async () => {
  localStorage.setItem('token', 'keep');
  const count = redirects.length;
  for (const [url, status] of [['/events/admin', 403], ['/auth/login', 401]])
    await assert.rejects(axiosClient.get(url, { adapter: config => Promise.reject({ config, response: { status } }) }));
  assert.equal(localStorage.getItem('token'), 'keep');
  assert.equal(redirects.length, count);
});

test('public API expired-session 401 clears session and preserves the return route', async () => {
  localStorage.setItem('token', token({ exp: 1, role: 'Buyer' }));
  localStorage.setItem('userId', '101');
  localStorage.setItem('role', 'Buyer');
  await assert.rejects(axiosClient.get('/events/open', {
    adapter: config => {
      assert.match(config.headers.Authorization, /^Bearer /);
      return Promise.reject({ config, response: { status: 401 } });
    },
  }));
  for (const key of ['token', 'role', 'userId']) assert.equal(localStorage.getItem(key), null);
  assert.equal(redirects.at(-1), '/login?returnUrl=%2Fevents%3Fpage%3D2');
});

test('anonymous public browsing supplies no Authorization and does not redirect', async () => {
  clearSession();
  const before = redirects.length;
  await axiosClient.get('/events/open', { adapter: async config => {
    assert.equal(config.headers.Authorization, undefined);
    return { data: { items: [] }, status: 200, statusText: 'OK', headers: {}, config };
  } });
  assert.equal(redirects.length, before);
});

test('reauthentication does not attach a stale stored Bearer token to login', async () => {
  localStorage.setItem('token', token({ exp: 1, role: 'Buyer' }));
  const response = await axiosClient.post('/auth/login', { email: 'fixture@example.test', password: 'unused-fixture-input' }, {
    adapter: async config => {
      assert.equal(config.headers.Authorization, undefined);
      return { data: { token: 'new-fixture-token' }, status: 200, statusText: 'OK', headers: {}, config };
    },
  });
  assert.equal(response.data.token, 'new-fixture-token');
  clearSession();
});
