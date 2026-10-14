import { useState, useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { getOpenEvents } from '../api/eventsApi';
import { createPublicFeed } from '../events/publicEvents';
import { formatVietnamTime } from '../events/management';

const Home = () => {
  const [{ items: events, nextCursor, loading, error }, setFeed] = useState({ items: [], nextCursor: null, loading: true, error: null });
  const feed = useRef(null);
  const sentinel = useRef(null);
  const navigate = useNavigate();

  useEffect(() => {
    const controller = createPublicFeed({
      page: async cursor => cursor ? getOpenEvents(cursor) : await getOpenEvents(), onChange: setFeed,
    });
    feed.current = controller;
    void controller.loadNext();
    return () => controller.dispose();
  }, []);

  useEffect(() => {
    if (!nextCursor || loading || error || !sentinel.current || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) void feed.current?.loadNext();
    });
    observer.observe(sentinel.current);
    return () => observer.disconnect();
  }, [nextCursor, loading, error]);

  if (loading && !events.length) return <div style={{ textAlign: 'center', marginTop: '50px' }}>Đang tải sự kiện...</div>;

  return (
    <div style={{ padding: '20px', maxWidth: '1200px', margin: '0 auto' }}>
      <h1>Sự kiện đang mở bán</h1>
      {error && <div role="alert">Lỗi: {error} <button onClick={() => feed.current?.loadNext()}>Thử lại</button></div>}
      {events.length === 0 && !error ? (
        <p style={{ textAlign: 'center', color: '#555' }}>Hiện chưa có sự kiện nào mở bán</p>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))', gap: '20px', marginTop: '20px' }}>
          {events.map((event) => (
            <div key={event.eventId} data-testid="public-event" data-event-id={event.eventId} style={{ border: '1px solid #ddd', borderRadius: '8px', overflow: 'hidden', cursor: 'pointer', display: 'flex', flexDirection: 'column' }}>
              {event.imageUrl ? (
                <img src={event.imageUrl} alt={event.name} style={{ width: '100%', height: '150px', objectFit: 'cover' }} />
              ) : (
                <div style={{ width: '100%', height: '150px', backgroundColor: '#ccc', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>Chưa có ảnh</div>
              )}
              <div style={{ padding: '15px', flex: 1, display: 'flex', flexDirection: 'column' }}>
                <h3 style={{ margin: '0 0 10px 0', fontSize: '1.2rem' }}>{event.name}</h3>
                <p style={{ margin: '0 0 5px 0', color: '#555' }}>Địa điểm: {event.location}</p>
                <p>Suất gần nhất: <time dateTime={event.nearestShowtime.startTime}>{formatVietnamTime(event.nearestShowtime.startTime)}</time></p>
                <button
                  style={{ marginTop: 'auto', padding: '8px', backgroundColor: '#007bff', color: 'white', border: 'none', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}
                  onClick={() => navigate(`/shows/${event.nearestShowtime.id}`)}
                >
                  Đặt vé ngay
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
      <div ref={sentinel} data-testid="feed-sentinel" aria-live="polite">
        {loading && events.length > 0 && <p>Đang tải thêm sự kiện...</p>}
        {nextCursor && <button disabled={loading} onClick={() => feed.current?.loadNext()}>Xem thêm sự kiện</button>}
      </div>
    </div>
  );
};

export default Home;
