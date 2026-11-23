// Synthetic metadata benchmark, not a live Revit performance claim.
// Run from the repository root: node scripts/benchmark-mcp-catalog.mjs [baseline-ref]
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { performance } from 'node:perf_hooks';
import { pathToFileURL, fileURLToPath } from 'node:url';
const packageUrl = new URL('../modules/codex-mcp/server/package.json', import.meta.url);
const require = createRequire(packageUrl);
const zodUrl = pathToFileURL(require.resolve('zod/v4')).href;
const z = await import(zodUrl);
const root = fileURLToPath(new URL('..', import.meta.url));
const baselineRef = process.argv[2] ?? 'HEAD';
const source = execFileSync('git', ['show', `${baselineRef}:modules/codex-mcp/server/src/tool-catalog.js`], { cwd: root, encoding: 'utf8' });
const baseline = await import(`data:text/javascript;base64,${Buffer.from(source.replace("'zod/v4'", JSON.stringify(zodUrl))).toString('base64')}`);
const current = await import('../modules/codex-mcp/server/src/tool-catalog.js');
function fixture(Type) {
  const catalog = new Type();
  for (let i = 0; i < 150; i++) catalog.add(`read_elements_${i}`,
    `Прочитать элементы проекта ${i}: параметры, уровни, типы и семейства, постраничный результат.`,
    z.object({ offset: z.number().int().min(0).default(0), limit: z.number().int().min(1).max(500).default(100),
      filters: z.array(z.object({ name: z.string(), value: z.string(), operation: z.enum(['eq', 'contains', 'startsWith']) })).max(20).optional() }),
    { readOnlyHint: true }, async () => ({}));
  return catalog;
}
function measure(catalog, action) {
  for (let i = 0; i < 100; i++) action(catalog);
  const samples = [];
  for (let batch = 0; batch < 7; batch++) {
    const start = performance.now();
    for (let i = 0; i < 1000; i++) action(catalog);
    samples.push(performance.now() - start);
  }
  return samples.sort((a, b) => a - b)[3];
}
const before = fixture(baseline.ToolCatalog), after = fixture(current.ToolCatalog);
const cases = {
  search: catalog => catalog.search({ query: 'элементы параметры' }),
  describe10: catalog => catalog.describe(Array.from({ length: 10 }, (_, i) => `read_elements_${i}`))
};
const measurements = {};
for (const [name, action] of Object.entries(cases)) {
  const baselineMs = measure(before, action), currentMs = measure(after, action);
  measurements[name] = { baselineMs, currentMs, ratio: baselineMs / currentMs };
}
console.log(JSON.stringify({ fixture: '150 synthetic tools; median of 7 batches of 1000 calls after warm-up',
  node: process.version, baseline: execFileSync('git', ['rev-parse', baselineRef], { cwd: root, encoding: 'utf8' }).trim(), measurements }, null, 2));
