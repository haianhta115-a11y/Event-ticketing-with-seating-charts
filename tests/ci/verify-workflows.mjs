import { readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import process from 'node:process';
import { parseDocument } from 'yaml';

export const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
export function parseWorkflow(source) {
  const document = parseDocument(source, { uniqueKeys: true });
  if (document.errors.length) throw new Error('Workflow YAML syntax/duplicate-key error.');
  const data = document.toJS();
  if (!data || typeof data !== 'object' || !data.jobs) throw new Error('Workflow jobs are missing.');
  return data;
}
export function readWorkflows(root = repo) {
  return ['ci.yml', 'deploy-staging.yml'].map(name => parseWorkflow(readFileSync(path.join(root, '.github/workflows', name), 'utf8')));
}
const list = value => value == null ? [] : Array.isArray(value) ? value : [value];
const runText = job => (job?.steps || []).map(step => step.run || '').join('\n');
const requireCheck = (condition, message) => { if (!condition) throw new Error(message); };
export function verify(ci, delivery) {
  for (const workflow of [ci, delivery]) {
    requireCheck(workflow.permissions?.contents === 'read', 'Default contents permission must be read-only.');
    requireCheck(!workflow.on?.pull_request_target, 'Privileged PR execution is forbidden.');
    const visit = (name, chain = []) => {
      requireCheck(!!workflow.jobs[name], 'Unknown job dependency.');
      requireCheck(!chain.includes(name), 'Cyclic job dependencies.');
      for (const dep of list(workflow.jobs[name].needs)) visit(dep, [...chain, name]);
    };
    for (const [name, job] of Object.entries(workflow.jobs)) {
      visit(name);
      requireCheck(!job['continue-on-error'], 'Required job cannot continue on error.');
      for (const step of job.steps || []) {
        requireCheck(!step['continue-on-error'], 'Required step cannot continue on error.');
        requireCheck(!step.if || !/always\(|failure\(/.test(step.if), 'Steps cannot bypass successful prior checks.');
        if (step.uses) requireCheck(/^[\w-]+\/[\w-]+@[a-f\d]{40}$/.test(step.uses), 'Actions must use immutable SHAs.');
      }
      requireCheck(!/\|\|\s*(true|exit\s+0)|dotnet\s+ef|database\s+update|compose\s+down|system\s+prune/i.test(runText(job)), 'Unsafe or failure-swallowing command.');
    }
  }
  for (const branch of ['main', 'develop', 'staging']) {
    requireCheck(ci.on?.push?.branches.includes(branch), 'CI push trigger missing.');
    requireCheck(ci.on?.pull_request?.branches.includes(branch), 'CI PR trigger missing.');
  }
  requireCheck(Object.hasOwn(ci.on, 'workflow_call'), 'Reusable quality workflow missing.');
  const backend = ci.jobs.backend, frontend = ci.jobs.frontend, gate = ci.jobs['quality-gate'];
  requireCheck(runText(backend).includes('dotnet build EventTicketing.slnx') && runText(backend).includes('dotnet format'), 'Backend build/format required.');
  requireCheck(runText(backend).includes('run-backend-tests.mjs') && runText(backend).includes('EnvironmentSafetyChecks'), 'Full backend and environment checks required.');
  requireCheck(runText(backend).includes('playwright@1.62.1') && runText(backend).includes('install --with-deps chrome'), 'Real browser runtime required.');
  requireCheck(backend.env.BATCH1A1_POSTGRES.includes('Host=127.0.0.1') && backend.env.BATCH1A1_POSTGRES.includes('Database=event_ticketing_test_'), 'CI PostgreSQL must be isolated/loopback.');
  requireCheck(backend.env.BATCH1A1_REDIS.includes('127.0.0.1') && backend.env.BATCH1A1_REDIS.includes('defaultDatabase=15'), 'CI Redis must use DB15.');
  requireCheck(backend.services.postgres.image === 'postgres:15-alpine' && backend.services.redis.image === 'redis:7-alpine', 'Disposable CI services required.');
  requireCheck(!backend.services.postgres.volumes && !backend.services.redis.volumes, 'CI cannot mount persistent volumes.');
  requireCheck(backend.services.postgres.ports.includes('127.0.0.1:5432:5432') && backend.services.redis.ports.includes('127.0.0.1:6379:6379'), 'CI service ports must bind loopback.');
  for (const command of ['npm ci', 'npm run lint', 'node --test tests/*.test.mjs', 'npm run build', 'npm test --prefix tests/ci', 'npm run check --prefix tests/ci'])
    requireCheck(runText(frontend).includes(command), 'Frontend build/test/lint/workflow gate missing.');
  requireCheck(JSON.stringify(list(gate.needs).sort()) === JSON.stringify(['backend', 'frontend']), 'Required gate must join both jobs.');
  requireCheck(gate.if === '${{ always() }}' && runText(gate).includes('quality-gate.mjs'), 'Fail-closed aggregate gate missing.');
  requireCheck(gate.steps.some(step => step.env?.BACKEND_RESULT === '${{ needs.backend.result }}' && step.env?.FRONTEND_RESULT === '${{ needs.frontend.result }}'), 'Gate must use actual job results.');
  requireCheck(delivery.jobs.quality?.uses === './.github/workflows/ci.yml', 'Delivery must run same-commit quality workflow.');
  requireCheck(!delivery.jobs.quality.secrets, 'CI must not inherit deployment secrets.');
  requireCheck(list(delivery.jobs.candidate.needs).includes('quality') && !delivery.jobs.candidate.if, 'Candidate packaging must require successful quality.');
  requireCheck(delivery.on.workflow_dispatch.inputs.publish_image.default === false, 'Publication must be opt-in/code-only.');
  const publish = delivery.jobs.publish;
  requireCheck(list(publish.needs).includes('quality') && list(publish.needs).includes('candidate'), 'Publication must require quality and packaging.');
  requireCheck(publish.if.includes("github.event_name == 'workflow_dispatch'") && publish.if.includes('inputs.publish_image == true') && !/always\(|failure\(/.test(publish.if), 'Automatic/bypassed publication forbidden.');
  requireCheck(publish.if.includes('refs/heads/main') && publish.if.includes('refs/heads/staging'), 'Publication must reject arbitrary branches.');
  requireCheck(!delivery.jobs.deploy && !Object.values(delivery.jobs).some(job => job.environment), 'Unconfigured staging transport/environment cannot be fabricated.');
  return { workflows: 2, enforcedJobs: Object.keys(ci.jobs).length + Object.keys(delivery.jobs).length, publicationDefault: false };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { console.log(JSON.stringify(verify(...readWorkflows()))); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
