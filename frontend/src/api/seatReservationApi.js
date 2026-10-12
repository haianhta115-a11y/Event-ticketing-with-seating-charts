import axiosClient from './axiosClient';

/**
 * API dịch vụ giữ chỗ ghế (chuẩn Showtime theo Scrum)
 */
export const getPerformanceSeats = async (showtimeId, currentUserId) => {
    const url = currentUserId
        ? `/showtimes/${showtimeId}/seats?currentUserId=${currentUserId}`
        : `/showtimes/${showtimeId}/seats`;
    const response = await axiosClient.get(url);
    return response.data;
};

export const holdSeat = async (showtimeId, seatId, userId) => {
    const body = userId ? { userId } : {};
    const response = await axiosClient.post(
        `/showtimes/${showtimeId}/seats/${seatId}/hold`,
        body
    );
    return response.data;
};

export const releaseSeat = async (showtimeId, seatId, userId) => {
    const url = userId
        ? `/showtimes/${showtimeId}/seats/${seatId}/hold?userId=${userId}`
        : `/showtimes/${showtimeId}/seats/${seatId}/hold`;
    const response = await axiosClient.delete(url);
    return response.data;
};

export const getMyHoldSession = async (showtimeId, currentUserId) => {
    const url = currentUserId
        ? `/showtimes/${showtimeId}/seats/my-hold?currentUserId=${currentUserId}`
        : `/showtimes/${showtimeId}/seats/my-hold`;
    const response = await axiosClient.get(url);
    return response.data;
};