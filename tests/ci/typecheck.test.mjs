import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { repo } from './verify-workflows.mjs';
test('full frontend checkJs remains enabled and an actual incompatible assignment fails tsc', () => {
  const config = JSON.parse(fs.readFileSync(path.join(repo, 'frontend/tsconfig.json'), 'utf8'));
  assert.equal(config.compilerOptions.checkJs, true); assert.equal(config.compilerOptions.allowJs, true);
  assert.equal(config.compilerOptions.noEmit, true); assert.ok(config.include.some(p => p.includes('src')));
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'finalfix-typecheck-'));
  const fixture = path.join(directory, 'negative.js');
  fs.writeFileSync(fixture, '/** @type {number} */\nconst fixtureNumber = "incompatible";\n', { flag: 'wx' });
  const result = spawnSync(process.execPath, [path.join(repo, 'frontend/node_modules/typescript/bin/tsc'),
    '--allowJs', '--checkJs', '--noEmit', '--skipLibCheck', '--lib', 'es2022', fixture], { encoding: 'utf8' });
  assert.equal(result.status, 2); assert.match(result.stdout, /TS2322/);
});
