const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url'),{execFileSync}=require('node:child_process');
(async()=>{
 const dir=path.resolve('artifacts/table-sorting');fs.mkdirSync(dir,{recursive:true});
 const data=JSON.parse(fs.readFileSync('artifacts/demo/building.json','utf8'));data.rooms.forEach((r,i)=>r.id=i+1);
 const room=data.rooms[0],surface=room.surfaces[0];
 room.surfaces=[2,10,100].map((area,i)=>({...surface,key:`sort-${i}`,elementId:String(102+i),hostKey:`sort-wall-${i}`,grossArea:area,netArea:area,cuts:[]}));
 fs.writeFileSync(path.join(dir,'building.json'),JSON.stringify(data));
 execFileSync('dotnet',['run','--project','tests/ExportChecks.csproj','-c','Release','--','--render',path.join(dir,'building.json')]);
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage({viewport:{width:1600,height:1050}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(dir,'heat_loss_viewer.html')).href);await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  assert.equal(await page.locator('header #print-report').isVisible(),true);
  await page.locator('#room-list tr').first().click();await page.locator('#surface-summary').click();
  const headers=page.locator('.surface-table thead th'),areas=()=>page.locator('#surface-list tr').evaluateAll(rows=>rows.map(r=>Number(r.cells[1].dataset.sortValue)));
  await headers.nth(1).click();assert.deepEqual(await areas(),[2,10,100]);assert.equal(await headers.nth(1).getAttribute('aria-sort'),'ascending');
  await headers.nth(1).click();assert.deepEqual(await areas(),[100,10,2]);
  await headers.nth(1).focus();await headers.nth(1).press('Enter');assert.deepEqual(await areas(),[2,10,100]);
  for(const [id,value] of [[102,'10'],[103,'2'],[104,'1']]){const input=page.getByRole('spinbutton',{name:'U поверхности '+id,exact:true});await input.fill(value);await input.press('Tab');}
  await headers.nth(3).click();assert.deepEqual(await page.locator('#surface-list input[aria-label^="U поверхности"]').evaluateAll(inputs=>inputs.map(i=>Number(i.value))),[1,2,10]);
  await page.locator('#room-parameters > summary').click();await page.locator('#room-indoor').fill('22');await page.locator('#room-indoor').press('Tab');
  await page.waitForFunction(()=>[...document.querySelectorAll('#surface-list input[aria-label^="U поверхности"]')].map(i=>i.value).join(',')==='1,2,10');
  await page.locator('.tabs [data-view=totals]').click();
  const floorHeader=page.locator('#floor-totals').locator('..').locator('thead th').nth(2);await floorHeader.click();assert.equal(await floorHeader.getAttribute('aria-sort'),'ascending');
  const download=page.waitForEvent('download');await page.locator('#export-results').click();const result=JSON.parse(fs.readFileSync(await (await download).path(),'utf8'));assert.equal(result.rooms.length,data.rooms.length);
  await page.setViewportSize({width:390,height:844});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2));
  assert.deepEqual(errors,[]);console.log('PASS header print, numeric surface sorting, editable U sorting, keyboard direction, sort persists after recalculation, floor totals and mobile header');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
