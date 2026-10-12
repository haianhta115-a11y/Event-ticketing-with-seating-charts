import axios from 'axios';

// 1. Tự động lấy URL từ file .env, nếu không có thì trỏ thẳng về Backend Render
const API_URL = import.meta.env.VITE_API_BASE_URL || 'https://event-ticketing-with-seating-charts.onrender.com/api';

const axiosClient = axios.create({
    baseURL: API_URL,
    headers: {
        'Content-Type': 'application/json',
    },
});

// 2. Tự động đính kèm Token đăng nhập vào Header nếu đã đăng nhập
axiosClient.interceptors.request.use(
    (config) => {
        const token = localStorage.getItem('token');
        if (token) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => {
        return Promise.reject(error);
    }
);

export default axiosClient;