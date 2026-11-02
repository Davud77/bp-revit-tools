const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const {pathToFileURL} = require('node:url');

(async () => {
  const directory = process.argv[2] || path.join(os.homedir(),'Documents','RevitHeatLossExport');
  const data = JSON.parse(fs.readFileSync(path.join(directory,'building.json'),'utf8'));
  assert.equal(data.rooms.length,185);
  assert.equal(data.warnings.length,0);
  assert.equal(new Set(data.rooms.map(r=>r.key)).size,185);
  assert.equal(data.rooms.reduce((s,r)=>s+r.boundariesCount,0),2219);
  assert.ok(Math.abs(data.rooms.reduce((s,r)=>s+r.area,0)-3164.053095908744)<1e-6);
  for (const r of data.rooms) {
    assert.ok(r.area>0 && r.height>0 && r.polygons.length>0);
    assert.ok(Math.abs(r.height*r.area-r.volume)<1e-6);
    for (const p of r.polygons) for (const ring of [p.contour,...p.holes]) {
      assert.ok(ring.length>=3 && ring.every(p=>p.length===2 && p.every(Number.isFinite)));
    }
  }
  const browser = await chromium.launch({headless:true,channel:'msedge'});
  const page = await browser.newPage({viewport:{width:1600,height:1050}});
  const errors = []; page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(directory,'heat_loss_viewer.html')).href);
  await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  assert.equal(await page.locator('#room-list tr').count(),185);
  await page.locator('[data-slice="single"]').click();
  const levels = [...new Map(data.rooms.map(r=>[r.levelKey,r])).values()].sort((a,b)=>a.elevation-b.elevation);
  const slices = [];
  for (let index=0;index<levels.length;index++) {
    await page.locator('#level-slider').evaluate((el,value)=>{el.value=value;el.dispatchEvent(new Event('input'));},index);
    const expected = data.rooms.filter(r=>r.levelKey===levels[index].levelKey).length;
    assert.equal(await page.locator('#room-list tr').count(),expected);
    await page.locator('#room-list tr').first().click();
    const key = await page.locator('#room-list tr.selected').getAttribute('data-key');
    assert.equal(await page.locator('#card-id').textContent(),data.rooms.find(r=>r.key===key).idText);
    slices.push({level:levels[index].levelName,rooms:expected});
  }
  await page.locator('[data-slice="all"]').click();
  await page.locator('#search-box').fill('Спальня');
  assert.equal(await page.locator('#room-list tr').count(),data.rooms.filter(r=>`${r.number} ${r.name} ${r.levelName}`.toLowerCase().includes('спальня')).length);
  await page.locator('#search-box').fill('');
  await page.locator('[data-mode="level"]').click();
  await page.locator('#btn-view-iso').click();
  await page.waitForTimeout(300);
  await page.screenshot({path:'artifacts/model-viewer.png',fullPage:true});
  await page.locator('#btn-view-top').click();
  await page.waitForTimeout(300);
  await page.screenshot({path:'artifacts/model-top.png',fullPage:true});
  await page.locator('#btn-view-iso').click();
  assert.deepEqual(errors,[]);
  const report = {passed:true,source:'Блок 1 от 12.07.26.rvt (неизменённая тестовая копия)',
    totalRooms:data.totalRooms,totalBoundaries:data.totalBoundaries,warnings:data.warnings,
    totalArea:data.rooms.reduce((s,r)=>s+r.area,0),slices,browserErrors:errors};
  fs.writeFileSync('artifacts/model-checks.json',JSON.stringify(report,null,2));
  console.log(JSON.stringify(report,null,2));
  await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
