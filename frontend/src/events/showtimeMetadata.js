// Text-only DOM assignments: imported event descriptions are not executable HTML.
export function showtimeMetadata(document, showtime, url) {
  const beforeTitle = document.title;
  const restore = [];
  if (!showtime?.eventName) return () => {};
  document.title = `${showtime.eventName} — Vé sự kiện`;
  for (const [attribute, key, content] of [
    ['name', 'description', showtime.description || showtime.eventName],
    ['property', 'og:title', showtime.eventName],
    ['property', 'og:description', showtime.description || showtime.eventName],
    ['property', 'og:type', 'website'],
    ['property', 'og:url', url],
  ]) {
    const existing = document.head.querySelector(`meta[${attribute}="${key}"]`);
    const node = existing || document.createElement('meta');
    const previous = node.getAttribute('content');
    node.setAttribute(attribute, key); node.setAttribute('content', content);
    if (!existing) document.head.append(node);
    restore.push(() => { if (!existing) node.remove(); else if (previous === null) node.removeAttribute('content'); else node.setAttribute('content', previous); });
  }
  return () => { document.title = beforeTitle; restore.forEach(reset => reset()); };
}
