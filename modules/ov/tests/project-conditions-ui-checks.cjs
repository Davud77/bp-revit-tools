const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url'),{execFileSync}=require('node:child_process');
(async()=>{
 const output=path.resolve('../../Выходные данные/Проверки ОВ/project-conditions-ui');fs.mkdirSync(output,{recursive:true});
 const data=JSON.parse(fs.readFileSync(path.resolve('../../Выходные данные/Проверки ОВ/demo/building.json'),'utf8'));
 data.rooms.forEach((room,i)=>{room.id=1000+i;room.idText=String(room.id);});
 const room=data.rooms[0];
 data.projectConditions={name:data.projectName,climateRegion:'II',climateSubregion:'IIБ',outdoorTemperature:-23.5,indoorTemperature:21,windSpeed:0,buildingHeight:12,firstFloorHeight:.6,notes:'</script><script>window.injected=true</script>',calculation:{building:{wall:.31},floorOverrides:{[room.levelKey]:{indoor:24}},roomOverrides:{[room.key]:{indoor:26}},orientationAdditions:true,reviewed:true}};
 const json=path.join(output,'building.json');fs.writeFileSync(json,JSON.stringify(data));
 execFileSync('dotnet',['run','--project','tests/ExportChecks.csproj','-c','Release','--','--render',json]);
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(output,'heat_loss_viewer.html')).href);
  assert.equal(await page.locator('#inputs-panel,[data-view=inputs]').count(),0);
  await page.locator('.tabs button[data-view=totals]').click();
  assert.match(await page.locator('#project-conditions').textContent(),/IIБ/);assert.match(await page.locator('#project-conditions').textContent(),/0.6/);
  assert.equal(await page.evaluate(()=>globalThis.injected),undefined);
  const resultEvent=page.waitForEvent('download');await page.locator('#export-results').click();const resultDownload=await resultEvent;
  const result=JSON.parse(fs.readFileSync(await resultDownload.path(),'utf8'));
  assert.equal(result.settings.outdoor,-23.5);assert.equal(result.settings.indoor,21);assert.equal(result.settings.windSpeed,0);assert.equal(result.settings.wall,.31);
  assert.equal(result.settings.floorOverrides[room.levelKey].indoor,24);assert.equal(result.settings.roomOverrides[room.key].indoor,26);
  assert.ok(result.rooms.every(r=>r.method==='poteri' && r.ventilation===0));
  await page.locator('.tabs button[data-view=model]').click();await page.locator('#room-list tr').first().click();
  await page.locator('#room-parameters summary').click();assert.equal(await page.locator('#room-indoor').inputValue(),'26');
  await page.locator('#room-indoor').fill('28');await page.locator('#room-indoor').press('Tab');
  await page.reload();await page.locator('#room-list tr').first().click();await page.locator('#room-parameters summary').click();assert.equal(await page.locator('#room-indoor').inputValue(),'28');
  assert.equal(JSON.parse(fs.readFileSync(json,'utf8')).projectConditions.calculation.roomOverrides[room.key].indoor,26);
  await page.screenshot({path:path.join(output,'project-conditions.png'),fullPage:true});assert.deepEqual(errors,[]);
  console.log('PASS native project/building/floor/room inputs initialize browser calculation, zero wind, HTML safety, single Poteri, persisted local override without modifying source');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
