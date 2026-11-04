import { cp, mkdir, writeFile, readFile, readdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { createHash } from 'node:crypto';

const [repo, stage] = process.argv.slice(2);
if (!repo || !stage) throw new Error('Usage: package.mjs repo stage');
const noticeCache = path.resolve(repo, '../../Выходные данные/Сборочные данные/licenses');
async function notice(name, url) {
  const file = path.join(noticeCache, name);
  const hash = data => createHash('sha256').update(data).digest('hex');
  try {
    const meta = JSON.parse(await readFile(file + '.json', 'utf8'));
    const data = await readFile(file);
    if (meta.url === url && meta.sha256 === hash(data) && data.length > 100) return data;
  } catch {}
  const response = await fetch(url, { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`Could not obtain required license: ${url}`);
  const data = Buffer.from(await response.arrayBuffer());
  if (data.length < 100) throw new Error(`Empty license: ${url}`);
  await mkdir(noticeCache, { recursive: true });
  await writeFile(file, data);
  await writeFile(file + '.json', JSON.stringify({ url, sha256: hash(data) }));
  return data;
}
await mkdir(stage, { recursive: true });
await cp(path.join(repo, 'server', 'src'), path.join(stage, 'server', 'src'), { recursive: true });
await cp(path.join(repo, 'server', 'package.json'), path.join(stage, 'server', 'package.json'));
const require = createRequire(path.join(repo, 'server', 'package.json'));
const copied = new Set();
async function copyPackage(name, resolver = require) {
  if (copied.has(name)) return; copied.add(name);
  let entry = resolver.resolve(name);
  let dir = path.dirname(entry);
  while (true) {
    try { if (JSON.parse(await readFile(path.join(dir, 'package.json'), 'utf8')).name === name) break; } catch { }
    const parent = path.dirname(dir); if (parent === dir) throw new Error(`Package root not found: ${name}`); dir = parent;
  }
  const pkg = JSON.parse(await readFile(path.join(dir, 'package.json'), 'utf8'));
  await cp(dir, path.join(stage, 'server', 'node_modules', name), { recursive: true, dereference: true });
  const childResolver = createRequire(path.join(dir, 'package.json'));
  for (const dependency of Object.keys(pkg.dependencies ?? {})) await copyPackage(dependency, childResolver);
}
const pkg = JSON.parse(await readFile(path.join(repo, 'server', 'package.json'), 'utf8'));
for (const name of Object.keys(pkg.dependencies)) await copyPackage(name);
await mkdir(path.join(stage, 'node'), { recursive: true });
await cp(process.execPath, path.join(stage, 'node', 'node.exe'));
await writeFile(path.join(stage, 'node', 'LICENSE'), await notice(`node-${process.version}-LICENSE`, `https://raw.githubusercontent.com/nodejs/node/${process.version}/LICENSE`));
await mkdir(path.join(stage, 'licenses'), { recursive: true });
for (const name of ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']) {
  await writeFile(path.join(stage, 'licenses', 'dotnet-' + name), await notice('dotnet-v8.0.0-' + name, `https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/${name}`));
}
await cp(path.join(repo, 'installer', 'configure-mcp.mjs'), path.join(stage, 'configure-mcp.mjs'));
await cp(path.join(repo, 'LICENSE'), path.join(stage, 'LICENSE'));
await cp(path.join(repo, 'installer', 'INSTALLATION.md'), path.join(stage, 'INSTALLATION.md'));
await mkdir(path.join(stage, 'plugin'), { recursive: true });
await cp(path.join(repo, '../../Выходные данные/Сборочные данные/dotnet/WEAM.Revit.AI', 'bin', 'Release', 'net8.0-windows', 'WEAM.Revit.AI.dll'), path.join(stage, 'plugin', 'WEAM.Revit.AI.dll'));
async function verify(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    if (entry.isSymbolicLink()) throw new Error('Payload must not contain symlinks');
    if (/^(RevitAPI(UI)?\.dll|bridge\.token|settings\.json)$/i.test(entry.name) || /\.(rvt|rfa|rte|rft)$/i.test(entry.name)) throw new Error('Private or Autodesk file in payload');
    if (entry.isDirectory()) await verify(path.join(dir, entry.name));
  }
}
await verify(stage);
await writeFile(path.join(stage, 'release.json'), JSON.stringify({ version: pkg.version, node: process.version, packages: [...copied], target: 'Revit 2026 / Windows x64' }, null, 2));
process.stdout.write(JSON.stringify({ packaged: true, node: process.version, packages: copied.size }) + '\n');
