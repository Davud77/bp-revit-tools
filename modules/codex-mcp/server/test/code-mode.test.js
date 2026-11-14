import test from 'node:test';
import assert from 'node:assert/strict';
import * as z from 'zod/v4';
import { ToolCatalog } from '../src/tool-catalog.js';
import { executeCode } from '../src/code-runtime.js';

const read = { readOnlyHint: true, destructiveHint: false, openWorldHint: false };
function fixture() {
  const catalog = new ToolCatalog(), seen = [];
  catalog.add('list_rooms', 'Помещения постранично', z.object({ offset: z.number().int().min(0), limit: z.number().int().min(1).max(2) }).strict(), read,
    async ({ offset, limit }) => { seen.push(offset); return { rooms: [10, 20, 30, 40, 50].slice(offset, offset + limit) }; });
  catalog.add('apply_parameter_plan', 'Применить план', z.object({ planId: z.literal('approved-plan') }).strict(),
    { ...read, readOnlyHint: false }, async args => { seen.push(args.planId); return { applied: true }; });
  return { catalog, seen };
}

test('discovery finds Russian text and exact schema, rejects unknown commands and duplicate registration', () => {
  const { catalog } = fixture();
  assert.equal(catalog.search({ query: 'Помещения' }).tools[0].name, 'list_rooms');
  assert.equal(catalog.describe(['list_rooms'])[0].inputSchema.properties.limit.maximum, 2);
  assert.throws(() => catalog.describe(['missing']), /Unknown tool/);
  assert.throws(() => catalog.add('list_rooms', '', z.object({}), read, () => {}), /Duplicate/);
});

test('pagination and aggregation return only the chosen summary; guest has no host IO', async () => {
  const { catalog, seen } = fixture();
  const r = await executeCode({ toolNames: ['list_rooms'], code: `
    let sum = 0, count = 0;
    for (let offset = 0; ; offset += 2) {
      const page = await tools.call('list_rooms', {offset, limit: 2});
      sum += page.rooms.reduce((a,b) => a+b, 0); count += page.rooms.length;
      if (page.rooms.length < 2) break;
    }
    return {sum, count, process: typeof process, require: typeof require, fetch: typeof fetch};
  ` }, catalog);
  assert.equal(r.failed, undefined, JSON.stringify(r));
  assert.deepEqual(r.result, { sum: 150, count: 5, process: 'undefined', require: 'undefined', fetch: 'undefined' });
  assert.deepEqual(seen, [0, 2, 4]);
  assert.equal(r.calls.length, 3);
  assert.ok(!JSON.stringify(r).includes('"rooms":'));
});

test('allowlist and Zod validation are enforced before handlers, including writes', async () => {
  const { catalog, seen } = fixture();
  for (const code of [
    `return await tools.call('apply_parameter_plan', {planId:'approved-plan'})`,
    `return await tools.call('list_rooms', {offset:0,limit:500})`,
    `return await tools.call('execute_code', {})`
  ]) {
    const r = await executeCode({ toolNames: ['list_rooms'], code }, catalog);
    assert.equal(r.failed, true);
  }
  const r = await executeCode({ toolNames: ['apply_parameter_plan'], code:
    `return await tools.call('apply_parameter_plan', {planId:'stale'})` }, catalog);
  assert.equal(r.failed, true);
  assert.deepEqual(seen, []);
  const success = await executeCode({ toolNames: ['apply_parameter_plan'], code:
    `return await tools.call('apply_parameter_plan', {planId:'approved-plan'})` }, catalog);
  assert.equal(success.result.applied, true);
});

test('Promise.all is serialized; launched calls are drained even when not awaited', async () => {
  const catalog = new ToolCatalog(); let active = 0, peak = 0, completed = 0;
  catalog.add('read', '', z.object({}), read, async () => {
    peak = Math.max(peak, ++active);
    await new Promise(resolve => setTimeout(resolve, 5));
    active--; completed++; return completed;
  });
  const r = await executeCode({ toolNames: ['read'], code:
    `tools.call('read', {}); return await Promise.all([tools.call('read',{}),tools.call('read',{})]);` }, catalog);
  assert.equal(r.failed, undefined, JSON.stringify(r));
  assert.equal(peak, 1); assert.equal(completed, 3);
});

test('call budget cannot be bypassed by catching tool errors', async () => {
  const { catalog, seen } = fixture();
  const r = await executeCode({ toolNames: ['list_rooms'], maxCalls: 1, code: `
    await tools.call('list_rooms',{offset:0,limit:1});
    try { await tools.call('list_rooms',{offset:1,limit:1}); } catch {}
    return 'hidden';` }, catalog);
  assert.equal(r.failed, true); assert.match(r.error, /call limit/); assert.deepEqual(seen, [0]);
});

test('CPU, wall time, output and memory limits fail without poisoning the next run', async () => {
  const { catalog } = fixture();
  for (const [code, options, pattern] of [
    ['while(true) {}', { cpuMs: 30 }, /CPU limit/],
    ['await new Promise(() => {});', {}, /time limit/],
    ["return 'a'.repeat(70000)", {}, /64 KB/],
    ["print('a'.repeat(9000))", {}, /print limit/],
    ["return new Array(1000000).fill('x')", { memoryBytes: 1024 * 1024 }, /memory|alloc/i],
    ['for (;;) await Promise.resolve();', { cpuMs: 30 }, /CPU|time limit/]
  ]) {
    const r = await executeCode({ toolNames: [], code, timeoutMs: 100 }, catalog, options);
    assert.equal(r.failed, true, code); assert.match(r.error, pattern);
  }
  assert.equal((await executeCode({ toolNames: [], code: 'return 42' }, catalog)).result, 42);
});

test('cancellation reaches an in-flight tool; a failed script reports possible effects', async () => {
  const catalog = new ToolCatalog(), controller = new AbortController(); let aborted = false;
  catalog.add('wait', '', z.object({}), read, (_args, { signal }) => new Promise((resolve, reject) => {
    signal.addEventListener('abort', () => { aborted = true; reject(new Error('cancelled')); }, { once: true });
  }));
  const timer = setTimeout(() => controller.abort(), 20);
  const r = await executeCode({ toolNames: ['wait'], code: `await tools.call('wait',{})` }, catalog, { signal: controller.signal });
  clearTimeout(timer); assert.equal(r.failed, true); assert.equal(aborted, true); assert.equal(r.effectsPossible, true);
});
