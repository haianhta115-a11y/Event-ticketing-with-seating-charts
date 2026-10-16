import { useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { signMockPayment, sendMockWebhook } from '../api/paymentApi.js';

export default function MockPaymentGateway() {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();

  const orderId = searchParams.get('orderId');
  const orderCode = searchParams.get('orderCode') || (orderId ? `ORD-${orderId}` : 'Chưa xác định');
  const amount = Number(searchParams.get('amount') || 0);
  const returnUrl = searchParams.get('returnUrl') || (orderId ? `/orders/${orderId}` : '/');

  const [loading, setLoading] = useState(false);
  const [statusMessage, setStatusMessage] = useState(null);
  const [errorMessage, setErrorMessage] = useState(null);

  const handleRedirect = () => {
    if (returnUrl.startsWith('http://') || returnUrl.startsWith('https://')) {
      window.location.href = returnUrl;
    } else {
      navigate(returnUrl);
    }
  };

  const handleSuccessPayment = async () => {
    if (!orderId) {
      setErrorMessage('Không tìm thấy thông tin mã đơn hàng hợp lệ.');
      return;
    }

    try {
      setLoading(true);
      setErrorMessage(null);
      setStatusMessage('Đang tạo chữ ký webhook giả lập hợp lệ...');

      const transactionId = `MOCK-TXN-${Date.now()}`;
      
      // 1. Sinh chữ ký HMAC-SHA256 hợp lệ từ backend
      const signRes = await signMockPayment({
        orderId,
        transactionId,
        amount,
        success: true,
      });

      setStatusMessage('Đang gửi webhook xác nhận thanh toán vào hệ thống...');

      // 2. Gửi webhook giả lập kèm chữ ký
      await sendMockWebhook({
        orderId,
        transactionId,
        amount,
        success: true,
        signature: signRes.signature,
      });

      setStatusMessage('✓ Thanh toán thành công! Đang chuyển hướng về trang đơn hàng...');
      setTimeout(() => {
        handleRedirect();
      }, 1000);
    } catch (err) {
      setErrorMessage(err.response?.data?.message || err.message || 'Lỗi khi gửi webhook thanh toán.');
      setLoading(false);
    }
  };

  const handleFailedPayment = async () => {
    if (!orderId) {
      handleRedirect();
      return;
    }

    try {
      setLoading(true);
      setErrorMessage(null);
      setStatusMessage('Đang ghi nhận hủy/thất bại thanh toán...');

      const transactionId = `MOCK-FAIL-${Date.now()}`;

      // 1. Sinh chữ ký
      const signRes = await signMockPayment({
        orderId,
        transactionId,
        amount,
        success: false,
      });

      // 2. Gửi webhook báo thất bại (đơn vẫn giữ nguyên Pending theo S-24)
      await sendMockWebhook({
        orderId,
        transactionId,
        amount,
        success: false,
        signature: signRes.signature,
      });

      setStatusMessage('Đã hủy giao dịch. Đang chuyển hướng quay lại...');
      setTimeout(() => {
        handleRedirect();
      }, 1000);
    } catch (err) {
      setErrorMessage(err.response?.data?.message || err.message || 'Lỗi khi ghi nhận hủy thanh toán.');
      setLoading(false);
    }
  };

  return (
    <div
      style={{
        maxWidth: '560px',
        margin: '40px auto',
        padding: '30px',
        backgroundColor: '#1e293b',
        borderRadius: '12px',
        boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3)',
        color: '#f8fafc',
        fontFamily: 'system-ui, -apple-system, sans-serif',
      }}
    >
      {/* Header */}
      <div style={{ textAlign: 'center', marginBottom: '24px' }}>
        <div
          style={{
            display: 'inline-block',
            padding: '4px 12px',
            backgroundColor: '#854d0e',
            color: '#fef08a',
            borderRadius: '9999px',
            fontSize: '12px',
            fontWeight: '600',
            letterSpacing: '0.05em',
            marginBottom: '10px',
          }}
        >
          ⚠️ MÔI TRƯỜNG GIẢ LẬP (MOCK SANDBOX)
        </div>
        <h2 style={{ fontSize: '22px', margin: '6px 0', color: '#38bdf8' }}>
          Cổng Thanh Toán Giả Lập
        </h2>
        <p style={{ fontSize: '13px', color: '#94a3b8', margin: 0 }}>
          Dành cho nhà phát triển kiểm thử tính năng đặt vé khi chưa có cổng thật
        </p>
      </div>

      {/* Thông tin đơn hàng */}
      <div
        style={{
          backgroundColor: '#0f172a',
          borderRadius: '8px',
          padding: '18px 20px',
          marginBottom: '24px',
          border: '1px solid #334155',
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '10px' }}>
          <span style={{ color: '#94a3b8', fontSize: '14px' }}>Mã đơn hàng:</span>
          <span style={{ fontWeight: '600', color: '#f8fafc', fontSize: '14px' }}>{orderCode}</span>
        </div>
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '10px' }}>
          <span style={{ color: '#94a3b8', fontSize: '14px' }}>Tổng thanh toán:</span>
          <span style={{ fontWeight: '700', color: '#4ade80', fontSize: '18px' }}>
            {new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND' }).format(amount)}
          </span>
        </div>
        <div style={{ display: 'flex', justifyContent: 'space-between' }}>
          <span style={{ color: '#94a3b8', fontSize: '14px' }}>Phương thức:</span>
          <span style={{ color: '#e2e8f0', fontSize: '14px' }}>Thẻ / QR Giả lập nội bộ</span>
        </div>
      </div>

      {/* Trạng thái / Thông báo */}
      {statusMessage && (
        <div
          style={{
            padding: '12px',
            marginBottom: '20px',
            backgroundColor: '#064e3b',
            color: '#6ee7b7',
            borderRadius: '6px',
            fontSize: '13px',
            textAlign: 'center',
            fontWeight: '500',
          }}
        >
          {statusMessage}
        </div>
      )}

      {errorMessage && (
        <div
          role="alert"
          style={{
            padding: '12px',
            marginBottom: '20px',
            backgroundColor: '#7f1d1d',
            color: '#fca5a5',
            borderRadius: '6px',
            fontSize: '13px',
            textAlign: 'center',
          }}
        >
          ⚠️ {errorMessage}
        </div>
      )}

      {/* Hành động (2 nút theo đúng yêu cầu SCRUM-113) */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
        <button
          onClick={handleSuccessPayment}
          disabled={loading}
          style={{
            padding: '14px',
            borderRadius: '8px',
            border: 'none',
            backgroundColor: '#16a34a',
            color: '#ffffff',
            fontSize: '15px',
            fontWeight: '600',
            cursor: loading ? 'not-allowed' : 'pointer',
            opacity: loading ? 0.7 : 1,
            transition: 'background-color 0.2s',
          }}
        >
          ✓ Thanh toán thành công (Simulate Success)
        </button>

        <button
          onClick={handleFailedPayment}
          disabled={loading}
          style={{
            padding: '14px',
            borderRadius: '8px',
            border: 'none',
            backgroundColor: '#dc2626',
            color: '#ffffff',
            fontSize: '15px',
            fontWeight: '600',
            cursor: loading ? 'not-allowed' : 'pointer',
            opacity: loading ? 0.7 : 1,
            transition: 'background-color 0.2s',
          }}
        >
          ✕ Thanh toán thất bại / Hủy (Simulate Failure)
        </button>
      </div>
    </div>
  );
}
