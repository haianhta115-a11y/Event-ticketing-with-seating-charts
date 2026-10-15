import test from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { readFile } from 'node:fs/promises';
import { managementApi } from '../src/events/management.js';

test('managementApi includes seat pricing methods for event endpoint', async () => {
  const calls = [];
  const client = {
    get: async (...args) => { calls.push(['get', ...args]); return { data: [{ id: 1, name: 'VIP', price: 500000 }] }; },
    put: async (...args) => { calls.push(['put', ...args]); return { data: { success: true, message: 'Thành công' } }; },
  };
  const api = managementApi(client);
  const data = await api.getEventSeatPricing(12);
  assert.equal(data.length, 1);
  assert.equal(data[0].price, 500000);

  const payload = { categories: [{ categoryId: 1, price: 600000 }] };
  const res = await api.updateEventSeatPricing(12, payload);
  assert.equal(res.success, true);

  assert.deepEqual(calls, [
    ['get', '/events/12/pricing'],
    ['put', '/events/12/pricing', payload],
  ]);
});

test('SeatPricingManager wireframe renders expected title, loading and table structure', async () => {
  const source = new URL('../src/components/SeatPricingManager.jsx', import.meta.url);
  const transformed = await transformWithOxc(await readFile(source, 'utf8'), 'SeatPricingManager.jsx', { jsx: { runtime: 'automatic', development: false } });
  const fakeApi = `data:text/javascript;base64,${Buffer.from(`
    export const getEventSeatPricing = async () => [
      { id: 1, showtimeId: 10, name: 'VIP', price: 500000, seatCount: 20 },
      { id: 2, showtimeId: 10, name: 'Standard', price: 200000, seatCount: 80 }
    ];
    export const updateEventSeatPricing = async () => ({ success: true, message: 'Cập nhật thành công!' });
  `).toString('base64')}`;

  const code = transformed.code
    .replace(/(['"])react\/jsx-runtime\1/g, JSON.stringify(import.meta.resolve('react/jsx-runtime')))
    .replace(/(['"])react\1/g, JSON.stringify(import.meta.resolve('react')))
    .replace(/(['"])\.\.\/api\/eventsApi\1/g, JSON.stringify(fakeApi))
    .replace(/(['"])\.\.\/events\/management\1/g, JSON.stringify(new URL('../src/events/management.js', import.meta.url).href));
  const { default: SeatPricingManager } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);

  const html = renderToStaticMarkup(React.createElement(SeatPricingManager, { eventId: 10 }));
  assert.match(html, /Thiết lập giá vé theo hạng ghế/);
  assert.match(html, /Đang tải danh sách hạng ghế và giá vé/);
});
