/** @returns {Record<string,string>} */
export function validateEvent(form) {
  /** @type {Record<string,string>} */
  const errors = {};
  for (const [field, label, limit] of [['name', 'Tên sự kiện', 200], ['description', 'Mô tả', null], ['location', 'Địa điểm', 300]]) {
    if (!form[field]?.trim()) errors[field] = `${label} là bắt buộc.`;
    else if (limit && form[field].length > limit) errors[field] = `${label} tối đa ${limit} ký tự.`;
  }
  return errors;
}

// datetime-local represents Vietnam wall time, independent of the browser timezone.
export function vietnamToUtc(value) {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) throw new Error('Thời gian không hợp lệ.');
  const date = new Date(`${value}:00+07:00`);
  if (!Number.isFinite(date.getTime())) throw new Error('Thời gian không hợp lệ.');
  if (new Date(date.getTime() + 7 * 3600000).toISOString().slice(0, 16) !== value) throw new Error('Thời gian không hợp lệ.');
  return date.toISOString();
}

/** @returns {Record<string,string>} */
export function validateShowtime(startTime, endTime, now = Date.now()) {
  /** @type {Record<string,string>} */
  const errors = {};
  let start;
  try { start = new Date(vietnamToUtc(startTime)).getTime(); }
  catch { errors.startTime = 'Vui lòng chọn thời điểm bắt đầu hợp lệ.'; }
  if (start <= now) errors.startTime = 'Thời điểm bắt đầu phải ở tương lai.';
  if (endTime) {
    try { if (new Date(vietnamToUtc(endTime)).getTime() <= start) errors.endTime = 'Thời điểm kết thúc phải sau thời điểm bắt đầu.'; }
    catch { errors.endTime = 'Thời điểm kết thúc không hợp lệ.'; }
  }
  return errors;
}

export function formatVietnamTime(value) {
  return value ? new Date(value).toLocaleString('vi-VN', {
    timeZone: 'Asia/Ho_Chi_Minh', hour: '2-digit', minute: '2-digit',
    day: '2-digit', month: '2-digit', year: 'numeric',
  }) : '';
}

/** @returns {Record<string,string>} */
export function apiErrors(error) {
  /** @type {Record<string,string>} */
  const errors = {};
  for (const [key, messages] of Object.entries(error?.response?.data?.errors || {})) {
    const field = key.replace(/^\$\./, '');
    errors[field.charAt(0).toLowerCase() + field.slice(1)] = Array.isArray(messages) ? messages.join(' ') : messages;
  }
  return errors;
}

export function apiMessage(error, fallback = 'Không thể hoàn thành yêu cầu. Vui lòng thử lại.') {
  if (error?.response?.status === 403) return 'Bạn không có quyền truy cập sự kiện này.';
  if (error?.response?.status === 401) return 'Phiên đăng nhập đã hết hạn.';
  if (error?.response?.status === 404) return 'Không tìm thấy sự kiện.';
  return error?.response?.data?.message || error?.response?.data?.title || fallback;
}

// React state is asynchronous; a synchronous gate also blocks same-tick submits.
export function submissionGate() {
  let pending = false;
  return { enter() { if (pending) return false; pending = true; return true; }, leave() { pending = false; } };
}

export function managementApi(client) {
  return {
    async getEvents() { return (await client.get('/events/admin')).data; },
    async getEventById(id) { return (await client.get(`/events/${id}`)).data; },
    async createEvent(data) { return (await client.post('/events', data)).data; },
    async updateEvent(id, data) { return (await client.put(`/events/${id}`, data)).data; },
    async getShowtimes(id) { return (await client.get(`/events/${id}/showtimes`)).data; },
    async createShowtime(id, data) { return (await client.post(`/events/${id}/showtimes`, data)).data; },
    async getEventSeatPricing(id) { return (await client.get(`/events/${id}/pricing`)).data; },
    async updateEventSeatPricing(id, data) { return (await client.put(`/events/${id}/pricing`, data)).data; },
  };
}
