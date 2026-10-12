import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import axiosClient from '../api/axiosClient';

export default function Home() {
  const [events, setEvents] = useState([]);
  const [nextCursor, setNextCursor] = useState(null);
  const [loading, setLoading] = useState(false);
  const navigate = useNavigate();

  const fetchEvents = async (cursor = null) => {
    try {
      setLoading(true);
      const url = cursor 
        ? `/events/open?limit=20&cursor=${encodeURIComponent(cursor)}` 
        : `/events/open?limit=20`;
      
      const response = await axiosClient.get(url);
      
      const items = response.data.items || [];
      const returnedCursor = response.data.nextCursor;
      
      if (cursor) {
        setEvents((prev) => [...prev, ...items]);
      } else {
        setEvents(items);
      }
      
      setNextCursor(returnedCursor);
    } catch (error) {
      console.error('Error fetching events:', error);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
// eslint-disable-next-line react-hooks/set-state-in-effect
    fetchEvents();
  }, []);

  const formatDate = (utcString) => {
    const date = new Date(utcString);
    return date.toLocaleString('vi-VN', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  };

  return (
    <div style={{ padding: '20px', maxWidth: '1200px', margin: '0 auto' }}>
      <h1>Sự kiện đang mở bán</h1>
      
      <div style={{ 
        display: 'grid', 
        gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))', 
        gap: '20px',
        marginTop: '20px'
      }}>
        {events.map((event) => (
          <div 
            key={event.eventId} 
            style={{ 
              border: '1px solid #ddd', 
              borderRadius: '8px', 
              overflow: 'hidden',
              cursor: 'pointer',
              display: 'flex',
              flexDirection: 'column'
            }}
            onClick={() => navigate(`/shows/${event.nearestShowtime.id}`)}
          >
            {event.imageUrl ? (
              <img 
                src={event.imageUrl} 
                alt={event.name} 
                style={{ width: '100%', height: '150px', objectFit: 'cover' }} 
              />
            ) : (
              <div style={{ width: '100%', height: '150px', backgroundColor: '#ccc', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                <span style={{ color: '#666' }}>Không có ảnh</span>
              </div>
            )}
            
            <div style={{ padding: '15px', flex: 1, display: 'flex', flexDirection: 'column' }}>
              <h3 style={{ margin: '0 0 10px 0', fontSize: '1.2rem' }}>{event.name}</h3>
              <p style={{ margin: '0 0 5px 0', color: '#555' }}>📍 {event.location}</p>
              <p style={{ margin: '0 0 5px 0', color: '#555', fontWeight: 'bold' }}>
                📅 {formatDate(event.nearestShowtime.startTime)}
              </p>
              <button 
                style={{ marginTop: 'auto', padding: '8px', backgroundColor: '#007bff', color: 'white', border: 'none', borderRadius: '4px', cursor: 'pointer', fontWeight: 'bold' }}
                onClick={(e) => { e.stopPropagation(); navigate(`/shows/${event.nearestShowtime.id}`); }}
              >
                Xem chi tiết
              </button>
            </div>
          </div>
        ))}
      </div>

      {loading && <div style={{ textAlign: 'center', marginTop: '20px' }}>Đang tải...</div>}

      {!loading && events.length === 0 && (
        <div style={{ textAlign: 'center', marginTop: '20px', color: '#555', fontSize: '16px' }}>
          Hiện chưa có sự kiện nào mở bán
        </div>
      )}

      {nextCursor && !loading && (
        <div style={{ textAlign: 'center', marginTop: '30px' }}>
          <button 
            onClick={() => fetchEvents(nextCursor)}
            style={{
              padding: '10px 20px',
              fontSize: '16px',
              backgroundColor: '#007bff',
              color: 'white',
              border: 'none',
              borderRadius: '4px',
              cursor: 'pointer'
            }}
          >
            Tải thêm
          </button>
        </div>
      )}
    </div>
  );
}
