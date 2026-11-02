import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';

const root = path.resolve(process.argv[2]);
const release = JSON.parse(await readFile(path.join(root, 'release.json'), 'utf8'));
for (const [file, hash] of Object.entries(release.files)) {
  if (!path.resolve(root, file).startsWith(root + path.sep)) throw new Error(`Invalid payload path: ${file}`);
  if (createHash('sha256').update(await readFile(path.join(root, file))).digest('hex') !== hash)
    throw new Error(`Hash mismatch: ${file}`);
}
const server = spawn(path.join(root, 'node/node.exe'), [path.join(root, 'server/src/index.js')],
  { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
const pending = new Map();
let buffer = '', errors = '';
server.stderr.on('data', chunk => errors += chunk);
server.stdout.on('data', chunk => {
  buffer += chunk;
  while (buffer.includes('\n')) {
    const end = buffer.indexOf('\n');
    const value = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
    pending.get(value.id)?.(value);
  }
});
server.on('error', error => { for (const resolve of pending.values()) resolve({ error }); });
let id = 0;
async function request(method, params) {
  const current = ++id;
  let timer;
  try {
    return await new Promise((resolve, reject) => {
      pending.set(current, value => value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result));
      timer = setTimeout(() => reject(new Error(`MCP timeout: ${method}; ${errors}`)), 15000);
      server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method, params }) + '\n');
    });
  } finally { clearTimeout(timer); pending.delete(current); }
}
try {
  const info = await request('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'BotPlus-package-check', version: '1.0.0' } });
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const list = await request('tools/list', {});
  if (list.tools.length < 90) throw new Error('Missing MCP tools');
  console.log(JSON.stringify({ verifiedFiles: Object.keys(release.files).length, mcpVersion: info.serverInfo.version, tools: list.tools.length }));
} finally { server.kill(); }
