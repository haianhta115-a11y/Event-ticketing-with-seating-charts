import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { getEvents } from '../api/eventsApi';
import { getEventStatusInfo } from '../constants/eventStatus';

export default function EventList() {
  const [events, setEvents] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const handleRetry = () => {
    setLoading(true);
    setError(null);
    getEvents()
      .then((data) => {
        setEvents(data);
      })
      .catch((err) => {
        console.error('Error fetching events:', err);
        setError(err?.message || 'Có lỗi xảy ra khi tải danh sách sự kiện.');
      })
      .finally(() => {
        setLoading(false);
      });
  };

  useEffect(() => {
    let ignore = false;

    getEvents()
      .then((data) => {
        if (!ignore) {
          setEvents(data);
        }
      })
      .catch((err) => {
        console.error('Error fetching events:', err);
        if (!ignore) {
          setError(err?.message || 'Có lỗi xảy ra khi tải danh sách sự kiện.');
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoading(false);
        }
      });

    return () => {
      ignore = true;
    };
  }, []);

  return (
    <div style={{ padding: '24px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '24px' }}>
        <div style={{ textAlign: 'left' }}>
          <h2 style={{ margin: '0 0 4px 0', fontSize: '24px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
            Danh sách sự kiện
          </h2>
          <p style={{ margin: 0, color: 'var(--text, #9ca3af)', fontSize: '14px' }}>
            Quản lý các sự kiện, suất diễn và theo dõi trạng thái mở bán
          </p>
        </div>
        <Link
          to="/events/create"
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '8px',
            padding: '10px 18px',
            backgroundColor: '#2563eb',
            color: '#ffffff',
            borderRadius: '6px',
            textDecoration: 'none',
            fontWeight: '600',
            fontSize: '14px',
            boxShadow: '0 1px 2px rgba(0, 0, 0, 0.05)',
          }}
        >
          <span>+</span> Tạo sự kiện
        </Link>
      </div>

      {loading && (
        <div
          style={{
            padding: '48px 16px',
            textAlign: 'center',
            backgroundColor: 'var(--code-bg, #1f2028)',
            borderRadius: '8px',
            border: '1px dashed var(--border, #2e303a)',
            color: 'var(--text, #9ca3af)',
          }}
        >
          <div style={{ fontSize: '16px', fontWeight: '500', color: 'var(--text-h, #f1f5f9)' }}>
            Đang tải danh sách sự kiện...
          </div>
        </div>
      )}

      {!loading && error && (
        <div
          style={{
            padding: '24px',
            textAlign: 'center',
            backgroundColor: '#fef2f2',
            border: '1px solid #fecaca',
            borderRadius: '8px',
            color: '#b91c1c',
          }}
        >
          <p style={{ margin: '0 0 12px 0', fontWeight: '500' }}>{error}</p>
          <button
            onClick={handleRetry}
            style={{
              padding: '8px 16px',
              backgroundColor: '#dc2626',
              color: '#ffffff',
              border: 'none',
              borderRadius: '4px',
              cursor: 'pointer',
              fontWeight: '500',
            }}
          >
            Thử lại
          </button>
        </div>
      )}

      {!loading && !error && events.length === 0 && (
        <div
          style={{
            padding: '48px 16px',
            textAlign: 'center',
            backgroundColor: 'var(--code-bg, #1f2028)',
            borderRadius: '8px',
            border: '1px dashed var(--border, #2e303a)',
            color: 'var(--text, #9ca3af)',
          }}
        >
          <h4 style={{ margin: '0 0 8px 0', fontSize: '18px', color: 'var(--text-h, #f1f5f9)' }}>
            Chưa có sự kiện nào
          </h4>
          <p style={{ margin: '0 0 16px 0', fontSize: '14px', color: 'var(--text, #9ca3af)' }}>
            Hệ thống chưa ghi nhận sự kiện nào. Bạn có thể bắt đầu tạo sự kiện đầu tiên ngay bây giờ.
          </p>
          <Link
            to="/events/create"
            style={{
              padding: '8px 16px',
              backgroundColor: '#2563eb',
              color: '#ffffff',
              borderRadius: '4px',
              textDecoration: 'none',
              fontSize: '14px',
              fontWeight: '500',
            }}
          >
            Tạo sự kiện ngay
          </Link>
        </div>
      )}

      {!loading && !error && events.length > 0 && (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
            gap: '20px',
          }}
        >
          {events.map((evt) => {
            const statusInfo = getEventStatusInfo(evt.status);
            return (
              <Link
                key={evt.id}
                to={`/events/${evt.id}`}
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  backgroundColor: 'var(--code-bg, #1f2028)',
                  borderRadius: '10px',
                  border: '1px solid var(--border, #2e303a)',
                  overflow: 'hidden',
                  boxShadow: '0 1px 3px rgba(0, 0, 0, 0.1)',
                  textDecoration: 'none',
                  color: 'inherit',
                  transition: 'transform 0.15s ease, box-shadow 0.15s ease',
                  cursor: 'pointer',
                  textAlign: 'left',
                }}
              >
                <div style={{ height: '170px', width: '100%', overflow: 'hidden', backgroundColor: 'var(--bg, #16171d)', position: 'relative' }}>
                  <img
                    src={evt.imageUrl}
                    alt={evt.name}
                    style={{ width: '100%', height: '100%', objectFit: 'cover' }}
                    onError={(e) => {
                      e.currentTarget.src = 'https://images.unsplash.com/photo-1501386761578-eac5c94b800a?w=600&auto=format&fit=crop&q=80';
                    }}
                  />
                  <span
                    style={{
                      position: 'absolute',
                      top: '12px',
                      right: '12px',
                      padding: '4px 10px',
                      borderRadius: '12px',
                      fontSize: '12px',
                      fontWeight: '600',
                      ...statusInfo.style,
                    }}
                  >
                    {statusInfo.label}
                  </span>
                </div>

                <div style={{ padding: '16px', flex: '1', display: 'flex', flexDirection: 'column' }}>
                  <h3
                    style={{
                      margin: '0 0 8px 0',
                      fontSize: '17px',
                      fontWeight: '600',
                      color: 'var(--text-h, #f1f5f9)',
                      lineHeight: '1.4',
                    }}
                  >
                    {evt.name}
                  </h3>

                  <p
                    style={{
                      margin: '0 0 12px 0',
                      fontSize: '13px',
                      color: 'var(--text, #9ca3af)',
                      lineHeight: '1.5',
                      flex: '1',
                    }}
                  >
                    {evt.description}
                  </p>

                  <div style={{ borderTop: '1px solid var(--border, #2e303a)', paddingTop: '12px', fontSize: '13px' }}>
                    <div style={{ marginBottom: '6px', display: 'flex', alignItems: 'flex-start', gap: '6px' }}>
                      <span style={{ fontWeight: '500', minWidth: '65px', color: 'var(--text-h, #f1f5f9)' }}>Địa điểm:</span>
                      <span style={{ color: 'var(--text, #9ca3af)' }}>{evt.location}</span>
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                      <span style={{ fontWeight: '500', minWidth: '65px', color: 'var(--text-h, #f1f5f9)' }}>Suất diễn:</span>
                      <span
                        style={{
                          backgroundColor: 'var(--bg, #16171d)',
                          padding: '2px 8px',
                          borderRadius: '4px',
                          fontWeight: '600',
                          color: 'var(--text-h, #f1f5f9)',
                          border: '1px solid var(--border, #2e303a)',
                        }}
                      >
                        {evt.showCount} suất
                      </span>
                    </div>
                  </div>
                </div>
              </Link>
            );
          })}
        </div>
      )}
    </div>
  );
}
