import { spawn } from 'node:child_process';
import process from 'node:process';

export function localBrowserServer() {
  const target = new URL(process.env.VITE_API_PROXY_TARGET || '');
  if (target.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(target.hostname)
    || target.username || target.password || target.search || target.hash) throw new Error('Explicit local fixture API required.');
  const vite = spawn(process.execPath, ['node_modules/vite/bin/vite.js', '--host', '127.0.0.1', '--port', '0'],
    { cwd: process.cwd(), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const ready = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Vite startup timeout')), 20000);
    let output = '';
    vite.once('exit', () => { clearTimeout(timer); reject(new Error('Vite startup failed')); });
    vite.stdout.on('data', data => {
      output += data.toString().replace(/\x1b\[[0-9;]*m/g, '');
      const match = output.match(/http:\/\/127\.0\.0\.1:(\d+)\//);
      if (match) { clearTimeout(timer); resolve(match[0].replace(/\/$/, '')); }
    });
  });
  return { vite, ready, api: target.origin + '/api' };
}
