import test from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { readFile } from 'node:fs/promises';
import { managementApi } from '../src/events/management.js';
import { seatPresentation, existingPrice } from '../src/events/seatPresentation.js';
import { getShowtimeSeatPricing, updateShowtimeSeatPricing } from '../src/api/eventsApi.js';
import axiosClient from '../src/api/axiosClient.js';

test('managementApi includes showtime-level seat pricing methods', async () => {
  const calls = [];
  const client = {
    get: async (...args) => { calls.push(['get', ...args]); return { data: [{ id: 5, name: 'VIP', price: 350000 }] }; },
    put: async (...args) => { calls.push(['put', ...args]); return { data: { success: true, message: 'Cập nhật thành công' } }; },
  };
  const api = managementApi(client);
  const data = await api.getShowtimeSeatPricing(88);
  assert.equal(data.length, 1);
  assert.equal(data[0].price, 350000);

  const payload = { categories: [{ categoryId: 5, price: 400000 }] };
  const res = await api.updateShowtimeSeatPricing(88, payload);
  assert.equal(res.success, true);

  assert.deepEqual(calls, [
    ['get', '/showtimes/88/pricing'],
    ['put', '/showtimes/88/pricing', payload],
  ]);
});

test('eventsApi exports call expected showtime seat pricing routes', async () => {
  const calls = [];
  const originalGet = axiosClient.get;
  const originalPut = axiosClient.put;

  try {
    axiosClient.get = async (url, ...args) => {
      calls.push(['get', url, ...args]);
      return { data: [{ id: 10, name: 'Standard', price: 150000 }] };
    };
    axiosClient.put = async (url, ...args) => {
      calls.push(['put', url, ...args]);
      return { data: { success: true, message: 'OK' } };
    };

    const resGet = await getShowtimeSeatPricing(99);
    assert.deepEqual(resGet, [{ id: 10, name: 'Standard', price: 150000 }]);
    assert.equal(calls[0][1], '/showtimes/99/pricing');

    const payload = { categories: [{ categoryId: 10, price: 180000 }] };
    const resPut = await updateShowtimeSeatPricing(99, payload);
    assert.deepEqual(resPut, { success: true, message: 'OK' });
    assert.equal(calls[1][1], '/showtimes/99/pricing');
  } finally {
    axiosClient.get = originalGet;
    axiosClient.put = originalPut;
  }
});

test('seatPresentation correctly sets priceDisplay and handles null prices gracefully without showing 0', () => {
  const seatPriced = { id: 1, row: 'A', number: '1', category: 'VIP', displayStatus: 'Available', price: 250000 };
  const visualPriced = seatPresentation(seatPriced, '#f59e0b');
  assert.equal(visualPriced.priceDisplay, '250.000 đ');

  const seatFree = { id: 2, row: 'A', number: '2', category: 'Standard', displayStatus: 'Available', price: 0 };
  const visualFree = seatPresentation(seatFree, '#3b82f6');
  assert.equal(visualFree.priceDisplay, '0 đ');

  const seatNullPrice = { id: 3, row: 'A', number: '3', category: 'Economy', displayStatus: 'Available', price: null };
  const visualNull = seatPresentation(seatNullPrice, '#10b981');
  assert.equal(visualNull.priceDisplay, null);

  const seatUndefinedPrice = { id: 4, row: 'A', number: '4', category: 'VIP', displayStatus: 'Available' };
  const visualUndefined = seatPresentation(seatUndefinedPrice, '#f59e0b');
  assert.equal(visualUndefined.priceDisplay, null);
});

async function getActualTile() {
  const source = await readFile(new URL('../src/pages/SeatMap.jsx', import.meta.url), 'utf8');
  const start = source.indexOf('const SeatItem = React.memo(');
  const end = source.indexOf('\n});', start) + 4;
  assert.ok(start >= 0 && end > start);
  const fragment = `import React from 'react';\nimport { seatPresentation } from '${new URL('../src/events/seatPresentation.js', import.meta.url).href}';\n${source.slice(start, end)}\nexport default SeatItem;`;
  const transformed = await transformWithOxc(fragment, 'ActualSeatTile.jsx', { jsx: { runtime: 'automatic', development: false } });
  const code = transformed.code
    .replace(/(['"])react\/jsx-runtime\1/, JSON.stringify(import.meta.resolve('react/jsx-runtime')))
    .replace(/(['"])react\1/, JSON.stringify(import.meta.resolve('react')));
  return (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
}

test('SeatItem tooltip displays formatted price and handles null price gracefully without showing 0', async () => {
  const Tile = await getActualTile();

  // Test priced seat
  const seatPriced = { id: 10, row: 'A', number: '5', category: 'VIP', displayStatus: 'Available', price: 300000 };
  const markupPriced = renderToStaticMarkup(React.createElement(Tile, {
    seat: seatPriced,
    pixelX: 10,
    pixelY: 10,
    size: 32,
    categoryColor: '#f59e0b',
  }));
  assert.match(markupPriced, /<title>[^<]*300\.000 đ<\/title>/);

  // Test unpriced (null) seat - must say "Chưa có giá" and NOT "0" or "0 đ"
  const seatUnset = { id: 11, row: 'A', number: '6', category: 'Standard', displayStatus: 'Available', price: null };
  const markupUnset = renderToStaticMarkup(React.createElement(Tile, {
    seat: seatUnset,
    pixelX: 50,
    pixelY: 10,
    size: 32,
    categoryColor: '#3b82f6',
  }));
  assert.match(markupUnset, /<title>[^<]*Chưa có giá<\/title>/);
  assert.doesNotMatch(markupUnset, /<title>[^<]*0 đ<\/title>/);
});

test('SeatPricingManager handles showtime-level pricing and empty states gracefully', async () => {
  const source = new URL('../src/components/SeatPricingManager.jsx', import.meta.url);
  const transformed = await transformWithOxc(await readFile(source, 'utf8'), 'SeatPricingManager.jsx', { jsx: { runtime: 'automatic', development: false } });
  const fakeApi = `data:text/javascript;base64,${Buffer.from(`
    export const getEventSeatPricing = async () => [];
    export const updateEventSeatPricing = async () => ({ success: true });
    export const getShowtimeSeatPricing = async () => [];
    export const updateShowtimeSeatPricing = async () => ({ success: true });
  `).toString('base64')}`;

  const code = transformed.code
    .replace(/(['"])react\/jsx-runtime\1/g, JSON.stringify(import.meta.resolve('react/jsx-runtime')))
    .replace(/(['"])react\1/g, JSON.stringify(import.meta.resolve('react')))
    .replace(/(['"])\.\.\/api\/eventsApi\1/g, JSON.stringify(fakeApi))
    .replace(/(['"])\.\.\/events\/management\1/g, JSON.stringify(new URL('../src/events/management.js', import.meta.url).href));
  const { default: SeatPricingManager } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);

  // Test empty state with showtimeId
  const htmlWithShowtime = renderToStaticMarkup(React.createElement(SeatPricingManager, {
    showtimeId: 101,
    showtimes: [{ id: 101, startTime: '2026-12-01T10:00:00Z', hasSeatMap: false }],
  }));
  assert.match(htmlWithShowtime, /Thiết lập giá vé theo hạng ghế/);
  assert.match(htmlWithShowtime, /Đang tải danh sách hạng ghế và giá vé/);

  // Test empty state with empty showtimes array
  const htmlEmptyShowtimes = renderToStaticMarkup(React.createElement(SeatPricingManager, {
    eventId: 200,
    showtimes: [],
  }));
  assert.match(htmlEmptyShowtimes, /Thiết lập giá vé theo hạng ghế/);
});
