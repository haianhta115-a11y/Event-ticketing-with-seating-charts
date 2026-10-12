import { previewGrid } from '../events/seatImport';

const coordinates = seat => seat.x == null || seat.y == null ? 'Chưa có tọa độ' : `(${seat.x}, ${seat.y})`;

export default function SeatingChartPreview({ preview, attempted, busy, onConfirm, onCancel }) {
  if (!preview && !attempted) return null;
  const grid = previewGrid(preview?.seats || []);
  return (
    <section aria-label="Xem trước sơ đồ ghế" style={{ padding: 16, marginBottom: 16, border: '1px solid #94a3b8', borderRadius: 8 }}>
      <h4>Xem trước sơ đồ {preview ? `— Suất diễn #${preview.showtimeId}` : ''}</h4>
      {preview ? <>
        <p>{preview.file.name}: {preview.validation.totalSeats} ghế / {preview.validation.totalRows} hàng. Chưa lưu dữ liệu.</p>
        <div aria-label="Chú giải hạng ghế" style={{ display: 'flex', flexWrap: 'wrap', gap: 12 }}>
          {grid.categories.map(c => <span key={c.name} style={{ background: c.color, color: '#111827', padding: '4px 8px' }}>{c.name}</span>)}
        </div>
        <div style={{ overflowX: 'auto', maxHeight: 420, margin: '16px 0' }}>
          {grid.rows.map(row => <div key={row.name} style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8, width: 'max-content' }}>
            <strong style={{ minWidth: 60 }}>Hàng {row.name}</strong>
            {row.seats.map(seat => <span key={seat.number} title={`${seat.row}-${seat.number}: ${seat.category} ${coordinates(seat)}`}
              style={{ display: 'inline-block', textAlign: 'center', minWidth: 36, padding: 6, borderRadius: 4, background: seat.color, color: '#111827' }}>
              {seat.number}
              <small style={{ display: 'block', fontSize: 10 }}>{coordinates(seat)}</small>
            </span>)}
          </div>)}
        </div>
      </> : <p>{busy ? 'Đang kiểm tra tệp…' : 'Chưa có sơ đồ hợp lệ. Sửa các lỗi được liệt kê rồi chọn lại tệp.'}</p>}
      <button type="button" disabled={!preview || busy} onClick={onConfirm}>Xác nhận nạp sơ đồ</button>{' '}
      <button type="button" disabled={busy} onClick={onCancel}>Hủy xem trước</button>
    </section>
  );
}
