import { hasAnyRole } from '../auth/session';

export default function RequireRole({ allowedRoles, children }) {
  if (!hasAnyRole(allowedRoles)) return null;

  return <>{children}</>;
}
