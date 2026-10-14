import test from 'node:test';
import assert from 'node:assert/strict';
import { showtimeMetadata } from '../src/events/showtimeMetadata.js';

function dom() {
  const nodes = [];
  return { title: 'Original', nodes, head: {
    querySelector(selector) { const [, attribute, key] = selector.match(/meta\[(\w+)="([^"]+)"\]/); return nodes.find(n => n.getAttribute(attribute) === key); },
    append(node) { nodes.push(node); },
  }, createElement() {
    const attributes = new Map();
    return { getAttribute: key => attributes.get(key) ?? null, setAttribute: (key, value) => attributes.set(key, value),
      removeAttribute: key => attributes.delete(key), remove() { nodes.splice(nodes.indexOf(this), 1); } };
  } };
}
test('detail metadata is text-only and restores previous title and tags on navigation', () => {
  const document = dom(), existing = document.createElement('meta');
  existing.setAttribute('name', 'description'); existing.setAttribute('content', 'Before'); document.head.append(existing);
  const restore = showtimeMetadata(document, { eventName: 'Concert', description: '<script>not executable</script>' }, 'https://example.test/shows/1');
  assert.equal(document.title, 'Concert — Vé sự kiện'); assert.equal(document.nodes.length, 5);
  assert.equal(existing.getAttribute('content'), '<script>not executable</script>');
  restore(); assert.equal(document.title, 'Original'); assert.deepEqual(document.nodes, [existing]); assert.equal(existing.getAttribute('content'), 'Before');
});
test('missing description falls back to event name; missing original attribute and empty show are preserved', () => {
  const document = dom(), existing = document.createElement('meta'); existing.setAttribute('name', 'description'); document.head.append(existing);
  const restore = showtimeMetadata(document, { eventName: 'Concert' }, 'https://example.test');
  assert.equal(existing.getAttribute('content'), 'Concert'); restore(); assert.equal(existing.getAttribute('content'), null);
  showtimeMetadata(document, null, '')(); assert.equal(document.title, 'Original');
});
