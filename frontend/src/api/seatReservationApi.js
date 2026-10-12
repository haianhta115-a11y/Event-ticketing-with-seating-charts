import axiosClient from './axiosClient';

/**
 * API dịch vụ giữ chỗ ghế (SCRUM-17)
 */

export const getPerformanceSeats = async (performanceId, currentUserId) => {
  const url = currentUserId 
    ? `/performances/${performanceId}/seats?currentUserId=${currentUserId}`
    : `/performances/${performanceId}/seats`;
  const response = await axiosClient.get(url);
  return response.data;
};

export const holdSeat = async (performanceId, seatId, userId) => {
  const body = userId ? { userId } : {};
  const response = await axiosClient.post(
    `/performances/${performanceId}/seats/${seatId}/hold`,
    body
  );
  return response.data;
};

export const releaseSeat = async (performanceId, seatId, userId) => {
  const url = userId
    ? `/performances/${performanceId}/seats/${seatId}/hold?userIdQuery=${userId}`
    : `/performances/${performanceId}/seats/${seatId}/hold`;
  const response = await axiosClient.delete(url);
  return response.data;
};

export const getMyHoldSession = async (performanceId, currentUserId) => {
  const url = currentUserId
    ? `/performances/${performanceId}/seats/my-hold?currentUserId=${currentUserId}`
    : `/performances/${performanceId}/seats/my-hold`;
  const response = await axiosClient.get(url);
  return response.data;
};

