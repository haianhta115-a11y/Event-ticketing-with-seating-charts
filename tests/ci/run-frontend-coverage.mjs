import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { parseLcov, verifyChangedCoverage, resolveCoverageBase, parseApprovedInitialBaseline } from './coverage.mjs';
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo, encoding: 'utf8' }).trim();
const requested = process.env.COVERAGE_BASE_SHA;
// Initial pushes have no previous ref. Use only the existing, measured and
// version-controlled earlier baseline, never HEAD or an inferred parent.
const approvedInitial = /^0{40}$/.test(requested || '')
  ? process.env.COVERAGE_INITIAL_BASE_SHA || parseApprovedInitialBaseline(fs.readFileSync(path.join(repo, 'tests/ci/coverage-baseline.json'), 'utf8'))
  : process.env.COVERAGE_INITIAL_BASE_SHA;
const baseline = resolveCoverageBase(requested, approvedInitial, head);
if (execFileSync('git', ['rev-parse', '--verify', baseline + '^{commit}'], { cwd: repo, encoding: 'utf8' }).trim() !== baseline)
  throw new Error('Coverage baseline not available locally.');
if (spawnSync('git', ['merge-base', '--is-ancestor', baseline, 'HEAD'], { cwd: repo }).status !== 0)
  throw new Error('Coverage baseline must be an ancestor of the current code.');
const evidence = fs.mkdtempSync(path.join(process.env.RUNNER_TEMP || os.tmpdir(), 'finalfix-coverage-'));
const archive = path.join(evidence, 'baseline.tar');
fs.writeFileSync(archive, execFileSync('git', ['archive', '--format=tar', baseline, 'frontend'], { cwd: repo, maxBuffer: 100 * 1024 * 1024 }));
const exported = path.join(evidence, 'baseline'); fs.mkdirSync(exported);
execFileSync('tar', ['-xf', archive, '-C', exported]);
const run = (command, args, cwd) => {
  const windowsNpm = process.platform === 'win32' && command === 'npm';
  if (windowsNpm && args.join(' ') !== 'ci --ignore-scripts') throw new Error('Only fixed npm install arguments permitted.');
  const result = spawnSync(windowsNpm ? process.env.ComSpec : command,
    windowsNpm ? ['/d', '/s', '/c', 'npm ci --ignore-scripts'] : args, { cwd, stdio: 'inherit', timeout: 300000 });
  if (result.status !== 0 || result.error) throw new Error('Coverage preparation/test failed.');
};
run('npm', ['ci', '--ignore-scripts'], path.join(exported, 'frontend'));
for (const [name, root] of [['baseline', exported], ['current', repo]]) {
  const tests = fs.readdirSync(path.join(root, 'frontend/tests')).filter(n => n.endsWith('.test.mjs')).map(n => `frontend/tests/${n}`);
  run(process.execPath, ['--test', '--experimental-test-coverage', '--test-coverage-include=frontend/src/**/*.js',
    '--test-reporter=lcov', `--test-reporter-destination=${path.join(evidence, name + '.lcov')}`,
    '--test-reporter=tap', `--test-reporter-destination=${path.join(evidence, name + '.tap')}`, ...tests], root);
}
const changed = execFileSync('git', ['diff', '--name-only', '--diff-filter=ACM', baseline], { cwd: repo, encoding: 'utf8' }).trim().split(/\r?\n/);
// Local uncommitted new files are not present in git diff; include them explicitly.
changed.push(...execFileSync('git', ['ls-files', '--others', '--exclude-standard'], { cwd: repo, encoding: 'utf8' }).trim().split(/\r?\n/));
const baseMetrics = parseLcov(fs.readFileSync(path.join(evidence, 'baseline.lcov'), 'utf8'));
const source = [...new Set(changed.filter(n => n.startsWith('frontend/src/')))];
// A documentation/JSX-only change must still preserve ALL previously measured JS
// helpers. No empty/same-commit shortcut or missing-baseline success path.
const measuredSource = source.some(n => n.endsWith('.js')) ? source : [...baseMetrics.keys(), ...source];
const report = verifyChangedCoverage(baseMetrics, parseLcov(fs.readFileSync(path.join(evidence, 'current.lcov'), 'utf8')), measuredSource);
fs.writeFileSync(path.join(evidence, 'comparison.json'), JSON.stringify({ baseline, ...report }, null, 2), { flag: 'wx' });
console.log(JSON.stringify({ evidence, measured: report.measured.length, unmeasured: report.unmeasured, scope: report.scope }));
