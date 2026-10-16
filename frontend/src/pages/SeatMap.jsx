import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useHoldRecovery } from '../events/useHoldRecovery';
import { toggleBatch } from '../events/batchSelection';
import { seatClick } from '../events/seatClick';
import { seatMapGeometry, computeFitScale, initialSeatScale, maximumSeatScale, zoomAtPoint } from '../events/seatMapGeometry';
import { categoryPalette } from '../events/seatImport';
import { seatPresentation, rowLabels, existingPrice, blockedSeatMessage } from '../events/seatPresentation';
import { createOrder } from '../api/ordersApi.js';

/**
 * Tối ưu hóa từng ghế bằng React.memo để tránh render lại toàn bộ hàng nghìn ghế.
 * Không gắn onClick riêng cho từng ghế để đạt hiệu năng cao nhất (dùng event delegation).
 */
const SeatItem = React.memo(/** @param {{seat: {id:number, row:string, number:string, category?:string, section?:string, displayStatus:string, canSelect?:boolean}, pixelX:number, pixelY:number, size:number, categoryColor?:string, isSelected?:boolean, isPending?:boolean, showLabel?:boolean, onClick?:(seat:object)=>void}} props */
function SeatItem({ seat, pixelX, pixelY, size, categoryColor, isSelected, isPending, showLabel, onClick }) {
  const visual = seatPresentation(seat, categoryColor, isPending, isSelected);
  const isAvailable = seat.displayStatus === 'Available';
  const number = String(seat.number);
  const fontSize = Math.min(16, 38 / Math.max(1, number.length));
  const select = () => {
    if (visual.selectable) onClick?.(seat);
  };

  return (
    <g
      data-seat-id={seat.id}
      data-seat-status={seat.displayStatus}
      data-category={seat.category}
      data-pending-selection={visual.queued ? 'true' : 'false'}
      tabIndex={visual.selectable ? 0 : -1}
      aria-disabled={!visual.selectable}
      aria-label={visual.label}
      role="button"
      style={{ cursor: visual.selectable ? 'pointer' : 'not-allowed' }}
      // Pointer-up hit testing is authoritative. A touch-generated click can target a
      // different seat after the detail panel changes scroll anchoring. Do not select twice.
      onClick={event => { if (event.detail === 0) select(); }}
      onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(); } }}
    >
      <title>{`${visual.label} — ${seat.category || 'Chưa có hạng ghế'} — ${visual.priceDisplay !== null ? visual.priceDisplay : 'Chưa có giá'}`}</title>
      <rect
        data-seat-id={seat.id}
        x={pixelX}
        y={pixelY}
        width={size}
        height={size}
        rx={4}
        fill={visual.fill}
        stroke={visual.stroke}
        strokeWidth={visual.queued || isSelected ? '2.5' : '1.2'}
        strokeDasharray={visual.queued ? '3 2' : undefined}
      />
      <text
        x={pixelX + (showLabel && isAvailable ? size * 0.82 : size / 2)}
        y={pixelY + (showLabel ? size * 0.32 : size * 0.66)}
        textAnchor="middle"
        fill={visual.ink}
        fontSize={showLabel && isAvailable ? 7 : showLabel ? 11 : 16}
        fontWeight="bold"
        pointerEvents="none"
      >
        {visual.icon}
      </text>
      {showLabel && <text data-seat-row="true" x={pixelX + 3} y={pixelY + 8}
        fill={visual.ink} fontSize="6.5" fontWeight="600" pointerEvents="none">
        {String(seat.row).length > 4 ? `${String(seat.row).slice(0, 3)}…` : seat.row}
      </text>}
      {showLabel && <text data-seat-number="true" x={pixelX + size / 2}
        y={pixelY + (isAvailable ? size * 0.70 : size * 0.82)} textAnchor="middle"
        fill={visual.ink} fontSize={isAvailable ? fontSize : Math.min(12, fontSize)} fontWeight="bold"
        pointerEvents="none">{number}</text>}
      {/* Viền nổi bật cho ghế đang được xem */}
      {isSelected && visual.selectable && (
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
  return <SeatMapScreen key={id} id={id} />;
}

function SeatMapScreen({ id }) {
  const navigate = useNavigate();
  const recovery = useHoldRecovery(id);
  const { data: seatData, loading, error, session, remainingSeconds, pending: isHolding,
    notice: holdError, dismissNotice, refresh, confirmHold, confirmBatch, confirmRelease } = recovery;
  const [batchIds, setBatchIds] = useState([]);
  const [batchMode, setBatchMode] = useState(false);
  const pendingIds = useMemo(() => new Set(batchIds), [batchIds]);
  const [interactionNotice, setInteractionNotice] = useState(null);
  useEffect(() => {
    const clear = () => { setBatchIds([]); setInteractionNotice(null); };
    window.addEventListener('sessionchange', clear); window.addEventListener('storage', clear);
    return () => { window.removeEventListener('sessionchange', clear); window.removeEventListener('storage', clear); };
  }, []);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [selectedSeatId, setSelectedSeatId] = useState(null);
  const selectedSeat = useMemo(() => seatData?.seats?.find(s => s.id === selectedSeatId) ?? null,
    [seatData, selectedSeatId]);
  const setSelectedSeat = useCallback(seat => setSelectedSeatId(seat?.id ?? null), []);
  const handleSeatClick = useCallback(seat => {
    setInteractionNotice(null); dismissNotice();
    void seatClick({ seat, batchMode, pending: isHolding, focus: setSelectedSeat,
      hold: confirmHold, release: confirmRelease,
      toggle: picked => setBatchIds(ids => toggleBatch(ids, picked)) });
  }, [batchMode, isHolding, dismissNotice, setSelectedSeat, confirmHold, confirmRelease]);
  const [isPaymentModalOpen, setIsPaymentModalOpen] = useState(false);
  const countdown = `${Math.floor(remainingSeconds / 60).toString().padStart(2, '0')}:${(remainingSeconds % 60).toString().padStart(2, '0')}`;
  const handleConfirmHold = async () => {
    if (selectedSeat?.displayStatus === 'Available') await confirmHold(selectedSeat.id);
  };
  const handleConfirmBatch = async () => {
    if (batchIds.length && await confirmBatch(batchIds)) setBatchIds([]);
  };

  const [isCreatingOrder, setIsCreatingOrder] = useState(false);
  const [orderError, setOrderError] = useState(null);

  const handleCreateOrder = async () => {
    try {
      setIsCreatingOrder(true);
      setOrderError(null);
      const res = await createOrder(id);
      if (res?.success && res.order) {
        navigate(`/orders/${res.order.id}`);
      }
    } catch (err) {
      if (err.response?.status === 409 && err.response?.data?.errorCode === 'SEAT_HOLD_EXPIRED') {
        setInteractionNotice('Ghế giữ đã hết hạn. Vui lòng chọn lại.');
        await refresh();
      } else {
        setOrderError(err.response?.data?.message || 'Không thể tạo đơn hàng. Vui lòng thử lại.');
      }
    } finally {
      setIsCreatingOrder(false);
    }
  };

  const [zoomLevel, setZoomLevel] = useState(1);

  // Refs quản lý DOM và hiệu năng zoom/pan không render lại React
  const containerRef = useRef(null);
  const contentGroupRef = useRef(null);
  const transformRef = useRef({ x: 0, y: 0, scale: 1 });
  const fitScaleRef = useRef(1);
  const fitModeRef = useRef(true);
  const rafIdRef = useRef(null);
  const animRafRef = useRef(null);
  const wheelDebounceRef = useRef(null);
  const geometryKey = JSON.stringify(seatData?.seats?.map(({ id, x, y }) => ({ id, x, y })) ?? []);
  const geometry = useMemo(() => seatMapGeometry(JSON.parse(geometryKey)), [geometryKey]);

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
    setIsRefreshing(isRefresh);
    try { await refresh(); }
    finally { setIsRefreshing(false); }
  }, [refresh]);

  // Tối ưu hóa tính toán layout và thống kê số lượng ghế bằng useMemo
  const layout = useMemo(() => {
    const seats = seatData?.seats;
    if (!seats || seats.length === 0 || seats.some(s => !Number.isFinite(s.x) || !Number.isFinite(s.y))) {
      return null;
    }

    let availableCount = 0;
    let heldCount = 0;
    let mineCount = 0;
    let soldCount = 0;
    const seatMapById = new Map();

    for (let i = 0; i < seats.length; i++) {
      const s = seats[i];
      seatMapById.set(s.id, s);

      if (s.displayStatus === 'Available') availableCount++;
      else if (s.displayStatus === 'HeldByMe' || s.displayStatus === 'HeldByOther') {
        heldCount++;
        if (s.displayStatus === 'HeldByMe') mineCount++;
      }
      else if (s.displayStatus === 'Sold') soldCount++;
    }

    if (!geometry) return null;
    const mappedSeats = seats.map(s => ({ ...s, ...geometry.positions.get(s.id) }));
    const categories = categoryPalette(seats.map(s => s.category || 'Chưa có hạng ghế')).map(c => {
      const match = seats.find(s => (s.category || 'Chưa có hạng ghế') === c.name && s.price !== undefined && s.price !== null);
      const anyMatch = seats.find(s => (s.category || 'Chưa có hạng ghế') === c.name);
      return {
        ...c,
        price: match ? match.price : (anyMatch?.price ?? null)
      };
    });

    return {
      ...geometry,
      counts: {
        total: seats.length,
        available: availableCount,
        held: heldCount,
        mine: mineCount,
        other: heldCount - mineCount,
        sold: soldCount,
      },
      mappedSeats,
      seatMapById,
      categories,
      categoryColors: new Map(categories.map(c => [c.name, c.color])),
      rowLabels: rowLabels(mappedSeats, geometry.paddingX),
    };
  }, [seatData, geometry]);

  // Hoạt ảnh chuyển đổi mượt khoảng 150ms cho các thao tác bấm nút (+, −, Đặt lại)
  const animateTransformTo = useCallback((targetTransform, duration = 150) => {
    if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
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
    if (!geometry || !containerRef.current) return;
    const container = containerRef.current;
    let initialized = false;
    fitModeRef.current = geometry.positions.size > 200;
    const resize = () => {
      if (!containerRef.current) return;
      const cw = containerRef.current.clientWidth || 800;
      const ch = containerRef.current.clientHeight || 500;
      const fitScale = computeFitScale(cw, ch, geometry.width, geometry.height);

      fitScaleRef.current = fitScale;

      const scale = initialized ? (fitModeRef.current ? fitScale : Math.max(fitScale, transformRef.current.scale)) : initialSeatScale(fitScale, geometry.positions.size);
      const fit = initialized
        ? clampTransform(transformRef.current.x, transformRef.current.y, scale, cw, ch, geometry.width, geometry.height)
        : { scale, x: (cw - geometry.width * scale) / 2, y: (ch - geometry.height * scale) / 2 };
      initialized = true;
      if (animRafRef.current) cancelAnimationFrame(animRafRef.current);
      if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);

      transformRef.current = fit;

      if (contentGroupRef.current) {
        contentGroupRef.current.setAttribute(
          'transform',
          `translate(${fit.x}, ${fit.y}) scale(${fit.scale})`
        );
      }
      setZoomLevel(fit.scale);
    };
    const raf = requestAnimationFrame(resize);
    const observer = new ResizeObserver(resize);
    observer.observe(container);

    return () => {
      cancelAnimationFrame(raf);
      observer.disconnect();
      if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
      if (animRafRef.current) {
        cancelAnimationFrame(animRafRef.current);
        animRafRef.current = null;
      }
    };
  }, [geometry]);

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
      const pivotX = e.clientX - rect.left - container.clientLeft;
      const pivotY = e.clientY - rect.top - container.clientTop;

      // Hệ số zoom mượt liên tục theo deltaY
      const factor = Math.exp(-e.deltaY * 0.0015);

      const current = transformRef.current;
      const minScale = fitScaleRef.current;
      const maxScale = maximumSeatScale(fitScaleRef.current);

      const targetScale = Math.max(minScale, Math.min(maxScale, current.scale * factor));
      if (targetScale === current.scale) return;
      fitModeRef.current = targetScale === minScale;

      const anchored = zoomAtPoint(current, targetScale, pivotX, pivotY);

      const clamped = clampTransform(
        anchored.x,
        anchored.y,
        targetScale,
        container.clientWidth,
        container.clientHeight,
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
    // Vì đang dùng setPointerCapture, e.target sẽ bị trỏ về container chứ không phải thẻ <g> của ghế.
    // Nên ta dùng elementFromPoint để tìm chính xác phần tử tại vị trí chuột.
    const elemUnderPointer = document.elementFromPoint(e.clientX, e.clientY);
    const seatElem = elemUnderPointer ? elemUnderPointer.closest('[data-seat-id]') : null;
    if (seatElem && layout) {
      const seatId = parseInt(seatElem.getAttribute('data-seat-id'), 10);
      const seat = layout.seatMapById.get(seatId);
      if (seat) {
        if (seatPresentation(seat).selectable) {
          handleSeatClick(seat);
        } else {
          setInteractionNotice(blockedSeatMessage(seat.displayStatus));
          void refresh(); // Read authoritative availability; never POST for a disabled seat.
        }
        return;
      }
    }
    // Bấm vào chỗ trống (nền SVG) để bỏ chọn/ẩn khung
    setSelectedSeat(null);
  }, [layout, setSelectedSeat, handleSeatClick, refresh]);

  // Xử lý Pointer Down (bắt đầu kéo hoặc pinch)
  const handlePointerDown = (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    if (e.target.closest && e.target.closest('button')) return;

    if (animRafRef.current) {
      cancelAnimationFrame(animRafRef.current);
      animRafRef.current = null;
    }

    if (rafIdRef.current) cancelAnimationFrame(rafIdRef.current);
    const { x, y, scale } = transformRef.current;
    contentGroupRef.current?.setAttribute('transform', `translate(${x}, ${y}) scale(${scale})`);

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
      const screenMidX = (pts[0].x + pts[1].x) / 2 - rect.left - containerRef.current.clientLeft;
      const screenMidY = (pts[0].y + pts[1].y) / 2 - rect.top - containerRef.current.clientTop;

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
    const cw = containerRef.current.clientWidth;
    const ch = containerRef.current.clientHeight;
    const minScale = fitScaleRef.current;
    const maxScale = maximumSeatScale(fitScaleRef.current);

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
        fitModeRef.current = newScale === minScale;

        const currentScreenMidX = (pts[0].x + pts[1].x) / 2 - rect.left - containerRef.current.clientLeft;
        const currentScreenMidY = (pts[0].y + pts[1].y) / 2 - rect.top - containerRef.current.clientTop;

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
    // Toolbar gestures were never registered as map pointers. Their bubbling up must
    // not clear the focused seat/reflow the page before the button's synthesized click.
    if (!activePointers.current.has(e.pointerId)) return;
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
    if (!activePointers.current.has(e.pointerId)) return;
    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      // Bỏ qua lỗi nếu con trỏ đã được giải phóng
    }
    activePointers.current.delete(e.pointerId);
    if (activePointers.current.size === 1) {
      const remaining = Array.from(activePointers.current.values())[0];
      pointerStartPos.current = { x: remaining.x, y: remaining.y };
      dragStartTransform.current = { ...transformRef.current };
      isDraggingRef.current = true;
    }
    if (activePointers.current.size === 0) {
      isDraggingRef.current = false;
      setZoomLevel(transformRef.current.scale);
    }
  };

  // Nút "+": zoom quanh TÂM của khung nhìn, nhân 1.25, hoạt ảnh 150ms
  const handleZoomIn = () => {
    if (!containerRef.current || !layout) return;
    const cw = containerRef.current.clientWidth;
    const ch = containerRef.current.clientHeight;
    const pivotX = cw / 2;
    const pivotY = ch / 2;

    const current = transformRef.current;
    const maxScale = maximumSeatScale(fitScaleRef.current);

    const targetScale = Math.min(maxScale, current.scale * 1.25);
    if (targetScale === current.scale) return;
    fitModeRef.current = false;

    const anchored = zoomAtPoint(current, targetScale, pivotX, pivotY);
    const clamped = clampTransform(anchored.x, anchored.y, targetScale, cw, ch, layout.width, layout.height);
    animateTransformTo(clamped, 150);
  };

  // Nút "−": zoom quanh TÂM của khung nhìn, chia 1.25, hoạt ảnh 150ms
  const handleZoomOut = () => {
    if (!containerRef.current || !layout) return;
    const cw = containerRef.current.clientWidth;
    const ch = containerRef.current.clientHeight;
    const pivotX = cw / 2;
    const pivotY = ch / 2;

    const current = transformRef.current;
    const minScale = fitScaleRef.current;

    const targetScale = Math.max(minScale, current.scale / 1.25);
    if (targetScale === current.scale) return;
    fitModeRef.current = targetScale === minScale;

    const anchored = zoomAtPoint(current, targetScale, pivotX, pivotY);
    const clamped = clampTransform(anchored.x, anchored.y, targetScale, cw, ch, layout.width, layout.height);
    animateTransformTo(clamped, 150);
  };

  // Nút "Đặt lại": về fitScale và căn giữa, hoạt ảnh 150ms
  const handleResetZoom = () => {
    if (!containerRef.current || !layout) return;
    fitModeRef.current = true;
    const cw = containerRef.current.clientWidth;
    const ch = containerRef.current.clientHeight;
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

  const handleSelectionZoom = () => {
    if (!containerRef.current || !layout) return;
    fitModeRef.current = false;
    const cw = containerRef.current.clientWidth, ch = containerRef.current.clientHeight;
    const current = transformRef.current;
    const seat = selectedSeatId ? layout.mappedSeats.find(s => s.id === selectedSeatId) : null;
    const x = seat ? seat.pixelX + seat.size / 2 : (cw / 2 - current.x) / current.scale;
    const y = seat ? seat.pixelY + seat.size / 2 : (ch / 2 - current.y) / current.scale;
    const scale = Math.max(fitScaleRef.current, 1.375); // 44px targets, one click, also on mobile.
    animateTransformTo(clampTransform(cw / 2 - x * scale, ch / 2 - y * scale,
      scale, cw, ch, layout.width, layout.height));
  };

  const handleMapKeyDown = e => {
    if (e.target !== e.currentTarget || !layout || !containerRef.current) return;
    if (e.key === '+' || e.key === '=') { e.preventDefault(); handleZoomIn(); }
    else if (e.key === '-') { e.preventDefault(); handleZoomOut(); }
    else if (e.key === '0') { e.preventDefault(); handleResetZoom(); }
    else if (e.key.startsWith('Arrow')) {
      e.preventDefault();
      const t = transformRef.current;
      animateTransformTo(clampTransform(t.x + (e.key === 'ArrowLeft' ? 60 : e.key === 'ArrowRight' ? -60 : 0),
        t.y + (e.key === 'ArrowUp' ? 60 : e.key === 'ArrowDown' ? -60 : 0), t.scale,
        containerRef.current.clientWidth, containerRef.current.clientHeight, layout.width, layout.height));
    }
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
  if (seatData && (seatData.hasSeatMap === false || seatData.seats.length === 0)) {
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

  if (seatData && !layout) {
    return <div role="alert" style={{ padding: '40px', textAlign: 'center' }}>
      <h2>Sơ đồ ghế chưa đủ thông tin vị trí</h2>
      <p>Không thể vẽ ghế thiếu tọa độ. Vui lòng liên hệ ban tổ chức.</p>
      <button onClick={() => navigate(`/shows/${id}`)}>Quay lại chi tiết suất diễn</button>
    </div>;
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

      <div aria-label="Chọn nhiều ghế" style={{ marginBottom: '16px' }}>
        <label><input type="checkbox" checked={batchMode} disabled={isHolding}
          onChange={event => { setBatchMode(event.target.checked); setBatchIds([]); }} />
          Chọn nhiều ghế — chỉ giữ khi xác nhận cả nhóm
        </label>
        {!batchMode && <p>Bấm ghế trống để giữ ngay. Bấm lại ghế bạn đang giữ để nhả.</p>}
        <p>Danh sách ghế chọn chờ giữ: {batchIds.length ? batchIds.map(seatId => {
          const seat = seatData?.seats?.find(s => s.id === seatId);
          return seat ? `${seat.row}·${seat.number}` : `#${seatId}`;
        }).join(', ') : 'Chưa chọn ghế'}</p>
        {batchIds.map(seatId => <button key={seatId} aria-label={`Bỏ ghế ${seatId}`} disabled={isHolding}
          onClick={() => setBatchIds(ids => ids.filter(id => id !== seatId))}>Bỏ {seatData?.seats?.find(s => s.id === seatId)?.row}·{seatData?.seats?.find(s => s.id === seatId)?.number ?? seatId}</button>)}
        <button onClick={handleConfirmBatch} disabled={!batchMode || !batchIds.length || isHolding || isRefreshing}>
          {isHolding ? 'Đang giữ...' : `Giữ tất cả ${batchIds.length} ghế cùng lúc`}
        </button>
        <button onClick={() => setBatchIds([])} disabled={isHolding}>Bỏ danh sách chọn</button>
        {holdError && <p role="alert">{holdError}</p>}
        {interactionNotice && <p role="alert">{interactionNotice}</p>}
      </div>
      {session && remainingSeconds > 0 && (
        <div role="status" aria-live="polite" data-testid="hold-session" style={{ marginBottom: '16px', color: '#fbbf24' }}>
          Bạn đang giữ {session.heldSeats.length} ghế. Thời gian còn lại: {countdown}
          <span> — {session.heldSeats.map(s => `Hàng ${s.row}, ghế ${s.number}`).join('; ')}</span>
          <div aria-label="Nhả ghế đang giữ">
            {session.heldSeats.map(seat => <button key={seat.id} type="button" disabled={isHolding}
              aria-label={`Nhả ghế ${seat.id}`} onClick={() => confirmRelease(seat.id)}>
              Nhả hàng {seat.row}, ghế {seat.number}
            </button>)}
          </div>
        </div>
      )}
      {/* Tiêu đề trang & Chế độ xem */}
      <div style={{ marginBottom: '16px', textAlign: 'left' }}>
        <h2 style={{ margin: '0 0 6px 0', fontSize: '24px', color: 'var(--text-h, #f1f5f9)', fontWeight: '700' }}>
          Sơ Đồ Ghế Suất Diễn
        </h2>
        <p style={{ margin: 0, fontSize: '14px', color: 'var(--text, #9ca3af)' }}>
          Chế độ xem sơ đồ ghế thời gian thực (Kéo để di chuyển • Cuộn hoặc 2 ngón để phóng to thu nhỏ)
        </p>
      </div>

      {/* Cùng bảng màu với preview Organizer; màu hạng không phải giá vé. */}
      {layout && (
        <section className="seat-map-legend-container" aria-label="Chú giải sơ đồ ghế"
          style={{ padding: '14px 16px', border: '1px solid var(--border, #94a3b8)', borderRadius: 8, marginBottom: 16 }}>
          <div aria-label="Chú giải hạng ghế" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 10, marginBottom: 12 }}>
            <strong>Hạng ghế:</strong>
            {layout.categories.map(c => {
              const priceText = existingPrice(c.price);
              return (
                <span key={c.name} data-category-color={c.name}
                  style={{ background: c.color, color: '#111827', padding: '5px 10px', borderRadius: 4, border: '1px solid #64748b' }}>
                  {c.name}: {priceText !== null ? priceText : 'Chưa có giá'}
                </span>
              );
            })}
            <span style={{ marginLeft: 'auto' }}>Tổng số ghế: <strong>{layout.counts.total}</strong></span>
          </div>
          <div className="seat-map-legend-items" aria-label="Chú giải trạng thái"
            style={{ display: 'flex', flexWrap: 'wrap', gap: 10, fontSize: 13 }}>
            <span style={{ padding: '5px 8px', border: '1px solid #64748b', borderRadius: 4 }}>✓ Trống — theo màu hạng ({layout.counts.available})</span>
            <span style={{ padding: '5px 8px', background: '#dbeafe', color: '#1e40af', border: '1px solid #2563eb', borderRadius: 4 }}>⏳ Bạn đang giữ ({layout.counts.mine})</span>
            <span style={{ padding: '5px 8px', background: '#fef3c7', color: '#92400e', border: '1px solid #b45309', borderRadius: 4 }}>⏳ Người khác đang giữ ({layout.counts.other})</span>
            <span style={{ padding: '5px 8px', background: '#e5e7eb', color: '#4b5563', border: '1px solid #9ca3af', borderRadius: 4 }}>✕ Đã bán ({layout.counts.sold})</span>
            <span style={{ padding: '5px 8px', border: '2px dashed #7c3aed', borderRadius: 4 }}>✓ Chọn chờ giữ — chưa xác nhận</span>
          </div>
          <p style={{ margin: '10px 0 0', fontSize: 12 }}>Viền xanh: đang xem chi tiết. Viền tím nét đứt: danh sách chọn tạm, chưa phải phiên giữ ghế.</p>
        </section>
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
          <span>Toàn cảnh — bấm “Chọn ghế” để có ô ghế dễ chạm (hiện tại: ~{Math.round(currentSeatPixelSize)}px)</span>
        </div>
      )}

      {/* Khu vực vẽ toàn bộ ghế bằng MỘT thẻ <svg> duy nhất với Zoom & Pan */}
      {layout && (
        <div
          ref={containerRef}
          className="seat-map-viewport"
          role="region"
          aria-label="Sơ đồ ghế: phím mũi tên để di chuyển, cộng trừ để zoom, 0 để xem toàn cảnh"
          tabIndex={0}
          onKeyDown={handleMapKeyDown}
          style={{
            position: 'relative',
            width: '100%',
            height: 'clamp(360px, 65vh, 620px)',
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
          onLostPointerCapture={handlePointerCancel}
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
            <button type="button" onClick={handleSelectionZoom} aria-label="Phóng tới kích thước dễ chọn ghế"
              style={{ minWidth: '44px', minHeight: '44px', padding: '4px', borderRadius: '8px', cursor: 'pointer' }}>
              Chọn ghế
            </button>
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
              Toàn cảnh
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
                  categoryColor={layout.categoryColors.get(seat.category || 'Chưa có hạng ghế')}
                  isSelected={selectedSeat?.id === seat.id}
                  isPending={pendingIds.has(seat.id)}
                  showLabel={currentSeatPixelSize >= 24}
                  onClick={handleSeatClick}
                  />
              ))}
              {currentSeatPixelSize >= 24 && layout.rowLabels.map(row => (
                <text key={row.key} data-row-label={row.row} x={row.x} y={row.y + 4} textAnchor="end"
                  fontSize={Math.min(12, 40 / Math.max(1, String(row.row).length))} fontWeight="bold"
                  fill="var(--text-h, #f1f5f9)" pointerEvents="none">
                  <title>{`Hàng ${row.row}`}</title>{row.row}
                </text>
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
                Chi tiết ghế: {selectedSeat.section ? `Khu ${selectedSeat.section} - ` : ''}Hàng {selectedSeat.row} - Ghế {selectedSeat.number}
              </h3>
            </div>
            <button
              type="button"
onClick={() => { dismissNotice(); setSelectedSeat(null); }}
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
                {selectedSeat.section || 'Chưa có thông tin'}
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
                {selectedSeat.category || 'Chưa có thông tin'}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Giá vé</div>
              <div style={{ fontSize: '15px', fontWeight: '600', color: existingPrice(selectedSeat.price) !== null ? '#10b981' : 'var(--text, #9ca3af)' }}>
                {existingPrice(selectedSeat.price) !== null ? existingPrice(selectedSeat.price) : 'Chưa có giá'}
              </div>
            </div>
            <div>
              <div style={{ fontSize: '12px', color: 'var(--text, #9ca3af)', marginBottom: '4px' }}>Trạng thái</div>
              <div>
                {selectedSeat.displayStatus === 'Available' && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#14532d', color: '#86efac', fontSize: '13px', fontWeight: '600' }}>
                    ✓ Trống
                  </span>
                )}
                {(selectedSeat.displayStatus === 'HeldByMe' || selectedSeat.displayStatus === 'HeldByOther') && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#78350f', color: '#fde047', fontSize: '13px', fontWeight: '600' }}>
                    ⏳ {selectedSeat.displayStatus === 'HeldByMe' ? 'Bạn đang giữ' : 'Người khác đang giữ'}
                  </span>
                )}
                {selectedSeat.displayStatus === 'Sold' && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '3px 8px', borderRadius: '4px', backgroundColor: '#374151', color: '#d1d5db', fontSize: '13px', fontWeight: '600' }}>
                    ✕ Đã bán
                  </span>
                )}
              </div>
            </div>
          </div>

          {/* Nút xác nhận đặt vé */}
          <div style={{ marginTop: '20px', display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: '8px' }}>
            {holdError && <div style={{ color: '#ef4444', fontSize: '13px' }}>{holdError}</div>}
            {orderError && <div style={{ color: '#ef4444', fontSize: '13px' }}>{orderError}</div>}

            <div style={{ display: 'flex', gap: '10px' }}>
              {selectedSeat.displayStatus === 'Available' && (
                <button disabled={isHolding} onClick={() => setBatchIds(ids => toggleBatch(ids, selectedSeat))}>
                  {batchIds.includes(selectedSeat.id) ? 'Bỏ ghế khỏi danh sách' : 'Thêm ghế vào danh sách giữ'}
                </button>
              )}
              {selectedSeat.displayStatus === 'Available' && (
                <button
                  disabled={isHolding}
                  className={`font-bold py-2 px-6 rounded shadow-md transition duration-200 ${
                    !isHolding
                      ? 'bg-blue-600 hover:bg-blue-700 text-white cursor-pointer'
                      : 'bg-gray-600 text-gray-400 cursor-not-allowed opacity-50'
                  }`}
                  onClick={handleConfirmHold}
                >
                  {isHolding ? 'Đang xử lý...' : 'Giữ ghế'}
                </button>
              )}

              {selectedSeat.displayStatus === 'HeldByMe' && session && (
                <button
                  disabled={isHolding || isCreatingOrder}
                  className="font-bold py-2 px-6 rounded shadow-md transition duration-200 bg-green-600 hover:bg-green-700 text-white cursor-pointer"
                  onClick={handleCreateOrder}
                >
                  {isCreatingOrder ? 'Đang tạo đơn...' : 'Xác nhận Đặt vé'}
                </button>
              )}
            </div>
          </div>
        </div>
      )}



      {/* Modal thanh toán (Placeholder) */}
{isPaymentModalOpen && selectedSeat?.displayStatus === 'HeldByMe' && session && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-60 backdrop-blur-sm">
          <div className="bg-gray-800 rounded-xl shadow-2xl p-6 w-full max-w-md border border-gray-700">
            <div className="flex justify-between items-center mb-6">
              <h2 className="text-xl font-bold text-white">Giao diện Thanh toán</h2>
              <button
                onClick={() => setIsPaymentModalOpen(false)}
                className="text-gray-400 hover:text-white transition duration-200"
              >
                ✕
              </button>
            </div>
            <div className="text-gray-300 mb-6">
              <p>Ghế đã chọn: Khu {selectedSeat?.section} - Hàng {selectedSeat?.row} - Số {selectedSeat?.number}</p>
              <p>Giá: {existingPrice(selectedSeat?.price) !== null ? existingPrice(selectedSeat.price) : 'Chưa có giá'}</p>
              <p className="mt-4 italic text-sm text-gray-500">Tính năng thanh toán đang được phát triển...</p>
              {selectedSeat?.heldUntilUtc && (() => {
                  const remaining = remainingSeconds;

                  if (remaining > 0) {
                      const m = Math.floor(remaining / 60).toString().padStart(2, '0');
                      const s = (remaining % 60).toString().padStart(2, '0');
                      return (
                        <div className="mt-4 p-3 bg-yellow-900 bg-opacity-30 border border-yellow-700 rounded text-yellow-300 font-bold text-center">
                          Vui lòng thanh toán trong vòng: {m}:{s}
                        </div>
                      );
                  } else {
                      return (
                        <div className="mt-4 p-3 bg-red-900 bg-opacity-30 border border-red-700 rounded text-red-400 font-bold text-center">
                          Hết thời gian giữ ghế. Vui lòng chọn lại!
                        </div>
                      );
                  }
              })()}
            </div>
            <div className="flex justify-end">
              <button
                onClick={() => setIsPaymentModalOpen(false)}
                className="bg-gray-600 hover:bg-gray-700 text-white font-semibold py-2 px-4 rounded transition duration-200"
              >
                Đóng
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
