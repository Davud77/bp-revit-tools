import { readFile, writeFile, mkdir, rename, copyFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const name = 'bp_revit_ai';
const options = root => ({ command: path.join(root, 'node', 'node.exe'), args: [path.join(root, 'server', 'src', 'index.js')] });

// Replace only a top-level key in the selected table, including multiline arrays.
function assignmentEnd(text, start) {
  let quote = '', escaped = false, depth = 0, comment = false;
  for (let i = start; i < text.length; i++) {
    const c = text[i];
    if (comment) { if (c === '\n') { comment = false; if (depth === 0) return i; } continue; }
    if (quote) {
      if (escaped) { escaped = false; continue; }
      if (c === '\\' && quote === '"') { escaped = true; continue; }
      if (c === quote) quote = '';
      continue;
    }
    if (c === '"' || c === "'") quote = c;
    else if (c === '#') comment = true;
    else if (c === '[' || c === '{') depth++;
    else if (c === ']' || c === '}') depth--;
    else if (c === '\n' && depth === 0) return i;
  }
  if (quote || depth !== 0) throw new Error('Не завершено значение MCP в config.toml; исходный файл сохранён.');
  return text.length;
}

function editKey(block, key, value) {
  const match = new RegExp(`^${key}\\s*=`, 'm').exec(block);
  if (!match) return block.trimEnd() + `\n${key} = ${value}\n`;
  const end = assignmentEnd(block, match.index + match[0].length);
  return block.slice(0, match.index) + `${key} = ${value}` + block.slice(end);
}

function packagedEntry(entry, ownPrefix) {
  if (typeof entry.command !== 'string' || !isChild(entry.command, ownPrefix) ||
      !Array.isArray(entry.args) || entry.args.length !== 1 || typeof entry.args[0] !== 'string') return false;
  const release = path.dirname(path.dirname(entry.command));
  const expected = options(release);
  return path.relative(expected.command, entry.command) === '' && path.relative(expected.args[0], entry.args[0]) === '';
}

function retargetCodexName(text, ownPrefix) {
  const candidates = [];
  for (const section of text.matchAll(/^\[mcp_servers\.([\w-]+)\][^\n]*\n/gm)) {
    const remainder = text.slice(section.index + section[0].length);
    const nextHeader = remainder.search(/^\[/m);
    const block = nextHeader === -1 ? remainder : remainder.slice(0, nextHeader);
    const command = /^command\s*=\s*("(?:\\.|[^"\\])*"|'[^']*')/m.exec(block)?.[1];
    const args = /^args\s*=/m.exec(block);
    if (!command || !args) continue;
    const start = args.index + args[0].length;
    try {
      const entry = { command: command.startsWith('"') ? JSON.parse(command) : command.slice(1, -1),
        args: JSON.parse(block.slice(start, assignmentEnd(block, start))) };
      if (packagedEntry(entry, ownPrefix)) candidates.push(section[1]);
    } catch { /* Only unambiguous packaged entries can be renamed. */ }
  }
  if (candidates.length !== 1) return text;
  return text.replace(new RegExp(`^\\[mcp_servers\\.${candidates[0]}(?=[.\\]])`, 'gm'), `[mcp_servers.${name}`);
}

// mode: false — add or repair; true — remove own entry; 'retarget' — move an existing own entry to this release only.
export function editCodex(text, root, mode = false) {
  const newline = text.includes('\r\n') ? '\r\n' : '\n';
  let normalized = text.replaceAll('\r\n', '\n');
  const ownPrefix = path.dirname(path.dirname(root)); // Application directory, any installed version.
  const header = /^\[mcp_servers\.bp_revit_ai\][^\n]*\n?/m;
  if (mode === 'retarget' && !header.test(normalized)) normalized = retargetCodexName(normalized, ownPrefix);
  const table = header.exec(normalized);
  const next = table ? /^\[/m.exec(normalized.slice(table.index + table[0].length)) : null;
  const end = table ? (next ? table.index + table[0].length + next.index : normalized.length) : normalized.length;
  let block = table ? normalized.slice(table.index, end) : `[mcp_servers.${name}]\n`;
  if (mode) {
    const command = /^command\s*=\s*("(?:\\.|[^"\\])*"|'[^']*')/m.exec(block)?.[1];
    const decoded = command?.startsWith('"') ? JSON.parse(command) : command?.slice(1, -1);
    if (!table || !decoded || !isChild(decoded, ownPrefix)) return text;
    if (mode === true) return (normalized.slice(0, table.index) + normalized.slice(end)).replaceAll('\n', newline);
  }
  const config = options(root);
  block = editKey(block, 'command', JSON.stringify(config.command));
  block = editKey(block, 'args', JSON.stringify(config.args));
  if (!/^default_tools_approval_mode\s*=/m.test(block)) block = editKey(block, 'default_tools_approval_mode', '"writes"');
  if (!/^tool_timeout_sec\s*=/m.test(block)) block = editKey(block, 'tool_timeout_sec', '320');
  return (table ? normalized.slice(0, table.index) + block + normalized.slice(end)
    : normalized.trimEnd() + (normalized.trim() ? '\n\n' : '') + block).replaceAll('\n', newline);
}

function isChild(value, root) {
  const relative = path.relative(path.resolve(root), path.resolve(value));
  return relative !== '' && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

export function editClaude(text, root, mode = false) {
  if (mode === 'retarget' && !text.trim()) return text;
  const value = text.trim() ? JSON.parse(text) : {};
  if (value === null || Array.isArray(value) || typeof value !== 'object') throw new Error('Некорректная конфигурация Claude; файл сохранён.');
  const servers = value.mcpServers ?? {};
  if (servers === null || Array.isArray(servers) || typeof servers !== 'object') throw new Error('Некорректный mcpServers Claude; файл сохранён.');
  const ownPrefix = path.dirname(path.dirname(root));
  let renamed = false;
  if (mode === 'retarget' && !Object.hasOwn(servers, name)) {
    const candidates = Object.keys(servers).filter(key => servers[key] && packagedEntry(servers[key], ownPrefix));
    if (candidates.length === 1) {
      const [previous] = candidates;
      servers[name] = servers[previous];
      delete servers[previous];
      renamed = true;
    }
  }
  const owned = typeof servers[name]?.command === 'string' && isChild(servers[name].command, ownPrefix);
  if (mode && !owned) return text;
  const before = JSON.stringify(servers[name]);
  if (mode === true) delete servers[name];
  else servers[name] = { ...servers[name], ...options(root) };
  if (mode === 'retarget' && !renamed && JSON.stringify(servers[name]) === before) return text;
  value.mcpServers = servers;
  return JSON.stringify(value, null, 2) + '\n';
}

async function update(file, transform, backupDir) {
  const original = existsSync(file) ? await readFile(file, 'utf8') : '';
  const modified = transform(original);
  if (modified === original) return;
  await mkdir(path.dirname(file), { recursive: true });
  if (original) {
    await mkdir(backupDir, { recursive: true });
    await copyFile(file, path.join(backupDir, path.basename(file) + '.' + randomUUID() + '.bak'));
  }
  const temporary = file + '.' + randomUUID() + '.tmp';
  await writeFile(temporary, modified, 'utf8'); await rename(temporary, file);
}

async function main() {
  const args = process.argv.slice(2);
  const root = path.dirname(fileURLToPath(import.meta.url));
  const backupDir = args.find(a => a.startsWith('--backup-dir='))?.slice(13) ?? path.join(root, 'backups');
  const remove = args.includes('--remove') ? true : args.includes('--retarget') ? 'retarget' : false;
  if (args.includes('--examples')) {
    const dir = path.join(root, 'connection'); await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, 'MCP-config.json'), JSON.stringify({ mcpServers: { [name]: options(root) } }, null, 2));
    await writeFile(path.join(dir, 'Codex-MCP.toml'), editCodex('', root));
  }
  if (args.includes('--codex')) {
    const file = path.join(os.homedir(), '.codex', 'config.toml');
    if (!remove || existsSync(file)) await update(file, text => editCodex(text, root, remove), backupDir);
  }
  if (args.includes('--claude')) {
    const file = path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'Claude', 'claude_desktop_config.json');
    if (!remove || existsSync(file)) await update(file, text => editClaude(text, root, remove), backupDir);
  }
  process.stdout.write(JSON.stringify({ configured: true, removed: remove === true, retargeted: remove === 'retarget', codex: args.includes('--codex'), claude: args.includes('--claude') }) + '\n');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch(error => {
  process.stderr.write(error.message + '\n'); process.exitCode = 1;
});
