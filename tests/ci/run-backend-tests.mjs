import { spawnSync } from 'node:child_process';
import { readFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { pathToFileURL } from 'node:url';
import { fileURLToPath } from 'node:url';

export const phases = [
  ['api', 'FullyQualifiedName!~RealBrowser&FullyQualifiedName!~Burst200Requests100Seats&Category!=Performance', 372],
  ['concurrency', 'FullyQualifiedName~Burst200Requests100Seats', 1, 10],
  ['browser-s13', 'FullyQualifiedName~RealBrowser_SelectFive', 1],
  ['browser-s08-s11', 'FullyQualifiedName~RealBrowser_PublicPagination', 1],
  ['latency', 'Category=Performance', 2],
];
export function verifyCounters(xml, minimum) {
  const tag = xml.match(/<Counters\b([^>]+)\/?\s*>/);
  if (!tag) throw new Error('Test results missing counters.');
  const attributes = Object.fromEntries([...tag[1].matchAll(/(\w+)="(\d+)"/g)].map(m => [m[1], Number(m[2])]));
  if (!(attributes.total >= minimum) || attributes.executed !== attributes.total || attributes.passed !== attributes.total
      || attributes.failed !== 0 || attributes.notExecuted !== 0 || attributes.error !== 0 || attributes.aborted !== 0)
    throw new Error('Missing, failed, skipped or aborted required tests.');
  return attributes.total;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
  const results = path.resolve(process.env.RUNNER_TEMP || path.join(repo, 'EventTicketing.Tests/TestResults/ci'), 'backend-results');
  mkdirSync(results, { recursive: true });
  const config = process.env.CI_BUILD_CONFIGURATION || 'Release';
  if (!['Debug', 'Release'].includes(config)) throw new Error('Unsupported test build configuration.');
  for (const [name, filter, minimum, repeats = 1] of phases) {
   for (let iteration = 1; iteration <= repeats; iteration++) {
    const file = `ci-${name}-${iteration}-${Date.now()}.trx`;
    const run = spawnSync('dotnet', ['test', 'EventTicketing.Tests/EventTicketing.Tests.csproj', '-c', config, '--no-build', '--no-restore',
      '--filter', filter, '--logger', `trx;LogFileName=${file}`, '--results-directory', results,
      ...(name === 'api' ? ['--collect:XPlat Code Coverage'] : [])], { cwd: repo, stdio: 'inherit', timeout: 600000 });
    if (run.status !== 0 || run.error) { process.exitCode = 1; break; }
    try { console.log(`${name}: ${verifyCounters(readFileSync(path.join(results, file), 'utf8'), minimum)} required tests passed.`); }
    catch (error) { console.error(error.message); process.exitCode = 1; break; }
   }
   if (process.exitCode) break;
  }
}
