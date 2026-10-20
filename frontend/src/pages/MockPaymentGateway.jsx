import { useState, useEffect, useRef } from 'react';
import { useParams, useSearchParams, Link } from 'react-router-dom';

export default function OrderDetail() {
    const { orderId: paramOrderId } = useParams();
    const [searchParams] = useSearchParams();
    const orderId = paramOrderId || searchParams.get('orderId');

    const [paymentStatus, setPaymentStatus] = useState('confirming');
    const [statusMessage, setStatusMessage] = useState('đang xác nhận thanh toán');
    const [ticketUrl, setTicketUrl] = useState('');

    const pollingRef = useRef(null);
    const startTimeRef = useRef(null);

    useEffect(() => {
        if (!orderId) return;

        if (!startTimeRef.current) {
            startTimeRef.current = Date.now();
        }

        const checkServerStatus = async () => {
            const secondsPassed = (Date.now() - startTimeRef.current) / 1000;
            if (secondsPassed >= 60) {
                if (pollingRef.current) clearInterval(pollingRef.current);
                setPaymentStatus('timeout');
                setStatusMessage('Chưa có xác nhận sau 60 giây. Vui lòng kiểm tra lại trong lịch sử đơn thay vì kết luận thất bại.');
                return;
            }
    // ... sisa kode lainnya tetap sama

            try {
                // 2. Selalu panggil server langsung (tidak mempercayai URL parameter)
                const res = await fetch(`/api/orders/${orderId}/payment-status`);
                if (res.ok) {
                    const data = await res.json();
                    if (data.isPaid || data.status?.toLowerCase() === 'paid') {
                        if (pollingRef.current) clearInterval(pollingRef.current);
                        setPaymentStatus('paid');
                        setStatusMessage('đã thanh toán');
                        setTicketUrl(data.ticketUrl || `/tickets/order/${orderId}`);
                    }
                }
            } catch (err) {
                console.error('Lỗi kiểm tra trạng thái:', err);
            }
        };

        checkServerStatus();

        // 3. Polling server setiap 2 detik
        pollingRef.current = setInterval(checkServerStatus, 2000);

        return () => {
            if (pollingRef.current) clearInterval(pollingRef.current);
        };
    }, [orderId]);

    return (
        <div style={{ maxWidth: '600px', margin: '40px auto', padding: '24px', textAlign: 'center', background: '#fff', borderRadius: '8px', border: '1px solid #e2e8f0' }}>
            <h2>Kết quả thanh toán đơn hàng: ORD-{orderId}</h2>

            {paymentStatus === 'confirming' && (
                <div style={{ marginTop: '20px' }}>
                    <p style={{ fontSize: '18px', color: '#0284c7' }}>⏳ {statusMessage}...</p>
                    <p style={{ color: '#64748b', fontSize: '14px' }}>Hệ thống đang tự hỏi lại máy chủ mỗi 2 giây.</p>
                </div>
            )}

            {paymentStatus === 'paid' && (
                <div style={{ marginTop: '20px' }}>
                    <p style={{ fontSize: '20px', color: '#16a34a', fontWeight: 'bold' }}>✅ {statusMessage}</p>
                    <div style={{ marginTop: '16px' }}>
                        <Link to={ticketUrl} style={{ padding: '10px 20px', background: '#2563eb', color: '#fff', borderRadius: '6px', textDecoration: 'none' }}>Xem vé của bạn
                        </Link>
                    </div>
                </div>
            )}

            {paymentStatus === 'timeout' && (
                <div style={{ marginTop: '20px' }}>
                    <p style={{ fontSize: '18px', color: '#d97706', fontWeight: 'bold' }}>⏱️ Hết thời gian chờ</p>
                    <p style={{ marginTop: '8px', color: '#475569' }}>{statusMessage}</p>
                    <div style={{ marginTop: '16px' }}>
                        <Link to="/orders/history" style={{ padding: '10px 20px', background: '#475569', color: '#fff', borderRadius: '6px', textDecoration: 'none' }}>
                            Kiểm tra trong lịch sử đơn
                        </Link>
                    </div>
                </div>
            )}
        </div>
    );
}