import axiosClient from './axiosClient';

// TODO: thay bằng GET /api/showtimes/{id}/seats khi backend hoàn tất
// export const getShowtimeSeats = async (showtimeId) => {
//   const response = await axiosClient.get(`/showtimes/${showtimeId}/seats`);
//   return response.data;
// };

/**
 * Sinh danh sách ghế mẫu mặc định (~120 ghế: 10 hàng x 12 ghế, chia 3 khu vực)
 */
function generateDefaultSeats() {
  const sections = [
    { name: 'VIP', rows: ['A', 'B', 'C'], category: 'VIP', price: 500000 },
    { name: 'Standard', rows: ['D', 'E', 'F', 'G', 'H'], category: 'Standard', price: 250000 },
    { name: 'Balcony', rows: ['I', 'J'], category: 'Economy', price: 150000 },
  ];

  const seats = [];
  let seatId = 1;
  let globalY = 1;

  sections.forEach((sec) => {
    sec.rows.forEach((rowName) => {
      for (let num = 1; num <= 12; num++) {
        // Phân bổ trạng thái ngẫu nhiên nhưng ổn định cho mock:
        // ~70% Available, ~10% Held, ~20% Sold
        const seed = (seatId * 17 + num * 31) % 100;
        let status = 'Available';
        if (seed < 20) {
          status = 'Sold';
        } else if (seed < 32) {
          status = 'Held';
        }

        seats.push({
          id: `seat-${seatId++}`,
          section: sec.name,
          row: rowName,
          number: String(num),
          category: sec.category,
          price: sec.price,
          x: num,
          y: globalY,
          status,
        });
      }
      globalY++;
    });
    // Khoảng trống giữa các phân khu
    globalY++;
  });

  return seats;
}

/**
 * Sinh đúng 2000 ghế (40 hàng x 50 ghế)
 */
function generate2000Seats() {
  const seats = [];
  let seatId = 1;

  for (let r = 1; r <= 40; r++) {
    const rowName = `R${r}`;
    const sectionName = r <= 10 ? 'VIP Floor' : r <= 30 ? 'Main Hall' : 'Upper Deck';
    const category = r <= 10 ? 'VIP' : r <= 30 ? 'Standard' : 'Economy';
    const price = r <= 10 ? 600000 : r <= 30 ? 300000 : 180000;

    for (let c = 1; c <= 50; c++) {
      const seed = (seatId * 23 + c * 7) % 100;
      let status = 'Available';
      if (seed < 25) {
        status = 'Sold';
      } else if (seed < 35) {
        status = 'Held';
      }

      seats.push({
        id: `seat-${seatId++}`,
        section: sectionName,
        row: rowName,
        number: String(c),
        category,
        price,
        x: c,
        y: r,
        status,
      });
    }
  }

  return seats;
}

/**
 * Lấy sơ đồ và trạng thái ghế của suất diễn
 * Hỗ trợ các kịch bản kiểm thử qua URL query parameter ?mock=:
 * - ?mock=empty : Trả hasSeatMap: false (suất diễn chưa có sơ đồ / chưa mở bán)
 * - ?mock=2000  : Sinh đúng 2000 ghế để kiểm thử hiệu năng render
 * - ?mock=error : Giả lập ném lỗi mạng / lỗi server
 * - Mặc định     : Trả ~120 ghế phân bổ nhiều hàng và khu vực
 */
export const getShowtimeSeats = async (showtimeId) => {
  // Giữ axiosClient để khi backend sẵn sàng chỉ cần mở comment
  if (!axiosClient) {
    throw new Error('axiosClient is not initialized');
  }

  // Đọc query param ?mock= từ URL hiện tại
  const urlParams = typeof window !== 'undefined' ? new URLSearchParams(window.location.search) : new URLSearchParams();
  const mockType = urlParams.get('mock');

  return new Promise((resolve, reject) => {
    setTimeout(() => {
      // 1. Kịch bản lỗi
      if (mockType === 'error') {
        const error = new Error('Lỗi máy chủ khi tải sơ đồ ghế (HTTP 500)');
        error.response = { status: 500, data: { message: 'Không thể kết nối đến máy chủ sơ đồ ghế' } };
        return reject(error);
      }

      // 2. Kịch bản chưa có sơ đồ ghế
      if (mockType === 'empty') {
        return resolve({
          showtimeId,
          hasSeatMap: false,
          seats: [],
        });
      }

      // 3. Kịch bản 2000 ghế kiểm thử hiệu năng
      if (mockType === '2000') {
        return resolve({
          showtimeId,
          hasSeatMap: true,
          seats: generate2000Seats(),
        });
      }

      // 4. Kịch bản mặc định ~120 ghế
      return resolve({
        showtimeId,
        hasSeatMap: true,
        seats: generateDefaultSeats(),
      });
    }, 500);
  });
};
