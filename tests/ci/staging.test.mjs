import test from 'node:test';
import assert from 'node:assert/strict';
import { deploy, validateDeployment, DockerStagingDriver } from '../../scripts/staging-blue-green.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
const c = { approved: 'true', sha: 'a'.repeat(40), project: 'demo-stage', apiImage: `ghcr.io/example/repo-api@sha256:${'b'.repeat(64)}`,
  webImage: `ghcr.io/example/repo-web@sha256:${'c'.repeat(64)}`, routeFile: '/approved/app/current.conf', envFile: '/approved/private/app.env', liveUrl: 'https://example.test' };
function driver(failure) {
  const state = { route: 'old', oldStopped: false, candidates: 0, restored: 0, checks: 0 };
  return { state, snapshot: async () => 'old', prepare: async () => { state.candidates++; if (failure === 'prepare') throw Error('prepare'); return 'new'; },
    candidateHealthy: async () => failure !== 'candidate', activate: async () => { state.route = 'new'; if (failure === 'activate') throw Error('activate'); },
    restore: async value => { state.route = value; state.restored++; }, liveHealthy: async () => ++state.checks > 1 || failure !== 'live' };
}
for (const failure of ['prepare', 'candidate', 'activate', 'live']) test(`deployment ${failure} failure preserves healthy old version`, async () => {
  const d = driver(failure); await assert.rejects(deploy(c, d)); assert.equal(d.state.route, 'old'); assert.equal(d.state.oldStopped, false);
});
test('successful swap only follows candidate health; previous version retained', async () => {
  const d = driver(); assert.equal((await deploy(c, d)).previousRetained, true); assert.equal(d.state.route, 'new'); assert.equal(d.state.restored, 0);
});
for (const patch of [{ approved: 'false' }, { sha: 'main' }, { apiImage: 'latest' }, { routeFile: '/' }, { liveUrl: 'https://user:secret@example.test' }])
  test('unsafe/unapproved configuration stops before any driver call ' + Object.keys(patch)[0], async () => {
    const d = driver(); assert.throws(() => validateDeployment({ ...c, ...patch })); await assert.rejects(deploy({ ...c, ...patch }, d)); assert.equal(d.state.candidates, 0);
  });

test('cold interruption journal blocks a different release and recovery restores exact old route without Docker', async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'finalfix-journal-'));
  const config = { ...c, envFile: path.join(directory, 'private.env'), routeFile: path.join(directory, 'current.conf') };
  await fs.writeFile(config.envFile, 'fixture-only'); await fs.writeFile(config.routeFile, 'old route');
  const d = new DockerStagingDriver(config); d.liveHealthy = async () => true;
  d.command = async () => { throw Error('No real commands authorized in this test'); };
  d.restore = async previous => fs.writeFile(config.routeFile, previous);
  await d.beforeActivate('old route', { name: 'fixture', port: 9999 }); await fs.writeFile(config.routeFile, 'new route');
  const other = new DockerStagingDriver({ ...config, sha: 'd'.repeat(40) }); other.liveHealthy = async () => true;
  await assert.rejects(other.snapshot(), /Incomplete deployment/);
  await d.recover(); assert.equal(await fs.readFile(config.routeFile, 'utf8'), 'old route');
  assert.equal(JSON.parse(await fs.readFile(d.journalFile(), 'utf8')).phase, 'recovered');
  await assert.rejects(d.recover(), /No matching/);
  // Keep the isolated evidence directory; no automatic cleanup.
});
