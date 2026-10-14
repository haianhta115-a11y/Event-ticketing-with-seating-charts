import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Structural regression only; PRODUCT01 live-browser evidence separately verifies actual upload/replacement.
test('routed management screen keeps replacement input after a map exists and retains server-safe confirmation', () => {
  const source = readFileSync(new URL('../src/components/EventDetail.jsx', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /!s\.hasSeatMap\s*&&\s*\(/);
  assert.match(source, /s\.hasSeatMap\s*\?\s*'Thay Sơ đồ \(JSON\)'/);
  assert.match(source, /previewSeatingChart\(showtimeId, file\)/);
  assert.match(source, /window\.confirm\('Thay sơ đồ hiện tại\?/);
  assert.match(source, /uploadSeatingChart\(selected\.showtimeId, selected\.file\)/);
});
