import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { pathToFileURL } from 'node:url';

export function resolveCoverageBase(requested, approvedInitial, head) {
  const initialPush = /^0{40}$/.test(requested || '');
  const base = initialPush ? approvedInitial : requested;
  if (!/^[a-f\d]{40}$/.test(base || '') || /^0{40}$/.test(base) || initialPush && base === head)
    throw new Error('Explicit earlier approved coverage baseline required; no current-commit fallback.');
  return base;
}

export function parseLcov(text) {
  const files = new Map();
  for (const record of text.split('end_of_record')) {
    const filename = record.match(/^SF:(.+)$/m)?.[1]?.trim().replaceAll('\\', '/');
    if (!filename) continue;
    const key = filename.slice(filename.indexOf('frontend/src/'));
    const metric = name => Number(record.match(new RegExp(`^${name}:(\\d+)$`, 'm'))?.[1] ?? NaN);
    files.set(key, { lines: metric('LH') / metric('LF'), functions: metric('FNH') / metric('FNF') });
  }
  return files;
}
// This gate deliberately measures executable JS helpers only, not React JSX or
// C#. Missing/unmeasured files are reported, never silently claimed as covered.
export function verifyChangedCoverage(baseline, current, changed) {
  const measured = [], unmeasured = [];
  for (const file of changed) {
    if (!/^frontend\/src\/.+\.js$/.test(file)) { unmeasured.push(file); continue; }
    const now = current.get(file), before = baseline.get(file);
    if (!now || !Number.isFinite(now.lines) || !Number.isFinite(now.functions)) throw new Error(`Missing JS coverage: ${file}`);
    for (const name of ['lines', 'functions']) {
      const minimum = before?.[name] ?? 0.9;
      if (now[name] + 1e-9 < minimum) throw new Error(`Changed JS ${name} coverage regressed: ${file}`);
    }
    measured.push({ file, lines: now.lines, functions: now.functions, baseline: before ?? null });
  }
  if (!measured.length) throw new Error('No changed executable JS measured.');
  return { scope: 'changed frontend JS helpers; not full DoD coverage', measured, unmeasured };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [base, current, changed] = process.argv.slice(2);
  try {
    for (const file of [base, current, changed]) if (!file || !path.isAbsolute(file)) throw new Error('Explicit coverage artifact paths required.');
    console.log(JSON.stringify(verifyChangedCoverage(parseLcov(fs.readFileSync(base, 'utf8')), parseLcov(fs.readFileSync(current, 'utf8')), JSON.parse(fs.readFileSync(changed, 'utf8'))), null, 2));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
