import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  isPendingOrderStatus,
  hasFailedPaymentAttempt,
  shouldShowRetryPayment,
} from '../src/pages/orderPaymentRetry.js';
import axiosClient from '../src/api/axiosClient.js';
import { payOrder } from '../src/api/paymentApi.js';

test('isPendingOrderStatus recognizes all pending/waiting variations and rejects non-pending', () => {
  assert.equal(isPendingOrderStatus('Pending'), true);
  assert.equal(isPendingOrderStatus('pending'), true);
  assert.equal(isPendingOrderStatus('PENDING'), true);
  assert.equal(isPendingOrderStatus('Waiting'), true);
  assert.equal(isPendingOrderStatus('waiting'), true);
  assert.equal(isPendingOrderStatus('waiting_for_payment'), true);
  assert.equal(isPendingOrderStatus('waiting-for-payment'), true);

  assert.equal(isPendingOrderStatus('Paid'), false);
  assert.equal(isPendingOrderStatus('Expired'), false);
  assert.equal(isPendingOrderStatus('Cancelled'), false);
  assert.equal(isPendingOrderStatus(null), false);
  assert.equal(isPendingOrderStatus(undefined), false);
});

test('hasFailedPaymentAttempt identifies failed attempts across various event/history structures', () => {
  // Empty or missing
  assert.equal(hasFailedPaymentAttempt(null), false);
  assert.equal(hasFailedPaymentAttempt({}), false);
  assert.equal(hasFailedPaymentAttempt({ paymentEvents: [] }), false);
  assert.equal(hasFailedPaymentAttempt({ paymentEvents: [{ success: true }] }), false);

  // In paymentEvents with success: false
  assert.equal(hasFailedPaymentAttempt({ paymentEvents: [{ transactionId: 'TXN-1', success: false }] }), true);

  // In payment_events (snake_case)
  assert.equal(hasFailedPaymentAttempt({ payment_events: [{ success: false }] }), true);

  // In events array
  assert.equal(hasFailedPaymentAttempt({ events: [{ type: 'PaymentFailed' }] }), true);
  assert.equal(hasFailedPaymentAttempt({ events: [{ event: 'PaymentFailed' }] }), true);
  assert.equal(hasFailedPaymentAttempt({ events: [{ status: 'Failed' }] }), true);

  // In history array
  assert.equal(hasFailedPaymentAttempt({ history: [{ status: 'Failed' }] }), true);
  assert.equal(hasFailedPaymentAttempt({ history: [{ type: 'payment_failed' }] }), true);

  // In paymentAttempts or outcome GatewayFailed
  assert.equal(hasFailedPaymentAttempt({ paymentAttempts: [{ outcome: 'GatewayFailed' }] }), true);

  // Direct boolean or count flags
  assert.equal(hasFailedPaymentAttempt({ hasFailedPayment: true }), true);
  assert.equal(hasFailedPaymentAttempt({ hasFailedPayments: true }), true);
  assert.equal(hasFailedPaymentAttempt({ hasFailedPaymentHistory: true }), true);
  assert.equal(hasFailedPaymentAttempt({ order: { hasFailedPayments: true } }), true);
  assert.equal(hasFailedPaymentAttempt({ failedPaymentCount: 2 }), true);
  assert.equal(hasFailedPaymentAttempt({ failedPaymentCount: 0 }), false);
});

test('shouldShowRetryPayment strictly enforces: Pending AND at least one failed payment attempt', () => {
  // Pending + hasFailedPayments flag => TRUE
  assert.equal(
    shouldShowRetryPayment({
      status: 'Pending',
      hasFailedPayments: true,
    }),
    true
  );

  // Pending + nested order hasFailedPayments flag => TRUE
  assert.equal(
    shouldShowRetryPayment({
      order: {
        status: 'Pending',
        hasFailedPayments: true,
      },
    }),
    true
  );

  // Pending + Failed attempt => TRUE
  assert.equal(
    shouldShowRetryPayment({
      status: 'Pending',
      paymentEvents: [{ transactionId: 'TXN-FAIL-1', success: false }],
    }),
    true
  );

  // Pending + No failed attempt => FALSE
  assert.equal(
    shouldShowRetryPayment({
      status: 'Pending',
      paymentEvents: [{ transactionId: 'TXN-OK-1', success: true }],
    }),
    false
  );
  assert.equal(
    shouldShowRetryPayment({
      status: 'Pending',
      paymentEvents: [],
    }),
    false
  );
  assert.equal(
    shouldShowRetryPayment({
      status: 'Pending',
    }),
    false
  );

  // Paid + Failed attempt => FALSE (must be hidden once paid)
  assert.equal(
    shouldShowRetryPayment({
      status: 'Paid',
      paymentEvents: [{ transactionId: 'TXN-FAIL-1', success: false }],
    }),
    false
  );

  // Expired + Failed attempt => FALSE (must be hidden once expired)
  assert.equal(
    shouldShowRetryPayment({
      status: 'Expired',
      paymentEvents: [{ transactionId: 'TXN-FAIL-1', success: false }],
    }),
    false
  );

  // Cancelled + Failed attempt => FALSE
  assert.equal(
    shouldShowRetryPayment({
      status: 'Cancelled',
      paymentEvents: [{ transactionId: 'TXN-FAIL-1', success: false }],
    }),
    false
  );

  // Null order => FALSE
  assert.equal(shouldShowRetryPayment(null), false);
});

test('retry payment reuses the EXACT SAME order ID without creating a new order', async () => {
  const calls = [];
  const originalPost = axiosClient.post;

  try {
    axiosClient.post = async (url, ...args) => {
      calls.push(['post', url, ...args]);
      if (url === '/orders/99/pay') {
        return {
          data: {
            orderId: 99,
            orderCode: 'ORD-99',
            amount: 500000,
            redirectUrl: 'https://payment-gateway.example.com/checkout?token=abc-99',
          },
        };
      }
      return { data: null };
    };

    const res = await payOrder(99);

    assert.equal(res.orderId, 99);
    assert.equal(res.redirectUrl, 'https://payment-gateway.example.com/checkout?token=abc-99');

    // Confirmed: Only one call to /orders/99/pay, no call to /showtimes/.../orders
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], 'post');
    assert.equal(calls[0][1], '/orders/99/pay');
  } finally {
    axiosClient.post = originalPost;
  }
});

test('OrderDetail.jsx satisfies UI/UX design requirements with modern Tailwind CSS classes and SVG icon', () => {
  const source = readFileSync(new URL('../src/pages/OrderDetail.jsx', import.meta.url), 'utf8');

  // Modern Tailwind CSS gradient background
  assert.match(source, /bg-gradient-to-r/, 'Should use Tailwind bg-gradient-to-r');

  // Smooth hover animations
  assert.match(source, /hover:scale-105/, 'Should use hover:scale-105 animation');
  assert.match(source, /duration-300/, 'Should use transition duration-300');

  // Nice drop shadows
  assert.match(source, /shadow-lg/, 'Should use shadow-lg');
  assert.match(source, /hover:shadow-xl/, 'Should use hover:shadow-xl');

  // Rounded corners
  assert.match(source, /rounded-xl|rounded-full/, 'Should use rounded-xl or rounded-full');

  // SVG reload/refresh icon
  assert.match(source, /<svg[^>]*>[\s\S]*?<\/svg>/, 'Should render an SVG icon for retry');

  // Vietnamese button label
  assert.match(source, /Thanh toán lại/, 'Should display "Thanh toán lại"');

  // Prominently placed inside the order summary section
  assert.match(source, /order-summary/, 'Should be placed within the order summary section');
});
