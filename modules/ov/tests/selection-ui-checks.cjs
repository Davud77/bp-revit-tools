const {chromium}=require('playwright'),assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1600,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.resolve(process.argv[2] || '../../Выходные данные/Проверки ОВ/demo/heat_loss_viewer.html')).href);
  await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  const rows=page.locator('#room-list tr'),selected=()=>page.locator('#room-list tr[aria-selected=true]').count();
  await rows.nth(0).click();await rows.nth(1).click({modifiers:['Control']});assert.equal(await selected(),2);
  await rows.nth(0).click({modifiers:['Shift']});assert.equal(await selected(),1);
  await rows.nth(2).click();assert.equal(await selected(),1);assert.equal(await rows.nth(2).getAttribute('aria-selected'),'true');
  await rows.nth(2).click({modifiers:['Shift']});assert.equal(await selected(),0);
  await rows.nth(0).click();await rows.nth(1).click({modifiers:['Control']});await rows.nth(1).press('Escape');assert.equal(await selected(),0);
  const canvas=page.locator('#canvas-container canvas'),box=await canvas.boundingBox();
  const count=selected;
  const candidates=[];for(const y of [.2,.35,.5,.65,.8])for(const x of [.2,.35,.5,.65,.8])candidates.push({x:box.width*x,y:box.height*y});
  let first=null;
  for(const point of candidates){await canvas.click({position:point});if(await count()===1){first=point;break;}}
  assert.ok(first,'A visible 3D object must be selectable');
  let second=false;
  for(const point of candidates){await canvas.click({position:point,modifiers:['Control']});if(await count()===2){second=true;break;}}
  assert.ok(second,'Ctrl must add a different visible 3D object');
  await canvas.click({position:first,modifiers:['Shift']});assert.equal(await count(),1);
  await page.locator('#card-clear').click();assert.equal(await count(),0);
  await page.screenshot({path:'../../Выходные данные/Проверки ОВ/selection-verified.png',fullPage:true});
  assert.deepEqual(errors,[]);console.log('PASS multi-selection and Shift subtraction through actual table and 3D pointer events, replacement, Esc and clear');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
