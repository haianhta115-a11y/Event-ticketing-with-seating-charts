import { useState } from 'react';
import { createShowtime } from '../api/eventsApi';

export default function ShowtimeForm({ eventId, existingShowtimes = [], onShowtimeCreated }) {
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [error, setError] = useState('');
  const [warning, setWarning] = useState('');
  const [isSaving, setIsSaving] = useState(false);

  // Kiểm tra trùng lặp hoàn toàn
  const checkExactDuplicate = (startVal, endVal) => {
    const startTimestamp = new Date(startVal).getTime();
    const endTimestamp = endVal ? new Date(endVal).getTime() : null;

    return existingShowtimes.some((s) => {
      const sStart = new Date(s.startTime).getTime();
      const sEnd = s.endTime ? new Date(s.endTime).getTime() : null;
      return sStart === startTimestamp && sEnd === endTimestamp;
    });
  };

  const validate = () => {
    if (!startTime) {
      return 'Vui lòng chọn thời điểm bắt đầu';
    }

    const startDate = new Date(startTime);
    // Bắt buộc thời điểm bắt đầu ở tương lai
    if (startDate.getTime() <= Date.now()) {
      return 'Thời điểm bắt đầu phải ở tương lai';
    }

    // Chỉ kiểm tra khi người dùng có nhập thời điểm kết thúc
    if (endTime) {
      const endDate = new Date(endTime);
      if (endDate.getTime() <= startDate.getTime()) {
        return 'Thời gian kết thúc phải sau thời gian bắt đầu';
      }
    }

    return null;
  };

  const handleSave = async (forceSave = false) => {
    setError('');

    const validationError = validate();
    if (validationError) {
      setError(validationError);
      setWarning('');
      return;
    }

    // Kiểm tra trùng lặp nếu chưa được xác nhận bỏ qua cảnh báo
    if (!forceSave) {
      const isDuplicate = checkExactDuplicate(startTime, endTime);
      if (isDuplicate) {
        setWarning('Trùng thời gian với một suất diễn khác. Có thể bạn đang tổ chức song song ở hai phòng');
        return;
      }
    }

    setIsSaving(true);
    setWarning('');

    try {
      const created = await createShowtime(eventId, {
        startTime,
        endTime: endTime || null,
      });

      // Reset form sau khi lưu thành công
      setStartTime('');
      setEndTime('');
      setError('');
      setWarning('');

      if (onShowtimeCreated) {
        onShowtimeCreated(created);
      }
    } catch (err) {
      setError(err?.message || 'Có lỗi xảy ra khi lưu suất diễn. Vui lòng thử lại.');
    } finally {
      setIsSaving(false);
    }
  };

  const onSubmit = (e) => {
    e.preventDefault();
    handleSave(false);
  };

  return (
    <div
      style={{
        backgroundColor: 'var(--bg, #16171d)',
        border: '1px solid var(--border, #2e303a)',
        borderRadius: '8px',
        padding: '20px',
        marginTop: '24px',
        textAlign: 'left',
      }}
    >
      <h4 style={{ margin: '0 0 4px 0', fontSize: '16px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
        Thêm Suất Diễn Mới
      </h4>
      <p style={{ margin: '0 0 16px 0', fontSize: '13px', color: 'var(--text, #9ca3af)' }}>
        Thiết lập thời gian diễn ra sự kiện. Thời điểm bắt đầu phải ở tương lai.
      </p>

      {/* Thông báo lỗi đỏ (chặn lưu) */}
      {error && (
        <div
          style={{
            padding: '10px 14px',
            marginBottom: '16px',
            backgroundColor: '#fef2f2',
            border: '1px solid #fecaca',
            borderRadius: '6px',
            color: '#b91c1c',
            fontSize: '13px',
            fontWeight: '500',
          }}
        >
          {error}
        </div>
      )}

      {/* Cảnh báo vàng (trùng giờ, vẫn cho lưu) */}
      {warning && (
        <div
          style={{
            padding: '12px 14px',
            marginBottom: '16px',
            backgroundColor: '#fffbeb',
            border: '1px solid #fde68a',
            borderRadius: '6px',
            color: '#92400e',
            fontSize: '13px',
            lineHeight: '1.5',
          }}
        >
          <div style={{ fontWeight: '600', marginBottom: '6px', display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span>⚠️</span> {warning}
          </div>
          <div style={{ display: 'flex', gap: '10px', marginTop: '8px' }}>
            <button
              type="button"
              onClick={() => handleSave(true)}
              disabled={isSaving}
              style={{
                padding: '6px 14px',
                backgroundColor: '#d97706',
                color: '#ffffff',
                border: 'none',
                borderRadius: '4px',
                fontSize: '13px',
                fontWeight: '600',
                cursor: isSaving ? 'not-allowed' : 'pointer',
              }}
            >
              {isSaving ? 'Đang lưu...' : 'Vẫn lưu'}
            </button>
            <button
              type="button"
              onClick={() => setWarning('')}
              disabled={isSaving}
              style={{
                padding: '6px 12px',
                backgroundColor: 'transparent',
                color: '#78350f',
                border: '1px solid #d97706',
                borderRadius: '4px',
                fontSize: '13px',
                cursor: 'pointer',
              }}
            >
              Sửa lại thời gian
            </button>
          </div>
        </div>
      )}

      <form onSubmit={onSubmit} noValidate>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
            gap: '16px',
            marginBottom: '18px',
          }}
        >
          {/* Thời gian bắt đầu */}
          <div>
            <label
              htmlFor="startTime"
              style={{
                display: 'block',
                marginBottom: '6px',
                fontSize: '13px',
                fontWeight: '500',
                color: 'var(--text-h, #f1f5f9)',
              }}
            >
              Bắt đầu <span style={{ color: '#ef4444' }}>*</span>
            </label>
            <input
              id="startTime"
              type="datetime-local"
              value={startTime}
              onChange={(e) => {
                setStartTime(e.target.value);
                setError('');
                setWarning('');
              }}
              disabled={isSaving}
              style={{
                width: '100%',
                padding: '9px 12px',
                borderRadius: '6px',
                border: '1px solid var(--border, #475569)',
                outline: 'none',
                fontSize: '13px',
                boxSizing: 'border-box',
                backgroundColor: 'var(--code-bg, #1f2028)',
                color: 'var(--text-h, #f1f5f9)',
              }}
            />
          </div>

          {/* Thời gian kết thúc */}
          <div>
            <label
              htmlFor="endTime"
              style={{
                display: 'block',
                marginBottom: '6px',
                fontSize: '13px',
                fontWeight: '500',
                color: 'var(--text-h, #f1f5f9)',
              }}
            >
              Kết thúc <span style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', fontWeight: 'normal' }}>(tùy chọn)</span>
            </label>
            <input
              id="endTime"
              type="datetime-local"
              value={endTime}
              onChange={(e) => {
                setEndTime(e.target.value);
                setError('');
                setWarning('');
              }}
              disabled={isSaving}
              style={{
                width: '100%',
                padding: '9px 12px',
                borderRadius: '6px',
                border: '1px solid var(--border, #475569)',
                outline: 'none',
                fontSize: '13px',
                boxSizing: 'border-box',
                backgroundColor: 'var(--code-bg, #1f2028)',
                color: 'var(--text-h, #f1f5f9)',
              }}
            />
          </div>
        </div>

        {/* Nút submit */}
        {!warning && (
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <button
              type="submit"
              disabled={isSaving}
              style={{
                padding: '9px 18px',
                backgroundColor: isSaving ? '#93c5fd' : '#2563eb',
                color: '#ffffff',
                border: 'none',
                borderRadius: '6px',
                fontSize: '13px',
                fontWeight: '600',
                cursor: isSaving ? 'not-allowed' : 'pointer',
                display: 'inline-flex',
                alignItems: 'center',
                gap: '6px',
              }}
            >
              <span>+</span> {isSaving ? 'Đang lưu...' : 'Lưu suất diễn'}
            </button>
          </div>
        )}
      </form>
    </div>
  );
}
