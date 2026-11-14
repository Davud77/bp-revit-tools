import { getQuickJS } from 'quickjs-emscripten';
import { setTimeout as delay } from 'node:timers/promises';

const MAX_JSON = 8 * 1024 * 1024;
const MAX_RESULT = 64 * 1024;
let activeRuns = 0;

function json(value, max = MAX_JSON) {
  const text = JSON.stringify(value ?? null);
  if (Buffer.byteLength(text) > max) throw new Error(`JSON exceeds ${max} bytes. Return a summary.`);
  return text;
}

// QuickJS/WASM has no Node globals, modules, filesystem or network. Only named,
// validated tool calls cross the boundary; this is not node:vm or host eval.
export async function executeCode({ code, toolNames, timeoutMs = 30000, maxCalls = 100 }, catalog,
  { signal, cpuMs = 1000, memoryBytes = 64 * 1024 * 1024 } = {}) {
  signal?.throwIfAborted();
  if (activeRuns >= 2) throw new Error('Code Mode is busy; retry after the active run finishes.');
  if (typeof code !== 'string' || code.length > 32000) throw new Error('Invalid code size.');
  if (!Array.isArray(toolNames) || toolNames.length > 20 || toolNames.some(n => !catalog.has(n)))
    throw new Error('Choose up to 20 existing tools explicitly.');
  activeRuns++;
  const controller = new AbortController();
  const relay = () => controller.abort(signal.reason);
  signal?.addEventListener('abort', relay, { once: true });
  const started = Date.now(), deadline = started + timeoutMs;
  const allowed = new Set(toolNames), pending = new Set(), deferred = [];
  const completions = [], calls = [], logs = [];
  let runtime, ctx, promise, fatal, logBytes = 0, chain = Promise.resolve();
  let cpuUsed = 0, sliceStarted = 0;
  const check = () => {
    if (fatal) throw fatal;
    controller.signal.throwIfAborted();
    if (Date.now() >= deadline) throw new Error('Code Mode time limit exceeded.');
  };
  const runGuest = fn => {
    sliceStarted = Date.now();
    try { return fn(); } finally { cpuUsed += Date.now() - sliceStarted; sliceStarted = 0; }
  };
  try {
    const QuickJS = await getQuickJS();
    check();
    runtime = QuickJS.newRuntime();
    runtime.setMemoryLimit(memoryBytes);
    runtime.setMaxStackSize(512 * 1024);
    runtime.setInterruptHandler(() => {
      if (cpuUsed + (sliceStarted ? Date.now() - sliceStarted : 0) > cpuMs)
        fatal = new Error('Code Mode CPU limit exceeded.');
      return !!fatal || controller.signal.aborted || Date.now() >= deadline;
    });
    ctx = runtime.newContext();
    const hostCall = ctx.newFunction('__call', (nameHandle, argsHandle) => {
      check();
      const name = ctx.getString(nameHandle), argsText = ctx.getString(argsHandle);
      if (!allowed.has(name)) throw new Error(`Tool is outside toolNames: ${name}`);
      if (calls.length >= maxCalls) { fatal = new Error('Code Mode call limit exceeded.'); throw fatal; }
      if (Buffer.byteLength(argsText) > 1024 * 1024) throw new Error('Tool arguments exceed 1 MB.');
      const args = JSON.parse(argsText);
      const p = ctx.newPromise();
      deferred.push(p);
      const record = { name, status: 'queued' };
      calls.push(record);
      // Serial execution prevents Promise.all from racing document mutations.
      const work = chain.then(async () => {
        check();
        record.status = 'running';
        return json(await catalog.call(name, args, { signal: controller.signal }));
      });
      chain = work.catch(() => {});
      pending.add(work);
      work.then(value => { record.status = 'completed'; completions.push({ p, value }); }, error => {
        record.status = 'failed'; completions.push({ p, error: String(error.message ?? error).slice(0, 2000) });
      }).finally(() => pending.delete(work));
      return p.handle;
    });
    ctx.setProp(ctx.global, '__call', hostCall); hostCall.dispose();
    const print = ctx.newFunction('__print', handle => {
      const text = ctx.getString(handle);
      logBytes += Buffer.byteLength(text);
      if (logBytes > 8192) throw new Error('Code Mode print limit exceeded.');
      logs.push(text);
    });
    ctx.setProp(ctx.global, '__print', print); print.dispose();
    const source = `(() => {
      const call = globalThis.__call, output = globalThis.__print;
      delete globalThis.__call; delete globalThis.__print;
      const tools = Object.freeze({ call: async (name, args = {}) => JSON.parse(await call(name, JSON.stringify(args))) });
      const print = (...values) => output(JSON.stringify(values));
      return (async () => { const result = await (async () => {\n${code}\n})(); return JSON.stringify(result ?? null); })();
    })()`;
    promise = ctx.unwrapResult(runGuest(() => ctx.evalCode(source, 'code-mode.js')));
    for (;;) {
      check();
      for (const { p, value, error } of completions.splice(0)) {
        const h = error ? ctx.newError(error) : ctx.newString(value);
        try { if (error) p.reject(h); else p.resolve(h); } finally { h.dispose(); }
      }
      const jobs = runGuest(() => runtime.executePendingJobs(100));
      if (jobs.error) { const e = ctx.dump(jobs.error); jobs.error.dispose(); throw new Error(e.message ?? String(e)); }
      const state = ctx.getPromiseState(promise);
      if (state.type === 'rejected') {
        const e = ctx.dump(state.error); state.error.dispose(); throw new Error(e.message ?? String(e));
      }
      if (state.type === 'fulfilled') {
        const value = ctx.getString(state.value); state.value.dispose();
        if (pending.size === 0 && completions.length === 0) {
          check();
          if (Buffer.byteLength(value) > MAX_RESULT) throw new Error('Result exceeds 64 KB. Return a summary.');
          return { result: JSON.parse(value), logs, calls, elapsedMs: Date.now() - started };
        }
      }
      // Yield between bounded job slices, including promise-only loops.
      await delay(1, undefined, { signal: controller.signal });
    }
  } catch (error) {
    return { failed: true, error: String((fatal ?? error).message ?? error).slice(0, 4000), calls, logs,
      effectsPossible: calls.some(c => c.status !== 'queued'), elapsedMs: Date.now() - started };
  } finally {
    controller.abort();
    signal?.removeEventListener('abort', relay);
    for (const p of deferred) p.dispose();
    promise?.dispose(); ctx?.dispose(); runtime?.dispose();
    activeRuns--;
  }
}
