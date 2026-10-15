import test from 'node:test';
import assert from 'node:assert/strict';
import axiosClient from '../src/api/axiosClient.js';
import {
  getEventSeatPricing,
  updateEventSeatPricing,
  getEvents,
  getAdminEvents,
  getEventById,
  createEvent,
  updateEvent,
  publishEvent,
  unpublishEvent,
  getShowtimes,
  createShowtime,
  updateShowtimeSalesStatus,
  importSeatingChart,
  uploadSeatingChart,
  previewSeatingChart,
  getOpenEvents,
} from '../src/api/eventsApi.js';

test('eventsApi getEventSeatPricing and updateEventSeatPricing call the expected endpoints', async () => {
  const calls = [];
  const originalGet = axiosClient.get;
  const originalPut = axiosClient.put;

  try {
    axiosClient.get = async (url, ...args) => {
      calls.push(['get', url, ...args]);
      return { data: [{ id: 1, name: 'VIP', price: 500000 }] };
    };
    axiosClient.put = async (url, ...args) => {
      calls.push(['put', url, ...args]);
      return { data: { success: true, message: 'Cập nhật thành công!' } };
    };

    const pricing = await getEventSeatPricing(42);
    assert.deepEqual(pricing, [{ id: 1, name: 'VIP', price: 500000 }]);
    assert.equal(calls[0][0], 'get');
    assert.equal(calls[0][1], '/events/42/pricing');

    const payload = { categories: [{ categoryId: 1, price: 650000 }] };
    const updateRes = await updateEventSeatPricing(42, payload);
    assert.deepEqual(updateRes, { success: true, message: 'Cập nhật thành công!' });
    assert.equal(calls[1][0], 'put');
    assert.equal(calls[1][1], '/events/42/pricing');
    assert.deepEqual(calls[1][2], payload);
  } finally {
    axiosClient.get = originalGet;
    axiosClient.put = originalPut;
  }
});

test('eventsApi exports call expected HTTP routes and handle methods', async () => {
  const calls = [];
  const originalGet = axiosClient.get;
  const originalPost = axiosClient.post;
  const originalPut = axiosClient.put;

  try {
    axiosClient.get = async (url, ...args) => {
      calls.push(['get', url, ...args]);
      if (url.includes('/events/open')) {
        return { data: { items: [], nextCursor: null } };
      }
      return { data: [{ id: 10, name: 'Test Event' }] };
    };
    axiosClient.post = async (url, ...args) => {
      calls.push(['post', url, ...args]);
      return { data: { id: 10, success: true } };
    };
    axiosClient.put = async (url, ...args) => {
      calls.push(['put', url, ...args]);
      return { data: { id: 10, success: true } };
    };

    await getEvents();
    assert.equal(calls[calls.length - 1][1], '/events/admin');

    await getAdminEvents();
    assert.equal(calls[calls.length - 1][1], '/events/admin');

    await getEventById(10);
    assert.equal(calls[calls.length - 1][1], '/events/10');

    await createEvent({ name: 'Rock Concert' });
    assert.equal(calls[calls.length - 1][1], '/events');

    await updateEvent(10, { name: 'Updated Concert' });
    assert.equal(calls[calls.length - 1][1], '/events/10');

    await publishEvent(10);
    assert.equal(calls[calls.length - 1][1], '/events/10/publish');

    await unpublishEvent(10);
    assert.equal(calls[calls.length - 1][1], '/events/10/unpublish');

    await getShowtimes(10);
    assert.equal(calls[calls.length - 1][1], '/events/10/showtimes');

    await createShowtime(10, { startTime: '2030-01-01T10:00:00Z' });
    assert.equal(calls[calls.length - 1][1], '/events/10/showtimes');

    await updateShowtimeSalesStatus(20, 'OnSale');
    assert.equal(calls[calls.length - 1][1], '/showtimes/20/status');

    await importSeatingChart(20, { seats: [] });
    assert.equal(calls[calls.length - 1][1], '/showtimes/20/seats/import');

    const fakeFile = new File(['{}'], 'chart.json', { type: 'application/json' });
    await uploadSeatingChart(20, fakeFile);
    assert.equal(calls[calls.length - 1][1], '/showtimes/20/seats/import');

    await previewSeatingChart(20, fakeFile);
    assert.equal(calls[calls.length - 1][1], '/showtimes/20/seats/preview');

    await getOpenEvents('cursor-1');
    assert.ok(calls[calls.length - 1][1].includes('/events/open'));
  } finally {
    axiosClient.get = originalGet;
    axiosClient.post = originalPost;
    axiosClient.put = originalPut;
  }
});
