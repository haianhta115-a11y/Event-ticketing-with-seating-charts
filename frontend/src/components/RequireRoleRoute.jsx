import { Navigate } from 'react-router-dom';

export default function RequireRoleRoute({ allowedRoles, children }) {
  const token = localStorage.getItem('token');
  const currentRole = localStorage.getItem('role');

  if (!token) {
    return <Navigate to="/login" replace />;
  }

  const isAllowed = Array.isArray(allowedRoles)
    ? allowedRoles.includes(currentRole)
    : allowedRoles === currentRole;

  if (!isAllowed) {
    return (
      <div
        style={{
          maxWidth: '600px',
          margin: '40px auto',
          padding: '24px',
          backgroundColor: '#fff1f2',
          border: '1px solid #fecdd3',
          borderRadius: '8px',
          textAlign: 'center',
          color: '#9f1239',
        }}
      >
        <h3 style={{ margin: '0 0 8px 0', fontSize: '18px' }}>Truy cập bị từ chối</h3>
        <p style={{ margin: 0, fontSize: '14px' }}>Bạn không có quyền truy cập</p>
      </div>
    );
  }

  return children;
}
