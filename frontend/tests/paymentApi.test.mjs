import test from 'node:test';
import assert from 'node:assert/strict';
import axiosClient from '../src/api/axiosClient.js';
import {
  payOrder,
  createOrderForShowtime,
  getOrderById,
  getPendingOrderForShowtime,
  signMockPayment,
  sendMockWebhook,
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
            redirectUrl: 'http://127.0.0.1:5173/mock-gateway?orderId=10',
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
      if (url === '/payments/mock/sign') {
        return {
          data: {
            signature: 'mock_sig_123',
            payload: '10|300000|TXN-1|true',
          },
        };
      }
      if (url === '/payments/mock/webhook') {
        return {
          data: {
            success: true,
            outcome: 'Paid',
            message: 'Thanh toán thành công.',
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

    // 5. signMockPayment (SCRUM-35)
    const signRes = await signMockPayment({
      orderId: 10,
      transactionId: 'TXN-1',
      amount: 300000,
      success: true,
    });
    assert.equal(signRes.signature, 'mock_sig_123');
    assert.equal(calls[4][0], 'post');
    assert.equal(calls[4][1], '/payments/mock/sign');

    // 6. sendMockWebhook (SCRUM-35)
    const webhookRes = await sendMockWebhook({
      orderId: 10,
      transactionId: 'TXN-1',
      amount: 300000,
      success: true,
      signature: 'mock_sig_123',
    });
    assert.equal(webhookRes.success, true);
    assert.equal(webhookRes.outcome, 'Paid');
    assert.equal(calls[5][0], 'post');
    assert.equal(calls[5][1], '/payments/mock/webhook');
  } finally {
    axiosClient.get = originalGet;
    axiosClient.post = originalPost;
  }
});
