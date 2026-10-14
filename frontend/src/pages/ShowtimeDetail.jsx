import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import axiosClient from '../api/axiosClient';
import { formatVietnamTime } from '../events/management';
import { showtimeMetadata } from '../events/showtimeMetadata';

export default function ShowtimeDetail() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [showtime, setShowtime] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  useEffect(() => showtimeMetadata(document, loading || error ? null : showtime, window.location.href), [showtime, loading, error, id]);

  useEffect(() => {
    const abort = new AbortController();
    const fetchShowtime = async () => {
      try {
        setLoading(true);
        const response = await axiosClient.get(`/showtimes/${id}`, { signal: abort.signal });
        if (abort.signal.aborted) return;
        const data = response.data;
        
        if (!data || data.isOnSale === false) {
          setError(true);
        } else {
          setShowtime(data);
          setError(false);
        }
      } catch {
        if (!abort.signal.aborted) setError(true);
      } finally {
        if (!abort.signal.aborted) setLoading(false);
      }
    };
    
    fetchShowtime();
    return () => abort.abort();
  }, [id]);

  const handleSelectSeats = () => {
    const token = localStorage.getItem('token');
    if (token) {
      navigate(`/shows/${id}/seats`);
    } else {
      navigate(`/login?returnUrl=/shows/${id}/seats`);
    }
  };

  const formatDate = (utcString) => {
    if (!utcString) return '';
    return formatVietnamTime(utcString);
  };

  if (loading) {
    return <div style={{ textAlign: 'center', padding: '50px' }}>Đang tải...</div>;
  }

  if (error || !showtime) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '50vh' }}>
        <h2 style={{ color: '#d9534f' }}>Suất diễn này không mở bán</h2>
      </div>
    );
  }

  return (
    <div style={{ maxWidth: '800px', margin: '0 auto', padding: '20px' }}>
      <button 
        onClick={() => navigate('/')} 
        style={{ marginBottom: '20px', padding: '8px 12px', cursor: 'pointer', background: '#f8f9fa', border: '1px solid #ddd', borderRadius: '4px' }}
      >
        ← Quay lại
      </button>

      {showtime.imageUrl && (
        <img 
          src={showtime.imageUrl} 
          alt={showtime.eventName} 
          style={{ width: '100%', maxHeight: '400px', objectFit: 'cover', borderRadius: '8px', marginBottom: '20px' }} 
        />
      )}

      <h1>{showtime.eventName}</h1>
      
      <div style={{ background: '#f8f9fa', padding: '20px', borderRadius: '8px', marginBottom: '20px' }}>
        <p><strong>📍 Địa điểm:</strong> {showtime.location}</p>
        <p><strong>🕒 Thời gian:</strong> {formatDate(showtime.startTime)} {showtime.endTime ? `- ${formatDate(showtime.endTime)}` : ''}</p>
      </div>

      <div style={{ marginBottom: '30px', lineHeight: '1.6' }}>
        <h3>Mô tả</h3>
        <p>{showtime.description}</p>
      </div>

      <div style={{ textAlign: 'center' }}>
        <button 
          onClick={handleSelectSeats}
          style={{
            padding: '12px 30px',
            fontSize: '18px',
            backgroundColor: '#28a745',
            color: 'white',
            border: 'none',
            borderRadius: '5px',
            cursor: 'pointer',
            fontWeight: 'bold'
          }}
        >
          Chọn ghế
        </button>
      </div>
    </div>
  );
}
