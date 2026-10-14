import { jwtDecode } from 'jwt-decode';

// Browser checks only control presentation. The server verifies signature and permissions.
export function getSession(storage = localStorage, now = Date.now()) {
  const token = storage.getItem('token');
  if (!token) return null;
  try {
    /** @type {import('jwt-decode').JwtPayload & {role?: string|string[]}} */
    const claims = jwtDecode(token);
    if (!Number.isFinite(claims.exp) || claims.exp * 1000 <= now) return null;
    const value = claims['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? claims.role ?? [];
    return { token, roles: Array.isArray(value) ? value : [value] };
  } catch {
    return null;
  }
}

export function hasAnyRole(allowedRoles, session = getSession()) {
  const allowed = Array.isArray(allowedRoles) ? allowedRoles : [allowedRoles];
  return Boolean(session && session.roles.some(role => allowed.includes(role)));
}

export function clearSession(storage = localStorage) {
  for (const key of ['token', 'role', 'userId']) storage.removeItem(key);
  if (typeof globalThis.window?.dispatchEvent === 'function') window.dispatchEvent(new Event('sessionchange'));
}
