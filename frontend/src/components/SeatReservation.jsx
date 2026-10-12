import { useState, useEffect, useRef, useCallback } from 'react';
import {
  getPerformanceSeats,
  holdSeat,
  releaseSeat,
  getMyHoldSession,
} from '../api/seatReservationApi';

/**
 * Component Chọn ghế & Giữ chỗ có thời hạn (SCRUM-17)
 * Đáp ứng đầy đủ 5 Acceptance Criteria:
 * AC 1: Chọn ghế trống -> chuyển "tôi đang giữ" + đồng hồ đếm ngược 10 phút.
 * AC 2: Chọn ghế người khác đang giữ -> từ chối "ghế vừa có người chọn" + tự cập nhật sơ đồ.
 * AC 3: Chọn nhiều ghế -> dùng chung 1 thời hạn tính từ ghế đầu tiên, 1 đồng hồ duy nhất.
 * AC 4: Suất diễn đã đóng bán -> từ chối "suất không còn mở bán".
 * AC 5: Đồng hồ client lệch 5 phút -> tính chính xác theo Server-authoritative timestamp.
 */
export default function SeatReservation({ performanceId, currentUserId }) {
  const [seats, setSeats] = useState([]);
  const [loading, setLoading] = useState(true);
  const [errorMessage, setErrorMessage] = useState(null);
  const [heldSeats, setHeldSeats] = useState([]);

  // Timer State (Server-authoritative)
  const [remainingSeconds, setRemainingSeconds] = useState(0);
  const [serverTimeOffsetMs, setServerTimeOffsetMs] = useState(0); // Độ lệch giữa server và client
  const [expiresAtUtc, setExpiresAtUtc] = useState(null);

  const timerRef = useRef(null);

  // Calibrate client clock with server time (AC 5)
  const calibrateClock = useCallback((serverTimeUtcStr, heldUntilUtcStr, initialRemainingSeconds) => {
    if (!serverTimeUtcStr) return;

    const clientNow = Date.now();
    const serverTimeMs = new Date(serverTimeUtcStr).getTime();
    // serverOffset: giá trị cộng vào Date.now() để ra giờ server thực tế
    const offset = serverTimeMs - clientNow;
    setServerTimeOffsetMs(offset);

    if (heldUntilUtcStr) {
      setExpiresAtUtc(heldUntilUtcStr);
      const expiresMs = new Date(heldUntilUtcStr).getTime();
      const currentServerTime = clientNow + offset;
      const calculatedRemaining = Math.max(0, Math.floor((expiresMs - currentServerTime) / 1000));
      setRemainingSeconds(calculatedRemaining);
    } else if (typeof initialRemainingSeconds === 'number') {
      setRemainingSeconds(initialRemainingSeconds);
    }
  }, []);

  // Countdown Interval loop based on calibrated clock (AC 5)
  useEffect(() => {
    if (!expiresAtUtc || remainingSeconds <= 0) {
      if (timerRef.current) clearInterval(timerRef.current);
      return;
    }

    timerRef.current = setInterval(() => {
      const currentCalibratedServerTime = Date.now() + serverTimeOffsetMs;
      const targetExpiryMs = new Date(expiresAtUtc).getTime();
      const diffSeconds = Math.max(0, Math.floor((targetExpiryMs - currentCalibratedServerTime) / 1000));

      setRemainingSeconds(diffSeconds);

      if (diffSeconds <= 0) {
        clearInterval(timerRef.current);
        setExpiresAtUtc(null);
        setHeldSeats([]);
        // Tự động tải lại sơ đồ khi phiên giữ chỗ hết hạn
        loadSeats();
      }
    }, 1000);

    return () => {
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [expiresAtUtc, serverTimeOffsetMs]);

  // Load seats from server
  const loadSeats = useCallback(async () => {
    try {
      setLoading(true);
      const data = await getPerformanceSeats(performanceId, currentUserId);
      setSeats(data.seats || []);

      // Kiểm tra xem user có phiên giữ chỗ sẵn có không
      const session = await getMyHoldSession(performanceId, currentUserId).catch(() => null);
      if (session && session.remainingSeconds > 0) {
        setHeldSeats(session.heldSeats || []);
        calibrateClock(session.serverTimeUtc, session.heldUntilUtc, session.remainingSeconds);
      }
    } catch (err) {
      console.error('Lỗi khi tải sơ đồ ghế:', err);
    } finally {
      setLoading(false);
    }
  }, [performanceId, currentUserId, calibrateClock]);

  useEffect(() => {
    loadSeats();
  }, [loadSeats]);

  // Xử lý khi user click vào một ghế
  const handleSeatClick = async (seat) => {
    setErrorMessage(null);

    // Nếu ghế này đã được chính user giữ -> Cho phép user click để hủy giữ (Release)
    if (seat.displayStatus === 'HeldByMe') {
      const prevHeldSeats = [...heldSeats];
      const prevSeats = [...seats];
      const prevExpiresAtUtc = expiresAtUtc;
      const prevRemainingSeconds = remainingSeconds;

      // Optimistic update
      const newHeldSeats = prevHeldSeats.filter((s) => s.id !== seat.id);
      setHeldSeats(newHeldSeats);
      setSeats((prev) =>
        prev.map((s) => (s.id === seat.id ? { ...s, displayStatus: 'Available', canSelect: true } : s))
      );

      if (newHeldSeats.length === 0) {
        setExpiresAtUtc(null);
        setRemainingSeconds(0);
      }

      try {
        await releaseSeat(performanceId, seat.id, currentUserId);
      } catch (err) {
        console.error('Lỗi hủy giữ ghế:', err);
        // Rollback
        setHeldSeats(prevHeldSeats);
        setSeats(prevSeats);
        setExpiresAtUtc(prevExpiresAtUtc);
        setRemainingSeconds(prevRemainingSeconds);
        
        const status = err.response?.status;
        const msg = err.response?.data?.message || 'Không thể hủy ghế. Vui lòng thử lại.';
        if (status === 403) {
            setErrorMessage('Từ chối truy cập: ' + msg);
        } else {
            setErrorMessage(msg);
        }
      }
      return;
    }

    // Nếu ghế không được phép chọn (đã bán hoặc người khác đang giữ)
    if (!seat.canSelect) {
      return;
    }

    try {
      // Gửi yêu cầu giữ ghế (AC 1, AC 2, AC 3, AC 4, AC 5)
      const result = await holdSeat(performanceId, seat.id, currentUserId);

      if (result.success) {
        // AC 1: Chuyển ghế sang "tôi đang giữ"
        setSeats((prev) =>
          prev.map((s) => (s.id === seat.id ? { ...s, displayStatus: 'HeldByMe' } : s))
        );
        setHeldSeats(result.heldSeats || []);

        // AC 3 & AC 5: Đồng bộ thời hạn giữ chỗ chung cho tất cả ghế & Calibrate lệch giờ
        calibrateClock(result.serverTimeUtc, result.heldUntilUtc, result.remainingSeconds);
      }
    } catch (error) {
      const status = error.response?.status;
      const data = error.response?.data;

      // AC 2: Ghế vừa có người khác chọn (409 Conflict)
      if (status === 409) {
        const msg = data?.message || 'ghế vừa có người chọn';
        setErrorMessage(msg);

        // Tự động cập nhật trạng thái mới nhất của ghế trên sơ đồ
        setSeats((prev) =>
          prev.map((s) =>
            s.id === seat.id ? { ...s, displayStatus: 'HeldByOther', canSelect: false } : s
          )
        );
      }
      // AC 4: Suất diễn đã đóng bán (400 Bad Request)
      else if (status === 400 && data?.errorCode === 'SALES_CLOSED') {
        const msg = data?.message || 'suất không còn mở bán';
        setErrorMessage(msg);
      } else {
        setErrorMessage(data?.message || 'Không thể chọn ghế. Vui lòng thử lại.');
      }
    }
  };

  const formatTimer = (totalSeconds) => {
    const mins = Math.floor(totalSeconds / 60);
    const secs = totalSeconds % 60;
    return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  };

  if (loading) {
    return <div style={{ padding: '24px', textAlign: 'center' }}>Đang tải sơ đồ ghế...</div>;
  }

  return (
    <div style={{ maxWidth: '800px', margin: '0 auto', padding: '20px' }}>
      {/* Header & Đồng hồ đếm ngược (AC 1, AC 3, AC 5) */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          marginBottom: '16px',
          padding: '12px 16px',
          backgroundColor: '#1f2028',
          borderRadius: '8px',
          color: '#fff',
        }}
      >
        <h3 style={{ margin: 0, fontSize: '18px' }}>Chọn ghế suất diễn #{performanceId}</h3>

        {remainingSeconds > 0 && (
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
              backgroundColor: '#374151',
              padding: '6px 14px',
              borderRadius: '20px',
              fontWeight: '600',
              color: remainingSeconds < 60 ? '#ef4444' : '#10b981',
            }}
          >
            <span>⏱️ Thời gian giữ chỗ:</span>
            <span style={{ fontSize: '16px', fontFamily: 'monospace' }}>
              {formatTimer(remainingSeconds)}
            </span>
          </div>
        )}
      </div>

      {/* Thông báo lỗi / từ chối (AC 2, AC 4) */}
      {errorMessage && (
        <div
          style={{
            padding: '12px 16px',
            marginBottom: '16px',
            backgroundColor: '#fee2e2',
            border: '1px solid #f87171',
            borderRadius: '6px',
            color: '#b91c1c',
            fontWeight: '500',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
          }}
        >
          <span>⚠️ {errorMessage}</span>
          <button
            onClick={() => setErrorMessage(null)}
            style={{ background: 'none', border: 'none', cursor: 'pointer', fontWeight: 'bold' }}
          >
            ✕
          </button>
        </div>
      )}

      {/* Chú thích màu sắc */}
      <div style={{ display: 'flex', gap: '20px', marginBottom: '20px', fontSize: '14px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
          <div style={{ width: '20px', height: '20px', backgroundColor: '#e5e7eb', borderRadius: '4px' }} />
          <span>Ghế trống</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
          <div style={{ width: '20px', height: '20px', backgroundColor: '#3b82f6', borderRadius: '4px' }} />
          <span>Tôi đang giữ</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
          <div style={{ width: '20px', height: '20px', backgroundColor: '#f59e0b', borderRadius: '4px' }} />
          <span>Người khác giữ</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
          <div style={{ width: '20px', height: '20px', backgroundColor: '#9ca3af', borderRadius: '4px' }} />
          <span>Đã bán</span>
        </div>
      </div>

      {/* Lưới hiển thị ghế (Seat Grid) */}
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fill, minmax(60px, 1fr))',
          gap: '10px',
          padding: '20px',
          backgroundColor: '#111827',
          borderRadius: '8px',
        }}
      >
        {seats.map((seat) => {
          let bgColor = '#e5e7eb';
          let textColor = '#111827';
          let cursor = 'pointer';

          if (seat.displayStatus === 'HeldByMe') {
            bgColor = '#3b82f6';
            textColor = '#ffffff';
          } else if (seat.displayStatus === 'HeldByOther') {
            bgColor = '#f59e0b';
            textColor = '#ffffff';
            cursor = 'not-allowed';
          } else if (seat.displayStatus === 'Sold') {
            bgColor = '#4b5563';
            textColor = '#9ca3af';
            cursor = 'not-allowed';
          }

          return (
            <button
              key={seat.id}
              onClick={() => handleSeatClick(seat)}
              disabled={!seat.canSelect && seat.displayStatus !== 'HeldByMe'}
              style={{
                padding: '10px',
                backgroundColor: bgColor,
                color: textColor,
                border: 'none',
                borderRadius: '6px',
                fontWeight: '600',
                cursor: cursor,
                transition: 'all 0.2s',
              }}
              title={`Hàng ${seat.row} - Ghế ${seat.number}`}
            >
              {seat.row}
              {seat.number}
            </button>
          );
        })}
      </div>
    </div>
  );
}

