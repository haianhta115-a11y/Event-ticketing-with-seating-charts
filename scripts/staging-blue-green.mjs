import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { pathToFileURL } from 'node:url';
const exec = promisify(execFile);

export function validateDeployment(c) {
  if (c.approved !== 'true' || !/^[a-f\d]{40}$/.test(c.sha || '') || !/^[a-z][a-z0-9-]{2,40}$/.test(c.project || ''))
    throw new Error('Explicit staging approval, immutable SHA and scoped project are required.');
  for (const image of [c.apiImage, c.webImage])
    if (!/^ghcr\.io\/[a-z0-9-]+\/[a-z0-9_.-]+@sha256:[a-f\d]{64}$/.test(image || ''))
      throw new Error('Both images must be approved GHCR immutable digests.');
  for (const file of [c.routeFile, c.envFile])
    if (!path.isAbsolute(file || '') || path.dirname(file) === path.parse(file).root || file.includes('..'))
      throw new Error('Explicit scoped existing route and private environment files are required.');
  const url = new URL(c.liveUrl || '');
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || url.search || url.hash)
    throw new Error('Approved live URL must not contain credentials.');
  if (url.protocol !== 'https:' && !['localhost', '127.0.0.1'].includes(url.hostname))
    throw new Error('Non-local staging health must use HTTPS.');
}

// Start both candidate containers without stopping old ones. Activation is the
// only traffic-changing operation. No database/migration/volume/cleanup commands.
export async function deploy(c, driver) {
  validateDeployment(c);
  const previous = await driver.snapshot();
  const candidate = await driver.prepare(c);
  if (!await driver.candidateHealthy(candidate)) throw new Error('Candidate unhealthy; old route untouched.');
  try {
    await driver.beforeActivate?.(previous, candidate);
    await driver.activate(candidate);
    if (!await driver.liveHealthy()) throw new Error('Live health failed.');
    await driver.complete?.();
    return { sha: c.sha, active: candidate, previousRetained: true };
  } catch {
    await driver.restore(previous);
    if (!await driver.liveHealthy()) throw new Error('Rollback health failed; operator intervention required.');
    throw new Error('Deployment failed; previous healthy route restored.');
  }
}

export class DockerStagingDriver {
  constructor(c) { this.c = c; }
  async command(file, args) {
    try { return (await exec(file, args, { timeout: 120000, maxBuffer: 1024 * 1024 })).stdout.trim(); }
    catch { throw new Error('Staging command failed; sensitive process output withheld.'); }
  }
  async snapshot() {
    await fs.access(this.c.envFile);
    const prefix = path.basename(this.c.routeFile) + '.';
    for (const name of await fs.readdir(path.dirname(this.c.routeFile))) {
      if (!name.startsWith(prefix) || !name.endsWith('.journal.json')) continue;
      const journal = JSON.parse(await fs.readFile(path.join(path.dirname(this.c.routeFile), name), 'utf8'));
      if (journal.phase === 'pending') throw new Error('Incomplete deployment journal; explicit --recover approval required.');
    }
    const route = await fs.readFile(this.c.routeFile, 'utf8');
    if (!await this.liveHealthy()) throw new Error('Existing version must be healthy before deployment.');
    return route;
  }
  async prepare(c) {
    const name = `${c.project}-${c.sha.slice(0, 12)}`;
    // A second invocation refuses collisions; it never replaces or removes an instance.
    const existing = await this.command('docker', ['ps', '-a', '--format', '{{.Names}}']);
    if (existing.split('\n').some(n => [name + '-api', name + '-web'].includes(n))) throw new Error('Candidate already exists; inspect it manually.');
    await this.command('docker', ['network', 'inspect', c.project]); // pre-approved network must already exist
    await this.command('docker', ['pull', c.apiImage]);
    await this.command('docker', ['pull', c.webImage]);
    await this.command('docker', ['run', '-d', '--name', name + '-api', '--network', c.project,
      '--env-file', c.envFile, '-e', 'ASPNETCORE_URLS=http://+:8080',
      '-e', 'BackgroundJobs__SeatHoldCleanupEnabled=false', c.apiImage]);
    await this.command('docker', ['run', '-d', '--name', name + '-web', '--network', c.project,
      '-p', '127.0.0.1::80', '-e', `API_UPSTREAM=${name}-api:8080`, c.webImage]);
    const binding = await this.command('docker', ['port', name + '-web', '80/tcp']);
    const match = binding.match(/^127\.0\.0\.1:(\d+)$/);
    if (!match) throw new Error('Candidate must bind loopback only.');
    return { name, port: Number(match[1]) };
  }
  async probe(url) {
    try { const r = await fetch(url, { signal: AbortSignal.timeout(3000), redirect: 'error' }); await r.body?.cancel(); return r.status === 200; }
    catch { return false; }
  }
  async candidateHealthy(candidate) {
    for (let i = 0; i < 30; i++) {
      const base = `http://127.0.0.1:${candidate.port}`;
      if (await this.probe(base + '/') && await this.probe(base + '/health/ready')) return true;
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
    return false;
  }
  async replaceRoute(content) {
    const temp = `${this.c.routeFile}.${this.c.sha}.tmp`;
    await fs.writeFile(temp, content, { flag: 'wx', mode: 0o600 });
    await fs.rename(temp, this.c.routeFile);
    await this.command('nginx', ['-t']);
    await this.command('nginx', ['-s', 'reload']);
  }
  journalFile() { return `${this.c.routeFile}.${this.c.sha}.journal.json`; }
  async beforeActivate(previous, candidate) {
    await fs.writeFile(this.journalFile(), JSON.stringify({ sha: this.c.sha, phase: 'pending', previous, candidate }), { flag: 'wx', mode: 0o600 });
  }
  async complete() {
    const journal = JSON.parse(await fs.readFile(this.journalFile(), 'utf8'));
    journal.phase = 'complete'; await fs.writeFile(this.journalFile(), JSON.stringify(journal), { mode: 0o600 });
  }
  async recover() {
    const journal = JSON.parse(await fs.readFile(this.journalFile(), 'utf8'));
    if (journal.sha !== this.c.sha || journal.phase !== 'pending' || typeof journal.previous !== 'string')
      throw new Error('No matching incomplete release to recover.');
    await this.restore(journal.previous);
    if (!await this.liveHealthy()) throw new Error('Recovery health failed; operator intervention required.');
    journal.phase = 'recovered'; await fs.writeFile(this.journalFile(), JSON.stringify(journal), { mode: 0o600 });
  }
  activate(candidate) {
    return this.replaceRoute(`location / { proxy_pass http://127.0.0.1:${candidate.port}; proxy_set_header Host $host; proxy_set_header X-Forwarded-Proto $scheme; }\n`);
  }
  restore(previous) { return this.replaceRoute(previous); }
  async liveHealthy() {
    const base = this.c.liveUrl.replace(/\/$/, '');
    return await this.probe(base + '/') && await this.probe(base + '/health/ready');
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (!process.argv.includes('--execute')) throw new Error('Deployment disabled; --execute and separate approval required.');
  const c = { approved: process.env.STAGING_DEPLOYMENT_APPROVED, sha: process.env.GITHUB_SHA,
    project: process.env.STAGING_PROJECT, apiImage: process.env.STAGING_API_IMAGE, webImage: process.env.STAGING_WEB_IMAGE,
    routeFile: process.env.STAGING_ROUTE_FILE, envFile: process.env.STAGING_ENV_FILE, liveUrl: process.env.STAGING_HOME_URL };
  try {
    validateDeployment(c);
    const driver = new DockerStagingDriver(c);
    if (process.argv.includes('--recover')) { await driver.recover(); console.log('Previous release recovery verified.'); }
    else { const result = await deploy(c, driver); console.log(JSON.stringify({ sha: result.sha, previousRetained: true })); }
  }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
