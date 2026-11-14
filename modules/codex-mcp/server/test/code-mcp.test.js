import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';

async function session(t, env = {}) {
  const child = spawn(process.execPath, ['src/index.js'], { cwd: new URL('..', import.meta.url), windowsHide: true,
    env: { ...process.env, BP_REVIT_AI_TOOL_MODE: 'code', BP_REVIT_AI_DENIED_TOOLS: '', ...env }, stdio: ['pipe','pipe','pipe'] });
  t.after(() => child.kill());
  let data = '', id = 0, stderr = ''; const pending = new Map();
  child.stderr.on('data', chunk => { stderr += chunk; });
  child.stdout.setEncoding('utf8'); child.stdout.on('data', chunk => {
    data += chunk;
    while (data.includes('\n')) { const n=data.indexOf('\n'); const m=JSON.parse(data.slice(0,n)); data=data.slice(n+1); pending.get(m.id)?.(m); }
  });
  const rpc = (method, params) => new Promise((resolve,reject) => {
    const key=++id, timer=setTimeout(()=>reject(new Error(stderr || 'MCP timeout')),5000);
    pending.set(key,m=>{clearTimeout(timer);pending.delete(key);resolve(m);});
    child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:key,method,params})+'\n');
  });
  await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'code-test',version:'1'}});
  child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
  return rpc;
}

test('compact MCP exposes five tools, discovers the complete catalog and returns structured summaries', async t => {
  const rpc = await session(t);
  const list = (await rpc('tools/list',{})).result.tools;
  assert.deepEqual(list.map(t=>t.name).sort(),['describe_tools','execute_code','get_project_info','revit_ping','search_tools']);
  assert.equal(list.find(t=>t.name==='execute_code').annotations.destructiveHint,true);
  const fullBytes=127138; // Measured pre-change full catalog, not a token estimate.
  assert.ok(Buffer.byteLength(JSON.stringify(list)) < fullBytes / 5);
  const found=await rpc('tools/call',{name:'search_tools',arguments:{query:'',limit:1}});
  assert.equal(found.result.structuredContent.total,151);
  assert.equal(found.result.structuredContent.nextOffset,1);
  const schema=await rpc('tools/call',{name:'describe_tools',arguments:{names:['preview_connect_mep']}});
  assert.ok(schema.result.structuredContent.tools[0].inputSchema.properties.connectors);
  const result=await rpc('tools/call',{name:'execute_code',arguments:{toolNames:[],code:'return [1,2,3].reduce((a,b)=>a+b,0)'}});
  assert.equal(result.result.structuredContent.result,6);
  const failure=await rpc('tools/call',{name:'execute_code',arguments:{toolNames:[],code:'return process.env'}});
  assert.equal(failure.result.isError,true);
});

test('server denial applies to discovery, direct visibility and nested calls', async t => {
  const rpc=await session(t,{BP_REVIT_AI_DENIED_TOOLS:'apply_delete_plan,get_project_info'});
  const list=(await rpc('tools/list',{})).result.tools;
  assert.ok(!list.some(x=>x.name==='get_project_info'));
  const search=await rpc('tools/call',{name:'search_tools',arguments:{query:'apply_delete_plan'}});
  assert.equal(search.result.structuredContent.total,0);
  const denied=await rpc('tools/call',{name:'execute_code',arguments:{toolNames:['apply_delete_plan'],code:'return 1'}});
  assert.equal(denied.result.isError,true);
});
