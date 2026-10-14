// Read-only Git/content inventory. Emits JSON to stdout; never stages/commits/writes Git objects.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, lstatSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const BASE = '32b0416d1e1052241ba1e78c8a36975b324b705a';
export const SELF = 'docs/delivery02/candidate-files.json';
export const groups = {
  production: [
    'EventTicketing.Api/Email/EmailDelivery.cs', 'EventTicketing.Api/Configuration/RedisKeyScope.cs',
    'EventTicketing.Api/Security/LoginAttemptLimiter.cs', 'EventTicketing.Api/Services/RegistrationService.cs',
    'frontend/src/api/axiosClient.js', 'frontend/src/api/apiConfiguration.js', 'frontend/src/events/seatImport.js',
    'frontend/src/events/seatMapGeometry.js', 'frontend/src/events/seatPresentation.js', 'frontend/vite.config.js',
    'EventTicketing.Api/BackgroundServices/ExpiredSeatHoldCleanupService.cs', 'EventTicketing.Api/Data/AppDbContext.cs',
    'EventTicketing.Api/Endpoints/PublicEventEndpoints.cs', 'EventTicketing.Api/Endpoints/SeatReservationEndpoints.cs',
    'EventTicketing.Api/Program.cs', 'EventTicketing.Api/Services/ISeatReservationService.cs', 'EventTicketing.Api/Services/SeatReservationService.cs',
    'EventTicketing.Api/Configuration/SeatHoldOptions.cs', 'EventTicketing.Api/Controllers/SalesController.cs',
    'EventTicketing.Api/Entities/SalesTransition.cs', 'EventTicketing.Api/Services/ExpiredSeatHoldCleanup.cs', 'EventTicketing.Api/Services/SalesStatusService.cs',
    'frontend/src/api/eventsApi.js', 'frontend/src/api/seatsApi.js', 'frontend/src/auth/session.js', 'frontend/src/components/EventDetail.jsx',
    'frontend/src/events/publicEvents.js', 'frontend/src/pages/Home.jsx', 'frontend/src/pages/SeatMap.jsx', 'frontend/src/pages/ShowtimeDetail.jsx',
    'frontend/src/events/batchSelection.js', 'frontend/src/events/holdRecovery.js', 'frontend/src/events/sales.js', 'frontend/src/events/useHoldRecovery.js',
  ],
  tests: [
    'EventTicketing.Tests/Infrastructure/LocalPostgresDatabase.cs', 'EventTicketing.Tests/EventTicketing.Tests.csproj',
    'EventTicketing.Tests/AuthorizationEndpointTests.cs', 'EventTicketing.Tests/RegistrationActivationTests.cs',
    'EventTicketing.Tests/TeamLocalSetupTests.cs', 'frontend/tests/apiConfiguration.test.mjs',
    'frontend/tests/browser/localBrowserServer.mjs', 'frontend/tests/product01.test.mjs',
    'frontend/tests/seatMapGeometry.test.mjs', 'frontend/tests/seatPresentation.test.mjs',
    'EventTicketing.Tests/Infrastructure/LocalApplicationFactory.cs', 'EventTicketing.Tests/BatchHoldApiTests.cs',
    'EventTicketing.Tests/BrowseAndReleaseTests.cs', 'EventTicketing.Tests/ExpiredHoldCleanupTests.cs', 'EventTicketing.Tests/SalesWorkflowTests.cs',
    'frontend/tests/batchSelection.test.mjs', 'frontend/tests/browser/batchHold.mjs', 'frontend/tests/browser/browseRelease.mjs',
    'frontend/tests/fix05.test.mjs', 'frontend/tests/holdRecovery.test.mjs', 'frontend/tests/sales.test.mjs',
  ],
  ci: [
    '.dockerignore', '.github/workflows/ci.yml', '.github/workflows/deploy-staging.yml', 'tests/run-local-tests.ps1',
    'tests/ci/package.json', 'tests/ci/package-lock.json', 'tests/ci/quality-gate.mjs', 'tests/ci/run-backend-tests.mjs',
    'tests/ci/test-results.test.mjs', 'tests/ci/verify-workflows.mjs', 'tests/ci/workflows.test.mjs',
    'tests/ci/delivery-manifest.mjs', 'tests/ci/delivery-manifest.test.mjs',
  ],
  setup: ['EventTicketing.slnx', 'global.json', 'docker-compose.local.yml', 'scripts/local-env.ps1', 'frontend/.env.example',
    'tools/EventTicketing.LocalSetup/EventTicketing.LocalSetup.csproj', 'tools/EventTicketing.LocalSetup/FreshLocalDatabase.cs',
    'tools/EventTicketing.LocalSetup/LocalSetupCli.cs'],
  documentation: ['README.md', 'docs/fix01/sales-audit-schema.sql', 'docs/delivery02/INCREMENTAL-DELIVERY-02.md',
    'docs/team-ready/LOCAL-SETUP.md', 'docs/team-ready/NEON-COMPATIBILITY.md', 'docs/team-ready/TEAM-READY-01-report.md', SELF],
};
export function safePath(name) {
  if (name === 'frontend/.env.example') return true; // Reviewed public template only; no other dotenv exception.
  return !path.isAbsolute(name) && !name.includes('..') && !name.includes('\\')
    && !/(^|\/)(node_modules|bin|obj|dist|TestResults|artifacts|\.git|\.local-mail)(\/|$)|(^|\/)\.env|\.(dump|bak|vhdx|trx|log|zip|xlsx)$/i.test(name)
    && !/(^|\/)(pricing|checkout|orders?|pending[-_]?payment)(\/|\.)/i.test(name);
}
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
export function generate() {
  if (git('rev-parse', 'HEAD') !== BASE || git('branch', '--show-current') !== 'codex/fix-01-s07') throw new Error('Unexpected source branch/base.');
  if (git('diff', '--cached', '--name-only')) throw new Error('Source index must remain untouched.');
  const tracked = new Map(git('ls-files', '--stage').split('\n').map(line => { const [meta, file] = line.split('\t'); return [file, meta.split(' ')[0]]; }));
  const paths = Object.values(groups).flat().sort();
  if (new Set(paths).size !== paths.length || paths.some(p => !safePath(p))) throw new Error('Unsafe/duplicate candidate paths.');
  const changed = new Set([...git('diff', '--name-only', BASE).split('\n'), ...git('ls-files', '--others', '--exclude-standard').split('\n')].filter(Boolean));
  const entries = paths.map(name => {
    if (!changed.has(name) && name !== SELF) throw new Error(`Candidate not in working delta: ${name}`);
    const mode = tracked.get(name) || '100644';
    if (mode !== '100644' && mode !== '100755') throw new Error('Unsupported file mode.');
    const action = tracked.has(name) ? 'modified' : 'added';
    const group = Object.entries(groups).find(([, paths]) => paths.includes(name))[0];
    if (name === SELF) return { path: name, group, action, mode, sha256: null, reason: 'Self manifest; nonrecursive metadata' };
    if (!lstatSync(path.join(root, name)).isFile() || lstatSync(path.join(root, name)).isSymbolicLink()) throw new Error('Candidate must be a regular file.');
    const bytes = readFileSync(path.join(root, name));
    return { path: name, group, action, mode, sha256: createHash('sha256').update(bytes).digest('hex'),
      gitBlob: execFileSync('git', ['hash-object', `--path=${name}`, '--stdin'], { cwd: root, input: bytes, encoding: 'utf8' }).trim() };
  });
  const excluded = [...changed].filter(p => !paths.includes(p)).sort();
  if (excluded.some(p => !/^docs\/fix0[1-6]\/FIX-0[1-6]-report\.md$/.test(p))) throw new Error('Unreviewed changed paths outside manifest.');
  return { version: 1, repository: 'minhdoan6543-gif/Event-ticketing-with-seating-charts', base: BASE,
    sourceBranch: 'codex/fix-01-s07', codeOnly: true, blockedCriteria: ['S-07.3', 'S-08.3', 'S-12.4'],
    migrationPolicy: 'Never execute historical EF chain or DB03B; sales-audit-schema.sql is definition-only',
    counts: { total: entries.length, added: entries.filter(e => e.action === 'added').length, modified: entries.filter(e => e.action === 'modified').length, deleted: 0 },
    entries, excluded };
}
export function verifyManifest(manifest) {
  if (manifest.base !== BASE || manifest.codeOnly !== true) throw new Error('Manifest base/policy mismatch.');
  const expected = Object.values(groups).flat().sort();
  if (JSON.stringify(manifest.entries.map(e => e.path).sort()) !== JSON.stringify(expected)) throw new Error('Candidate path-set mismatch.');
  const modes = new Map(git('ls-files', '--stage').split('\n').map(line => { const [meta, file] = line.split('\t'); return [file, meta.split(' ')[0]]; }));
  for (const entry of manifest.entries) {
    if (!safePath(entry.path) || !['100644', '100755'].includes(entry.mode)) throw new Error('Unsafe path/mode.');
    if (entry.mode !== (modes.get(entry.path) || '100644')) throw new Error('Git file mode mismatch.');
    if (entry.path === SELF) { if (entry.sha256 !== null) throw new Error('Self manifest cannot hash itself.'); continue; }
    if (!lstatSync(path.join(root, entry.path)).isFile() || lstatSync(path.join(root, entry.path)).isSymbolicLink()) throw new Error('Candidate is not a regular file.');
    const bytes = readFileSync(path.join(root, entry.path));
    if (createHash('sha256').update(bytes).digest('hex') !== entry.sha256) throw new Error(`Hash mismatch: ${entry.path}`);
    if (execFileSync('git', ['hash-object', `--path=${entry.path}`, '--stdin'], { cwd: root, input: bytes, encoding: 'utf8' }).trim() !== entry.gitBlob)
      throw new Error('Git clean-filter content mismatch.');
  }
  return { verifiedPaths: expected.length, mismatches: 0, codeOnly: true };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { console.log(JSON.stringify(process.argv.includes('--verify') ? verifyManifest(JSON.parse(readFileSync(path.join(root, SELF), 'utf8'))) : generate(), null, 2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
