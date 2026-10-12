import { clearSession } from '../auth/session.js';
import axios from 'axios';

// 1. T? d?ng l?y URL t? file .env, n?u kh�ng c� th� tr? th?ng v? Backend Render
const API_URL = 'http://localhost:5009/api';

const axiosClient = axios.create({
    baseURL: API_URL,
    headers: {
        'Content-Type': 'application/json',
    },
});

// 2. T? d?ng d�nh k�m Token dang nh?p v�o Header n?u d� dang nh?p
axiosClient.interceptors.request.use(
    (config) => {
        // Clear the JSON default before Axios transforms FormData; the browser
        // supplies Content-Type together with its generated multipart boundary.
        if (typeof FormData !== 'undefined' && config.data instanceof FormData) {
            config.headers.setContentType(undefined);
        }
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

axiosClient.interceptors.response.use(
    response => response,
    error => {
        const isLogin = error.config?.url?.startsWith('/auth/login');
        if (error.response?.status === 401 && !isLogin && localStorage.getItem('token')) {
            clearSession();
            const returnUrl = window.location.pathname + window.location.search;
            window.location.assign(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
        }
        return Promise.reject(error);
    }
);

export default axiosClient;
