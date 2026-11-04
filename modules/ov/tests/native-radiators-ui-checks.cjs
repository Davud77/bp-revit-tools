const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url'),{execFileSync}=require('node:child_process');
(async()=>{
 const dir=path.resolve('../../Выходные данные/Проверки ОВ/native-radiators');fs.mkdirSync(dir,{recursive:true});
 const data=JSON.parse(fs.readFileSync(path.resolve('../../Выходные данные/Проверки ОВ/demo/building.json'),'utf8'));data.rooms.forEach((r,i)=>r.id=i+1);
 data.nativeRadiators=true;data.ovLibrary={materials:[],constructions:[],radiators:{settings:{supply:75,return:55,roomOverrides:{}},custom:[]}};
 const source=path.join(dir,'building.json');fs.writeFileSync(source,JSON.stringify(data));
 execFileSync('dotnet',['run','--project','tests/ExportChecks.csproj','-c','Release','--','--render',source]);
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(()=>{window.hostMessages=[];window.chrome.webview={postMessage:m=>window.hostMessages.push(m)};});
  await page.goto(pathToFileURL(path.join(dir,'heat_loss_viewer.html')).href);await page.waitForFunction(()=>globalThis.botplusRadiatorSnapshot!=null);
  assert.equal(await page.locator('body').getAttribute('data-view'),'radiators');assert.equal(await page.locator('#rad-supply').inputValue(),'75');
  await page.locator('#rad-supply').fill('68');await page.locator('#radiator-form button[type=submit]').click();
  assert.equal(await page.evaluate(()=>botplusRadiatorSnapshot().settings.supply),68);
  await page.locator('#rad-export').click();await page.locator('#rad-schedule').click();
  const messages=await page.evaluate(()=>hostMessages);assert.ok(messages.some(m=>m.type==='ready'));assert.ok(messages.some(m=>m.name==='radiator_selection.json'&&JSON.parse(m.text).settings.supply===68));assert.ok(messages.some(m=>m.name==='radiator_schedule.csv'&&m.text.includes('Помещение')));
  await page.locator('#rad-view-3d').click();await page.locator('#room-list tr').first().click();await page.locator('#radiator-room-details summary').first().click();
  await page.locator('#rad-room-target').fill('1234');await page.locator('#rad-room-target').press('Tab');
  const state=await page.evaluate(()=>botplusRadiatorSnapshot());assert.ok(Object.values(state.settings.roomOverrides).some(v=>v.targetWatts===1234));
  await page.locator('#rad-source-load').evaluate(el=>el.closest('details').open=true);
  await page.locator('#rad-source-load').selectOption('0');await page.locator('#rad-apply-source-load').click();
  const mapped=await page.evaluate(()=>botplusRadiatorSnapshot());assert.ok(Object.values(mapped.settings.roomOverrides).some(v=>v.sourceLoad&&v.targetWatts>0));
  await page.locator('.tabs button[data-view=radiators]').click();
  await page.locator('#custom-catalog').evaluate(d=>d.open=true);
  for(const [id,value] of Object.entries({'custom-model':'Проверка переноса','custom-source':'Функциональная проверка','custom-length':'1200','custom-height':'500','custom-depth':'100','custom-q':'2000','custom-n':'1.3'}))await page.locator('#'+id).fill(value);
  await page.locator('#custom-add').click();
  assert.ok((await page.evaluate(()=>botplusRadiatorSnapshot())).custom.some(p=>p.model==='Проверка переноса'));
  const imported={id:'import-test',model:'Импорт проверки',source:'JSON тест',series:'Свой каталог',mounts:['wall'],lengthMm:1000,heightMm:500,depthMm:100,qNominal:1500,n:1.3,referenceSupply:75,referenceReturn:65,referenceRoom:20};
  await page.locator('#rad-catalog-import').setInputFiles({name:'catalog.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify([imported]))});
  await page.waitForFunction(()=>document.getElementById('rad-custom-message').textContent.includes('Добавлено 1'));
  const customSaved=await page.evaluate(()=>botplusRadiatorSnapshot());assert.equal(customSaved.custom.length,2);
  await page.screenshot({path:path.join(dir,'radiators.png'),fullPage:true});
  assert.deepEqual(errors,[]);console.log('PASS native radiator mode, project settings, custom/imported products, PDF load mapping, host JSON/CSV export and 3D/card navigation');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
