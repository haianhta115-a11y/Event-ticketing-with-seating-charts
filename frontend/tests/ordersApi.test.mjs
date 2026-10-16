import test from 'node:test';
import assert from 'node:assert/strict';
import axiosClient from '../src/api/axiosClient.js';
import {
  ordersApi,
  createOrder,
  createOrderWithPayload,
  getOrderById,
  getPendingOrder,
} from '../src/api/ordersApi.js';

test('ordersApi correctly calls order endpoints and preserves contract', async () => {
  const requests = [];
  const fakeClient = {
    post: async (url, data) => {
      requests.push(['post', url, data]);
      return { data: { success: true, order: { id: 101, showtimeId: 10 } } };
    },
    get: async (url) => {
      requests.push(['get', url]);
      return { data: { id: 101, status: 'Pending', totalAmount: 400000 } };
    },
  };

  const api = ordersApi(fakeClient);

  // 1. Create order for showtime
  const res1 = await api.createOrder(10);
  assert.equal(res1.success, true);
  assert.equal(requests[0][0], 'post');
  assert.equal(requests[0][1], '/showtimes/10/orders');
  assert.deepEqual(requests[0][2], {});

  // 2. Create order with extra data / tampered client total (AC 2)
  const res2 = await api.createOrder(10, { totalAmount: 1000 });
  assert.equal(res2.success, true);
  assert.equal(requests[1][0], 'post');
  assert.equal(requests[1][1], '/showtimes/10/orders');
  assert.deepEqual(requests[1][2], { totalAmount: 1000 });

  // 3. Create order with direct payload
  const res3 = await api.createOrderWithPayload({ showtimeId: 10, totalAmount: 500 });
  assert.equal(res3.success, true);
  assert.equal(requests[2][0], 'post');
  assert.equal(requests[2][1], '/orders');
  assert.deepEqual(requests[2][2], { showtimeId: 10, totalAmount: 500 });

  // 4. Get order by id
  const res4 = await api.getOrderById(101);
  assert.equal(res4.id, 101);
  assert.equal(requests[3][0], 'get');
  assert.equal(requests[3][1], '/orders/101');

  // 5. Get pending order for showtime
  const res5 = await api.getPendingOrder(10);
  assert.equal(res5.id, 101);
  assert.equal(requests[4][0], 'get');
  assert.equal(requests[4][1], '/showtimes/10/orders/pending');
});

test('exported ordersApi wrapper functions route through global axiosClient', async () => {
  const calls = [];
  const originalGet = axiosClient.get;
  const originalPost = axiosClient.post;

  try {
    axiosClient.get = async (url) => {
      calls.push(['get', url]);
      return { data: { id: 55, eventName: 'Concert' } };
    };
    axiosClient.post = async (url, data) => {
      calls.push(['post', url, data]);
      return { data: { success: true, order: { id: 55 } } };
    };

    await createOrder(25);
    assert.equal(calls[0][0], 'post');
    assert.equal(calls[0][1], '/showtimes/25/orders');

    await createOrderWithPayload({ showtimeId: 25 });
    assert.equal(calls[1][0], 'post');
    assert.equal(calls[1][1], '/orders');

    await getOrderById(55);
    assert.equal(calls[2][0], 'get');
    assert.equal(calls[2][1], '/orders/55');

    await getPendingOrder(25);
    assert.equal(calls[3][0], 'get');
    assert.equal(calls[3][1], '/showtimes/25/orders/pending');
  } finally {
    axiosClient.get = originalGet;
    axiosClient.post = originalPost;
  }
});
