// Presentation only. Ownership/expiry continue to come from the existing server contract.
export function seatPresentation(seat, categoryColor, pending = false, focused = false) {
  const available = seat.displayStatus === 'Available';
  const mine = seat.displayStatus === 'HeldByMe';
  const other = seat.displayStatus === 'HeldByOther';
  const sold = seat.displayStatus === 'Sold';
  const queued = available && pending;
  const selectable = available && seat.canSelect !== false || mine;
  const status = available ? (queued ? 'Trống, đã chọn chờ giữ' : 'Trống')
    : mine ? 'Bạn đang giữ' : other ? 'Người khác đang giữ' : sold ? 'Đã bán' : 'Không khả dụng';
  const priceDisplay = existingPrice(seat?.price);
  return {
    selectable, queued, status, priceDisplay, priceText: priceDisplay,
    icon: available ? '✓' : mine || other ? '⏳' : sold ? '✕' : '?',
    fill: available ? categoryColor : mine ? '#dbeafe' : other ? '#fef3c7' : '#e5e7eb',
    ink: available ? '#111827' : mine ? '#1e40af' : other ? '#92400e' : '#4b5563',
    stroke: queued ? '#7c3aed' : focused && selectable ? '#38bdf8' : mine ? '#2563eb' : other ? '#b45309' : sold ? '#9ca3af' : '#64748b',
    label: `${seat.section ? `Khu ${seat.section}, ` : ''}hàng ${seat.row}, ghế ${seat.number} - ${status}`,
  };
}

export function rowLabels(seats, paddingX) {
  const rows = new Map();
  for (const seat of seats) {
    const key = `${seat.section ?? ''}\u0000${seat.row}`;
    if (!rows.has(key)) rows.set(key, []);
    rows.get(key).push(seat);
  }
  return [...rows].flatMap(([key, items]) => {
    const left = items.reduce((a, b) => a.pixelX <= b.pixelX ? a : b);
    // Label only horizontal rows at the outer margin; never overlap inner sections/curves.
    if (left.pixelX > paddingX + 0.001 || items.some(s => Math.abs(s.pixelY - left.pixelY) > 0.001)) return [];
    return [{ key, row: left.row, x: left.pixelX - 10, y: left.pixelY + left.size / 2 }];
  });
}

export function existingPrice(price) {
  // No S-15 fallback, no invented value; 0 is also a legitimate explicitly provided price.
  return typeof price === 'number' && Number.isFinite(price) && price >= 0
    ? `${price.toLocaleString('vi-VN')} đ` : null;
}

export function blockedSeatMessage(status) {
  return status === 'HeldByOther' ? 'ghế vừa có người chọn. Vui lòng chọn ghế trống.'
    : status === 'Sold' ? 'Ghế đã bán, không thể chọn.' : 'Ghế hiện không khả dụng.';
}
