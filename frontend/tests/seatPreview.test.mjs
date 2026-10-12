import test from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { readFile } from 'node:fs/promises';
import { previewChart, previewGrid, importMessage, importChart } from '../src/events/seatImport.js';

const seats = [
  { row: 'B', number: '10', category: 'VIP', x: 2, y: 2 },
  { row: 'A', number: '1', category: 'Standard', x: 1, y: 1 },
  { row: 'B', number: '2', category: 'VIP', x: 1, y: 2 },
];

test('preview calls only read-only multipart route; it never calls import', async () => {
  const calls = [];
  const file = new File(['{}'], 'chart.json');
  const data = await previewChart({ post: async (...args) => { calls.push(args); return { data: { seats, persisted: false } }; } }, 10, file);
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/showtimes/10/seats/preview');
  assert.equal(calls[0][1].get('file').name, file.name);
  assert.equal(data.persisted, false);
});

test('grid groups exact rows and seats with natural ordering and consistent category colors', () => {
  const grid = previewGrid(seats);
  assert.deepEqual(grid.rows.map(r => r.name), ['A', 'B']);
  assert.deepEqual(grid.rows[1].seats.map(s => s.number), ['2', '10']);
  assert.equal(grid.rows.reduce((n, r) => n + r.seats.length, 0), 3);
  assert.equal(grid.rows[1].seats[0].color, grid.rows[1].seats[1].color);
  assert.notEqual(grid.rows[0].seats[0].color, grid.rows[1].seats[0].color);
  assert.equal(grid.rows[1].seats[1].x, 2);
});

test('all structured error paths and messages remain visible including malformed JSON position', () => {
  const message = importMessage({ response: { data: { errors: [
    { path: 'seats[0].row', message: 'Missing row' },
    { path: 'seats[1].seatNumber', message: 'Duplicate seats[0]' },
    { path: 'root', message: 'JSON dòng 2, cột 10' },
  ] } } });
  assert.match(message, /seats\[0\].row: Missing row/);
  assert.match(message, /seats\[1\].seatNumber: Duplicate seats\[0\]/);
  assert.match(message, /dòng 2, cột 10/);
  assert.equal(message.split('\n').length, 4);
});

test('actual preview component renders grid before confirmation and disables invalid/busy confirmation', async () => {
  const source = new URL('../src/components/SeatingChartPreview.jsx', import.meta.url);
  const transformed = await transformWithOxc(await readFile(source, 'utf8'), 'SeatingChartPreview.jsx', { jsx: { runtime: 'automatic', development: false } });
  const code = transformed.code
    .replace('"../events/seatImport"', JSON.stringify(new URL('../src/events/seatImport.js', import.meta.url).href))
    .replace('"react/jsx-runtime"', JSON.stringify(import.meta.resolve('react/jsx-runtime')));
  const { default: Preview } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
  const preview = { seats, file: { name: 'chart.json' }, showtimeId: 10, validation: { totalSeats: 3, totalRows: 2 } };
  const html = renderToStaticMarkup(React.createElement(Preview, { preview, busy: false }));
  assert.match(html, /3 ghế \/ 2 hàng/); assert.match(html, /Chưa lưu dữ liệu/);
  assert.match(html, /Hàng A/); assert.match(html, /Hàng B/);
  assert.match(html, /\(2, 2\)/);
  assert.match(html, /Standard/); assert.match(html, /VIP/);
  assert.doesNotMatch(html, /disabled/);
  const unknown = renderToStaticMarkup(React.createElement(Preview, { preview: { ...preview, seats: [{ row: 'A', number: '1', category: 'VIP', x: null, y: null }] } }));
  assert.match(unknown, /Chưa có tọa độ/);
  assert.doesNotMatch(unknown, /\(0, 0\)|\(null, null\)/);
  const invalid = renderToStaticMarkup(React.createElement(Preview, { preview: null, attempted: true }));
  assert.match(invalid, /disabled=""[^>]*>Xác nhận nạp sơ đồ/);
  const busy = renderToStaticMarkup(React.createElement(Preview, { preview, busy: true }));
  assert.match(busy, /disabled=""[^>]*>Xác nhận nạp sơ đồ/);
  assert.equal(renderToStaticMarkup(React.createElement(Preview, {})), '');
  const calls = [];
  const file = new File(['{}'], 'original.json');
  const tree = Preview({ preview: { ...preview, file }, busy: false, onConfirm: () => importChart({
    post: async (...args) => { calls.push(args); return { data: { success: true } }; }
  }, 10, file) });
  renderToStaticMarkup(tree);
  assert.equal(calls.length, 0, 'rendering valid preview must not import');
  const buttons = tree.props.children.filter(child => child?.type === 'button');
  assert.equal(buttons[0].props.disabled, false);
  await buttons[0].props.onClick();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/showtimes/10/seats/import');
  assert.equal(calls[0][1].get('file').name, 'original.json');
});
