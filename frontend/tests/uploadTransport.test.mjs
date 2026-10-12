import test from 'node:test';
import assert from 'node:assert/strict';
import { previewChart, importChart } from '../src/events/seatImport.js';

const values = new Map();
globalThis.localStorage = {
  getItem: key => values.get(key) ?? null,
  setItem: (key, value) => values.set(key, value),
  removeItem: key => values.delete(key),
};
const { default: client } = await import('../src/api/axiosClient.js');
const capture = async operation => {
  let actual;
  const adapter = client.defaults.adapter;
  client.defaults.adapter = async config => {
    actual = config;
    return { config, status: 200, statusText: 'OK', headers: {}, data: { success: true } };
  };
  try { await operation(); return actual; }
  finally { client.defaults.adapter = adapter; }
};

for (const [name, upload] of [['preview', previewChart], ['import', importChart]]) {
  test(`${name} passes the original file through the real Axios transforms as authenticated FormData`, async () => {
    localStorage.setItem('token', 'fixture-token');
    const file = new File(['{"seats":[]}'], 'chart.json', { type: 'application/json' });
    const request = await capture(() => upload(client, 10, file));
    assert.equal(request.url, `/showtimes/10/seats/${name}`);
    assert.ok(request.data instanceof FormData, 'must not serialize FormData to JSON');
    assert.equal(request.data.get('file').name, 'chart.json');
    assert.equal(await request.data.get('file').text(), await file.text());
    assert.notEqual(request.headers.getContentType(), 'application/json');
    assert.doesNotMatch(String(request.headers.getContentType()), /boundary=/, 'application must not invent a boundary');
    assert.equal(request.headers.get('Authorization'), 'Bearer fixture-token');
  });
}

for (const method of ['post', 'put']) {
  test(`ordinary ${method} still serializes JSON and preserves authorization after multipart`, async () => {
    localStorage.setItem('token', 'fixture-token');
    const body = { name: 'Event', location: 'Venue' };
    const request = await capture(() => client[method]('/events', body));
    assert.equal(request.headers.getContentType(), 'application/json');
    assert.deepEqual(JSON.parse(request.data), body);
    assert.equal(request.headers.get('Authorization'), 'Bearer fixture-token');
    assert.equal(client.defaults.headers['Content-Type'], 'application/json');
  });
}

test('pre-serialized JSON remains JSON and anonymous requests gain no authorization', async () => {
  localStorage.removeItem('token');
  const body = '{"email":"fixture@example.test"}';
  const request = await capture(() => client.post('/auth/login', body));
  assert.equal(request.headers.getContentType(), 'application/json');
  assert.equal(request.data, body);
  assert.equal(request.headers.get('Authorization'), undefined);
});

test('FormData clears a per-request JSON content type as well as the inherited default', async () => {
  const form = new FormData(); form.append('file', new File(['{}'], 'chart.json'));
  const request = await capture(() => client.post('/showtimes/10/seats/preview', form,
    { headers: { 'content-type': 'application/json' } }));
  assert.equal(request.data, form);
  assert.notEqual(request.headers.getContentType(), 'application/json');
});
