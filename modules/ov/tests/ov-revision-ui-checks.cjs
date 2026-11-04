const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url'),{execFileSync}=require('node:child_process');
(async()=>{
 const output=path.resolve('../../Выходные данные/Проверки ОВ/ov-revision-ui');fs.mkdirSync(output,{recursive:true});
 const data=JSON.parse(fs.readFileSync(path.resolve(process.argv[2] || '../../Выходные данные/Проверки ОВ/demo/building.json'),'utf8'));
 data.rooms.forEach((room,i)=>{room.id=1000+i;room.idText=String(room.id);});
 const room=data.rooms[0];
 if(!data.openings.some(o=>!o.roomKeys?.length))data.openings.push({key:'unbound-test',idText:'999999',kind:'window',name:'Проверка без помещения',roomKeys:[],position:[room.contour[0][0]-2,room.contour[0][1],room.baseElevation+1.5],tangent:[1,0],width:1.2,height:1.5,area:1.8});
 const json=path.join(output,'building.json');fs.writeFileSync(json,JSON.stringify(data));
 execFileSync('dotnet',['run','--project','tests/ExportChecks.csproj','-c','Release','--','--render',json]);
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage({viewport:{width:1600,height:1050}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(output,'heat_loss_viewer.html')).href);
  await page.waitForFunction(()=>document.getElementById('load-state').hidden,{},{timeout:45000});
  assert.equal(await page.locator('header h1').textContent(),data.projectName);
  assert.equal(await page.locator('header .summary,header .brand,header .badge,#project-subtitle,#selection-count,#calculation-method,#air-method,#input-airChanges,#inputs-panel,[data-view=inputs]').count(),0);
  assert.equal(await page.locator('#totals-panel .summary').count(),1);
  const known=new Set(data.rooms.map(r=>r.key)),orphans=data.openings.filter(o=>!o.roomKeys?.some(k=>known.has(k)));
  assert.equal(await page.locator('#unbound-opening-list tr').count(),0);
  const exported=await page.evaluate(()=>JSON.parse(document.getElementById('building-data').textContent));
  assert.ok(exported.openings.every(o=>o.roomKeys.some(k=>known.has(k))));
  assert.ok(exported.warnings.some(w=>w.includes('исключено окон и дверей')));
  for(const mode of ['opaque','transparent','transparent-dark']) {
   if(mode==='transparent')await page.locator('#show-shell').check();
   if(mode==='transparent-dark')await page.locator('#btn-theme').click();
   await page.waitForTimeout(700);await page.screenshot({path:path.join(output,mode+'.png'),fullPage:true});
  }
  await page.locator('.tabs button[data-view="totals"]').click();assert.equal(await page.locator('.summary').isVisible(),true);await page.screenshot({path:path.join(output,'totals.png'),fullPage:true});
  await page.locator('.tabs button[data-view="totals"]').click();
  await page.evaluate(()=>{const data=JSON.parse(document.getElementById('building-data').textContent),key='heatloss-inputs:'+data.projectName+':'+data.exportedAt;localStorage.setItem(key,JSON.stringify({settings:{...globalThis.HeatLossEngine.defaults,method:'simple',airMode:'airChanges',airChanges:100},overrides:{}}));});
  await page.reload();
  await page.locator('.tabs button[data-view="totals"]').click();
  const downloaded=page.waitForEvent('download');await page.locator('#export-results').click();
  const download=await downloaded,result=JSON.parse(fs.readFileSync(await download.path(),'utf8'));
  assert.equal(result.settings.method,'poteri');assert.ok(result.rooms.every(r=>r.method==='poteri' && r.ventilation===0));
  assert.deepEqual(errors,[]);console.log(`PASS compact header, summary in totals, ${orphans.length} unbound openings excluded from JSON/HTML, opaque/transparent themes, sole Poteri method`);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
