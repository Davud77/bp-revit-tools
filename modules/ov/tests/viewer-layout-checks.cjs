const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url'),{execFileSync}=require('node:child_process');
(async()=>{
 const output=path.resolve('artifacts/viewer-layout');fs.mkdirSync(output,{recursive:true});
 const data=JSON.parse(fs.readFileSync(path.resolve(process.argv[2] || 'artifacts/demo/building.json'),'utf8'));
 data.warnings=['Замечание для проверки логов','Второе замечание'];
 data.rooms.forEach((r,i)=>{r.id=i+1;r.sourceDocument='model-20261103-161619159';r.sourceInstance=r.sourceDocument;});
 const source=path.join(output,'building.json');fs.writeFileSync(source,JSON.stringify(data));
 execFileSync('dotnet',['run','--project','tests/ExportChecks.csproj','-c','Release','--','--render',source]);
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage({viewport:{width:1600,height:1050}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(output,'heat_loss_viewer.html')).href);await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  assert.equal(await page.locator('header #export-results').count(),1);
  assert.equal(await page.locator('.tabs button[data-view=list],footer').count(),0);
  assert.equal(await page.locator('#unbound-openings').isVisible(),false);
  assert.equal(await page.locator('#warnings').isVisible(),false);
  const title=await page.locator('#project-title').boundingBox(),nav=await page.locator('.tabs').boundingBox(),json=await page.locator('#export-results').boundingBox(),theme=await page.locator('#btn-theme').boundingBox();
  assert.ok(nav.x>=title.x+title.width && Math.abs(nav.y-title.y)<25);
  assert.ok(json.x+json.width<=theme.x);
  await page.locator('#room-list tr').first().click();
  assert.doesNotMatch(await page.locator('#card-sub').textContent(),/model-/);assert.equal(await page.locator('#card-source').textContent(),'');
  await page.locator('.tabs button[data-view=totals]').click();
  assert.equal(await page.locator('#totals-panel #table-panel').isVisible(),true);assert.equal(await page.locator('#room-list tr').count(),data.rooms.length);
  assert.equal(await page.locator('#print-report').isVisible(),true);
  await page.locator('#search-box').fill(data.rooms[0].name);assert.ok(await page.locator('#room-list tr').count()>0);
  await page.locator('#reset-filters').click();
  await page.screenshot({path:path.join(output,'totals.png'),fullPage:true});
  await page.locator('.tabs button[data-view=logs]').click();
  assert.equal(await page.locator('#warning-list li').count(),2);assert.equal(await page.locator('#warnings').isVisible(),true);
  assert.equal(await page.locator('#stage').isVisible(),false);assert.equal(await page.locator('#table-panel').isVisible(),false);assert.equal(await page.locator('#panel-resizer').isVisible(),false);
  await page.locator('.tabs button[data-view=model]').click();assert.equal(await page.locator('#app > #table-panel').isVisible(),true);assert.equal(await page.locator('#warnings').isVisible(),false);
  const download=page.waitForEvent('download');await page.locator('#export-results').click();assert.equal((await download).suggestedFilename(),'heat_loss_results.json');
  for(const width of [1600,900,390]){await page.setViewportSize({width,height:1000});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2));await page.screenshot({path:path.join(output,`model-${width}.png`),fullPage:true});}
  assert.deepEqual(errors,[]);console.log('PASS header navigation/JSON, clean card/footer, merged room totals with filters, logs isolation, JSON download and desktop/mobile layout');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
