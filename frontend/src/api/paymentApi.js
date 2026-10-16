import axiosClient from './axiosClient.js';

/**
 * Khởi tạo thanh toán cho đơn hàng qua cổng sandbox
 * @param {number|string} orderId - ID đơn hàng
 * @returns {Promise<{ orderId: number, orderCode: string, amount: number, redirectUrl: string }>}
 */
export const payOrder = async (orderId) => {
  const response = await axiosClient.post(`/orders/${orderId}/pay`);
  return response.data;
};

/**
 * Tạo đơn hàng từ các ghế đang giữ cho suất diễn (hoặc trả về đơn PENDING hiện có)
 * @param {number|string} showtimeId - ID suất diễn
 * @returns {Promise<any>}
 */
export const createOrderForShowtime = async (showtimeId) => {
  const response = await axiosClient.post(`/showtimes/${showtimeId}/orders`);
  return response.data;
};

/**
 * Lấy thông tin đơn hàng theo ID
 * @param {number|string} orderId - ID đơn hàng
 * @returns {Promise<any>}
 */
export const getOrderById = async (orderId) => {
  const response = await axiosClient.get(`/orders/${orderId}`);
  return response.data;
};

/**
 * Lấy đơn hàng đang chờ thanh toán cho suất diễn
 * @param {number|string} showtimeId - ID suất diễn
 * @returns {Promise<any>}
 */
export const getPendingOrderForShowtime = async (showtimeId) => {
  const response = await axiosClient.get(`/showtimes/${showtimeId}/orders/pending`);
  return response.data;
};

/**
 * SCRUM-35: Ký chữ ký hợp lệ cho webhook giả lập
 * @param {{ orderId: number|string, transactionId: string, amount: number, success: boolean }} param
 * @returns {Promise<{ signature: string, payload: string }>}
 */
export const signMockPayment = async ({ orderId, transactionId, amount, success }) => {
  const response = await axiosClient.post('/payments/mock/sign', {
    orderId: Number(orderId),
    transactionId,
    amount: Number(amount),
    success: Boolean(success),
  });
  return response.data;
};

/**
 * SCRUM-35: Gửi webhook thanh toán giả lập kèm chữ ký
 * @param {{ orderId: number|string, transactionId: string, amount: number, success: boolean, signature: string }} param
 * @returns {Promise<any>}
 */
export const sendMockWebhook = async ({ orderId, transactionId, amount, success, signature }) => {
  const response = await axiosClient.post('/payments/mock/webhook', {
    orderId: Number(orderId),
    transactionId,
    amount: Number(amount),
    success: Boolean(success),
    signature,
  });
  return response.data;
};
