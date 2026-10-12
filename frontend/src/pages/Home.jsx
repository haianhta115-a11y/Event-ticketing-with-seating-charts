import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { getOpenEvents } from '../api/eventsApi';

const Home = () => {
  const [events, setEvents] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const navigate = useNavigate();

  useEffect(() => {
    const fetchEvents = async () => {
      try {
        setLoading(true);
        const payload = await getOpenEvents();

        const items = payload.items || [];
        setEvents(items);
      } catch (err) {
        setError(err.message);
      } finally {
        setLoading(false);
      }
    };

    fetchEvents();
  }, []);

  if (loading) return <div style={{ textAlign: 'center', marginTop: '50px' }}>Đang tải sự kiện...</div>;

  if (error) return <div style={{ textAlign: 'center', marginTop: '50px', color: 'red' }}>Lỗi: {error}</div>;

  return (
    <div style={{ padding: '20px', maxWidth: '1200px', margin: '0 auto' }}>
      <h1>Sự kiện đang mở bán</h1>
      {events.length === 0 ? (
        <p style={{ textAlign: 'center', color: '#555' }}>Hiện chưa có sự kiện nào mở bán</p>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))', gap: '20px', marginTop: '20px' }}>
          {events.map((event) => (
            <div key={event.eventId} style={{ border: '1px solid #ddd', borderRadius: '8px', overflow: 'hidden', cursor: 'pointer', display: 'flex', flexDirection: 'column' }}>
              {event.imageUrl ? (
                <img src={event.imageUrl} alt={event.name} style={{ width: '100%', height: '150px', objectFit: 'cover' }} />
              ) : (
                <div style={{ width: '100%', height: '150px', backgroundColor: '#ccc', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>Chưa có ảnh</div>
              )}
              <div style={{ padding: '15px', flex: 1, display: 'flex', flexDirection: 'column' }}>
                <h3 style={{ margin: '0 0 10px 0', fontSize: '1.2rem' }}>{event.name}</h3>
                <p style={{ margin: '0 0 5px 0', color: '#555' }}>Địa điểm: {event.location}</p>
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
    </div>
  );
};

export default Home;
