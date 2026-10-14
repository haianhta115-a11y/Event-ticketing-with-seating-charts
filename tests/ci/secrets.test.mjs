import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { repo } from './verify-workflows.mjs';
import path from 'node:path';
test('current application configuration contains environment references, not credential literals', () => {
  for (const dir of ['EventTicketing.Api', 'Event-ticketing-with-seating-charts-main/EventTicketing.Api']) {
    for (const name of fs.readdirSync(path.join(repo, dir)).filter(n => /^appsettings.*\.json$/.test(n))) {
      const config = JSON.parse(fs.readFileSync(path.join(repo, dir, name), 'utf8'));
      const check = (value, field) => assert.equal(!value || /\$\{[A-Z_]+\}/.test(value), true, `${dir}/${name}: credential reference required for ${field}`);
      check(config.ConnectionStrings?.DefaultConnection, 'database'); check(config.Jwt?.Key, 'JWT'); check(config.Jwt?.SigningKey, 'JWT');
      const visit = object => { for (const [key, value] of Object.entries(object || {})) {
        if (value && typeof value === 'object') visit(value);
        else if (/^(password|secret|signingkey)$/i.test(key)) check(value, key);
      } };
      visit(config);
    }
  }
});
test('production bootstrap never invokes migrations or default-user seeders', () => {
  const source = fs.readFileSync(path.join(repo, 'EventTicketing.Api/Program.cs'), 'utf8');
  assert.equal(/\b(?:Migrate(?:Async)?|EnsureCreated(?:Async)?|Seeder\.Seed\w*)\s*\(/.test(source), false);
});
