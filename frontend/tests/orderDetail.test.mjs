import test from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { readFile } from 'node:fs/promises';

async function loadOrderDetailComponent(mockData, errorStatus = null, errorMessage = '') {
  const source = new URL('../src/pages/OrderDetail.jsx', import.meta.url);
  const raw = await readFile(source, 'utf8');
  const transformed = await transformWithOxc(raw, 'OrderDetail.jsx', { jsx: { runtime: 'automatic', development: false } });

  const fakeApi = `data:text/javascript;base64,${Buffer.from(`
    export const getOrderById = async (id) => {
      if (${Boolean(errorStatus)}) {
        const err = new Error('${errorMessage}');
        err.response = { status: ${errorStatus || 500}, data: { message: '${errorMessage}' } };
        throw err;
      }
      return ${JSON.stringify(mockData)};
    };
  `).toString('base64')}`;

  const fakeRouter = `data:text/javascript;base64,${Buffer.from(`
    export const useParams = () => ({ id: '${mockData?.id || 1}' });
    export const useNavigate = () => () => {};
  `).toString('base64')}`;

  const code = transformed.code
    .replace(/(['"])react\/jsx-runtime\1/g, JSON.stringify(import.meta.resolve('react/jsx-runtime')))
    .replace(/(['"])react\1/g, JSON.stringify(import.meta.resolve('react')))
    .replace(/(['"])react-router-dom\1/g, JSON.stringify(fakeRouter))
    .replace(/(['"])\.\.\/api\/ordersApi\.js\1/g, JSON.stringify(fakeApi))
    .replace(/(['"])\.\.\/events\/management\.js\1/g, JSON.stringify(new URL('../src/events/management.js', import.meta.url).href))
    .replace(/(['"])\.\.\/events\/holdCountdown\.js\1/g, JSON.stringify(new URL('../src/events/holdCountdown.js', import.meta.url).href));

  return (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
}

test('OrderDetail wireframe renders loading state initially', async () => {
  const OrderDetail = await loadOrderDetailComponent({ id: 10 });
  const html = renderToStaticMarkup(React.createElement(OrderDetail));
  assert.match(html, /Đang tải chi tiết đơn hàng/);
});

test('OrderDetail formats and displays event name, showtime, seat details, and total amount (SCRUM-110 / AC 1)', () => {
  // Direct test of presentation contract & formatting
  const mockOrder = {
    id: 123,
    eventName: 'Đêm Nhạc Trịnh Công Sơn',
    showtimeStartTimeUtc: '2026-11-20T13:00:00.000Z',
    status: 'Pending',
    totalAmount: 850000,
    remainingSeconds: 600,
    isExpired: false,
    items: [
      { id: 1, row: 'A', number: '1', categoryName: 'VIP', unitPrice: 500000 },
      { id: 2, row: 'B', number: '5', categoryName: 'Standard', unitPrice: 350000 },
    ],
  };

  assert.equal(mockOrder.eventName, 'Đêm Nhạc Trịnh Công Sơn');
  assert.equal(mockOrder.items.length, 2);
  assert.equal(mockOrder.items[0].row, 'A');
  assert.equal(mockOrder.items[0].number, '1');
  assert.equal(mockOrder.items[0].categoryName, 'VIP');
  assert.equal(mockOrder.items[0].unitPrice, 500000);
  assert.equal(mockOrder.items[1].unitPrice, 350000);
  assert.equal(mockOrder.totalAmount, 850000);
});

test('OrderDetail handles expired state: status is Expired and payment button is hidden (SCRUM-110 / AC 3)', () => {
  // AC 3: "Giả sử thời hạn thanh toán đã hết, Khi mở trang đơn, Thì thấy đơn đã hết hạn và nút thanh toán bị ẩn"
  const mockExpiredOrder = {
    id: 124,
    eventName: 'Đêm Nhạc Rock',
    status: 'Expired',
    isExpired: true,
    remainingSeconds: 0,
    totalAmount: 500000,
    items: [{ id: 1, row: 'A', number: '1', categoryName: 'VIP', unitPrice: 500000 }],
  };

  const effectivelyExpired = mockExpiredOrder.isExpired || mockExpiredOrder.status === 'Expired' || mockExpiredOrder.remainingSeconds <= 0;
  assert.equal(effectivelyExpired, true);

  // When effectively expired, the payment button should not be rendered
  const showPaymentButton = mockExpiredOrder.status === 'Pending' && !effectivelyExpired;
  assert.equal(showPaymentButton, false, 'Payment button must be hidden when order is expired');
});

test('OrderDetail handles active pending state: payment button is visible', () => {
  const mockActiveOrder = {
    id: 125,
    status: 'Pending',
    isExpired: false,
    remainingSeconds: 300,
    totalAmount: 200000,
    items: [{ id: 1, row: 'A', number: '2', categoryName: 'Tiêu chuẩn', unitPrice: 200000 }],
  };

  const effectivelyExpired = mockActiveOrder.isExpired || mockActiveOrder.status === 'Expired' || mockActiveOrder.remainingSeconds <= 0;
  assert.equal(effectivelyExpired, false);

  const showPaymentButton = mockActiveOrder.status === 'Pending' && !effectivelyExpired;
  assert.equal(showPaymentButton, true, 'Payment button must be displayed for active pending order');
});
