import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getOrderById, payOrder } from '../api/paymentApi.js';

export default function OrderDetail() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [order, setOrder] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [paymentError, setPaymentError] = useState(null);
  const [isPaying, setIsPaying] = useState(false);

  useEffect(() => {
    let active = true;
    const fetchOrder = async () => {
      try {
        setLoading(true);
        const orderId = Number(id);
        if (!orderId || Number.isNaN(orderId)) {
          throw new Error('Mã đơn hàng không hợp lệ.');
        }
        const data = await getOrderById(orderId);
        if (active) {
          setOrder(data);
        }
      } catch (err) {
        if (active) {
          setError(err.response?.data?.message || 'Không thể tải thông tin đơn hàng.');
        }
      } finally {
        if (active) {
          setLoading(false);
        }
      }
    };

    fetchOrder();
    return () => {
      active = false;
    };
  }, [id]);

  const handlePay = async () => {
    if (!order) return;
    try {
      setIsPaying(true);
      setPaymentError(null);
      const res = await payOrder(order.id);
      if (res.redirectUrl) {
        window.location.href = res.redirectUrl;
      }
    } catch (err) {
      const code = err.response?.data?.errorCode;
      if (code === 'ORDER_EXPIRED') {
        setPaymentError('Đơn hàng đã hết hạn thanh toán.');
      } else if (code === 'ORDER_NOT_PAYABLE') {
        setPaymentError('Đơn hàng không ở trạng thái có thể thanh toán.');
      } else {
        setPaymentError(err.response?.data?.message || 'Có lỗi xảy ra khi thực hiện thanh toán.');
      }
    } finally {
      setIsPaying(false);
    }
  };

  if (loading) {
    return <div style={{ textAlign: 'center', padding: '50px' }}>Đang tải thông tin đơn hàng...</div>;
  }

  if (error || !order) {
    return (
      <div style={{ maxWidth: '600px', margin: '40px auto', padding: '20px', textAlign: 'center' }}>
        <h2 style={{ color: '#ef4444' }}>{error || 'Đơn hàng không tồn tại'}</h2>
        <button
          onClick={() => navigate('/')}
          style={{ marginTop: '16px', padding: '8px 16px', cursor: 'pointer' }}
        >
          Trang chủ
        </button>
      </div>
    );
  }

  return (
    <div style={{ maxWidth: '700px', margin: '40px auto', padding: '24px', backgroundColor: 'var(--card-bg, #1e293b)', borderRadius: '8px', color: '#fff' }}>
      <button
        onClick={() => navigate(-1)}
        style={{ marginBottom: '16px', padding: '6px 12px', cursor: 'pointer', background: 'transparent', border: '1px solid #475569', color: '#cbd5e1', borderRadius: '4px' }}
      >
        ← Quay lại
      </button>

      <h2>Chi Tiết Đơn Hàng #{order.id}</h2>
      <div style={{ marginTop: '16px', lineHeight: '1.8' }}>
        <p><strong>Mã đơn hàng:</strong> ORD-{order.id}</p>
        <p>
          <strong>Trạng thái:</strong>{' '}
          <span style={{
            padding: '2px 8px',
            borderRadius: '4px',
            backgroundColor: order.status === 'Paid' ? '#166534' : '#854d0e',
            color: '#fff',
            fontWeight: 'bold'
          }}>
            {order.status === 'Paid' ? 'Đã thanh toán' : order.status}
          </span>
        </p>
        <p><strong>Tổng tiền:</strong> {new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' }).format(order.totalAmount)}</p>
        <p><strong>Thời hạn thanh toán:</strong> {new Date(order.expiresAtUtc).toLocaleString('vi-VN')}</p>
      </div>

      <div style={{ marginTop: '24px' }}>
        <h3>Danh sách vé ({order.items?.length || 0})</h3>
        <ul style={{ listStyle: 'none', padding: 0, marginTop: '8px' }}>
          {order.items?.map((item) => (
            <li
              key={item.id}
              style={{ padding: '8px 12px', borderBottom: '1px solid #334155', display: 'flex', justifyContent: 'space-between' }}
            >
              <span>Hàng {item.row}, Ghế {item.number} ({item.categoryName || 'Tiêu chuẩn'})</span>
              <span>{new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' }).format(item.unitPrice)}</span>
            </li>
          ))}
        </ul>
      </div>

      {paymentError && (
        <div role="alert" style={{ marginTop: '16px', padding: '12px', backgroundColor: '#7f1d1d', color: '#fca5a5', borderRadius: '4px' }}>
          {paymentError}
        </div>
      )}

      {order.status === 'Pending' && (
        <div style={{ marginTop: '24px', textAlign: 'center' }}>
          <button
            onClick={handlePay}
            disabled={isPaying}
            style={{
              padding: '12px 32px',
              backgroundColor: '#10b981',
              color: '#fff',
              fontSize: '16px',
              fontWeight: 'bold',
              border: 'none',
              borderRadius: '6px',
              cursor: isPaying ? 'not-allowed' : 'pointer'
            }}
          >
            {isPaying ? 'Đang chuyển hướng sang cổng thanh toán...' : 'Thanh toán'}
          </button>
        </div>
      )}
    </div>
  );
}
