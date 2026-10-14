import process from 'node:process';
import { pathToFileURL } from 'node:url';

// Use the same fail-closed decision in CI and the offline failure-injection tests.
export const qualitySucceeded = results => results.backend === 'success' && results.frontend === 'success';
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const passed = qualitySucceeded({ backend: process.env.BACKEND_RESULT, frontend: process.env.FRONTEND_RESULT });
  console.log(passed ? 'Required quality checks passed.' : 'Delivery blocked: every required job must succeed.');
  process.exitCode = passed ? 0 : 1;
}
