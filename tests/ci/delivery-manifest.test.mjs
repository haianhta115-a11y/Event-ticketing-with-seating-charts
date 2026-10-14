import test from 'node:test';
import assert from 'node:assert/strict';
import { groups, safePath, BASE, SELF } from './delivery-manifest.mjs';
test('candidate includes complete sales, cleanup, recovery, batch/release, feed and CI dependencies', () => {
  const paths = Object.values(groups).flat(); assert.equal(paths.length, 83); assert.equal(new Set(paths).size, 83);
  for (const p of paths) assert.equal(safePath(p), true);
  assert.ok(paths.includes('docs/fix01/sales-audit-schema.sql')); assert.ok(paths.includes(SELF));
  assert.ok(paths.includes('tools/EventTicketing.LocalSetup/FreshLocalDatabase.cs'));
  assert.ok(paths.includes('frontend/.env.example'));
  assert.equal(BASE, '32b0416d1e1052241ba1e78c8a36975b324b705a');
});
for (const p of ['../private.dump', 'D:/DockerColdBackups/backup.vhdx', '.env', 'api/bin/output.dll', 'frontend/node_modules/x.js',
  'EventTicketing.Tests/TestResults/private.trx', 'frontend/dist/index.html', 'api/orders/Order.cs', 'api/pricing/Price.cs', '.git/config'])
  test(`delivery allowlist rejects ${p}`, () => assert.equal(safePath(p), false));
