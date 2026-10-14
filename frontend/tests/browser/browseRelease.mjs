// Real routed UI -> real Kestrel -> unique local PostgreSQL + Redis15. No request mocks.
import assert from 'node:assert/strict';
import { localBrowserServer } from './localBrowserServer.mjs';
import { createRequire } from 'node:module';
import process from 'node:process';
import { categoryPalette } from '../../src/events/seatImport.js';
const fixture = JSON.parse(process.env.FIX05_BROWSER_FIXTURE);
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.FIX03_PLAYWRIGHT || 'playwright');
const { vite, ready, api } = localBrowserServer();
let browser;
try {
  const origin = await ready;
  browser = await chromium.launch({ channel: 'chrome', headless: true });
  const publicContext = await browser.newContext({ viewport: { width: 1400, height: 1000 }, timezoneId: 'America/New_York' });
  const home = await publicContext.newPage();
  await home.goto(origin);
  await home.waitForFunction(() => document.querySelectorAll('[data-testid="public-event"]').length === 20);
  const first = await home.getByTestId('public-event').evaluateAll(cards => cards.map(card => ({ id: card.dataset.eventId, time: card.querySelector('time').dateTime, text: card.querySelector('time').textContent })));
  assert.equal(first.length, 20); assert.ok(first.every(e => Number.isFinite(Date.parse(e.time))));
  assert.deepEqual(first.map(e => Date.parse(e.time)), first.map(e => Date.parse(e.time)).sort((a, b) => a - b));
  const vietnamTime = value => new Date(value).toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh', hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
  first.forEach(e => assert.equal(e.text, vietnamTime(e.time))); // browser timezone is New York, not Vietnam
  await home.getByTestId('feed-sentinel').scrollIntoViewIfNeeded(); // observer, not manual pagination click
  await home.waitForFunction(() => document.querySelectorAll('[data-testid="public-event"]').length >= 40);
  const eventIds = await home.getByTestId('public-event').evaluateAll(cards => cards.map(card => card.dataset.eventId));
  assert.equal(new Set(eventIds).size, eventIds.length);
  await home.goto(`${origin}/shows/${fixture.emptyShowId}`);
  await home.getByRole('heading', { name: 'Suất diễn này không mở bán' }).waitFor();
  assert.equal(await home.locator('svg').count(), 0);
  const detail = await (await home.request.get(`${api}/showtimes/${fixture.showId}`)).json();
  await home.goto(`${origin}/shows/${fixture.showId}`);
  await home.getByRole('heading', { name: detail.eventName, exact: true }).waitFor();
  await home.getByText(`🕒 Thời gian: ${vietnamTime(detail.startTime)}`, { exact: false }).waitFor();

  const context = await browser.newContext({ viewport: { width: 1400, height: 1000 }, timezoneId: 'America/New_York' });
  await context.addInitScript(token => localStorage.setItem('token', token), fixture.token);
  const page = await context.newPage();
  await page.goto(`${origin}/shows/${fixture.emptyShowId}/seats`);
  await page.getByRole('heading', { name: 'Suất diễn chưa mở bán', exact: true }).waitFor();
  assert.equal(await page.locator('g[data-seat-id]').count(), 0);
  await page.goto(`${origin}/shows/999999/seats`);
  await page.getByText('Suất diễn không tồn tại.', { exact: true }).waitFor();
  assert.equal(await page.locator('g[data-seat-id]').count(), 0);
  await page.goto(`${origin}/shows/${fixture.unknownShowId}/seats`);
  await page.getByRole('heading', { name: 'Sơ đồ ghế chưa đủ thông tin vị trí' }).waitFor();
  assert.equal(await page.locator('g[data-seat-id]').count(), 0);

  const started = performance.now();
  await page.goto(`${origin}/shows/${fixture.showId}/seats`);
  await page.waitForFunction(() => document.querySelectorAll('g[data-seat-id]').length === 2000);
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const renderMs = performance.now() - started;
  assert.ok(renderMs < 2000, `2000-seat full render took ${renderMs.toFixed(2)}ms`);
  const group = id => page.locator(`g[data-seat-id="${id}"]`);
  assert.equal(await group(fixture.ids[0]).locator('text').first().textContent(), '⏳');
  assert.equal(await group(fixture.ids[0]).locator('rect').first().getAttribute('fill'), '#fef3c7');
  assert.equal(await group(fixture.ids[0]).getAttribute('aria-disabled'), 'true');
  assert.equal(await group(fixture.ids[1]).locator('text').first().textContent(), '✕');
  assert.equal(await group(fixture.ids[1]).locator('rect').first().getAttribute('fill'), '#e5e7eb');
  assert.equal(await group(fixture.ids[1]).getAttribute('aria-disabled'), 'true');
  assert.equal(await group(fixture.ids[2]).locator('text').first().textContent(), '✓');
  const actualChart = await (await page.request.get(`${api}/showtimes/${fixture.showId}/seats`)).json();
  const categoryColors = new Map(categoryPalette(actualChart.seats.map(s => s.category || 'Chưa có hạng ghế')).map(c => [c.name, c.color]));
  const availableSeat = actualChart.seats.find(s => s.id === fixture.ids[2]);
  assert.equal(await group(fixture.ids[2]).locator('rect').first().getAttribute('fill'), categoryColors.get(availableSeat.category || 'Chưa có hạng ghế'));
  assert.equal(await group(fixture.ids[2]).getAttribute('aria-disabled'), 'false');
  await page.reload(); await group(fixture.ids[0]).waitFor();
  assert.equal(await group(fixture.ids[0]).locator('text').first().textContent(), '⏳');

  const headers = { Authorization: `Bearer ${fixture.token}` };
  const hold = await page.request.post(`${api}/showtimes/${fixture.showId}/seats/hold`, { headers, data: { seatIds: fixture.batchIds } });
  assert.equal(hold.status(), 200); const deadline = (await hold.json()).heldUntilUtc;
  await page.reload(); await page.getByTestId('hold-session').filter({ hasText: 'Bạn đang giữ 3 ghế' }).waitFor();
  const release = page.waitForResponse(r => r.request().method() === 'DELETE' && r.url().endsWith(`/seats/${fixture.batchIds[0]}/hold`));
  await page.getByRole('button', { name: `Nhả ghế ${fixture.batchIds[0]}`, exact: true }).click();
  assert.equal((await release).status(), 200);
  await page.getByTestId('hold-session').filter({ hasText: 'Bạn đang giữ 2 ghế' }).waitFor();
  const remaining = await (await page.request.get(`${api}/showtimes/${fixture.showId}/seats/my-hold`, { headers })).json();
  assert.equal(remaining.heldUntilUtc, deadline); assert.equal(remaining.heldSeats.length, 2);
  const other = await page.request.post(`${api}/showtimes/${fixture.showId}/seats/${fixture.batchIds[0]}/hold`,
    { headers: { Authorization: `Bearer ${fixture.otherToken}` }, data: {} });
  assert.equal(other.status(), 200); // released seat is immediately acquirable by another buyer
  const forbidden = await page.request.delete(`${api}/showtimes/${fixture.showId}/seats/${fixture.batchIds[0]}/hold`, { headers });
  assert.equal(forbidden.status(), 403);
  for (const id of fixture.batchIds.slice(1)) {
    const done = page.waitForResponse(r => r.request().method() === 'DELETE' && r.url().endsWith(`/seats/${id}/hold`));
    await page.getByRole('button', { name: `Nhả ghế ${id}`, exact: true }).click();
    assert.equal((await done).status(), 200);
    await page.getByRole('button', { name: `Nhả ghế ${id}`, exact: true }).waitFor({ state: 'detached' });
  }
  assert.equal(await page.getByTestId('hold-session').count(), 0);
  assert.equal((await page.request.get(`${api}/showtimes/${fixture.showId}/seats/my-hold`, { headers })).status(), 404);

  const mobileContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const mobile = await mobileContext.newPage(); await mobile.goto(`${origin}/shows/${fixture.showId}/seats`);
  await mobile.waitForFunction(() => document.querySelectorAll('g[data-seat-id]').length === 2000);
  const viewport = mobile.locator('.seat-map-viewport'); await viewport.scrollIntoViewIfNeeded();
  const transform = () => mobile.locator('.seat-map-viewport svg > g').getAttribute('transform');
  const settleGeometry = () => mobile.evaluate(() => new Promise(resolve => {
    let previous, stable = 0;
    const sample = () => {
      const viewport = document.querySelector('.seat-map-viewport'), box = viewport.getBoundingClientRect();
      const value = `${viewport.querySelector('svg > g').getAttribute('transform')}:${box.x}:${box.y}:${scrollY}`;
      stable = value === previous ? stable + 1 : 0; previous = value;
      if (stable >= 3) resolve(); else requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  }));
  const before = await transform();
  const box = await viewport.boundingBox();
  const cdp = await mobileContext.newCDPSession(mobile);
  const point = (id, x) => ({ id, x, y: box.y + box.height / 2, radiusX: 4, radiusY: 4 });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point(0, 150), point(1, 230)] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [point(0, 70), point(1, 310)] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await mobile.waitForFunction(previous => document.querySelector('.seat-map-viewport svg > g').getAttribute('transform') !== previous, before);
  for (let i = 0; i < 4; i++) {
    const previous = await transform();
    await mobile.getByRole('button', { name: 'Phóng to', exact: true }).tap();
    await mobile.waitForFunction(value => document.querySelector('.seat-map-viewport svg > g').getAttribute('transform') !== value, previous);
    await settleGeometry(); // wait for actual completed frames/scroll anchoring, not a fixed delay
  }
  const target = await mobile.evaluate(() => {
    const area = document.querySelector('.seat-map-viewport').getBoundingClientRect();
    for (const g of document.querySelectorAll('g[data-seat-id]')) {
      const rect = g.querySelector('rect'), b = rect.getBoundingClientRect();
      if (g.dataset.seatStatus === 'Available' && b.width >= 16 && b.left > area.left && b.right < area.right && b.top > area.top && b.bottom < area.bottom) {
        const x = b.left + b.width / 2, y = b.top + b.height / 2;
        if (document.elementFromPoint(x, y)?.closest('g[data-seat-id]') === g) return { id: g.dataset.seatId, x, y };
      }
    }
    return null;
  });
  assert.ok(target, 'No accurately tappable visible seat after mobile zoom');
  await mobile.evaluate(() => {
    window.fix05TouchEvents = [];
    for (const type of ['pointerdown', 'pointerup', 'click']) document.addEventListener(type, event =>
      window.fix05TouchEvents.push({ type, x: event.clientX, y: event.clientY, id: event.target.closest?.('[data-seat-id]')?.dataset.seatId, pointer: event.pointerType }), true);
  });
  await mobile.touchscreen.tap(target.x, target.y);
  // Wait for this exact target's React commit, not a detail heading left by an earlier gesture.
  try {
    await mobile.waitForFunction(id => document.querySelector(`g[data-seat-id="${id}"] rect`)?.getAttribute('stroke') === '#38bdf8', target.id, { timeout: 5000 });
  } catch (error) {
    console.error('FIX05_TOUCH_DIAGNOSTICS', JSON.stringify({ target, state: await mobile.evaluate(() => ({
      events: window.fix05TouchEvents, selected: [...document.querySelectorAll('g[data-seat-id]')].filter(g => g.querySelector('rect')?.getAttribute('stroke') === '#38bdf8').map(g => g.dataset.seatId),
      viewport: { width: innerWidth, height: innerHeight, scrollY }, transform: document.querySelector('.seat-map-viewport svg > g').getAttribute('transform'),
    })) }));
    throw error;
  }
  await mobile.getByRole('heading', { name: /Chi tiết ghế:/ }).waitFor();
  assert.equal(await mobile.locator(`g[data-seat-id="${target.id}"] rect`).first().getAttribute('stroke'), '#38bdf8');
  const zoomed = await transform();
  await mobile.getByRole('button', { name: 'Thu nhỏ', exact: true }).tap();
  await mobile.waitForFunction(previous => document.querySelector('.seat-map-viewport svg > g').getAttribute('transform') !== previous, zoomed);
  console.log(`FIX05_BROWSER|passed=1|failed=0|homeScrollCards=${eventIds.length}|release3to0=true|remainingDeadlinePreserved=true|otherBuyerAcquire=true|foreignRelease=403|emptyAndMissing=true|nullGeometryNotInvented=true|states=3|seats=2000|desktopRenderMs=${renderMs.toFixed(2)}|mobilePinchTapZoom=true`);
} finally {
  await browser?.close(); vite.kill(); // only the temporary child server created by this script
}
