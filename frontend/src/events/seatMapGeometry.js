// Imported coordinates are geometry, not integer row/column indexes.
// One uniform affine transform preserves angles, aisles and relative distances.
export function seatMapGeometry(points) {
  if (!points.length || points.some(p => !Number.isFinite(p.x) || !Number.isFinite(p.y))) return null;
  let spacing = Infinity;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let i = 0; i < points.length; i++) {
    const p = points[i];
    minX = Math.min(minX, p.x); maxX = Math.max(maxX, p.x);
    minY = Math.min(minY, p.y); maxY = Math.max(maxY, p.y);
    for (let j = 0; j < i; j++) {
      // Chebyshev separation keeps square glyphs apart, including diagonal layouts.
      const distance = Math.max(Math.abs(p.x - points[j].x), Math.abs(p.y - points[j].y));
      if (distance > 0) spacing = Math.min(spacing, distance);
    }
  }
  const coordinateScale = Number.isFinite(spacing) ? 40 / spacing : 1;
  const baseSeatSize = 32, paddingX = 40, stageHeight = 60;
  return {
    width: Math.max(160, (maxX - minX) * coordinateScale + baseSeatSize + 80),
    height: (maxY - minY) * coordinateScale + baseSeatSize + 140,
    baseSeatSize, paddingX, stageHeight, coordinateScale,
    positions: new Map(points.map(p => [p.id, {
      pixelX: paddingX + (p.x - minX) * coordinateScale,
      pixelY: 100 + (p.y - minY) * coordinateScale,
      size: baseSeatSize,
    }])),
  };
}

export function computeFitScale(cw, ch, width, height) {
  return Math.min(Math.max(1, cw - 48) / width, Math.max(1, ch - 48) / height);
}

export function initialSeatScale(fit, count) {
  // Small maps open ready to select; on narrow screens pan instead of tiny targets.
  return count <= 200 ? Math.max(fit, 0.875) : fit;
}

export function maximumSeatScale(fit) {
  // A long/large layout must still permit 64px targets, not just 8x a tiny fit.
  return Math.max(fit * 8, 2);
}

export function zoomAtPoint(current, scale, x, y) {
  const ratio = scale / current.scale;
  return { scale, x: x - (x - current.x) * ratio, y: y - (y - current.y) * ratio };
}
