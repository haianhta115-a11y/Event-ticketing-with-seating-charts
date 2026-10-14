export function importMessage(error) {
  const data = error?.response?.data;
  const details = Array.isArray(data?.errors) ? data.errors.map(e => typeof e === 'string' ? e : `${e.path || e.location || ''}: ${e.message}`).filter(Boolean) : [];
  if (details.length) return `${data?.message || 'Sơ đồ không hợp lệ.'} \n${details.join('\n')}`;
  if (error?.response?.status === 403) return 'Bạn không có quyền thay sơ đồ của suất diễn này.';
  if (error?.response?.status === 401) return 'Phiên đăng nhập đã hết hạn.';
  return data?.message || 'Không thể tải lên sơ đồ. Dữ liệu trước đó được giữ nguyên.';
}

export async function importChart(client, showtimeId, file) {
  const body = new FormData();
  body.append('file', file);
  return (await client.post(`/showtimes/${showtimeId}/seats/import`, body)).data;
}

export async function previewChart(client, showtimeId, file) {
  const body = new FormData();
  body.append('file', file);
  return (await client.post(`/showtimes/${showtimeId}/seats/preview`, body)).data;
}

export function categoryPalette(categoryNames) {
  const compare = (a, b) => a.localeCompare(b, 'vi', { numeric: true });
  const names = [...new Set(categoryNames)].sort(compare);
  return names.map((name, i) => ({ name, color: `hsl(${Math.round(i * 137.508) % 360} 65% 75%)` }));
}

export function previewGrid(seats) {
  const compare = (a, b) => a.localeCompare(b, 'vi', { numeric: true });
  const categories = categoryPalette(seats.map(s => s.category));
  const color = new Map(categories.map(c => [c.name, c.color]));
  const rows = new Map();
  for (const seat of seats) {
    if (!rows.has(seat.row)) rows.set(seat.row, []);
    rows.get(seat.row).push({ ...seat, color: color.get(seat.category) });
  }
  return { categories, rows: [...rows].sort(([a], [b]) => compare(a, b)).map(([name, items]) => ({
    name, seats: items.sort((a, b) => compare(a.number, b.number))
  })) };
}
