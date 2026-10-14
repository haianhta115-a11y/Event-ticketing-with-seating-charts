import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readWorkflows, verify, parseWorkflow } from './verify-workflows.mjs';
import { qualitySucceeded } from './quality-gate.mjs';

test('S01.1 builds and tests both applications on merge/PR using safe isolated fixtures', () => {
  const [ci] = readWorkflows();
  assert.ok(ci.jobs.frontend, 'Frontend quality job is missing.');
  assert.doesNotThrow(() => verify(...readWorkflows()));
});
test('S01.2 publication cannot bypass same-commit backend/frontend quality', () => {
  const [ci, delivery] = readWorkflows();
  assert.equal(delivery.jobs.quality?.uses, './.github/workflows/ci.yml');
  assert.doesNotThrow(() => verify(ci, delivery));
});
test('YAML syntax and duplicate keys fail validation', () => {
  for (const source of ['jobs: [', 'jobs: {}\njobs: {}']) assert.throws(() => parseWorkflow(source));
});
for (const status of ['failure', 'cancelled', 'skipped', undefined, 'unknown']) {
  for (const job of ['backend', 'frontend']) test(`${job} ${status} blocks the required gate`, () => {
    assert.equal(qualitySucceeded({ backend: 'success', frontend: 'success', [job]: status }), false);
  });
}
test('both checks succeeding is the only successful aggregate', () => {
  assert.equal(qualitySucceeded({ backend: 'success', frontend: 'success' }), true);
});
test('a real failing test process propagates through the exact CI gate CLI', () => {
  const failedTest = spawnSync(process.execPath, ['-e', "require('node:assert/strict').equal(1, 2)"], { encoding: 'utf8' });
  assert.notEqual(failedTest.status, 0);
  const gate = spawnSync(process.execPath, [fileURLToPath(new URL('./quality-gate.mjs', import.meta.url))],
    { encoding: 'utf8', env: { ...process.env, BACKEND_RESULT: failedTest.status === 0 ? 'success' : 'failure', FRONTEND_RESULT: 'success' } });
  assert.equal(gate.status, 1); assert.match(gate.stdout, /Delivery blocked/);
  let deliveryCalls = 0; if (gate.status === 0) deliveryCalls++;
  assert.equal(deliveryCalls, 0); // harmless local probe, not a real GitHub deployment claim
});

const mutations = {
  'continue-on-error': (ci) => { ci.jobs.backend['continue-on-error'] = true; },
  'step error suppression': (ci) => { ci.jobs.frontend.steps[0]['continue-on-error'] = true; },
  'missing frontend gate': (ci) => { ci.jobs['quality-gate'].needs = ['backend']; },
  'cycle': (ci) => { ci.jobs.backend.needs = ['quality-gate']; },
  'unknown job': (ci) => { ci.jobs.backend.needs = ['missing']; },
  'shared database': (ci) => { ci.jobs.backend.env.BATCH1A1_POSTGRES = 'Host=127.0.0.1;Database=event_ticketing'; },
  'wrong Redis partition': (ci) => { ci.jobs.backend.env.BATCH1A1_REDIS = '127.0.0.1:6379,defaultDatabase=0'; },
  'mutable action reference': (ci) => { ci.jobs.frontend.steps[0].uses = 'actions/checkout@main'; },
  'historical migration invocation': (ci) => { ci.jobs.backend.steps.push({ run: 'dotnet ef database update' }); },
  'missing develop PR': (ci) => { ci.on.pull_request.branches = ['main']; },
  'unguarded candidate': (_, d) => { d.jobs.candidate.needs = []; },
  'always publication': (_, d) => { d.jobs.publish.if = '${{ always() }}'; },
  'automatic publication': (_, d) => { d.on.workflow_dispatch.inputs.publish_image.default = true; },
  'secret inheritance': (_, d) => { d.jobs.quality.secrets = 'inherit'; },
};
for (const [name, mutate] of Object.entries(mutations)) test(`workflow guard rejects ${name}`, () => {
  const [ci, delivery] = readWorkflows(); mutate(ci, delivery); assert.throws(() => verify(ci, delivery));
});
