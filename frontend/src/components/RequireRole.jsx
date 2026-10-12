
export default function RequireRole({ allowedRoles, children }) {
  const currentRole = localStorage.getItem('role');

  if (!currentRole) {
    return null;
  }

  // Handle both array of roles or a single role string
  const isAllowed = Array.isArray(allowedRoles) 
    ? allowedRoles.includes(currentRole)
    : allowedRoles === currentRole;

  if (!isAllowed) {
    return null;
  }

  return <>{children}</>;
}
