import axiosClient from './axiosClient';

/**
 * Lấy sơ đồ và trạng thái ghế của suất diễn từ Backend thật
 */
export const getShowtimeSeats = async (showtimeId, currentUserId) => {
    const url = currentUserId
        ? `/showtimes/${showtimeId}/seats?currentUserId=${currentUserId}`
        : `/showtimes/${showtimeId}/seats`;
    const response = await axiosClient.get(url);
    return response.data;
};

export const holdSeat = async (showtimeId, seatId, userId) => {
    const response = await axiosClient.post(`/showtimes/${showtimeId}/seats/${seatId}/hold`, { userId });
    return response.data;
};
