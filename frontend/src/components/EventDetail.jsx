import { useState, useEffect } from 'react';
import { useParams, Link } from 'react-router-dom';
import { getEventById, getShowtimes, updateShowtimeSalesStatus } from '../api/eventsApi';
import { EVENT_STATUS, getEventStatusInfo, SHOWTIME_STATUS, getShowtimeStatusInfo } from '../constants/eventStatus';
import ShowtimeForm from './ShowtimeForm';

const formatDateTime = (isoString) => {
  if (!isoString) return '';
  const d = new Date(isoString);
  return d.toLocaleString('vi-VN', {
    hour: '2-digit',
    minute: '2-digit',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
};

export default function EventDetail() {
  const { id } = useParams();
  const [event, setEvent] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const [showtimes, setShowtimes] = useState([]);
  const [loadingShowtimes, setLoadingShowtimes] = useState(true);
  const [showtimesError, setShowtimesError] = useState(null);

  // SCRUM-86 & 87: Quản lý trạng thái Mở bán / Đóng bán
  const [actionError, setActionError] = useState(null);
  const [actionSuccess, setActionSuccess] = useState(null);
  const [actionLoadingId, setActionLoadingId] = useState(null);

  const handleToggleSales = async (showtime) => {
    setActionError(null);
    setActionSuccess(null);
    setActionLoadingId(showtime.id);

    try {
      const newStatus =
        showtime.status === SHOWTIME_STATUS.ON_SALE
          ? SHOWTIME_STATUS.CLOSED
          : SHOWTIME_STATUS.ON_SALE;

      const updated = await updateShowtimeSalesStatus(showtime.id, newStatus);
      setShowtimes((prev) =>
        prev.map((s) => (s.id === showtime.id ? { ...s, status: updated.status } : s))
      );
      setActionSuccess(
        newStatus === SHOWTIME_STATUS.ON_SALE
          ? 'Đã mở bán thành công suất diễn!'
          : 'Đã đóng bán suất diễn thành công!'
      );
    } catch (err) {
      setActionError(err?.message || 'Có lỗi xảy ra khi đổi trạng thái');
    } finally {
      setActionLoadingId(null);
    }
  };

  const fetchShowtimes = () => {
    setLoadingShowtimes(true);
    setShowtimesError(null);
    getShowtimes(id)
      .then((data) => {
        setShowtimes(data);
      })
      .catch((err) => {
        setShowtimesError(err?.message || 'Có lỗi xảy ra khi tải danh sách suất diễn.');
      })
      .finally(() => {
        setLoadingShowtimes(false);
      });
  };

  useEffect(() => {
    let ignore = false;

    // Tải thông tin sự kiện
    getEventById(id)
      .then((data) => {
        if (!ignore) {
          setEvent(data);
        }
      })
      .catch((err) => {
        if (!ignore) {
          setError(err);
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoading(false);
        }
      });

    // Tải danh sách suất diễn
    getShowtimes(id)
      .then((data) => {
        if (!ignore) {
          setShowtimes(data);
        }
      })
      .catch((err) => {
        if (!ignore) {
          setShowtimesError(err?.message || 'Có lỗi xảy ra khi tải danh sách suất diễn.');
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoadingShowtimes(false);
        }
      });

    return () => {
      ignore = true;
    };
  }, [id]);

  if (loading) {
    return (
      <div
        style={{
          maxWidth: '850px',
          margin: '40px auto',
          padding: '48px 16px',
          textAlign: 'center',
          backgroundColor: 'var(--code-bg, #1f2028)',
          borderRadius: '8px',
          border: '1px dashed var(--border, #2e303a)',
          color: 'var(--text, #9ca3af)',
        }}
      >
        <p style={{ margin: 0, fontSize: '16px', fontWeight: '500', color: 'var(--text-h, #f1f5f9)' }}>
          Đang tải thông tin sự kiện...
        </p>
      </div>
    );
  }

  if (error || !event) {
    return (
      <div
        style={{
          maxWidth: '600px',
          margin: '40px auto',
          padding: '24px',
          backgroundColor: '#fff1f2',
          border: '1px solid #fecdd3',
          borderRadius: '8px',
          textAlign: 'center',
          color: '#9f1239',
        }}
      >
        <h3 style={{ margin: '0 0 8px 0', fontSize: '18px' }}>Từ chối truy cập</h3>
        <p style={{ margin: '0 0 20px 0', fontSize: '14px' }}>Bạn không có quyền truy cập sự kiện này</p>
        <Link
          to="/events"
          style={{
            display: 'inline-block',
            padding: '8px 16px',
            backgroundColor: '#e11d48',
            color: '#ffffff',
            borderRadius: '6px',
            textDecoration: 'none',
            fontSize: '14px',
            fontWeight: '500',
          }}
        >
          Quay lại danh sách sự kiện
        </Link>
      </div>
    );
  }

  const statusInfo = getEventStatusInfo(event.status);
  const isDraft = event.status === EVENT_STATUS.DRAFT;

  // Sắp xếp suất diễn tăng dần theo thời gian bắt đầu
  const sortedShowtimes = [...showtimes].sort(
    (a, b) => new Date(a.startTime).getTime() - new Date(b.startTime).getTime()
  );

  return (
    <div
      style={{
        maxWidth: '850px',
        margin: '30px auto',
        padding: '24px',
        backgroundColor: 'var(--code-bg, #1f2028)',
        borderRadius: '8px',
        border: '1px solid var(--border, #2e303a)',
        boxShadow: '0 1px 3px rgba(0,0,0,0.1)',
        textAlign: 'left',
      }}
    >
      {/* Navigation bar */}
      <div style={{ marginBottom: '20px' }}>
        <Link
          to="/events"
          style={{
            color: 'var(--accent, #3b82f6)',
            textDecoration: 'none',
            fontSize: '14px',
            fontWeight: '500',
            display: 'inline-flex',
            alignItems: 'center',
            gap: '6px',
          }}
        >
          &larr; Quay lại danh sách sự kiện
        </Link>
      </div>

      {/* Draft banner notice */}
      {isDraft && (
        <div
          style={{
            padding: '12px 16px',
            marginBottom: '20px',
            backgroundColor: '#fffbeb',
            border: '1px solid #fde68a',
            borderRadius: '6px',
            color: '#92400e',
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            fontSize: '14px',
            fontWeight: '500',
          }}
        >
          <span>ℹ️</span> Chưa hiển thị với người mua
        </div>
      )}

      {/* Main Event Header */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'flex-start',
          flexWrap: 'wrap',
          gap: '16px',
          marginBottom: '24px',
          borderBottom: '1px solid var(--border, #2e303a)',
          paddingBottom: '20px',
        }}
      >
        <div style={{ flex: '1', minWidth: '280px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '8px', flexWrap: 'wrap' }}>
            <h1 style={{ margin: 0, fontSize: '24px', color: 'var(--text-h, #f1f5f9)', fontWeight: '700' }}>
              {event.name}
            </h1>
            <span
              style={{
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
          <div style={{ color: 'var(--text, #9ca3af)', fontSize: '14px', display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span style={{ fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>Địa điểm:</span>
            <span>{event.location}</span>
          </div>
        </div>
      </div>

      {/* Description section */}
      <div style={{ marginBottom: '32px' }}>
        <h3 style={{ margin: '0 0 10px 0', fontSize: '16px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
          Mô tả sự kiện
        </h3>
        <p style={{ margin: 0, color: 'var(--text, #9ca3af)', fontSize: '14px', lineHeight: '1.6', whiteSpace: 'pre-line' }}>
          {event.description}
        </p>
      </div>

      {/* Phân hệ Suất diễn */}
      <div
        style={{
          borderTop: '2px dashed var(--border, #2e303a)',
          paddingTop: '24px',
          marginTop: '24px',
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px', flexWrap: 'wrap', gap: '8px' }}>
          <h3 style={{ margin: 0, fontSize: '18px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
            Danh Sách Suất Diễn ({sortedShowtimes.length})
          </h3>
          <span style={{ fontSize: '12px', color: 'var(--text, #9ca3af)' }}>
            Sắp xếp theo thứ tự thời gian tăng dần
          </span>
        </div>

        {/* Thông báo lỗi / thành công khi đổi trạng thái mở/đóng bán */}
        {actionError && (
          <div
            style={{
              padding: '12px 16px',
              marginBottom: '16px',
              backgroundColor: '#fef2f2',
              border: '1px solid #fecaca',
              borderRadius: '6px',
              color: '#b91c1c',
              fontSize: '14px',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
            }}
          >
            <span>s {actionError}</span>
            <button
              onClick={() => setActionError(null)}
              style={{ background: 'none', border: 'none', color: '#b91c1c', cursor: 'pointer', fontWeight: 'bold' }}
            >
              ✕
            </button>
          </div>
        )}

        {actionSuccess && (
          <div
            style={{
              padding: '12px 16px',
              marginBottom: '16px',
              backgroundColor: '#f0fdf4',
              border: '1px solid #bbf7d0',
              borderRadius: '6px',
              color: '#15803d',
              fontSize: '14px',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
            }}
          >
            <span>✓ {actionSuccess}</span>
            <button
              onClick={() => setActionSuccess(null)}
              style={{ background: 'none', border: 'none', color: '#15803d', cursor: 'pointer', fontWeight: 'bold' }}
            >
              ✕
            </button>
          </div>
        )}

        {/* Loading suất diễn */}
        {loadingShowtimes && (
          <div
            style={{
              padding: '24px',
              textAlign: 'center',
              backgroundColor: 'var(--bg, #16171d)',
              border: '1px dashed var(--border, #2e303a)',
              borderRadius: '6px',
              color: 'var(--text, #9ca3af)',
              fontSize: '14px',
            }}
          >
            Đang tải danh sách suất diễn...
          </div>
        )}

        {/* Lỗi tải suất diễn */}
        {!loadingShowtimes && showtimesError && (
          <div
            style={{
              padding: '16px',
              backgroundColor: '#fef2f2',
              border: '1px solid #fecaca',
              borderRadius: '6px',
              color: '#b91c1c',
              fontSize: '14px',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
            }}
          >
            <span>{showtimesError}</span>
            <button
              onClick={fetchShowtimes}
              style={{
                padding: '4px 10px',
                backgroundColor: '#dc2626',
                color: '#fff',
                border: 'none',
                borderRadius: '4px',
                fontSize: '12px',
                cursor: 'pointer',
              }}
            >
              Thử lại
            </button>
          </div>
        )}

        {/* Trạng thái trống */}
        {!loadingShowtimes && !showtimesError && sortedShowtimes.length === 0 && (
          <div
            style={{
              padding: '30px 16px',
              backgroundColor: 'var(--bg, #16171d)',
              border: '1px dashed var(--border, #2e303a)',
              borderRadius: '6px',
              textAlign: 'center',
              color: 'var(--text, #9ca3af)',
            }}
          >
            <p style={{ margin: '0 0 6px 0', fontSize: '14px', fontWeight: '500', color: 'var(--text-h, #f1f5f9)' }}>
              Chưa có suất diễn nào
            </p>
            <p style={{ margin: 0, fontSize: '13px' }}>
              Hãy thêm suất diễn đầu tiên cho sự kiện bằng biểu mẫu bên dưới.
            </p>
          </div>
        )}

        {/* Danh sách các suất diễn */}
        {!loadingShowtimes && !showtimesError && sortedShowtimes.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
            {sortedShowtimes.map((s, idx) => {
              return (
                <div
                  key={s.id}
                  style={{
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    padding: '14px 16px',
                    backgroundColor: 'var(--bg, #16171d)',
                    border: '1px solid var(--border, #2e303a)',
                    borderRadius: '6px',
                    flexWrap: 'wrap',
                    gap: '10px',
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
                    <span
                      style={{
                        width: '28px',
                        height: '28px',
                        borderRadius: '50%',
                        backgroundColor: 'var(--code-bg, #1f2028)',
                        border: '1px solid var(--border, #2e303a)',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        fontSize: '12px',
                        fontWeight: '600',
                        color: 'var(--text-h, #f1f5f9)',
                      }}
                    >
                      {idx + 1}
                    </span>
                    <div>
                      <div style={{ fontSize: '14px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                        Bắt đầu: {formatDateTime(s.startTime)}
                      </div>
                      <div style={{ fontSize: '13px', color: 'var(--text, #9ca3af)', marginTop: '2px' }}>
                        {s.endTime ? `Kết thúc: ${formatDateTime(s.endTime)}` : 'Chưa thiết lập giờ kết thúc'}
                      </div>
                    </div>
                  </div>

                  <div style={{ display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' }}>
                    {/* SCRUM-86: Cột Trạng thái Suất diễn */}
                    <span
                      style={{
                        padding: '4px 12px',
                        borderRadius: '12px',
                        fontSize: '12px',
                        fontWeight: '600',
                        ...getShowtimeStatusInfo(s.status).style,
                      }}
                    >
                      {getShowtimeStatusInfo(s.status).label}
                    </span>

                    {/* Hiển thị trạng thái Sơ đồ ghế */}
                    <span
                      style={{
                        fontSize: '12px',
                        fontWeight: '500',
                        color: s.hasSeatMap ? '#22c55e' : '#f59e0b',
                        display: 'flex',
                        alignItems: 'center',
                        gap: '4px',
                      }}
                    >
                      {s.hasSeatMap ? '✓ Đã có sơ đồ ghế' : '⚠ Chưa có sơ đồ'}
                    </span>

                    {/* SCRUM-87: Nút Mở bán / Đóng bán */}
                    <button
                      onClick={() => handleToggleSales(s)}
                      disabled={actionLoadingId === s.id}
                      style={{
                        padding: '6px 14px',
                        borderRadius: '6px',
                        fontSize: '12px',
                        fontWeight: '600',
                        cursor: actionLoadingId === s.id ? 'not-allowed' : 'pointer',
                        border: 'none',
                        backgroundColor:
                          s.status === SHOWTIME_STATUS.ON_SALE
                            ? '#dc2626'
                            : '#16a34a',
                        color: '#ffffff',
                        transition: 'background-color 0.2s, opacity 0.2s',
                        opacity: actionLoadingId === s.id ? 0.6 : 1,
                      }}
                    >
                      {actionLoadingId === s.id
                        ? 'Đang xử lý...'
                        : s.status === SHOWTIME_STATUS.ON_SALE
                        ? 'Đóng bán'
                        : s.status === SHOWTIME_STATUS.CLOSED
                        ? 'Mở lại'
                        : 'Mở bán'}
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {/* Form thêm suất diễn */}
        <ShowtimeForm
          eventId={id}
          existingShowtimes={showtimes}
          onShowtimeCreated={(newShowtime) => {
            setShowtimes((prev) => [...prev, newShowtime]);
          }}
        />
      </div>
    </div>
  );
}
