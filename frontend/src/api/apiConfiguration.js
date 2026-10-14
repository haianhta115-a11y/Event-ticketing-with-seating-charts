export function apiBaseUrl(value) {
  const url = value?.trim() || '/api';
  if (url === '/api' || url === '/api/') return '/api';
  const parsed = new URL(url);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password
    || parsed.search || parsed.hash) throw new Error('Invalid public API URL; credentials are not allowed.');
  return url.replace(/\/+$/, '');
}
