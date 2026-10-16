import axiosClient from './axiosClient.js';

export const ordersApi = (client = axiosClient) => ({
  createOrder: async (showtimeId, extraData = {}) => {
    if (!client) throw new Error('axiosClient is not initialized');
    const response = await client.post(`/showtimes/${showtimeId}/orders`, extraData);
    return response.data;
  },

  createOrderWithPayload: async (payload) => {
    if (!client) throw new Error('axiosClient is not initialized');
    const response = await client.post('/orders', payload);
    return response.data;
  },

  getOrderById: async (orderId) => {
    if (!client) throw new Error('axiosClient is not initialized');
    const response = await client.get(`/orders/${orderId}`);
    return response.data;
  },

  getPendingOrder: async (showtimeId) => {
    if (!client) throw new Error('axiosClient is not initialized');
    const response = await client.get(`/showtimes/${showtimeId}/orders/pending`);
    return response.data;
  },
});

export const createOrder = (showtimeId, extraData) => ordersApi(axiosClient).createOrder(showtimeId, extraData);
export const createOrderWithPayload = (payload) => ordersApi(axiosClient).createOrderWithPayload(payload);
export const getOrderById = (orderId) => ordersApi(axiosClient).getOrderById(orderId);
export const getPendingOrder = (showtimeId) => ordersApi(axiosClient).getPendingOrder(showtimeId);
