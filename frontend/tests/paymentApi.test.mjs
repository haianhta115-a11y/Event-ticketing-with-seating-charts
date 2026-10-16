import test from 'node:test';
import assert from 'node:assert/strict';
import axiosClient from '../src/api/axiosClient.js';
import {
  payOrder,
  createOrderForShowtime,
  getOrderById,
  getPendingOrderForShowtime,
} from '../src/api/paymentApi.js';

test('paymentApi calls expected backend routes', async () => {
  const calls = [];
  const originalGet = axiosClient.get;
  const originalPost = axiosClient.post;

  try {
    axiosClient.get = async (url, ...args) => {
      calls.push(['get', url, ...args]);
      if (url === '/orders/10') {
        return { data: { id: 10, totalAmount: 300000, status: 'Pending' } };
      }
      if (url === '/showtimes/5/orders/pending') {
        return { data: { id: 10, showtimeId: 5, status: 'Pending' } };
      }
      return { data: null };
    };

    axiosClient.post = async (url, ...args) => {
      calls.push(['post', url, ...args]);
      if (url === '/orders/10/pay') {
        return {
          data: {
            orderId: 10,
            orderCode: 'ORD-10',
            amount: 300000,
            redirectUrl: 'http://127.0.0.1:5173/sandbox-gateway?orderId=10',
          },
        };
      }
      if (url === '/showtimes/5/orders') {
        return {
          data: {
            success: true,
            order: { id: 10, totalAmount: 300000, status: 'Pending' },
          },
        };
      }
      return { data: null };
    };

    // 1. payOrder
    const payRes = await payOrder(10);
    assert.equal(payRes.orderId, 10);
    assert.equal(payRes.orderCode, 'ORD-10');
    assert.equal(calls[0][0], 'post');
    assert.equal(calls[0][1], '/orders/10/pay');

    // 2. createOrderForShowtime
    const createRes = await createOrderForShowtime(5);
    assert.equal(createRes.success, true);
    assert.equal(createRes.order.id, 10);
    assert.equal(calls[1][0], 'post');
    assert.equal(calls[1][1], '/showtimes/5/orders');

    // 3. getOrderById
    const orderRes = await getOrderById(10);
    assert.equal(orderRes.id, 10);
    assert.equal(calls[2][0], 'get');
    assert.equal(calls[2][1], '/orders/10');

    // 4. getPendingOrderForShowtime
    const pendingRes = await getPendingOrderForShowtime(5);
    assert.equal(pendingRes.id, 10);
    assert.equal(calls[3][0], 'get');
    assert.equal(calls[3][1], '/showtimes/5/orders/pending');
  } finally {
    axiosClient.get = originalGet;
    axiosClient.post = originalPost;
  }
});
