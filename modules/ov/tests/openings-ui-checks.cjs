const {chromium}=require('playwright'),{PNG}=require('pngjs'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1600,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.resolve(process.argv[2])).href);await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  const data=await page.locator('#building-data').textContent(),building=JSON.parse(data);assert.equal(building.openings.length,5);assert.equal(building.openings.filter(o=>o.isEmpty).length,2);assert.equal(building.openings.filter(o=>!o.roomKeys.length).length,1);
  const canvas=page.locator('#canvas-container canvas');
  for(const theme of ['light','dark']) {
    if(theme==='dark')await page.locator('#btn-theme').click();
    const image=PNG.sync.read(await canvas.screenshot());let red=0;
    for(let i=0;i<image.data.length;i+=4)if(image.data[i]>240 && image.data[i+1]<20 && image.data[i+2]<20)red++;
    assert.ok(red>5,`${theme}: red door pixels must be visible, got ${red}`);
  }
  await page.screenshot({path:path.resolve(process.argv[3] || '../../Выходные данные/Проверки ОВ/openings-verified.png'),fullPage:true});
  assert.deepEqual(errors,[]);console.log('PASS native Revit fixture HTML: five openings, two empty voids, preserved unassociated object, visible red doors in both themes');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
