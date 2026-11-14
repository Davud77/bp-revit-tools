import { cp, lstat, mkdir, writeFile, readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const [repo, stage, python, version] = process.argv.slice(2);
if (!repo || !stage || !python || !version) throw new Error('Usage: package.mjs repo stage python version');
const templateManifest = path.join(repo, 'installer/project-templates.json');
const templateSource = path.join(repo, 'Входные данные/Шаблоны');
const projectTemplates = JSON.parse(await readFile(templateManifest, 'utf8'));
const allowedTemplates = new Set();
if (!(await lstat(templateSource)).isDirectory()) throw new Error('Project templates require a physical directory: ' + templateSource);
for (const { file } of projectTemplates) {
  if (!/^[^/\\,:=]+\.rte$/i.test(file)) throw new Error('Invalid project template filename: ' + file);
  const source = path.join(templateSource, file);
  if (!(await lstat(source)).isFile()) throw new Error('Project template must be a regular file: ' + source);
  allowedTemplates.add('project-templates/' + file);
}
function run(command, args) {
  const result = spawnSync(command, args, { stdio: 'inherit', windowsHide: true });
  if (result.status !== 0) throw result.error ?? new Error(`${command} failed: ${result.status}`);
}
run(process.execPath, [path.join(repo, 'scripts/package-mcp.mjs'), repo, stage]);
await cp(path.join(repo, 'installer/configure-mcp.mjs'), path.join(stage, 'configure-mcp.mjs'));
await cp(path.join(repo, 'LICENSE'), path.join(stage, 'LICENSE'));
await mkdir(path.join(stage, 'project-templates'), { recursive: true });
await cp(templateManifest, path.join(stage, 'project-templates/templates.json'));
for (const { file } of projectTemplates)
  await cp(path.join(templateSource, file), path.join(stage, 'project-templates', file));
await cp(path.join(repo, 'docs/INSTALLATION.md'), path.join(stage, 'INSTALLATION.md'));
await cp(path.join(repo, 'Выходные данные/Сборочные данные/dotnet/BotPlus.RT/bin/Release/net8.0-windows/BotPlus.RT.dll'), path.join(stage, 'BotPlus.RT.dll'));
await mkdir(path.join(stage, 'modules/ov'), { recursive: true });
await cp(path.join(repo, 'Выходные данные/Сборочные данные/dotnet/HeatLossViewer/bin/Release/net8.0-windows/HeatLossViewer.dll'), path.join(stage, 'modules/ov/HeatLossViewer.dll'));
await mkdir(path.join(stage, 'modules/openings'), { recursive: true });
await cp(path.join(repo, 'Выходные данные/Сборочные данные/dotnet/BotPlus.Openings/bin/Release/net8.0-windows/BotPlus.Openings.dll'), path.join(stage, 'modules/openings/BotPlus.Openings.dll'));
await mkdir(path.join(stage, 'modules/eom'), { recursive: true });
await cp(path.join(repo, 'Выходные данные/Сборочные данные/dotnet/BotPlus.Eom/bin/Release/net8.0-windows/BotPlus.Eom.dll'), path.join(stage, 'modules/eom/BotPlus.Eom.dll'));
const ac = path.join(stage, 'modules/ac2revit');
await mkdir(path.join(ac, 'dist/Revit2026'), { recursive: true });
await cp(path.join(repo, 'Выходные данные/Сборочные данные/dotnet/Ac2Revit/bin/Release/net8.0-windows/Ac2Revit.dll'), path.join(ac, 'dist/Revit2026/Ac2Revit.dll'));
const filter = file => !/[/\\](__pycache__|bin|obj)([/\\]|$)|\.pyc$/i.test(file);
for (const dir of ['tools', 'exporter', 'contracts', 'config'])
  await cp(path.join(repo, 'modules/ac2revit', dir), path.join(ac, dir), { recursive: true, filter: file => filter(file) && !/exchange-settings\.json$|\.local\.json$/i.test(file) });
await cp(path.join(repo, 'modules/ac2revit/revit/assets'), path.join(ac, 'dist/Revit2026/assets'), { recursive: true });
// Private RTE preferences are deliberately replaced with the portable default.
await writeFile(path.join(ac, 'config/exchange-settings.json'), '{"projectTemplate":""}\n');
run(python, [path.join(repo, 'scripts/package-python.py'), path.join(ac, 'runtime/python')]);
const tapir = JSON.parse(await readFile(path.join(repo, 'installer/tapir/releases.json'), 'utf8'));
await mkdir(path.join(stage, 'tapir'), { recursive: true });
for (const asset of tapir.assets) {
  const file = path.join(repo, 'Выходные данные/Сборочные данные/tapir', tapir.release, asset.file);
  if (createHash('sha256').update(await readFile(file)).digest('hex') !== asset.sha256) throw new Error('Tapir checksum: ' + asset.file);
  await cp(file, path.join(stage, 'tapir', asset.file));
}
await cp(path.join(repo, 'installer/tapir/releases.json'), path.join(stage, 'tapir/releases.json'));
await cp(path.join(repo, 'installer/tapir/LICENSE'), path.join(stage, 'tapir/LICENSE'));
for (const file of ['USAGE.md', 'docs/OPENINGS.md', 'docs/EOM.md', 'docs/ANNOTATIONS.md', 'modules/ov/docs/POTERI.md']) {
  const destination = path.join(stage, file);
  await mkdir(path.dirname(destination), { recursive: true });
  await cp(path.join(repo, file), destination);
}
const hashes = {};
async function walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Symlink in payload: ${file}`);
    const relative = path.relative(stage, file).replaceAll('\\', '/');
    if (/^(RevitAPI(UI)?\.dll|bridge\.token|settings\.json)$/i.test(entry.name) || /\.(rvt|ifc|pln|bpn)$/i.test(entry.name) ||
        (/\.rte$/i.test(entry.name) && !allowedTemplates.has(relative))) throw new Error(`Private file: ${file}`);
    if (entry.isDirectory()) await walk(file);
    else hashes[relative] = createHash('sha256').update(await readFile(file)).digest('hex');
  }
}
// The four distributable Ac2Revit families/templates are intentionally allowed.
await walk(stage);
await writeFile(path.join(stage, 'release.json'), JSON.stringify({ version, target: 'Revit 2026 / Windows x64', modules: ['MCP', 'Ac2Revit', 'ОВ', 'Отверстия', 'Марки'], node: process.version, files: hashes }, null, 2));
console.log(`BotPlus RT payload: ${Object.keys(hashes).length} files`);
