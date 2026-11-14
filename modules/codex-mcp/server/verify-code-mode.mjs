import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const child = spawn(process.execPath, [fileURLToPath(new URL('./src/index.js', import.meta.url))],
  { env: {...process.env,BP_REVIT_AI_TOOL_MODE:'code'},stdio:['pipe','pipe','pipe'],windowsHide:true });
let data='',id=0,stderr=''; const pending=new Map();
child.stderr.on('data',chunk=>{stderr+=chunk;});
child.stdout.setEncoding('utf8'); child.stdout.on('data',chunk=>{
  data+=chunk; while(data.includes('\n')) {const n=data.indexOf('\n'),m=JSON.parse(data.slice(0,n));data=data.slice(n+1);pending.get(m.id)?.(m);}
});
function rpc(method,params) { return new Promise((resolve,reject)=>{
  const key=++id,timer=setTimeout(()=>reject(new Error(stderr||'MCP timeout')),320000);
  pending.set(key,m=>{clearTimeout(timer);pending.delete(key);resolve(m);});
  child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:key,method,params})+'\n');
});}
try {
  await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'bp-code-validation',version:'1'}});
  child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
  const listed=(await rpc('tools/list',{})).result.tools;
  const response=await rpc('tools/call',{name:'execute_code',arguments:{
    toolNames:['get_project_info','list_rooms','search_elements'],maxCalls:100,timeoutMs:300000,code:`
      const project = await tools.call('get_project_info', {});
      let count=0, areaSquareMeters=0, pages=0;
      for(let offset=0;;offset+=500) {
        const page=await tools.call('list_rooms',{offset,limit:500});
        if(!Array.isArray(page.rooms)) throw new Error('Missing rooms in response');
        pages++; count+=page.rooms.length;
        areaSquareMeters+=page.rooms.reduce((sum,r)=>sum+(r.areaSquareMeters||0),0);
        if(!page.hasMore) {if(count!==page.matchingCount) throw new Error('Incomplete pagination');break;}
      }
      let wallCount=0, wallPages=0;
      for(let offset=0;;offset+=10) {
        const page=await tools.call('search_elements',{revitClass:'Wall',offset,limit:10});
        if(!Array.isArray(page.elements)) throw new Error('Missing elements');
        wallPages++;wallCount+=page.elements.length;
        if(!page.hasMore) {if(wallCount!==page.matchingCount) throw new Error('Incomplete wall pagination');break;}
      }
      return {project,roomCount:count,roomAreaSquareMeters:areaSquareMeters,pages,wallCount,wallPages};
    `}});
  const result={surfaceTools:listed.length,surfaceBytes:Buffer.byteLength(JSON.stringify(listed)),response};
  if(process.argv[2]) await writeFile(process.argv[2],JSON.stringify(result,null,2));
  console.log(JSON.stringify(result));
  if(response.error||response.result?.isError) process.exitCode=1;
} finally { child.kill(); }
