import { useState, useEffect } from 'react';
import { getEventSeatPricing, updateEventSeatPricing } from '../api/eventsApi';
import { apiMessage } from '../events/management';

function getCategoryColor(name) {
  const lower = (name || '').toLowerCase();
  if (lower.includes('vip')) return '#f59e0b';
  if (lower.includes('standard') || lower.includes('thường') || lower.includes('tiêu chuẩn')) return '#3b82f6';
  if (lower.includes('economy') || lower.includes('tiết kiệm')) return '#10b981';
  if (lower.includes('svip') || lower.includes('v.v.i.p')) return '#ec4899';
  return 'var(--accent, #aa3bff)';
}

/**
 * @param {{
 *   eventId: number | string,
 *   onPricingUpdated?: () => void
 * }} props
 */
export default function SeatPricingManager({ eventId, onPricingUpdated = () => {} }) {
  const [categories, setCategories] = useState([]);
  const [prices, setPrices] = useState({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);

  const fetchPricing = () => {
    if (!eventId) return;
    setLoading(true);
    setError(null);
    getEventSeatPricing(eventId)
      .then((data) => {
        const items = Array.isArray(data) ? data : [];
        setCategories(items);
        const initialPrices = {};
        for (const item of items) {
          initialPrices[item.id] = item.price ?? 0;
        }
        setPrices(initialPrices);
      })
      .catch((err) => {
        setError(apiMessage(err, 'Không thể tải thông tin giá vé theo hạng ghế.'));
      })
      .finally(() => {
        setLoading(false);
      });
  };

  useEffect(() => {
    let ignore = false;
    if (!eventId) return;

    getEventSeatPricing(eventId)
      .then((data) => {
        if (ignore) return;
        const items = Array.isArray(data) ? data : [];
        setCategories(items);
        const initialPrices = {};
        for (const item of items) {
          initialPrices[item.id] = item.price ?? 0;
        }
        setPrices(initialPrices);
      })
      .catch((err) => {
        if (ignore) return;
        setError(apiMessage(err, 'Không thể tải thông tin giá vé theo hạng ghế.'));
      })
      .finally(() => {
        if (ignore) return;
        setLoading(false);
      });

    return () => {
      ignore = true;
    };
  }, [eventId]);

  const handlePriceChange = (categoryId, value) => {
    setPrices((prev) => ({
      ...prev,
      [categoryId]: value,
    }));
    if (error) setError(null);
    if (success) setSuccess(null);
  };

  const handleSave = async (e) => {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    // Client-side validation
    for (const cat of categories) {
      const val = prices[cat.id];
      const num = Number(val);
      if (val === '' || val === null || val === undefined || isNaN(num) || num < 0) {
        setError('Giá vé phải lớn hơn hoặc bằng 0.');
        return;
      }
    }

    setSaving(true);
    try {
      const payload = {
        categories: categories.map((cat) => ({
          categoryId: cat.id,
          price: Number(prices[cat.id]),
        })),
      };

      const result = await updateEventSeatPricing(eventId, payload);
      setSuccess(result?.message || 'Cập nhật giá vé theo hạng ghế thành công!');

      // Update state with any updated category data returned
      if (result?.categories && Array.isArray(result.categories)) {
        setCategories(result.categories);
        const updated = {};
        for (const item of result.categories) {
          updated[item.id] = item.price;
        }
        setPrices(updated);
      }

      if (onPricingUpdated) {
        onPricingUpdated();
      }
    } catch (err) {
      setError(apiMessage(err, 'Có lỗi xảy ra khi cập nhật giá vé.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div
      className="seat-pricing-manager"
      style={{
        backgroundColor: 'var(--bg, #16171d)',
        border: '1px solid var(--border, #2e303a)',
        borderRadius: '8px',
        padding: '24px',
        marginTop: '24px',
        textAlign: 'left',
        boxShadow: '0 1px 3px rgba(0,0,0,0.08)',
      }}
    >
      <style>{`
        @keyframes seatPricingSpin {
          from { transform: rotate(0deg); }
          to { transform: rotate(360deg); }
        }
        .seat-pricing-btn-save:hover:not(:disabled) {
          background-color: #15803d !important;
        }
        .seat-pricing-btn-reset:hover:not(:disabled) {
          background-color: var(--code-bg, #1f2028) !important;
          color: var(--text-h, #f1f5f9) !important;
          border-color: var(--text, #9ca3af) !important;
        }
        .seat-pricing-input-wrap:focus-within {
          border-color: var(--accent, #aa3bff) !important;
          box-shadow: 0 0 0 2px var(--accent-bg, rgba(170, 59, 255, 0.25)) !important;
        }
        .seat-pricing-card:hover {
          border-color: var(--accent-border, rgba(170, 59, 255, 0.4)) !important;
        }
      `}</style>

      {/* Header section */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          marginBottom: '18px',
          flexWrap: 'wrap',
          gap: '8px',
        }}
      >
        <div>
          <h3
            style={{
              margin: '0 0 4px 0',
              fontSize: '18px',
              color: 'var(--text-h, #f1f5f9)',
              fontWeight: '600',
            }}
          >
            Thiết lập giá vé theo hạng ghế
          </h3>
          <p style={{ margin: 0, fontSize: '13px', color: 'var(--text, #9ca3af)' }}>
            Cấu hình giá bán vé (VNĐ) tương ứng với từng hạng ghế của các suất diễn trong sự kiện.
          </p>
        </div>
        {!loading && categories.length > 0 && (
          <span
            style={{
              padding: '4px 10px',
              borderRadius: '12px',
              fontSize: '12px',
              fontWeight: '600',
              backgroundColor: 'var(--code-bg, #1f2028)',
              border: '1px solid var(--border, #2e303a)',
              color: 'var(--text-h, #f1f5f9)',
            }}
          >
            {categories.length} hạng ghế
          </span>
        )}
      </div>

      {/* Loading state */}
      {loading && (
        <div
          style={{
            padding: '32px 16px',
            textAlign: 'center',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px dashed var(--border, #2e303a)',
            borderRadius: '6px',
            color: 'var(--text, #9ca3af)',
            fontSize: '14px',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            gap: '10px',
          }}
        >
          <svg
            style={{
              animation: 'seatPricingSpin 1s linear infinite',
              width: '20px',
              height: '20px',
              color: 'var(--accent, #aa3bff)',
            }}
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <path d="M21 12a9 9 0 1 1-6.219-8.56" />
          </svg>
          <p style={{ margin: 0 }}>Đang tải danh sách hạng ghế và giá vé...</p>
        </div>
      )}

      {/* Alert Error */}
      {error && (
        <div
          role="alert"
          style={{
            padding: '12px 16px',
            marginBottom: '16px',
            backgroundColor: '#fef2f2',
            border: '1px solid #fecaca',
            borderRadius: '6px',
            color: '#b91c1c',
            fontSize: '14px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            gap: '8px',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <span style={{ fontSize: '16px', lineHeight: 1 }}>⚠</span>
            <span style={{ whiteSpace: 'pre-wrap' }}>{error}</span>
          </div>
          <button
            type="button"
            onClick={() => setError(null)}
            style={{
              background: 'none',
              border: 'none',
              color: '#b91c1c',
              cursor: 'pointer',
              fontWeight: 'bold',
              fontSize: '14px',
              padding: '2px 6px',
            }}
            aria-label="Đóng thông báo lỗi"
          >
            ✕
          </button>
        </div>
      )}

      {/* Alert Success */}
      {success && (
        <div
          role="status"
          style={{
            padding: '12px 16px',
            marginBottom: '16px',
            backgroundColor: '#f0fdf4',
            border: '1px solid #bbf7d0',
            borderRadius: '6px',
            color: '#15803d',
            fontSize: '14px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            gap: '8px',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <span style={{ fontSize: '16px', lineHeight: 1 }}>✓</span>
            <span>{success}</span>
          </div>
          <button
            type="button"
            onClick={() => setSuccess(null)}
            style={{
              background: 'none',
              border: 'none',
              color: '#15803d',
              cursor: 'pointer',
              fontWeight: 'bold',
              fontSize: '14px',
              padding: '2px 6px',
            }}
            aria-label="Đóng thông báo thành công"
          >
            ✕
          </button>
        </div>
      )}

      {/* Empty state */}
      {!loading && categories.length === 0 && (
        <div
          style={{
            padding: '30px 16px',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px dashed var(--border, #2e303a)',
            borderRadius: '6px',
            textAlign: 'center',
            color: 'var(--text, #9ca3af)',
          }}
        >
          <p
            style={{
              margin: '0 0 6px 0',
              fontSize: '14px',
              fontWeight: '500',
              color: 'var(--text-h, #f1f5f9)',
            }}
          >
            Chưa có hạng ghế nào
          </p>
          <p style={{ margin: '0 0 16px 0', fontSize: '13px' }}>
            Chưa có hạng ghế nào cho sự kiện này. Vui lòng thêm suất diễn và tải lên sơ đồ ghế trước khi thiết lập giá vé.
          </p>
          <button
            type="button"
            onClick={fetchPricing}
            style={{
              padding: '6px 14px',
              borderRadius: '6px',
              border: '1px solid var(--border, #2e303a)',
              backgroundColor: 'var(--bg, #16171d)',
              color: 'var(--text-h, #f1f5f9)',
              fontSize: '13px',
              cursor: 'pointer',
            }}
          >
            Làm mới
          </button>
        </div>
      )}

      {/* Main pricing configuration form */}
      {!loading && categories.length > 0 && (
        <form onSubmit={handleSave}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px', marginBottom: '20px' }}>
            {categories.map((cat) => {
              const currentPrice = prices[cat.id];
              const numPrice = Number(currentPrice);
              const isValidNumber =
                currentPrice !== undefined &&
                currentPrice !== '' &&
                !isNaN(numPrice) &&
                numPrice >= 0;

              return (
                <div
                  key={cat.id}
                  className="seat-pricing-card"
                  style={{
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    padding: '16px 20px',
                    backgroundColor: 'var(--code-bg, #1f2028)',
                    border: '1px solid var(--border, #2e303a)',
                    borderRadius: '8px',
                    flexWrap: 'wrap',
                    gap: '16px',
                    transition: 'border-color 0.2s, box-shadow 0.2s',
                  }}
                >
                  {/* Category Details */}
                  <div style={{ display: 'flex', alignItems: 'center', gap: '14px', flexWrap: 'wrap' }}>
                    <span
                      style={{
                        display: 'inline-block',
                        width: '12px',
                        height: '12px',
                        borderRadius: '50%',
                        backgroundColor: getCategoryColor(cat.name),
                        boxShadow: `0 0 6px ${getCategoryColor(cat.name)}88`,
                        flexShrink: 0,
                      }}
                    />
                    <div>
                      <div
                        style={{
                          fontSize: '15px',
                          fontWeight: '600',
                          color: 'var(--text-h, #f1f5f9)',
                          display: 'flex',
                          alignItems: 'center',
                          gap: '8px',
                        }}
                      >
                        {cat.name}
                        <span
                          style={{
                            fontSize: '11px',
                            fontWeight: '500',
                            padding: '2px 8px',
                            borderRadius: '10px',
                            backgroundColor: 'rgba(255,255,255,0.06)',
                            border: '1px solid var(--border, #2e303a)',
                            color: 'var(--text, #9ca3af)',
                          }}
                        >
                          Mã #{cat.id}
                        </span>
                      </div>
                      <div
                        style={{
                          fontSize: '13px',
                          color: 'var(--text, #9ca3af)',
                          marginTop: '4px',
                          display: 'flex',
                          alignItems: 'center',
                          gap: '10px',
                          flexWrap: 'wrap',
                        }}
                      >
                        <span>
                          Suất diễn: <strong style={{ color: 'var(--text-h, #f1f5f9)' }}>#{cat.showtimeId}</strong>
                        </span>
                        <span>•</span>
                        <span>
                          Số ghế: <strong style={{ color: 'var(--text-h, #f1f5f9)' }}>{cat.seatCount ?? 0} ghế</strong>
                        </span>
                      </div>
                    </div>
                  </div>

                  {/* Price input with currency indicator */}
                  <div
                    style={{
                      display: 'flex',
                      flexDirection: 'column',
                      alignItems: 'flex-end',
                      gap: '4px',
                      minWidth: '220px',
                    }}
                  >
                    <label
                      htmlFor={`price-input-${cat.id}`}
                      style={{
                        fontSize: '12px',
                        fontWeight: '500',
                        color: 'var(--text, #9ca3af)',
                        marginBottom: '2px',
                      }}
                    >
                      Giá vé mỗi ghế
                    </label>
                    <div
                      className="seat-pricing-input-wrap"
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        backgroundColor: 'var(--bg, #16171d)',
                        border: '1px solid var(--border, #2e303a)',
                        borderRadius: '6px',
                        overflow: 'hidden',
                        width: '100%',
                        transition: 'border-color 0.15s, box-shadow 0.15s',
                      }}
                    >
                      <input
                        id={`price-input-${cat.id}`}
                        type="number"
                        min="0"
                        step="1000"
                        value={prices[cat.id] ?? ''}
                        onChange={(e) => handlePriceChange(cat.id, e.target.value)}
                        disabled={saving}
                        required
                        placeholder="0"
                        style={{
                          flex: 1,
                          padding: '8px 12px',
                          border: 'none',
                          outline: 'none',
                          backgroundColor: 'transparent',
                          color: 'var(--text-h, #f1f5f9)',
                          fontSize: '14px',
                          fontWeight: '600',
                          textAlign: 'right',
                          minWidth: '100px',
                          boxSizing: 'border-box',
                        }}
                      />
                      <span
                        style={{
                          padding: '8px 12px',
                          fontSize: '13px',
                          fontWeight: '600',
                          color: 'var(--text, #9ca3af)',
                          backgroundColor: 'rgba(255,255,255,0.03)',
                          borderLeft: '1px solid var(--border, #2e303a)',
                          userSelect: 'none',
                        }}
                      >
                        ₫
                      </span>
                    </div>

                    {isValidNumber && (
                      <span
                        style={{
                          fontSize: '12px',
                          color: 'var(--accent, #aa3bff)',
                          fontWeight: '500',
                          marginTop: '2px',
                        }}
                      >
                        ≈ {numPrice.toLocaleString('vi-VN')} VNĐ
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>

          {/* Action buttons */}
          <div
            style={{
              display: 'flex',
              justifyContent: 'flex-end',
              alignItems: 'center',
              gap: '12px',
              paddingTop: '16px',
              borderTop: '1px solid var(--border, #2e303a)',
              flexWrap: 'wrap',
            }}
          >
            <button
              type="button"
              onClick={fetchPricing}
              disabled={saving || loading}
              className="seat-pricing-btn-reset"
              style={{
                padding: '9px 18px',
                borderRadius: '6px',
                border: '1px solid var(--border, #2e303a)',
                backgroundColor: 'var(--bg, #16171d)',
                color: 'var(--text-h, #f1f5f9)',
                fontSize: '13px',
                fontWeight: '500',
                cursor: saving || loading ? 'not-allowed' : 'pointer',
                opacity: saving || loading ? 0.6 : 1,
                transition: 'background-color 0.15s, border-color 0.15s',
              }}
            >
              Hủy thay đổi / Làm mới
            </button>
            <button
              type="submit"
              disabled={saving || loading}
              className="seat-pricing-btn-save"
              style={{
                padding: '9px 20px',
                borderRadius: '6px',
                border: 'none',
                backgroundColor: saving ? '#15803d' : '#16a34a',
                color: '#ffffff',
                fontSize: '13px',
                fontWeight: '600',
                cursor: saving || loading ? 'not-allowed' : 'pointer',
                opacity: saving || loading ? 0.75 : 1,
                display: 'inline-flex',
                alignItems: 'center',
                gap: '8px',
                boxShadow: '0 1px 2px rgba(0,0,0,0.1)',
                transition: 'background-color 0.15s, opacity 0.15s',
              }}
            >
              {saving ? (
                <>
                  <svg
                    style={{
                      animation: 'seatPricingSpin 1s linear infinite',
                      width: '14px',
                      height: '14px',
                    }}
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  >
                    <path d="M21 12a9 9 0 1 1-6.219-8.56" />
                  </svg>
                  <span>Đang lưu...</span>
                </>
              ) : (
                <>
                  <span style={{ fontSize: '14px' }}>💾</span>
                  <span>Lưu bảng giá</span>
                </>
              )}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}
