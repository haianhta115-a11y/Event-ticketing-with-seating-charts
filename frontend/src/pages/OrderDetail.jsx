import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getOrderById } from '../api/ordersApi.js';
import { formatVietnamTime } from '../events/management.js';
import { startHoldCountdown } from '../events/holdCountdown.js';

export default function OrderDetail() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [order, setOrder] = useState(null);
  const [loading, setLoading] = useState(true);
  const [errorStatus, setErrorStatus] = useState(null);
  const [errorMessage, setErrorMessage] = useState('');
  const [remainingSeconds, setRemainingSeconds] = useState(0);
  const [isExpired, setIsExpired] = useState(false);
  const [paymentNotice, setPaymentNotice] = useState(null);

  useEffect(() => {
    let active = true;
    const fetchOrder = async () => {
      try {
        setLoading(true);
        setErrorStatus(null);
        const data = await getOrderById(id);
        if (!active) return;
        setOrder(data);
        const expired = Boolean(data.isExpired || data.status === 'Expired' || data.remainingSeconds <= 0);
        setIsExpired(expired);
        setRemainingSeconds(expired ? 0 : data.remainingSeconds || 0);
      } catch (err) {
        if (!active) return;
        const status = err?.response?.status || 500;
        setErrorStatus(status);
        if (status === 403) {
          setErrorMessage('Bạn không có quyền xem đơn hàng này.');
        } else if (status === 404) {
          setErrorMessage('Đơn hàng không tồn tại.');
        } else {
          setErrorMessage(err?.response?.data?.message || 'Không thể tải thông tin đơn hàng.');
        }
      } finally {
        if (active) setLoading(false);
      }
    };

    fetchOrder();
    return () => {
      active = false;
    };
  }, [id]);

  useEffect(() => {
    if (!order || order.status !== 'Pending' || isExpired) {
      return;
    }

    const serverOffset = order.serverTimeUtc
      ? Date.now() - new Date(order.serverTimeUtc).getTime()
      : 0;

    const stop = startHoldCountdown(
      {
        expiresAtUtc: order.expiresAtUtc,
        serverTimeOffsetMs: serverOffset,
        onTick: (remaining) => {
          setRemainingSeconds(remaining);
          if (remaining <= 0) {
            setIsExpired(true);
          }
        },
        onExpired: () => {
          setRemainingSeconds(0);
          setIsExpired(true);
        },
      }
    );

    return () => stop();
  }, [order, isExpired]);

  const formatPrice = (amount) => {
    return Number(amount || 0).toLocaleString('vi-VN') + ' đ';
  };

  const formatTimer = (totalSeconds) => {
    const sec = Math.max(0, totalSeconds || 0);
    const m = Math.floor(sec / 60).toString().padStart(2, '0');
    const s = (sec % 60).toString().padStart(2, '0');
    return `${m}:${s}`;
  };

  const handleProceedPayment = () => {
    // S-18 placeholder / payment integration
    setPaymentNotice('Đang chuyển hướng sang cổng thanh toán...');
  };

  if (loading) {
    return (
      <div style={{ maxWidth: '800px', margin: '40px auto', padding: '30px', textAlign: 'center', color: 'var(--text, #9ca3af)' }}>
        <p style={{ fontSize: '16px' }}>Đang tải chi tiết đơn hàng...</p>
      </div>
    );
  }

  if (errorStatus) {
    return (
      <div
        data-testid="order-error"
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
        <h3 style={{ margin: '0 0 8px 0', fontSize: '18px' }}>
          {errorStatus === 403 ? 'Truy cập bị từ chối' : 'Lỗi đơn hàng'}
        </h3>
        <p style={{ margin: '0 0 16px 0', fontSize: '14px' }}>{errorMessage}</p>
        <button
          onClick={() => navigate('/')}
          style={{
            padding: '8px 16px',
            backgroundColor: '#e11d48',
            color: '#fff',
            border: 'none',
            borderRadius: '4px',
            cursor: 'pointer',
            fontSize: '14px',
          }}
        >
          Về trang chủ
        </button>
      </div>
    );
  }

  if (!order) return null;

  const effectivelyExpired = isExpired || order.status === 'Expired' || remainingSeconds <= 0;
  const isPending = order.status === 'Pending' && !effectivelyExpired;

  return (
    <div
      data-testid="order-summary-page"
      style={{
        maxWidth: '850px',
        margin: '30px auto',
        padding: '24px',
        backgroundColor: 'var(--card-bg, #1e293b)',
        border: '1px solid var(--border, #334155)',
        borderRadius: '12px',
        color: 'var(--text, #f8fafc)',
      }}
    >
      {/* Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', borderBottom: '1px solid var(--border, #334155)', paddingBottom: '16px', marginBottom: '20px' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '24px', fontWeight: '700' }}>Tóm tắt đơn hàng #{order.id}</h1>
          <div style={{ fontSize: '13px', color: '#94a3b8', marginTop: '4px' }}>
            Thời gian tạo: {formatVietnamTime(order.createdAtUtc)}
          </div>
        </div>

        <div>
          {isPending && (
            <span
              data-testid="badge-pending"
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: '6px',
                padding: '6px 12px',
                borderRadius: '9999px',
                backgroundColor: '#78350f',
                color: '#fde047',
                fontSize: '13px',
                fontWeight: '600',
              }}
            >
              ⏳ Chờ thanh toán
            </span>
          )}

          {effectivelyExpired && (
            <span
              data-testid="badge-expired"
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: '6px',
                padding: '6px 12px',
                borderRadius: '9999px',
                backgroundColor: '#7f1d1d',
                color: '#fca5a5',
                fontSize: '13px',
                fontWeight: '600',
              }}
            >
              ✕ Đã hết hạn
            </span>
          )}

          {order.status === 'Paid' && (
            <span
              data-testid="badge-paid"
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: '6px',
                padding: '6px 12px',
                borderRadius: '9999px',
                backgroundColor: '#14532d',
                color: '#86efac',
                fontSize: '13px',
                fontWeight: '600',
              }}
            >
              ✓ Đã thanh toán
            </span>
          )}
        </div>
      </div>

      {/* Countdown Box if Pending */}
      {isPending && (
        <div
          data-testid="order-countdown-box"
          style={{
            backgroundColor: '#0f172a',
            border: '1px solid #38bdf8',
            borderRadius: '8px',
            padding: '16px',
            marginBottom: '20px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <div>
            <div style={{ fontSize: '13px', color: '#94a3b8' }}>Thời gian còn lại để thanh toán</div>
            <div style={{ fontSize: '12px', color: '#64748b' }}>Vui lòng hoàn tất thanh toán trước khi đơn hàng hết hạn</div>
          </div>
          <div
            data-testid="remaining-timer"
            style={{
              fontSize: '28px',
              fontFamily: 'monospace',
              fontWeight: '700',
              color: remainingSeconds < 120 ? '#ef4444' : '#38bdf8',
            }}
          >
            {formatTimer(remainingSeconds)}
          </div>
        </div>
      )}

      {/* Expired Warning Banner */}
      {effectivelyExpired && (
        <div
          data-testid="order-expired-banner"
          style={{
            backgroundColor: '#450a0a',
            border: '1px solid #991b1b',
            borderRadius: '8px',
            padding: '16px',
            marginBottom: '20px',
            color: '#fca5a5',
            fontSize: '14px',
          }}
        >
          <strong>Đơn hàng đã hết hạn thanh toán!</strong> Các ghế trong đơn đã được giải phóng để người khác có thể chọn. Bạn có thể chọn lại ghế để tạo đơn mới.
        </div>
      )}

      {/* Event and Showtime Details */}
      <div style={{ backgroundColor: '#0f172a', borderRadius: '8px', padding: '18px', marginBottom: '20px', border: '1px solid #334155' }}>
        <h2 style={{ fontSize: '16px', margin: '0 0 12px 0', color: '#38bdf8' }}>Thông tin sự kiện & suất diễn</h2>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
          <div>
            <div style={{ fontSize: '12px', color: '#94a3b8' }}>Tên sự kiện</div>
            <div data-testid="event-name" style={{ fontSize: '16px', fontWeight: '600', marginTop: '2px' }}>
              {order.eventName || 'Sự kiện'}
            </div>
          </div>
          <div>
            <div style={{ fontSize: '12px', color: '#94a3b8' }}>Suất diễn</div>
            <div data-testid="showtime-time" style={{ fontSize: '16px', fontWeight: '600', marginTop: '2px' }}>
              {order.showtimeStartTimeUtc ? formatVietnamTime(order.showtimeStartTimeUtc) : 'Chưa xác định'}
            </div>
          </div>
        </div>
      </div>

      {/* Seats Table */}
      <div style={{ marginBottom: '20px' }}>
        <h2 style={{ fontSize: '16px', margin: '0 0 12px 0', color: '#38bdf8' }}>Danh sách ghế đã chọn ({order.items?.length || 0} ghế)</h2>
        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left', fontSize: '14px' }}>
            <thead>
              <tr style={{ borderBottom: '1px solid #334155', color: '#94a3b8' }}>
                <th style={{ padding: '10px 12px' }}>#</th>
                <th style={{ padding: '10px 12px' }}>Vị trí ghế</th>
                <th style={{ padding: '10px 12px' }}>Hạng ghế</th>
                <th style={{ padding: '10px 12px', textAlign: 'right' }}>Đơn giá</th>
              </tr>
            </thead>
            <tbody>
              {order.items?.map((item, idx) => (
                <tr key={item.id || idx} style={{ borderBottom: '1px solid #1e293b' }}>
                  <td style={{ padding: '12px', color: '#64748b' }}>{idx + 1}</td>
                  <td style={{ padding: '12px', fontWeight: '600' }} data-testid={`seat-pos-${idx}`}>
                    Hàng {item.row} - Số {item.number}
                  </td>
                  <td style={{ padding: '12px' }} data-testid={`seat-cat-${idx}`}>
                    <span style={{ padding: '3px 8px', borderRadius: '4px', backgroundColor: '#334155', fontSize: '13px' }}>
                      {item.categoryName || 'Tiêu chuẩn'}
                    </span>
                  </td>
                  <td style={{ padding: '12px', textAlign: 'right', fontWeight: '600' }} data-testid={`seat-price-${idx}`}>
                    {formatPrice(item.unitPrice)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* Total Amount */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          backgroundColor: '#0f172a',
          padding: '18px 24px',
          borderRadius: '8px',
          border: '1px solid #334155',
          marginBottom: '24px',
        }}
      >
        <span style={{ fontSize: '18px', fontWeight: '600' }}>Tổng tiền thanh toán:</span>
        <span
          data-testid="order-total-amount"
          style={{ fontSize: '26px', fontWeight: '700', color: '#10b981' }}
        >
          {formatPrice(order.totalAmount)}
        </span>
      </div>

      {paymentNotice && (
        <div style={{ padding: '12px', backgroundColor: '#1e3a8a', color: '#bfdbfe', borderRadius: '6px', marginBottom: '16px', textAlign: 'center' }}>
          {paymentNotice}
        </div>
      )}

      {/* Actions */}
      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '12px' }}>
        <button
          onClick={() => navigate(`/shows/${order.showtimeId}/seats`)}
          style={{
            padding: '10px 18px',
            backgroundColor: 'transparent',
            border: '1px solid #475569',
            borderRadius: '6px',
            color: '#cbd5e1',
            cursor: 'pointer',
            fontSize: '14px',
            fontWeight: '500',
          }}
        >
          {effectivelyExpired ? 'Chọn lại ghế' : 'Quay lại sơ đồ ghế'}
        </button>

        {/* Nút thanh toán CHỈ HIỆN KHI ĐƠN CHƯA HẾT HẠN - KHI HẾT HẠN BỊ ẨN THEO AC 3 */}
        {isPending && (
          <button
            id="btn-proceed-payment"
            data-testid="btn-payment"
            onClick={handleProceedPayment}
            style={{
              padding: '10px 24px',
              backgroundColor: '#10b981',
              color: '#ffffff',
              border: 'none',
              borderRadius: '6px',
              cursor: 'pointer',
              fontSize: '15px',
              fontWeight: '600',
              boxShadow: '0 4px 6px -1px rgba(0, 0, 0, 0.1)',
            }}
          >
            Thanh toán ngay
          </button>
        )}
      </div>
    </div>
  );
}
