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
