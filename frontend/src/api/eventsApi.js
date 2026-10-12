import axiosClient from './axiosClient';
import { EVENT_STATUS, SHOWTIME_STATUS } from '../constants/eventStatus';

// ID của organizer hiện tại để mô phỏng phân quyền dữ liệu
const CURRENT_ORGANIZER_ID = 'org-current';

// Danh sách mock sự kiện lưu ở module scope để lưu được sự kiện mới tạo
const mockEvents = [
  {
    id: 'evt-001',
    name: 'Live Concert: Những Bản Tình Ca Mùa Thu',
    description: 'Đêm nhạc acoustic ấm cúng với những tình khúc vượt thời gian của các nghệ sĩ tên tuổi.',
    location: 'Nhà hát Lớn Hà Nội - Số 1 Tràng Tiền, Hoàn Kiếm, Hà Nội',
    imageUrl: 'https://images.unsplash.com/photo-1514525253161-7a46d19cd819?w=600&auto=format&fit=crop&q=80',
    status: EVENT_STATUS.PUBLISHED,
    ownerId: CURRENT_ORGANIZER_ID,
    showCount: 2,
  },
  {
    id: 'evt-002',
    name: 'Hội Thảo Công Nghệ: AI & Cloud Summit 2026',
    description: 'Diễn đàn cập nhật xu hướng trí tuệ nhân tạo, giải pháp đám mây và kỹ thuật phần mềm tiên tiến.',
    location: 'Trung tâm Hội nghị Quốc gia - Mễ Trì, Nam Từ Liêm, Hà Nội',
    imageUrl: 'https://images.unsplash.com/photo-1540575467063-178a50c2df87?w=600&auto=format&fit=crop&q=80',
    status: EVENT_STATUS.DRAFT,
    ownerId: CURRENT_ORGANIZER_ID,
    showCount: 1,
  },
  {
    id: 'evt-003',
    name: 'Vở Nhạc Kịch: Tiếng Chuông Lúc Nửa Đêm',
    description: 'Tác phẩm kịch nghệ đặc sắc được dàn dựng công phu với dàn diễn viên kịch tài năng.',
    location: 'Nhà hát Bến Thành - 6 Mạc Đĩnh Chi, Bến Nghé, Quận 1, TP. HCM',
    imageUrl: 'https://images.unsplash.com/photo-1507676184212-d03ab07a01bf?w=600&auto=format&fit=crop&q=80',
    status: EVENT_STATUS.PUBLISHED,
    ownerId: CURRENT_ORGANIZER_ID,
    showCount: 3,
  },
  {
    id: 'evt-004',
    name: 'Sự Kiện Độc Quyền (Thuộc Organizer Khác)',
    description: 'Sự kiện do ban tổ chức khác nắm quyền quản lý, dùng để kiểm thử lỗi 403 Forbidden.',
    location: 'Trung tâm Triển lãm SECC - Quận 7, TP. HCM',
    imageUrl: 'https://images.unsplash.com/photo-1492684223066-81342ee5ff30?w=600&auto=format&fit=crop&q=80',
    status: EVENT_STATUS.PUBLISHED,
    ownerId: 'org-other', // Thuộc organizer khác
    showCount: 1,
  },
  {
    id: 'evt-005',
    name: 'Triển Lãm & Đấu Giá Nghệ Thuật Đương Đại',
    description: 'Trưng bày hơn 100 tác phẩm hội họa và điêu khắc nghệ thuật đương đại Việt Nam.',
    location: 'The Factory Contemporary Arts Centre - Thảo Điền, TP. Thủ Đức, TP. HCM',
    imageUrl: 'https://images.unsplash.com/photo-1561214115-f2f134cc4912?w=600&auto=format&fit=crop&q=80',
    status: EVENT_STATUS.ENDED,
    ownerId: CURRENT_ORGANIZER_ID,
    showCount: 4,
  },
];

// Danh sách mock suất diễn lưu ở module scope
const mockShowtimes = [
  {
    id: 'st-001',
    eventId: 'evt-001',
    startTime: '2026-10-15T19:30',
    endTime: '2026-10-15T22:00',
    status: SHOWTIME_STATUS.ON_SALE,
    hasSeatMap: true,
  },
  {
    id: 'st-002',
    eventId: 'evt-001',
    startTime: '2026-10-16T19:30',
    endTime: '2026-10-16T22:00',
    status: SHOWTIME_STATUS.DRAFT,
    hasSeatMap: false,
  },
  {
    id: 'st-003',
    eventId: 'evt-002',
    startTime: '2026-11-05T08:00',
    endTime: '2026-11-05T17:00',
    status: SHOWTIME_STATUS.DRAFT,
    hasSeatMap: true,
  },
  {
    id: 'st-004',
    eventId: 'evt-003',
    startTime: '2026-10-20T20:00',
    endTime: '2026-10-20T22:30',
    status: SHOWTIME_STATUS.CLOSED,
    hasSeatMap: true,
  },
];

export const getEvents = async () => {
  // TODO: thay bằng API thật để sau này đổi dễ
  // Khi backend hoàn thiện:
  // const response = await axiosClient.get('/events');
  // return response.data;

  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  return new Promise((resolve) => {
    setTimeout(() => {
      resolve([...mockEvents]);
    }, 500);
  });
};

export const getEventById = async (id) => {
  // TODO: thay bằng API thật để sau này đổi dễ
  // Khi backend hoàn thiện:
  // const response = await axiosClient.get(`/events/${id}`);
  // return response.data;

  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  return new Promise((resolve, reject) => {
    setTimeout(() => {
      const event = mockEvents.find((e) => e.id === id);
      if (!event) {
        const error = new Error('Sự kiện không tồn tại');
        error.response = { status: 404, data: { message: 'Sự kiện không tồn tại' } };
        return reject(error);
      }

      // Kiểm tra quyền sở hữu sự kiện
      if (event.ownerId && event.ownerId !== CURRENT_ORGANIZER_ID) {
        const error = new Error('Bạn không có quyền truy cập sự kiện này');
        error.response = { status: 403, data: { message: 'Bạn không có quyền truy cập sự kiện này' } };
        return reject(error);
      }

      resolve({ ...event });
    }, 500);
  });
};

export const createEvent = async (data) => {
  // TODO: thay bằng API thật để sau này đổi dễ
  // Khi backend hoàn thiện:
  // const response = await axiosClient.post('/events', data);
  // return response.data;

  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  return new Promise((resolve) => {
    setTimeout(() => {
      const newEvent = {
        id: `evt-${Date.now()}`,
        name: data.name.trim(),
        description: data.description.trim(),
        location: data.location.trim(),
        imageUrl: data.imageUrl?.trim() || 'https://images.unsplash.com/photo-1492684223066-81342ee5ff30?w=600&auto=format&fit=crop&q=80',
        status: EVENT_STATUS.DRAFT,
        ownerId: CURRENT_ORGANIZER_ID,
        showCount: 0,
      };

      mockEvents.unshift(newEvent);
      resolve({ ...newEvent });
    }, 500);
  });
};

export const getShowtimes = async (eventId) => {
  // TODO: thay bằng API thật để sau này đổi dễ
  // Khi backend hoàn thiện:
  // const response = await axiosClient.get(`/events/${eventId}/showtimes`);
  // return response.data;

  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  return new Promise((resolve) => {
    setTimeout(() => {
      const list = mockShowtimes.filter((s) => s.eventId === eventId);
      resolve([...list]);
    }, 500);
  });
};

export const createShowtime = async (eventId, data) => {
  // TODO: thay bằng API thật để sau này đổi dễ
  // Khi backend hoàn thiện:
  // const response = await axiosClient.post(`/events/${eventId}/showtimes`, data);
  // return response.data;

  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  return new Promise((resolve) => {
    setTimeout(() => {
      const newShowtime = {
        id: `st-${Date.now()}`,
        eventId,
        startTime: data.startTime,
        endTime: data.endTime || null,
        status: SHOWTIME_STATUS.DRAFT,
        hasSeatMap: false,
      };

      mockShowtimes.push(newShowtime);

      // Cập nhật số suất diễn cho sự kiện trong mockEvents
      const targetEvent = mockEvents.find((e) => e.id === eventId);
      if (targetEvent) {
        targetEvent.showCount = (targetEvent.showCount || 0) + 1;
      }

      resolve({ ...newShowtime });
    }, 500);
  });
};

// ========================================================
// SCRUM-86 & 87: HÀM MỞ BÁN / ĐÓNG BÁN VỚI KIỂM TRA SƠ ĐỒ GHẾ
// ========================================================
export const updateShowtimeSalesStatus = async (showtimeId, newStatus) => {
  if (!axiosClient) throw new Error('axiosClient is not initialized');

  return new Promise((resolve, reject) => {
    setTimeout(() => {
      const showtime = mockShowtimes.find((s) => s.id === showtimeId);
      if (!showtime) {
        return reject(new Error('Suất diễn không tồn tại!'));
      }

      // KỊCH BẢN CHẶN: Nếu bấm Mở bán mà chưa có sơ đồ ghế -> Chặn kèm lý do
      if (newStatus === SHOWTIME_STATUS.ON_SALE && !showtime.hasSeatMap) {
        return reject(new Error('Suất diễn chưa có sơ đồ ghế! Vui lòng thiết lập sơ đồ ghế trước khi mở bán.'));
      }

      // Đổi trạng thái (chuyển đổi qua lại linh hoạt giữa Đang bán và Đóng bán)
      showtime.status = newStatus;
      resolve({ ...showtime });
    }, 300);
  });
};
