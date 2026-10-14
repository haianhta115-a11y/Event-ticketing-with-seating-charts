import test from 'node:test';
import assert from 'node:assert/strict';
import { apiBaseUrl } from '../src/api/apiConfiguration.js';

test('default API uses the same-origin Vite/reverse proxy, not a fixed port', () => {
  assert.equal(apiBaseUrl(undefined), '/api');
  assert.equal(apiBaseUrl(''), '/api');
  assert.equal(apiBaseUrl('/api/'), '/api');
});
test('approved explicit public HTTPS endpoint is configurable', () => {
  assert.equal(apiBaseUrl(' https://api.example.test/api/ '), 'https://api.example.test/api');
});
test('credentials, fragments, queries and unsafe protocols cannot enter the public API URL', () => {
  for (const value of ['https://user:secret@example.test/api', 'javascript:alert(1)', '/other',
    'https://example.test/api?key=secret', 'https://example.test/api#token']) {
    assert.throws(() => apiBaseUrl(value));
  }
});
