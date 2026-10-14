// Public browsing uses the server-filtered sales feed, never Organizer management data.
export async function openEvents(client, cursor) {
  const response = cursor ? await client.get('/events/open', { params: { cursor } }) : await client.get('/events/open');
  return response.data;
}

// One in-flight cursor request, append without duplicates, and ignore unmounted responses.
// No private management-data fallback and no client-side feed cache.
export function createPublicFeed({ page, onChange }) {
  let alive = true, busy = false, started = false;
  let state = { items: [], nextCursor: null, loading: true, error: null };
  const publish = patch => { state = { ...state, ...patch }; if (alive) onChange(state); };
  async function loadNext() {
    if (!alive || busy || started && !state.nextCursor) return;
    busy = true;
    const cursor = started ? state.nextCursor : null;
    publish({ loading: true, error: null });
    try {
      const response = await page(cursor);
      if (!alive) return;
      if (!Array.isArray(response.items) || response.nextCursor && response.nextCursor === cursor)
        throw new Error('Phản hồi phân trang không hợp lệ.');
      const items = new Map(state.items.map(item => [item.eventId, item]));
      response.items.forEach(item => items.set(item.eventId, item));
      started = true;
      publish({ items: [...items.values()], nextCursor: response.nextCursor || null, loading: false });
    } catch (error) {
      if (alive) publish({ loading: false, error: error.message || 'Không thể tải sự kiện.' });
    } finally { busy = false; }
  }
  return { loadNext, snapshot: () => state, dispose() { alive = false; } };
}
