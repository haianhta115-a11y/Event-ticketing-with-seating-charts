// Real browser + real isolated Kestrel API. No request mocks or production credentials.
import assert from 'node:assert/strict';
import { localBrowserServer } from './localBrowserServer.mjs';
import { createRequire } from 'node:module';
import process from 'node:process';
const fixture = JSON.parse(process.env.FIX03_BROWSER_FIXTURE);
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.FIX03_PLAYWRIGHT || 'playwright');
const { vite, ready, api } = localBrowserServer();
let browser;
try {
  const origin = await ready;
  browser = await chromium.launch({ channel: 'chrome', headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
  await page.addInitScript(token => localStorage.setItem('token', token), fixture.token);
  await page.goto(`${origin}/shows/${fixture.showId}/seats`);
  for (const id of fixture.ids) {
    await page.locator(`g[data-seat-id="${id}"]`).click();
    await page.getByRole('button', { name: 'Thêm ghế vào danh sách giữ', exact: true }).click();
  }
  const heldByOther = await page.request.post(`${api}/showtimes/${fixture.showId}/seats/${fixture.ids[2]}/hold`,
    { headers: { Authorization: `Bearer ${fixture.otherToken}` }, data: {} });
  assert.equal(heldByOther.status(), 200);
  const conflict = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/seats/hold'));
  await page.getByRole('button', { name: 'Giữ tất cả 5 ghế cùng lúc', exact: true }).click();
  assert.equal((await conflict).status(), 409);
  await page.getByRole('alert').filter({ hasText: `Ghế bị từ chối: ${fixture.ids[2]}` }).waitFor();
  const chart = await (await page.request.get(`${api}/showtimes/${fixture.showId}/seats`, { headers: { Authorization: `Bearer ${fixture.token}` } })).json();
  assert.equal(chart.seats.filter(s => s.displayStatus === 'HeldByMe').length, 0);
  assert.equal(chart.seats.filter(s => s.displayStatus === 'Available').length, 4);
  await page.getByRole('button', { name: `Bỏ ghế ${fixture.ids[2]}`, exact: true }).click();
  const success = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/seats/hold'));
  await page.getByRole('button', { name: 'Giữ tất cả 4 ghế cùng lúc', exact: true }).click();
  const response = await success; assert.equal(response.status(), 200);
  const deadline = (await response.json()).heldUntilUtc;
  await page.getByTestId('hold-session').filter({ hasText: 'Bạn đang giữ 4 ghế' }).waitFor();
  await page.reload();
  await page.getByTestId('hold-session').filter({ hasText: 'Bạn đang giữ 4 ghế' }).waitFor();
  const session = await (await page.request.get(`${api}/showtimes/${fixture.showId}/seats/my-hold`, { headers: { Authorization: `Bearer ${fixture.token}` } })).json();
  assert.equal(session.heldUntilUtc, deadline); assert.equal(session.heldSeats.length, 4);
  console.log('FIX03_BROWSER|passed=1|failed=0|selected=5|conflict=409|partialHolds=0|retryHeld=4|reloadDeadlinePreserved=true');
} finally {
  await browser?.close(); vite.kill(); // only the child dev server created above
}
