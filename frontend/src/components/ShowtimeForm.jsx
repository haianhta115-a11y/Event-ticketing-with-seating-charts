import { useState, useRef } from 'react';
import { createShowtime } from '../api/eventsApi';
import { vietnamToUtc, validateShowtime, apiErrors, apiMessage, submissionGate } from '../events/management';

export default function ShowtimeForm({ eventId, existingShowtimes = [], onShowtimeCreated }) {
  const [startTime, setStartTime] = useState('');
  const [endTime, setEndTime] = useState('');
  const [error, setError] = useState('');
  const [warning, setWarning] = useState('');
  const [isSaving, setIsSaving] = useState(false);
  const gate = useRef(submissionGate());
  const [fieldErrors, setFieldErrors] = useState({});
  const [savedWarning, setSavedWarning] = useState('');
  const [savedId, setSavedId] = useState(null);

  // Kiểm tra trùng lặp hoàn toàn
  const checkExactDuplicate = (startVal, endVal) => {
    const startTimestamp = new Date(vietnamToUtc(startVal)).getTime();
    const endTimestamp = endVal ? new Date(vietnamToUtc(endVal)).getTime() : null;

    return existingShowtimes.some((s) => {
      const sStart = new Date(s.startTime).getTime();
      const sEnd = s.endTime ? new Date(s.endTime).getTime() : null;
      return sStart === startTimestamp && sEnd === endTimestamp;
    });
  };

  const handleSave = async (forceSave = false) => {
    if (!gate.current.enter()) return;
    setError('');
    setFieldErrors({});
    setSavedWarning('');
    setSavedId(null);

    const validationErrors = validateShowtime(startTime, endTime);
    if (Object.keys(validationErrors).length) {
      setError('Vui lòng kiểm tra các ô nhập liệu.');
      setWarning('');
      setFieldErrors(validationErrors);
      gate.current.leave();
      return;
    }

    // Kiểm tra trùng lặp nếu chưa được xác nhận bỏ qua cảnh báo
    if (!forceSave) {
      const isDuplicate = checkExactDuplicate(startTime, endTime);
      if (isDuplicate) {
        setWarning('Trùng thời gian với một suất diễn khác. Có thể bạn đang tổ chức song song ở hai phòng');
        gate.current.leave();
        return;
      }
    }

    setIsSaving(true);
    setWarning('');

    try {
      const created = await createShowtime(eventId, {
        startTime: vietnamToUtc(startTime),
        endTime: endTime ? vietnamToUtc(endTime) : null,
      });

      // Reset form sau khi lưu thành công
      setStartTime('');
      setEndTime('');
      setError('');
      setWarning('');
      setSavedWarning((created.warnings || []).join(' '));
      setSavedId(created.id);

      if (onShowtimeCreated) {
        onShowtimeCreated(created);
      }
    } catch (err) {
      setFieldErrors(apiErrors(err));
      setError(apiMessage(err));
    } finally {
      setIsSaving(false);
      gate.current.leave();
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
        Nhập giờ Việt Nam (UTC+7). Thời điểm bắt đầu phải ở tương lai.
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
      {savedId !== null && <p role="status">Đã lưu suất diễn #{savedId}. {savedWarning}</p>}
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
              aria-describedby="startTime-error"
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
            {fieldErrors.startTime && <p id="startTime-error" role="alert">{fieldErrors.startTime}</p>}
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
              aria-describedby="endTime-error"
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
            {fieldErrors.endTime && <p id="endTime-error" role="alert">{fieldErrors.endTime}</p>}
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
