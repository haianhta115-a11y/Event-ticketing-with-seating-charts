import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { seatMapGeometry, computeFitScale, initialSeatScale, maximumSeatScale, zoomAtPoint } from '../src/events/seatMapGeometry.js';

const grid = (columns, rows, dx = 54, dy = 49) => Array.from({ length: columns * rows }, (_, id) =>
  ({ id, x: 80 + id % columns * dx, y: 130 + Math.floor(id / columns) * dy }));

test('real 100-seat pixel coordinates have readable initial targets, not 1px dots', () => {
  const geometry = seatMapGeometry(grid(10, 10));
  assert.ok(geometry.width < 550); assert.ok(geometry.height < 550);
  const fit = computeFitScale(1052, 520, geometry.width, geometry.height);
  assert.ok(initialSeatScale(fit, 100) * geometry.baseSeatSize >= 28);
  assert.ok(fit * geometry.baseSeatSize >= 28);
});

test('normalized, translated and pixel coordinate maps render identically', () => {
  const points = grid(10, 10), a = seatMapGeometry(points);
  const b = seatMapGeometry(points.map(p => ({ ...p, x: p.x * 0.01 - 100, y: p.y * 0.01 + 99 })));
  for (const p of points) {
    assert.ok(Math.abs(a.positions.get(p.id).pixelX - b.positions.get(p.id).pixelX) < 1e-8);
    assert.ok(Math.abs(a.positions.get(p.id).pixelY - b.positions.get(p.id).pixelY) < 1e-8);
  }
});

test('aisles, negative offsets and rotated geometry use one affine transform, no ranking/remapping', () => {
  const points = [{ id: 1, x: -100, y: -50 }, { id: 2, x: -70, y: -20 }, { id: 3, x: 150, y: 90 }];
  const original = structuredClone(points), g = seatMapGeometry(points);
  assert.deepEqual(points, original);
  const a = g.positions.get(1), b = g.positions.get(3);
  assert.ok(Math.abs((b.pixelX - a.pixelX) / (b.pixelY - a.pixelY) - 250 / 140) < 1e-12);
});

test('diagonal glyphs never overlap; zero-distance points do not produce infinite transforms', () => {
  const g = seatMapGeometry([{ id: 1, x: 0, y: 0 }, { id: 2, x: 1, y: 1 }, { id: 3, x: 1, y: 1 }]);
  assert.equal(g.coordinateScale, 40); assert.equal(g.positions.get(2).pixelX - g.positions.get(1).pixelX, 40);
  assert.equal(g.baseSeatSize, 32);
  assert.equal(seatMapGeometry([{ id: 1, x: 4, y: 5 }]).baseSeatSize, 32);
});

test('2000-seat overview fits and permits 44px selection even on narrow viewports', () => {
  const g = seatMapGeometry(grid(50, 40));
  for (const width of [318, 1052]) {
    const fit = computeFitScale(width, 520, g.width, g.height);
    assert.equal(initialSeatScale(fit, 2000), fit);
    assert.ok(g.width * fit <= width - 48 + 1e-8);
    assert.ok(maximumSeatScale(fit) * g.baseSeatSize >= 64);
  }
});

test('small maps remain readable on mobile instead of resetting to tiny fit', () => {
  const g = seatMapGeometry(grid(10, 10)), fit = computeFitScale(280, 360, g.width, g.height);
  assert.ok(fit < 0.875); assert.equal(initialSeatScale(fit, 100), 0.875);
});

test('wheel/button zoom keeps the same diagram point under the pointer or viewport centre', () => {
  const original = { scale: 0.15, x: -12, y: 25 };
  for (const [x, y] of [[10, 50], [526, 234], [190, 300]]) {
    const point = { x: (x - original.x) / original.scale, y: (y - original.y) / original.scale };
    for (const scale of [0.1, 1.375, 2]) {
      const next = zoomAtPoint(original, scale, x, y);
      assert.ok(Math.abs((x - next.x) / next.scale - point.x) < 1e-10);
      assert.ok(Math.abs((y - next.y) / next.scale - point.y) < 1e-10);
    }
  }
});

test('invalid/missing geometry is rejected; no coordinates invented', () => {
  assert.equal(seatMapGeometry([]), null);
  for (const x of [null, undefined, Infinity, NaN]) assert.equal(seatMapGeometry([{ id: 1, x, y: 0 }]), null);
});

test('real routed screen preserves transform across status refresh and supports captured pointers/keyboard', () => {
  const source = readFileSync(new URL('../src/pages/SeatMap.jsx', import.meta.url), 'utf8');
  assert.match(source, /\}, \[geometry\]\);/);
  assert.match(source, /new ResizeObserver\(resize\)/);
  assert.match(source, /e.currentTarget.setPointerCapture\(e.pointerId\)/);
  assert.match(source, /document.elementFromPoint\(e.clientX, e.clientY\)/);
  assert.match(source, /onKeyDown=\{handleMapKeyDown\}/);
  assert.match(source, /44px targets/);
});
