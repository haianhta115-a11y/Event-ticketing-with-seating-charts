import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getShowtimeSeats } from '../api/seatsApi';

/**
 * Tối ưu hóa từng ghế bằng React.memo để tránh render lại toàn bộ hàng nghìn ghế.
 * Không gắn onClick riêng cho từng ghế để đạt hiệu năng cao nhất (dùng event delegation).
 */
const SeatItem = React.memo(function SeatItem({ seat, pixelX, pixelY, size, isSelected }) {
  const isAvailable = seat.status === 'Available';
  const isHeld = seat.status === 'Held';

  // Màu sắc và ký hiệu tường minh cho 3 trạng thái
  const fillColor = isAvailable ? '#16a34a' : isHeld ? '#d97706' : '#4b5563';
  const strokeColor = isSelected ? '#38bdf8' : isAvailable ? '#15803d' : isHeld ? '#b45309' : '#374151';
  const symbol = isAvailable ? '✓' : isHeld ? '⏳' : '✕';
  const statusText = isAvailable ? 'Trống' : isHeld ? 'Đang có người giữ' : 'Đã bán';
  const label = `Khu ${seat.section}, hàng ${seat.row}, ghế ${seat.number} - ${statusText}`;

  const fontSize = Math.max(9, Math.round(size * 0.42));

  return (
    <g
      data-seat-id={seat.id}
      tabIndex={0}
      aria-label={label}
      role="button"
      style={{ outline: 'none', cursor: 'pointer' }}
    >
      <title>{label}</title>
      <rect
        data-seat-id={seat.id}
        x={pixelX}
        y={pixelY}
        width={size}
        height={size}
        rx={Math.max(2, Math.round(size * 0.18))}
        fill={fillColor}
        stroke={strokeColor}
        strokeWidth={isSelected ? '2.5' : '1.2'}
      />
      <text
        x={pixelX + size / 2}
        y={pixelY + size / 2 + (isHeld ? fontSize * 0.35 : fontSize * 0.38)}
        textAnchor="middle"
        fill="#ffffff"
        fontSize={fontSize}
        fontWeight="bold"
        pointerEvents="none"
        userSelect="none"
      >
        {symbol}
      </text>
      {/* Viền nổi bật cho ghế đang được xem */}
      {isSelected && (
        <rect
          x={pixelX - 3}
          y={pixelY - 3}
          width={size + 6}
          height={size + 6}
          rx={Math.max(4, Math.round(size * 0.22))}
          fill="none"
          stroke="#38bdf8"
          strokeWidth="2.5"
          strokeDasharray="4 2"
          pointerEvents="none"
        />
      )}
    </g>
  );
});

/**
 * Tính toán tỷ lệ fitScale để sơ đồ vừa khít khung nhìn
 */
function computeFitScale(containerWidth, containerHeight, contentWidth, contentHeight) {
  const padding = 24;
  const availW = Math.max(100, containerWidth - padding * 2);
  const availH = Math.max(100, containerHeight - padding * 2);

  return Math.min(availW / contentWidth, availH / contentHeight);
}

/**
 * Giới hạn vị trí kéo:
 * - Khi sơ đồ nhỏ hơn khung nhìn: căn giữa
 * - Khi sơ đồ lớn hơn: chỉ cho kéo sát mép (không đẩy sơ đồ ra ngoài khung)
 */
function clampTransform(tx, ty, scale, containerWidth, containerHeight, contentWidth, contentHeight) {
  const contentW = contentWidth * scale;
  const contentH = contentHeight * scale;

  let clampedX;
  if (contentW <= containerWidth) {
    clampedX = (containerWidth - contentW) / 2;
  } else {
    const minX = containerWidth - contentW;
    const maxX = 0;
    clampedX = Math.max(minX, Math.min(maxX, tx));
  }

  let clampedY;
  if (contentH <= containerHeight) {
    clampedY = (containerHeight - contentH) / 2;
  } else {
    const minY = containerHeight - contentH;
    const maxY = 0;
    clampedY = Math.max(minY, Math.min(maxY, ty));
  }

  return {
    scale,
    x: clampedX,
    y: clampedY,
  };
}

export default function SeatMap() {
  const { id } = useParams();
  const navigate = useNavigate();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [seatData, setSeatData] = useState(null);
  const [isRefreshing, setIsRefreshing] = useState(false);

  // Ghế đang được bấm xem thông tin
  const [selectedSeat, setSelectedSeat] = useState(null);

  // Tỷ lệ zoom hiện tại để hiển thị gợi ý kích thước ghế
  const [zoomLevel, setZoomLevel] = useState(1);

  // Refs quản lý DOM và hiệu năng zoom/pan không render lại React
  const containerRef = useRef(null);
  const contentGroupRef = useRef(null);
  const transformRef = useRef({ x: 0, y: 0, scale: 1 });
  const fitScaleRef = useRef(1);
  const rafIdRef = useRef(null);
  const animRafRef = useRef(null);
  const wheelDebounceRef = useRef(null);

  // Quản lý Pointer Events
  const activePointers = useRef(new Map());
  const pointerStartPos = useRef({ x: 0, y: 0 });
  const dragStartTransform = useRef({ x: 0, y: 0, scale: 1 });
  const isDraggingRef = useRef(false);

  // Pinch zoom state: lưu khoảng cách bắt đầu, scale bắt đầu và điểm giữa hai ngón theo tọa độ sơ đồ
  const pinchStartDist = useRef(0);
  const pinchStartScale = useRef(1);
  const pinchDiagramMid = useRef({ x: 0, y: 0 });

  const fetchSeats = useCallback(async (isRefresh = false) => {
    if (isRefresh) {
      setIsRefreshing(true);
    } else {
      setLoading(true);
    }
    setError(null);

    try {
      const res = await getShowtimeSeats(id);
      setSeatData(res);
      setSelectedSeat(null);
    } catch (err) {
      setError(err?.message || 'Không thể tải sơ đồ ghế từ máy chủ');
    } finally {
      setLoading(false);
      setIsRefreshing(false);
    }
  }, [id]);

  useEffect(() => {
    let ignore = false;

    getShowtimeSeats(id)
      .then((res) => {
        if (!ignore) {
          setSeatData(res);
        }
      })
      .catch((err) => {
        if (!ignore) {
          setError(err?.message || 'Không thể tải sơ đồ ghế từ máy chủ');
        }
      })
      .finally(() => {
        if (!ignore) {
          setLoading(false);
        }
      });

    return () => {
      ignore = true;
    };
  }, [id]);

  // Tối ưu hóa tính toán layout và thống kê số lượng ghế bằng useMemo
  const layout = useMemo(() => {
    const seats = seatData?.seats;
    if (!seats || seats.length === 0) {
      return null;
    }

    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;

    let availableCount = 0;
    let heldCount = 0;
    let soldCount = 0;
    const seatMapById = new Map();

    for (let i = 0; i < seats.length; i++) {
      const s = seats[i];
      seatMapById.set(s.id, s);

      if (s.x < minX) minX = s.x;
      if (s.x > maxX) maxX = s.x;
      if (s.y < minY) minY = s.y;
      if (s.y > maxY) maxY = s.y;

      if (s.status === 'Available') availableCount++;
      else if (s.status === 'Held') heldCount++;
      else if (s.status === 'Sold') soldCount++;
    }

    const cols = maxX - minX + 1;
    const rows = maxY - minY + 1;

    // Tự động scale kích thước theo mật độ ghế (hỗ trợ hiển thị mượt cho 2000 ghế)
    const seatSize = cols > 30 ? 22 : 32;
    const gap = cols > 30 ? 6 : 8;
    const paddingX = 40;
    const paddingY = 40;
    const stageHeight = 60;

    const width = Math.max(650, paddingX * 2 + cols * (seatSize + gap) - gap);
    const height = paddingY * 2 + stageHeight + rows * (seatSize + gap) - gap;

    const mappedSeats = seats.map((s) => ({
      ...s,
      pixelX: paddingX + (s.x - minX) * (seatSize + gap),
      pixelY: paddingY + stageHeight + (s.y - minY) * (seatSize + gap),
      size: seatSize,
    }));

    return {
      width,
      height,
      paddingX,
      stageHeight,
      baseSeatSize: seatSize,
      counts: {
        total: seats.length,
        available: availableCount,
        held: heldCount,
        sold: soldCount,
      },
      mappedSeats,
      seatMapById,
    };
  }, [seatData]);

  // Hoạt ảnh chuyển đổi mượt khoảng 150ms cho các thao tác bấm nút (+, −, Đặt lại)
  const animateTransformTo = useCallback((targetTransform, duration = 150) => {
    if (animRafRef.current) {
      cancelAnimationFrame(animRafRef.current);
      animRafRef.current = null;
    }

    const start = { ...transformRef.current };
    const startTime = performance.now();

    const step = (now) => {
      const elapsed = now - startTime;
      const progress = Math.min(1, elapsed / duration);
      // Ease-out cubic: 1 - (1 - progress)^3
      const ease = 1 - Math.pow(1 - progress, 3);

      const s = start.scale + (targetTransform.scale - start.scale) * ease;
      const x = start.x + (targetTransform.x - start.x) * ease;
      const y = start.y + (targetTransform.y - start.y) * ease;

      transformRef.current = { scale: s, x, y };

      if (contentGroupRef.current) {
        contentGroupRef.current.setAttribute('transform', `translate(${x}, ${y}) scale(${s})`);
      }

      if (progress < 1) {
        animRafRef.current = requestAnimationFrame(step);
      } else {
        animRafRef.current = null;
        transformRef.current = targetTransform;
        if (contentGroupRef.current) {
          contentGroupRef.current.setAttribute(
            'transform',
            `translate(${targetTransform.x}, ${targetTransform.y}) scale(${targetTransform.scale})`
          );
        }
        setZoomLevel(targetTransform.scale);
      }
    };

    animRafRef.current = requestAnimationFrame(step);
  }, []);

  // Tự động thu vừa khít khung nhìn và căn giữa khi tải xong sơ đồ hoặc đổi layout
  useEffect(() => {
    if (!layout || !containerRef.current) return;

    const raf = requestAnimationFrame(() => {
      if (!containerRef.current) return;
      const cw = containerRef.current.clientWidth || 800;
      const ch = containerRef.current.clientHeight || 500;
      const fitScale = computeFitScale(cw, ch, layout.width, layout.height);

      fitScaleRef.current = fitScale;

      const fitX = (cw - layout.width * fitScale) / 2;
      const fitY = (ch - layout.height * fitScale) / 2;
      const fit = { scale: fitScale, x: fitX, y: fitY };

      transformRef.current = fit;

      if (contentGroupRef.current) {
        contentGroupRef.current.setAttribute(
          'transform',
          `translate(${fit.x}, ${fit.y}) scale(${fit.scale})`
        );
      }
      setZoomLevel(fit.scale);
    });

    return () => {
      cancelAnimationFrame(raf);
      if (animRafRef.current) {
        cancelAnimationFrame(animRafRef.current);
        animRafRef.current = null;
      }
    };
  }, [layout]);

  // Xử lý zoom bằng lăn chuột (Wheel) trên máy tính dùng hệ số mượt Math.exp(-deltaY * 0.0015)
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const onWheel = (e) => {
      e.preventDefault();
      if (!layout) return;

      if (animRafRef.current) {
        cancelAnimationFrame(animRafRef.current);
        animRafRef.current = null;
      }

      const rect = container.getBoundingClientRect();
      const pivotX = e.clientX - rect.left;
      const pivotY = e.clientY - rect.top;

      // Hệ số zoom mượt liên tục theo deltaY
      const factor = Math.exp(-e.deltaY * 0.0015);

      const current = transformRef.current;
      const minScale = fitScaleRef.current;
      const maxScale = fitScaleRef.current * 8;

      const targetScale = Math.max(minScale, Math.min(maxScale, current.scale * factor));
      if (targetScale === current.scale) return;

      const ratio = targetScale / current.scale;
      const newX = pivotX - (pivotX - current.x) * ratio;
      const newY = pivotY - (pivotY - current.y) * ratio;

      const clamped = clampTransform(
        newX,
        newY,
        targetScale,
        rect.width,
        rect.height,
        layout.width,
        layout.height
      );

      transformRef.current = clamped;

      if (contentGroupRef.current) {
        if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = requestAnimationFrame(() => {
          if (contentGroupRef.current) {
            contentGroupRef.current.setAttribute(
              'transform',
              `translate(${clamped.x}, ${clamped.y}) scale(${clamped.scale})`
            );
          }
        });
      }

      if (wheelDebounceRef.current) clearTimeout(wheelDebounceRef.current);
      wheelDebounceRef.current = setTimeout(() => {
        setZoomLevel(transformRef.current.scale);
      }, 120);
    };

    container.addEventListener('wheel', onWheel, { passive: false });
    return () => {
      container.removeEventListener('wheel', onWheel);
      if (wheelDebounceRef.current) clearTimeout(wheelDebounceRef.current);
    };
  }, [layout]);

  // Bấm trúng ghế (Event Delegation trên SVG)
  const handleTap = useCallback((e) => {
    const target = e.target;
    const seatElem = target.closest ? target.closest('[data-seat-id]') : null;
    if (seatElem && layout) {
      const seatId = seatElem.getAttribute('data-seat-id');
      const seat = layout.seatMapById.get(seatId);
      if (seat) {
        setSelectedSeat(seat);
        return;
      }
    }
    // Bấm vào chỗ trống (nền SVG) để bỏ chọn/ẩn khung
    setSelectedSeat(null);
  }, [layout]);

  // Xử lý Pointer Down (bắt đầu kéo hoặc pinch)
  const handlePointerDown = (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    if (e.target.closest && e.target.closest('button')) return;

    if (animRafRef.current) {
      cancelAnimationFrame(animRafRef.current);
      animRafRef.current = null;
    }

    activePointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });

    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      // Bỏ qua lỗi nếu không thể capture con trỏ
    }

    if (activePointers.current.size === 1) {
      pointerStartPos.current = { x: e.clientX, y: e.clientY };
      dragStartTransform.current = { ...transformRef.current };
      isDraggingRef.current = false;
    } else if (activePointers.current.size === 2) {
      isDraggingRef.current = true;
      const pts = Array.from(activePointers.current.values());
      const dist = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
      pinchStartDist.current = Math.max(10, dist);
      pinchStartScale.current = transformRef.current.scale;

      const rect = containerRef.current.getBoundingClientRect();
      const screenMidX = (pts[0].x + pts[1].x) / 2 - rect.left;
      const screenMidY = (pts[0].y + pts[1].y) / 2 - rect.top;

      // Điểm giữa hai ngón theo tọa độ sơ đồ
      pinchDiagramMid.current = {
        x: (screenMidX - transformRef.current.x) / transformRef.current.scale,
        y: (screenMidY - transformRef.current.y) / transformRef.current.scale,
      };
    }
  };

  // Xử lý Pointer Move (kéo di chuyển hoặc pinch zoom)
  const handlePointerMove = (e) => {
    if (!activePointers.current.has(e.pointerId)) return;
    activePointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });

    if (!containerRef.current || !layout) return;
    const rect = containerRef.current.getBoundingClientRect();
    const cw = rect.width;
    const ch = rect.height;
    const minScale = fitScaleRef.current;
    const maxScale = fitScaleRef.current * 8;

    if (activePointers.current.size === 1) {
      const dx = e.clientX - pointerStartPos.current.x;
      const dy = e.clientY - pointerStartPos.current.y;
      const dist = Math.hypot(dx, dy);

      // Phân biệt chạm (tap) với kéo (drag): di chuyển > 8px coi là kéo
      if (!isDraggingRef.current && dist > 8) {
        isDraggingRef.current = true;
      }

      if (isDraggingRef.current) {
        const newX = dragStartTransform.current.x + dx;
        const newY = dragStartTransform.current.y + dy;
        const clamped = clampTransform(
          newX,
          newY,
          transformRef.current.scale,
          cw,
          ch,
          layout.width,
          layout.height
        );
        transformRef.current = clamped;

        if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = requestAnimationFrame(() => {
          if (contentGroupRef.current) {
            contentGroupRef.current.setAttribute(
              'transform',
              `translate(${clamped.x}, ${clamped.y}) scale(${clamped.scale})`
            );
          }
        });
      }
    } else if (activePointers.current.size === 2) {
      isDraggingRef.current = true;
      const pts = Array.from(activePointers.current.values());
      const currentDist = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);

      if (pinchStartDist.current > 0) {
        // scale = startScale * (currentDistance / startDistance), sau đó clamp
        const rawScale = pinchStartScale.current * (currentDist / pinchStartDist.current);
        const newScale = Math.max(minScale, Math.min(maxScale, rawScale));

        const currentScreenMidX = (pts[0].x + pts[1].x) / 2 - rect.left;
        const currentScreenMidY = (pts[0].y + pts[1].y) / 2 - rect.top;

        // Giữ điểm giữa hai ngón cố định trên màn hình (điều chỉnh translate theo zoom quanh điểm)
        const newTx = currentScreenMidX - pinchDiagramMid.current.x * newScale;
        const newTy = currentScreenMidY - pinchDiagramMid.current.y * newScale;

        const clamped = clampTransform(
          newTx,
          newTy,
          newScale,
          cw,
          ch,
          layout.width,
          layout.height
        );
        transformRef.current = clamped;

        if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = requestAnimationFrame(() => {
          if (contentGroupRef.current) {
            contentGroupRef.current.setAttribute(
              'transform',
              `translate(${clamped.x}, ${clamped.y}) scale(${clamped.scale})`
            );
          }
        });
      }
    }
  };

  // Xử lý Pointer Up (kết thúc thao tác)
  const handlePointerUp = (e) => {
    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      // Bỏ qua lỗi nếu con trỏ đã được giải phóng
    }
    activePointers.current.delete(e.pointerId);

    if (activePointers.current.size === 1) {
      // Còn lại 1 ngón sau pinch -> chuyển thành drag mượt mà, không giật vị trí
      const remaining = Array.from(activePointers.current.values())[0];
      pointerStartPos.current = { x: remaining.x, y: remaining.y };
      dragStartTransform.current = { ...transformRef.current };
      isDraggingRef.current = true;
    } else if (activePointers.current.size === 0) {
      if (!isDraggingRef.current) {
        handleTap(e);
      }
      isDraggingRef.current = false;
      setZoomLevel(transformRef.current.scale);
    }
  };

  const handlePointerCancel = (e) => {
    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      // Bỏ qua lỗi nếu con trỏ đã được giải phóng
    }
    activePointers.current.delete(e.pointerId);
    if (activePointers.current.size === 0) {
      isDraggingRef.current = false;
      setZoomLevel(transformRef.current.scale);
    }
  };

  // Nút "+": zoom quanh TÂM của khung nhìn, nhân 1.25, hoạt ảnh 150ms
  const handleZoomIn = () => {
    if (!containerRef.current || !layout) return;
    const rect = containerRef.current.getBoundingClientRect();
    const cw = rect.width;
    const ch = rect.height;
    const pivotX = cw / 2;
    const pivotY = ch / 2;

    const current = transformRef.current;
    const maxScale = fitScaleRef.current * 8;

    const targetScale = Math.min(maxScale, current.scale * 1.25);
    if (targetScale === current.scale) return;

    const ratio = targetScale / current.scale;
    const targetX = pivotX - (pivotX - current.x) * ratio;
    const targetY = pivotY - (pivotY - current.y) * ratio;

    const clamped = clampTransform(targetX, targetY, targetScale, cw, ch, layout.width, layout.height);
    animateTransformTo(clamped, 150);
  };

  // Nút "−": zoom quanh TÂM của khung nhìn, chia 1.25, hoạt ảnh 150ms
  const handleZoomOut = () => {
    if (!containerRef.current || !layout) return;
    const rect = containerRef.current.getBoundingClientRect();
    const cw = rect.width;
    const ch = rect.height;
    const pivotX = cw / 2;
    const pivotY = ch / 2;

    const current = transformRef.current;
    const minScale = fitScaleRef.current;

    const targetScale = Math.max(minScale, current.scale / 1.25);
    if (targetScale === current.scale) return;

    const ratio = targetScale / current.scale;
    const targetX = pivotX - (pivotX - current.x) * ratio;
    const targetY = pivotY - (pivotY - current.y) * ratio;

    const clamped = clampTransform(targetX, targetY, targetScale, cw, ch, layout.width, layout.height);
    animateTransformTo(clamped, 150);
  };

  // Nút "Đặt lại": về fitScale và căn giữa, hoạt ảnh 150ms
  const handleResetZoom = () => {
    if (!containerRef.current || !layout) return;
    const rect = containerRef.current.getBoundingClientRect();
    const cw = rect.width;
    const ch = rect.height;
    const fitScale = fitScaleRef.current;

    const targetX = (cw - layout.width * fitScale) / 2;
    const targetY = (ch - layout.height * fitScale) / 2;

    const target = {
      scale: fitScale,
      x: targetX,
      y: targetY,
    };
    animateTransformTo(target, 150);
  };

  // Kích thước hiển thị thực tế của ghế trên màn hình
  const currentSeatPixelSize = layout ? (layout.baseSeatSize || 24) * zoomLevel : 24;
  const isSeatSmall = currentSeatPixelSize < 24;

  // Trạng thái đang tải dữ liệu
  if (loading) {
    return (
      <div style={{ maxWidth: '900px', margin: '60px auto', padding: '40px', textAlign: 'center', backgroundColor: 'var(--code-bg, #1f2028)', borderRadius: '8px', border: '1px solid var(--border, #2e303a)', color: 'var(--text, #9ca3af)' }}>
        <p style={{ margin: 0, fontSize: '16px', fontWeight: '500', color: 'var(--text-h, #f1f5f9)' }}>
          Đang tải sơ đồ ghế...
        </p>
      </div>
    );
  }

  // Trạng thái gặp lỗi khi gọi API
  if (error) {
    return (
      <div style={{ maxWidth: '600px', margin: '60px auto', padding: '30px', textAlign: 'center', backgroundColor: '#fff1f2', border: '1px solid #fecdd3', borderRadius: '8px', color: '#9f1239' }}>
        <h3 style={{ margin: '0 0 10px 0', fontSize: '18px' }}>Lỗi tải sơ đồ ghế</h3>
        <p style={{ margin: '0 0 20px 0', fontSize: '14px' }}>{error}</p>
        <div style={{ display: 'flex', justifyContent: 'center', gap: '12px' }}>
          <button
            onClick={() => fetchSeats(false)}
            style={{ padding: '8px 18px', backgroundColor: '#e11d48', color: '#fff', border: 'none', borderRadius: '4px', cursor: 'pointer', fontWeight: '500', fontSize: '14px' }}
          >
            Thử lại
          </button>
          <button
            onClick={() => navigate(`/shows/${id}`)}
            style={{ padding: '8px 18px', backgroundColor: 'transparent', color: '#9f1239', border: '1px solid #fecdd3', borderRadius: '4px', cursor: 'pointer', fontSize: '14px' }}
          >
            Quay lại
          </button>
        </div>
      </div>
    );
  }

  // Nếu hasSeatMap là false: CHỈ hiện thông báo "Suất diễn chưa mở bán", không vẽ lưới rỗng
  if (seatData && seatData.hasSeatMap === false) {
    return (
      <div
        style={{
          maxWidth: '650px',
          margin: '80px auto',
          padding: '40px 24px',
          textAlign: 'center',
          backgroundColor: 'var(--code-bg, #1f2028)',
          border: '1px solid var(--border, #2e303a)',
          borderRadius: '10px',
          boxShadow: '0 1px 3px rgba(0,0,0,0.1)',
        }}
      >
        <div style={{ fontSize: '42px', marginBottom: '16px' }}>🎟️</div>
        <h2 style={{ margin: '0 0 12px 0', fontSize: '22px', color: 'var(--text-h, #f1f5f9)', fontWeight: '600' }}>
          Suất diễn chưa mở bán
        </h2>
        <p style={{ margin: '0 0 24px 0', fontSize: '14px', color: 'var(--text, #9ca3af)', lineHeight: '1.5' }}>
          Sơ đồ ghế của suất diễn này hiện chưa sẵn sàng hoặc chưa được phát hành vé. Vui lòng quay lại sau.
        </p>
        <button
          onClick={() => navigate(`/shows/${id}`)}
          style={{
            padding: '10px 24px',
            backgroundColor: '#2563eb',
            color: '#ffffff',
            border: 'none',
            borderRadius: '6px',
            fontSize: '14px',
            fontWeight: '600',
            cursor: 'pointer',
          }}
        >
          ← Quay lại chi tiết suất diễn
        </button>
      </div>
    );
  }

  return (
    <div style={{ maxWidth: '1200px', margin: '20px auto', padding: '16px' }}>
      {/* CSS responsive cho màn hình hẹp dưới 600px */}
      <style>{`
        @media (max-width: 600px) {
          .seat-map-top-bar {
            flex-direction: column !important;
            align-items: stretch !important;
          }
          .seat-map-top-bar button {
            width: 100% !important;
            justify-content: center !important;
          }
          .seat-map-legend-container {
            flex-direction: column !important;
            align-items: flex-start !important;
            gap: 12px !important;
          }
          .seat-map-legend-items {
            flex-direction: column !important;
            align-items: flex-start !important;
            gap: 8px !important;
            width: 100% !important;
          }
          .seat-map-viewport {
            height: 380px !important;
          }
        }
      `}</style>

      {/* Thanh điều hướng và nút Làm mới */}
      <div
        className="seat-map-top-bar"
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          marginBottom: '20px',
          flexWrap: 'wrap',
          gap: '12px',
        }}
      >
        <button
          onClick={() => navigate(`/shows/${id}`)}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '6px',
            padding: '8px 14px',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px solid var(--border, #2e303a)',
            color: 'var(--text-h, #f1f5f9)',
            borderRadius: '6px',
            cursor: 'pointer',
            fontSize: '14px',
            fontWeight: '500',
          }}
        >
          ← Quay lại chi tiết suất diễn
        </button>

        <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
          <button
            onClick={() => fetchSeats(true)}
            disabled={isRefreshing}
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: '6px',
              padding: '8px 16px',
              backgroundColor: '#2563eb',
              color: '#ffffff',
              border: 'none',
              borderRadius: '6px',
              cursor: isRefreshing ? 'not-allowed' : 'pointer',
              fontSize: '14px',
              fontWeight: '600',
              opacity: isRefreshing ? 0.7 : 1,
            }}
          >
            <span>🔄</span> {isRefreshing ? 'Đang làm mới...' : 'Làm mới'}
          </button>
        </div>
      </div>

      {/* Tiêu đề trang & Chế độ xem */}
      <div style={{ marginBottom: '16px', textAlign: 'left' }}>
        <h2 style={{ margin: '0 0 6px 0', fontSize: '24px', color: 'var(--text-h, #f1f5f9)', fontWeight: '700' }}>
          Sơ Đồ Ghế Suất Diễn
        </h2>
        <p style={{ margin: 0, fontSize: '14px', color: 'var(--text, #9ca3af)' }}>
          Chế độ xem sơ đồ ghế thời gian thực (Kéo để di chuyển • Cuộn hoặc 2 ngón để phóng to thu nhỏ)
        </p>
      </div>

      {/* Chú thích trạng thái (Legend) & Đếm số lượng ghế */}
      {layout && (
        <div
          className="seat-map-legend-container"
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            flexWrap: 'wrap',
            gap: '16px',
            padding: '14px 20px',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px solid var(--border, #2e303a)',
            borderRadius: '8px',
            marginBottom: '16px',
          }}
        >
          <div className="seat-map-legend-items" style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: '20px' }}>
            {/* Trống */}
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span
                style={{
                  width: '24px',
                  height: '24px',
                  borderRadius: '4px',
                  backgroundColor: '#16a34a',
                  border: '1px solid #15803d',
                  color: '#ffffff',
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  fontSize: '13px',
                  fontWeight: 'bold',
                }}
              >
                ✓
              </span>
              <span style={{ fontSize: '14px', color: 'var(--text-h, #f1f5f9)', fontWeight: '500' }}>
                Trống ({layout.counts.available})
              </span>
            </div>

            {/* Đang giữ */}
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span
                style={{
                  width: '24px',
                  height: '24px',
                  borderRadius: '4px',
                  backgroundColor: '#d97706',
                  border: '1px solid #b45309',
                  color: '#ffffff',
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  fontSize: '12px',
                  fontWeight: 'bold',
                }}
              >
                ⏳
              </span>
              <span style={{ fontSize: '14px', color: 'var(--text-h, #f1f5f9)', fontWeight: '500' }}>
                Đang có người giữ ({layout.counts.held})
              </span>
            </div>

            {/* Đã bán */}
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span
                style={{
                  width: '24px',
                  height: '24px',
                  borderRadius: '4px',
                  backgroundColor: '#4b5563',
                  border: '1px solid #374151',
                  color: '#ffffff',
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  fontSize: '12px',
                  fontWeight: 'bold',
                }}
              >
                ✕
              </span>
              <span style={{ fontSize: '14px', color: 'var(--text-h, #f1f5f9)', fontWeight: '500' }}>
                Đã bán ({layout.counts.sold})
              </span>
            </div>
          </div>

          <div style={{ fontSize: '14px', color: 'var(--text, #9ca3af)', fontWeight: '500' }}>
            Tổng số ghế: <strong style={{ color: 'var(--text-h, #f1f5f9)' }}>{layout.counts.total}</strong>
          </div>
        </div>
      )}

      {/* Gợi ý phóng to nếu kích thước ghế nhỏ hơn 24px */}
      {layout && isSeatSmall && (
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            padding: '10px 16px',
            backgroundColor: '#422006',
            border: '1px solid #a16207',
            borderRadius: '6px',
            color: '#fef08a',
            fontSize: '13px',
            fontWeight: '500',
            marginBottom: '14px',
          }}
        >
          <span style={{ fontSize: '16px' }}>🔍</span>
          <span>Phóng to để chọn ghế chính xác hơn (kích thước hiện tại: ~{Math.round(currentSeatPixelSize)}px)</span>
        </div>
      )}

      {/* Khu vực vẽ toàn bộ ghế bằng MỘT thẻ <svg> duy nhất với Zoom & Pan */}
      {layout && (
        <div
          ref={containerRef}
          className="seat-map-viewport"
          style={{
            position: 'relative',
            width: '100%',
            height: '520px',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px solid var(--border, #2e303a)',
            borderRadius: '8px',
            overflow: 'hidden',
            touchAction: 'none',
            userSelect: 'none',
            cursor: 'grab',
            boxShadow: 'inset 0 2px 4px rgba(0,0,0,0.1)',
          }}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
          onPointerCancel={handlePointerCancel}
        >
          {/* Thanh công cụ 3 nút zoom: +, −, Đặt lại */}
          <div
            onPointerDown={(e) => e.stopPropagation()}
            onClick={(e) => e.stopPropagation()}
            style={{
              position: 'absolute',
              right: '14px',
              bottom: '14px',
              display: 'flex',
              flexDirection: 'column',
              gap: '8px',
              zIndex: 10,
              backgroundColor: 'rgba(22, 23, 29, 0.88)',
              backdropFilter: 'blur(4px)',
              padding: '6px',
              borderRadius: '10px',
              border: '1px solid var(--border, #2e303a)',
              boxShadow: '0 4px 12px rgba(0, 0, 0, 0.35)',
            }}
          >
            <button
              type="button"
              onClick={handleZoomIn}
              aria-label="Phóng to"
              title="Phóng to (+)"
              style={{
                width: '44px',
                height: '44px',
                minWidth: '44px',
                minHeight: '44px',
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: 'var(--code-bg, #1f2028)',
                color: 'var(--text-h, #f1f5f9)',
                border: '1px solid var(--border, #2e303a)',
                borderRadius: '8px',
                cursor: 'pointer',
                fontSize: '22px',
                fontWeight: 'bold',
                lineHeight: 1,
              }}
            >
              +
            </button>
            <button
              type="button"
              onClick={handleZoomOut}
              aria-label="Thu nhỏ"
              title="Thu nhỏ (−)"
              style={{
                width: '44px',
                height: '44px',
                minWidth: '44px',
                minHeight: '44px',
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: 'var(--code-bg, #1f2028)',
                color: 'var(--text-h, #f1f5f9)',
                border: '1px solid var(--border, #2e303a)',
                borderRadius: '8px',
                cursor: 'pointer',
                fontSize: '22px',
                fontWeight: 'bold',
                lineHeight: 1,
              }}
            >
              −
            </button>
            <button
              type="button"
              onClick={handleResetZoom}
              aria-label="Đặt lại về vừa khít khung"
              title="Đặt lại về vừa khít khung"
              style={{
                minWidth: '44px',
                height: '44px',
                minHeight: '44px',
                padding: '0 8px',
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: 'var(--code-bg, #1f2028)',
                color: 'var(--text-h, #f1f5f9)',
                border: '1px solid var(--border, #2e303a)',
                borderRadius: '8px',
                cursor: 'pointer',
                fontSize: '11px',
                fontWeight: '600',
                whiteSpace: 'nowrap',
              }}
            >
              Đặt lại
            </button>
          </div>

          {/* SVG chính chứa toàn bộ sơ đồ ghế */}
          <svg
            width="100%"
            height="100%"
            style={{
              display: 'block',
              touchAction: 'none',
              overflow: 'visible',
            }}
          >
            {/* Thẻ <g> bọc toàn bộ nội dung nhận biến đổi transform khi zoom/pan */}
            <g ref={contentGroupRef}>
              {/* Thanh hiển thị Sân khấu */}
              <g>
                <rect
                  x={layout.width * 0.15}
                  y={15}
                  width={layout.width * 0.7}
                  height={32}
                  rx={6}
                  fill="var(--bg, #16171d)"
                  stroke="var(--border, #374151)"
                  strokeWidth="1.5"
                />
                <text
                  x={layout.width / 2}
                  y={36}
                  textAnchor="middle"
                  fill="var(--text-h, #f1f5f9)"
                  fontSize="13"
                  fontWeight="bold"
                  letterSpacing="3px"
                >
                  SÂN KHẤU / MÀN HÌNH
                </text>
              </g>

              {/* Render danh sách ghế được memoize */}
              {layout.mappedSeats.map((seat) => (
                <SeatItem
                  key={seat.id}
                  seat={seat}
                  pixelX={seat.pixelX}
                  pixelY={seat.pixelY}
                  size={seat.size}
                  isSelected={selectedSeat?.id === seat.id}
                />
              ))}
            </g>
          </svg>
        </div>
      )}

      {/* Khung thông tin chi tiết ghế đã chọn (CHỈ XEM, không có nút chọn) */}
      {selectedSeat && (
        <div
          style={{
            marginTop: '16px',
            padding: '18px 20px',
            backgroundColor: 'var(--code-bg, #1f2028)',
            border: '1px solid #38bdf8',
            borderRadius: '8px',
            boxShadow: '0 4px 16px rgba(0, 0, 0, 0.25)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              marginBottom: '14px',
              borderBottom: '1px solid var(--border, #2e303a)',
              paddingBottom: '10px',
              flexWrap: 'wrap',
              gap: '10px',
            }}
          >
            <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
              <span
                style={{
                  width: '10px',
                  height: '10px',
                  borderRadius: '50%',
                  backgroundColor: '#38bdf8',
                  display: 'inline-block',
                  boxShadow: '0 0 8px #38bdf8',
                }}
              />
              <h3 style={{ margin: 0, fontSize: '16px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                Chi tiết ghế: Khu {selectedSeat.section} - Hàng {selectedSeat.row} - Ghế {selectedSeat.number}
              </h3>
            </div>
            <button
              type="button"
              onClick={() => setSelectedSeat(null)}
              aria-label="Đóng thông tin ghế"
              title="Đóng (hoặc bấm chỗ trống trên sơ đồ)"
              style={{
                width: '36px',
                height: '36px',
                minWidth: '36px',
                minHeight: '36px',
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: 'transparent',
                border: '1px solid var(--border, #2e303a)',
                borderRadius: '6px',
                color: 'var(--text, #9ca3af)',
                fontSize: '16px',
                cursor: 'pointer',
              }}
            >
              ✕
            </button>
          </div>

          {/* 6 trường thông tin: Khu, hàng, ghế, hạng, giá, trạng thái */}
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))',
              gap: '14px',
            }}
          >
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Khu vực</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                {selectedSeat.section}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Hàng</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                Hàng {selectedSeat.row}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Số ghế</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                Số {selectedSeat.number}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Hạng ghế</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: 'var(--text-h, #f1f5f9)' }}>
                {selectedSeat.category}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Giá vé</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: '#10b981' }}>
                {selectedSeat.price ? `${selectedSeat.price.toLocaleString('vi-VN')} đ` : 'N/A'}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Trạng thái</div>
              <div>
                {selectedSeat.status === 'Available' && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#14532d', color: '#86efac', fontSize: '13px', fontWeight: '600' }}>
                    ✓ Trống
                  </span>
                )}
                {selectedSeat.status === 'Held' && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#78350f', color: '#fde047', fontSize: '13px', fontWeight: '600' }}>
                    ⏳ Đang có người giữ
                  </span>
                )}
                {selectedSeat.status === 'Sold' && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#374151', color: '#d1d5db', fontSize: '13px', fontWeight: '600' }}>
                    ✕ Đã bán
                  </span>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
