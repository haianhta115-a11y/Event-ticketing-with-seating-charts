export function validateRegistration({ email, password, username, fullName }) {
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim())) return 'Email không hợp lệ.';
  if (!username.trim() || !fullName.trim()) return 'Username và họ tên là bắt buộc.';
  if (password.length < 8) return 'Password phải có ít nhất 8 ký tự.';
  return '';
}

export function readActivationToken(location) {
  return new URLSearchParams(location.hash.slice(1)).get('token') || new URLSearchParams(location.search).get('token');
}

// Reuse the request during React StrictMode's effect replay; failed tokens still remain single-use.
export function activationRequest(token, post, previous) {
  return previous?.token === token ? previous : { token, promise: post('/auth/activate', { token }) };
}

export function resendMessage(error) {
  if (error?.response?.status === 429) return `Đã đạt giới hạn gửi lại. Thử lại sau ${error.response.data.remainingSeconds} giây.`;
  return error?.response?.data?.message || 'Không gửi được yêu cầu. Vui lòng thử lại sau.';
}
