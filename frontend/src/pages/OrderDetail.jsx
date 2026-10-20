import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getOrderById, payOrder } from '../api/paymentApi.js';
import {
  isPendingOrderStatus,
  hasFailedPaymentAttempt,
  shouldShowRetryPayment,
} from './orderPaymentRetry.js';

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
          setOrder(data?.order || data);
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
    return <div style={{ textAlign: 'center', padding: '50px' }}>Đang tải chi tiết đơn hàng...</div>;
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

  const orderData = order?.order || order;
  const isExpired = Boolean(
    orderData?.isExpired ||
    orderData?.status === 'Expired' ||
    (orderData?.serverTimeUtc && orderData?.expiresAtUtc && new Date(orderData.expiresAtUtc).getTime() <= new Date(orderData.serverTimeUtc).getTime()) ||
    (typeof orderData?.remainingSeconds === 'number' && orderData.remainingSeconds <= 0 && orderData.status === 'Pending')
  );
  const isPending = isPendingOrderStatus(orderData?.status) && !isExpired;
  const hasFailedPayment = Boolean(
    orderData?.hasFailedPayments ||
    orderData?.hasFailedPayment ||
    orderData?.hasFailedPaymentHistory ||
    (typeof orderData?.failedPaymentCount === 'number' && orderData.failedPaymentCount > 0) ||
    hasFailedPaymentAttempt(orderData)
  );

  // QA-B05: Nút Retry ("Thanh toán lại") CHỈ HIỆN KHI đơn Pending + có payment thất bại
  const showRetryButton = isPending && (shouldShowRetryPayment(orderData) || hasFailedPayment);
  const showInitialPayButton = isPending && !hasFailedPayment;

  return (
    <div style={{ maxWidth: '700px', margin: '40px auto', padding: '24px', backgroundColor: 'var(--card-bg, #1e293b)', borderRadius: '12px', color: '#fff' }}>
      <button
        onClick={() => navigate(-1)}
        style={{ marginBottom: '16px', padding: '6px 12px', cursor: 'pointer', background: 'transparent', border: '1px solid #475569', color: '#cbd5e1', borderRadius: '4px' }}
      >
        ← Quay lại
      </button>

      <h2>Chi Tiết Đơn Hàng #{order.id}</h2>

      {/* Order Summary Section */}
      <div
        className="order-summary"
        data-testid="order-summary"
        style={{
          marginTop: '16px',
          padding: '20px',
          backgroundColor: 'rgba(15, 23, 42, 0.6)',
          borderRadius: '10px',
          border: '1px solid #334155',
          lineHeight: '1.8',
        }}
      >
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
        <p><strong>Thời hạn thanh toán:</strong> {order.expiresAtUtc ? new Date(order.expiresAtUtc).toLocaleString('vi-VN') : ''}</p>

        {/* Failed payment attempt notice */}
        {showRetryButton && (
          <div
            role="status"
            className="mt-4 p-3.5 bg-amber-950/40 border border-amber-600/40 text-amber-200 text-sm rounded-xl flex items-center gap-3"
            style={{
              marginTop: '14px',
              padding: '10px 14px',
              backgroundColor: 'rgba(120, 53, 15, 0.3)',
              border: '1px solid rgba(217, 119, 6, 0.4)',
              borderRadius: '8px',
              color: '#fde68a',
              display: 'flex',
              alignItems: 'center',
              gap: '8px',
            }}
          >
            <svg
              className="w-5 h-5 flex-shrink-0 text-amber-400"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth="2"
              aria-hidden="true"
              style={{ width: '20px', height: '20px', flexShrink: 0 }}
            >
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <span>Thanh toán trước đó chưa hoàn tất hoặc thất bại. Bạn có thể nhấn <strong>Thanh toán lại</strong> bên dưới.</span>
          </div>
        )}

        {/* Prominently placed action button inside Order Summary */}
        {isPending && (
          <div
            className="mt-5 pt-4 border-t border-slate-700/60 flex justify-center"
            style={{ marginTop: '18px', paddingTop: '16px', borderTop: '1px solid #334155', textAlign: 'center' }}
          >
            {showRetryButton ? (
              <button
                type="button"
                onClick={handlePay}
                disabled={isPaying}
                aria-label="Thanh toán lại"
                data-testid="retry-payment-button"
                className="group relative inline-flex items-center justify-center gap-3 px-8 py-3.5 text-base sm:text-lg font-semibold text-white bg-gradient-to-r from-amber-500 via-orange-600 to-rose-600 hover:from-amber-400 hover:via-orange-500 hover:to-rose-500 rounded-xl shadow-lg hover:shadow-xl hover:scale-105 active:scale-95 transition-all transition-transform duration-300 ease-out cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed disabled:transform-none"
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: '10px',
                  padding: '14px 32px',
                  background: 'linear-gradient(135deg, #f59e0b 0%, #ea580c 50%, #e11d48 100%)',
                  color: '#ffffff',
                  fontSize: '16px',
                  fontWeight: '600',
                  border: 'none',
                  borderRadius: '12px',
                  boxShadow: '0 10px 25px -5px rgba(234, 88, 12, 0.4), 0 8px 10px -6px rgba(234, 88, 12, 0.3)',
                  cursor: isPaying ? 'not-allowed' : 'pointer',
                  opacity: isPaying ? 0.6 : 1,
                  transition: 'all 0.3s ease',
                }}
              >
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  className={`w-5 h-5 text-white transition-transform duration-500 ${isPaying ? 'animate-spin' : 'group-hover:rotate-180'}`}
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth="2.2"
                  aria-hidden="true"
                  style={{ width: '20px', height: '20px' }}
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"
                  />
                </svg>
                <span>{isPaying ? 'Đang chuyển hướng sang cổng thanh toán...' : 'Thanh toán lại'}</span>
              </button>
            ) : showInitialPayButton ? (
              <button
                type="button"
                onClick={handlePay}
                disabled={isPaying}
                aria-label="Thanh toán"
                className="group relative inline-flex items-center justify-center gap-2.5 px-8 py-3 text-base font-semibold text-white bg-gradient-to-r from-emerald-500 to-teal-600 hover:from-emerald-400 hover:to-teal-500 rounded-xl shadow-lg hover:shadow-xl hover:scale-105 active:scale-95 transition-all transition-transform duration-300 ease-out cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed disabled:transform-none"
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: '8px',
                  padding: '12px 32px',
                  backgroundColor: '#10b981',
                  color: '#fff',
                  fontSize: '16px',
                  fontWeight: 'bold',
                  border: 'none',
                  borderRadius: '8px',
                  cursor: isPaying ? 'not-allowed' : 'pointer',
                  opacity: isPaying ? 0.6 : 1,
                }}
              >
                <span>{isPaying ? 'Đang chuyển hướng sang cổng thanh toán...' : 'Thanh toán'}</span>
              </button>
            ) : null}
          </div>
        )}
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
    </div>
  );
}

