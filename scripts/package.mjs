import { cp, mkdir, rename, rm, writeFile, readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const [repo, stage, python] = process.argv.slice(2);
if (!repo || !stage || !python) throw new Error('Usage: package.mjs repo stage python');
function run(command, args) {
  const result = spawnSync(command, args, { stdio: 'inherit', windowsHide: true });
  if (result.status !== 0) throw result.error ?? new Error(`${command} failed: ${result.status}`);
}
run(process.execPath, [path.join(repo, 'modules/codex-mcp/installer/package.mjs'), path.join(repo, 'modules/codex-mcp'), stage]);
await mkdir(path.join(stage, 'modules/codex-mcp'), { recursive: true });
await rename(path.join(stage, 'plugin'), path.join(stage, 'modules/codex-mcp/plugin'));
await cp(path.join(repo, 'installer/configure-mcp.mjs'), path.join(stage, 'configure-mcp.mjs'));
await cp(path.join(repo, 'docs/INSTALLATION.md'), path.join(stage, 'INSTALLATION.md'));
await cp(path.join(repo, 'src/BotPlus.RT/bin/Release/net8.0-windows/BotPlus.RT.dll'), path.join(stage, 'BotPlus.RT.dll'));
await mkdir(path.join(stage, 'modules/ov'), { recursive: true });
await cp(path.join(repo, 'modules/ov/HeatLossViewer/bin/Release/net8.0-windows/HeatLossViewer.dll'), path.join(stage, 'modules/ov/HeatLossViewer.dll'));
const ac = path.join(stage, 'modules/ac2revit');
await mkdir(path.join(ac, 'dist/Revit2026'), { recursive: true });
await cp(path.join(repo, 'modules/ac2revit/revit/Ac2Revit/bin/Release/net8.0-windows/Ac2Revit.dll'), path.join(ac, 'dist/Revit2026/Ac2Revit.dll'));
const filter = file => !/[/\\](__pycache__|bin|obj)([/\\]|$)|\.pyc$/i.test(file);
for (const dir of ['tools', 'exporter', 'contracts', 'config'])
  await cp(path.join(repo, 'modules/ac2revit', dir), path.join(ac, dir), { recursive: true, filter: file => filter(file) && !/exchange-settings\.json$|\.local\.json$/i.test(file) });
await cp(path.join(repo, 'modules/ac2revit/revit/assets'), path.join(ac, 'dist/Revit2026/assets'), { recursive: true });
// Private RTE preferences are deliberately replaced with the portable default.
await writeFile(path.join(ac, 'config/exchange-settings.json'), '{"projectTemplate":""}\n');
run(python, [path.join(repo, 'scripts/package-python.py'), path.join(ac, 'runtime/python')]);
await cp(path.join(repo, 'README.md'), path.join(stage, 'README.md'));
const hashes = {};
async function walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Symlink in payload: ${file}`);
    if (/^(RevitAPI(UI)?\.dll|bridge\.token|settings\.json)$/i.test(entry.name) || /\.(rvt|rte|ifc|pln|bpn)$/i.test(entry.name)) throw new Error(`Private file: ${file}`);
    if (entry.isDirectory()) await walk(file);
    else hashes[path.relative(stage, file).replaceAll('\\', '/')] = createHash('sha256').update(await readFile(file)).digest('hex');
  }
}
await rm(path.join(stage, 'release.json'));
// The four distributable Ac2Revit families/templates are intentionally allowed.
await walk(stage);
await writeFile(path.join(stage, 'release.json'), JSON.stringify({ version: '1.1.0', target: 'Revit 2026 / Windows x64', modules: ['Codex MCP', 'Ac2Revit', 'ОВ'], node: process.version, files: hashes }, null, 2));
console.log(`BotPlus RT payload: ${Object.keys(hashes).length} files`);
