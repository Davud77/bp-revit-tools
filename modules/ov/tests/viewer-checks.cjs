const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const fs = require('node:fs');

(async () => {
  const browser = await chromium.launch({headless:true, channel:'msedge'});
  const page = await browser.newPage({viewport:{width:1440,height:1050}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(pathToFileURL(path.resolve('artifacts/demo/heat_loss_viewer.html')).href);
  await page.locator('canvas').waitFor({timeout:60000});
  await page.waitForFunction(() => document.getElementById('load-state').hidden);
  assert.equal(await page.locator('#room-list tr').count(),24);
  assert.equal(await page.evaluate(() => window.injected),undefined);
  await page.locator('#room-list tr').first().click();
  assert.equal(await page.locator('#card-id').textContent(),'9223372036854775807');
  assert.match(await page.locator('#card-title').textContent(), /<script>/);
  await page.locator('#search-box').fill('кабинет');
  assert.equal(await page.locator('#room-list tr').count(),6);
  await page.locator('#search-box').fill('несуществующее');
  assert.equal(await page.locator('#room-list tr').count(),0);
  assert.equal(await page.locator('#empty-table').isVisible(),true);
  await page.locator('#search-box').fill('');
  await page.locator('[data-slice="single"]').click();
  assert.equal(await page.locator('#room-list tr').count(),8);
  assert.equal(await page.locator('#btn-view-focus').isDisabled(),true);
  await page.locator('#level-slider').evaluate(el => { el.value = 1; el.dispatchEvent(new Event('input')); });
  assert.equal(await page.locator('#room-list tr').count(),8); // same level name as floor 0, distinct identity
  await page.locator('[data-slice="upTo"]').click();
  assert.equal(await page.locator('#room-list tr').count(),16);
  for (const mode of ['volume','level','area']) await page.locator(`[data-mode="${mode}"]`).click();
  await page.locator('[data-slice="all"]').click();
  await page.locator('#room-list tr').first().click();
  await page.locator('#btn-view-focus').click();
  await page.locator('#btn-view-top').click();
  await page.waitForTimeout(500);
  const rect = await page.locator('canvas').boundingBox();
  // Pick an upper-floor room through the actual Raycaster, independently of the table.
  let picked = false;
  for (let y = .3; y < .8 && !picked; y += .15) for (let x = .25; x < .85 && !picked; x += .15) {
    await page.mouse.click(rect.x+rect.width*x,rect.y+rect.height*y);
    picked = (await page.locator('#room-list tr.selected').getAttribute('data-key'))?.startsWith('link-2/');
  }
  assert.equal(picked,true,'raycaster selects a visible room');
  await page.locator('#btn-view-iso').click();
  await page.waitForTimeout(400);
  await page.screenshot({path:'artifacts/viewer-desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});
  await page.waitForTimeout(300);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),true,'mobile has no horizontal overflow');
  await page.screenshot({path:'artifacts/viewer-mobile.png',fullPage:true});
  assert.deepEqual(errors,[]);
  const offline = await browser.newPage();
  await offline.route('https://unpkg.com/**',route=>route.abort());
  await offline.goto(pathToFileURL(path.resolve('artifacts/demo/heat_loss_viewer.html')).href);
  await offline.waitForFunction(() => document.getElementById('load-state').textContent.includes('3D недоступен'));
  assert.equal(await offline.locator('#room-list tr').count(),24,'table works without CDN');
  await offline.locator('#room-list tr').first().click();
  assert.equal(await offline.locator('#card-id').textContent(),'9223372036854775807');
  console.log('PASS file://, WebGL, table, search, 64-bit IDs, XSS, slices, duplicate levels, colors, camera, Raycaster, mobile, CDN failure');
  fs.writeFileSync('artifacts/browser-checks.json',JSON.stringify({passed:true,errors},null,2));
  await browser.close();
})().catch(error => { console.error(error); process.exit(1); });
