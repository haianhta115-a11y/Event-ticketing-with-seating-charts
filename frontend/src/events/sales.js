export function salesApi(client) {
  return {
    async publishEvent(id) { return (await client.put(`/events/${id}/publish`)).data; },
    async unpublishEvent(id) { return (await client.put(`/events/${id}/unpublish`)).data; },
    async updateShowtimeSalesStatus(id, newStatus) {
      return (await client.put(`/showtimes/${id}/status`, { newStatus })).data;
    },
  };
}

export function salesDisabledReason(event, showtime) {
  if (showtime.status === 'OnSale') return '';
  if (event.status !== 'Published') return 'Cần xuất bản sự kiện trước khi mở bán.';
  if (!showtime.hasSeatMap) return 'Cần nạp sơ đồ ghế trước khi mở bán.';
  return '';
}
