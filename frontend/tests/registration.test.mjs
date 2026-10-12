import test from 'node:test';
import assert from 'node:assert/strict';
import { validateRegistration, readActivationToken, activationRequest, resendMessage } from '../src/auth/registration.js';

test('registration blocks short passwords, invalid email and missing names', () => {
  const valid = { email: 'buyer@example.test', username: 'buyer', fullName: 'Buyer', password: '12345678' };
  assert.equal(validateRegistration(valid), '');
  for (const change of [{ password: '1234567' }, { email: 'invalid' }, { username: ' ' }, { fullName: ' ' }])
    assert.notEqual(validateRegistration({ ...valid, ...change }), '');
});

test('activation accepts fragment links and legacy query links', () => {
  assert.equal(readActivationToken({ hash: '#token=fragment-value', search: '' }), 'fragment-value');
  assert.equal(readActivationToken({ hash: '', search: '?token=legacy-value' }), 'legacy-value');
  assert.equal(readActivationToken({ hash: '', search: '' }), null);
});

test('StrictMode effect replay reuses one activation request', async () => {
  let calls = 0;
  const post = async (url, body) => { calls++; assert.equal(url, '/auth/activate'); assert.ok(body.token); };
  const first = activationRequest('test-token', post, null);
  const replay = activationRequest('test-token', post, first);
  await replay.promise;
  assert.equal(first, replay);
  assert.equal(calls, 1);
});

test('resend limit and network failure are not displayed as successful delivery', () => {
  assert.match(resendMessage({ response: { status: 429, data: { remainingSeconds: 120 } } }), /120/);
  assert.match(resendMessage(new Error('network')), /Không gửi được/);
});
