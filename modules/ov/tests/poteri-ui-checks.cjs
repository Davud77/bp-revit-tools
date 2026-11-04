const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
  const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.resolve(process.argv[2] || '../../Выходные данные/Проверки ОВ/demo/heat_loss_viewer.html')).href);
  await page.locator('#room-list tr').first().click();const original=await page.locator('#card-loss').textContent();assert.match(original,/Вт/);
  assert.equal(await page.locator('#calculation-method,#air-method,#inputs-panel').count(),0);
  await page.locator('#room-parameters summary').click();await page.locator('#room-indoor').fill('26');await page.locator('#room-indoor').press('Tab');
  assert.notEqual(await page.locator('#card-loss').textContent(),original);
  await page.locator('#room-indoor').fill('');await page.locator('#room-indoor').press('Tab');assert.equal(await page.locator('#card-loss').textContent(),original);
  assert.deepEqual(errors,[]);console.log('PASS sole Poteri UI, room override and clearing back to native project/floor inheritance');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
