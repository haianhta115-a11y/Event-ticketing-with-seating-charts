import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { createEvent } from '../api/eventsApi';

export default function CreateEvent() {
  const navigate = useNavigate();
  const [formData, setFormData] = useState({
    name: '',
    description: '',
    location: '',
  });

  const [errors, setErrors] = useState({});
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [generalError, setGeneralError] = useState('');

  const validate = () => {
    const newErrors = {};
    if (!formData.name.trim()) {
      newErrors.name = 'Vui lòng nhập tên sự kiện';
    }
    if (!formData.description.trim()) {
      newErrors.description = 'Vui lòng nhập mô tả sự kiện';
    }
    if (!formData.location.trim()) {
      newErrors.location = 'Vui lòng nhập địa điểm tổ chức';
    }
    return newErrors;
  };

  const handleChange = (e) => {
    const { name, value } = e.target;
    setFormData((prev) => ({ ...prev, [name]: value }));
    if (errors[name]) {
      setErrors((prev) => ({ ...prev, [name]: '' }));
    }
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setGeneralError('');

    const validationErrors = validate();
    if (Object.keys(validationErrors).length > 0) {
      setErrors(validationErrors);
      return;
    }

    setIsSubmitting(true);
    try {
      const newEvent = await createEvent({
        name: formData.name,
        description: formData.description,
        location: formData.location,
      });

      navigate(`/events/${newEvent.id}`);
    } catch (err) {
      setGeneralError(err?.message || 'Không thể tạo sự kiện. Vui lòng thử lại.');
      setIsSubmitting(false);
    }
  };

  return (
    <div
      style={{
        maxWidth: '640px',
        margin: '30px auto',
        padding: '24px',
        backgroundColor: 'var(--code-bg, #1f2028)',
        borderRadius: '8px',
        border: '1px solid var(--border, #2e303a)',
        boxShadow: '0 1px 3px rgba(0,0,0,0.1)',
        textAlign: 'left',
      }}
    >
      {/* Header */}
      <div style={{ marginBottom: '20px', borderBottom: '1px solid var(--border, #2e303a)', paddingBottom: '12px' }}>
        <h2 style={{ margin: '0 0 6px 0', fontSize: '22px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
          Tạo Sự Kiện Mới
        </h2>
        <p style={{ margin: 0, fontSize: '14px', color: 'var(--text, #9ca3af)' }}>
          Nhập các thông tin cơ bản của sự kiện. Sự kiện mới sẽ được lưu dưới dạng <strong>Bản nháp</strong>.
        </p>
      </div>

      {generalError && (
        <div style={{ padding: '12px', marginBottom: '16px', backgroundColor: '#fef2f2', border: '1px solid #fecaca', borderRadius: '6px', color: '#b91c1c', fontSize: '14px' }}>
          {generalError}
        </div>
      )}

      <form onSubmit={handleSubmit} noValidate>
        {/* Tên sự kiện */}
        <div style={{ marginBottom: '18px' }}>
          <label htmlFor="name" style={{ display: 'block', marginBottom: '6px', fontWeight: '500', fontSize: '14px', color: 'var(--text-h, #f1f5f9)' }}>
            Tên sự kiện <span style={{ color: '#ef4444' }}>*</span>
          </label>
          <input
            id="name"
            name="name"
            type="text"
            value={formData.name}
            onChange={handleChange}
            placeholder="Ví dụ: Đại Nhạc Hội Mùa Hè 2026"
            disabled={isSubmitting}
            style={{
              width: '100%',
              padding: '10px 12px',
              borderRadius: '6px',
              border: `1px solid ${errors.name ? '#ef4444' : 'var(--border, #475569)'}`,
              outline: 'none',
              fontSize: '14px',
              boxSizing: 'border-box',
              backgroundColor: isSubmitting ? 'var(--code-bg, #1f2028)' : 'var(--bg, #16171d)',
              color: 'var(--text-h, #f1f5f9)',
            }}
          />
          {errors.name && (
            <p style={{ margin: '5px 0 0 0', fontSize: '13px', color: '#ef4444' }}>
              {errors.name}
            </p>
          )}
        </div>

        {/* Địa điểm */}
        <div style={{ marginBottom: '18px' }}>
          <label htmlFor="location" style={{ display: 'block', marginBottom: '6px', fontWeight: '500', fontSize: '14px', color: 'var(--text-h, #f1f5f9)' }}>
            Địa điểm tổ chức <span style={{ color: '#ef4444' }}>*</span>
          </label>
          <input
            id="location"
            name="location"
            type="text"
            value={formData.location}
            onChange={handleChange}
            placeholder="Ví dụ: Trung tâm Hội nghị Quốc gia, Hà Nội"
            disabled={isSubmitting}
            style={{
              width: '100%',
              padding: '10px 12px',
              borderRadius: '6px',
              border: `1px solid ${errors.location ? '#ef4444' : 'var(--border, #475569)'}`,
              outline: 'none',
              fontSize: '14px',
              boxSizing: 'border-box',
              backgroundColor: isSubmitting ? 'var(--code-bg, #1f2028)' : 'var(--bg, #16171d)',
              color: 'var(--text-h, #f1f5f9)',
            }}
          />
          {errors.location && (
            <p style={{ margin: '5px 0 0 0', fontSize: '13px', color: '#ef4444' }}>
              {errors.location}
            </p>
          )}
        </div>

        {/* Mô tả */}
        <div style={{ marginBottom: '24px' }}>
          <label htmlFor="description" style={{ display: 'block', marginBottom: '6px', fontWeight: '500', fontSize: '14px', color: 'var(--text-h, #f1f5f9)' }}>
            Mô tả sự kiện <span style={{ color: '#ef4444' }}>*</span>
          </label>
          <textarea
            id="description"
            name="description"
            rows="5"
            value={formData.description}
            onChange={handleChange}
            placeholder="Giới thiệu nội dung, nghệ sĩ tham gia và các thông tin cần lưu ý..."
            disabled={isSubmitting}
            style={{
              width: '100%',
              padding: '10px 12px',
              borderRadius: '6px',
              border: `1px solid ${errors.description ? '#ef4444' : 'var(--border, #475569)'}`,
              outline: 'none',
              fontSize: '14px',
              boxSizing: 'border-box',
              resize: 'vertical',
              backgroundColor: isSubmitting ? 'var(--code-bg, #1f2028)' : 'var(--bg, #16171d)',
              color: 'var(--text-h, #f1f5f9)',
            }}
          />
          {errors.description && (
            <p style={{ margin: '5px 0 0 0', fontSize: '13px', color: '#ef4444' }}>
              {errors.description}
            </p>
          )}
        </div>

        {/* Buttons */}
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '12px', alignItems: 'center' }}>
          <Link
            to="/events"
            style={{
              padding: '10px 18px',
              borderRadius: '6px',
              border: '1px solid var(--border, #475569)',
              backgroundColor: 'var(--bg, #16171d)',
              color: 'var(--text-h, #f1f5f9)',
              textDecoration: 'none',
              fontSize: '14px',
              fontWeight: '500',
              cursor: isSubmitting ? 'not-allowed' : 'pointer',
              pointerEvents: isSubmitting ? 'none' : 'auto',
            }}
          >
            Hủy
          </Link>
          <button
            type="submit"
            disabled={isSubmitting}
            style={{
              padding: '10px 20px',
              borderRadius: '6px',
              border: 'none',
              backgroundColor: isSubmitting ? '#93c5fd' : '#2563eb',
              color: '#ffffff',
              fontSize: '14px',
              fontWeight: '600',
              cursor: isSubmitting ? 'not-allowed' : 'pointer',
              boxShadow: '0 1px 2px rgba(0,0,0,0.05)',
            }}
          >
            {isSubmitting ? 'Đang lưu...' : 'Lưu sự kiện'}
          </button>
        </div>
      </form>
    </div>
  );
}
