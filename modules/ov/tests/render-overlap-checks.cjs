const {chromium}=require('playwright'),{PNG}=require('pngjs'),assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{pathToFileURL}=require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1600,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.resolve(process.argv[2])).href);await page.waitForFunction(()=>document.getElementById('load-state').hidden);
  const canvas=page.locator('#canvas-container canvas'),out=path.resolve(process.argv[3] || '../../Выходные данные/Проверки ОВ/render-overlap');fs.mkdirSync(out,{recursive:true});
  for(const mode of ['iso','top','transparent','dark']) {
    if(mode==='iso')await page.locator('#btn-view-iso').click();
    if(mode==='top')await page.locator('#btn-view-top').click();
    if(mode==='transparent')await page.locator('#show-shell').check();
    if(mode==='dark')await page.locator('#btn-theme').click();
    await page.waitForTimeout(700);
    const a=await canvas.screenshot(),b=await canvas.screenshot();
    const first=PNG.sync.read(a),second=PNG.sync.read(b);let changed=0;
    for(let p=0;p<first.data.length;p+=4)if(Math.max(...[0,1,2].map(c=>Math.abs(first.data[p+c]-second.data[p+c])))>3)changed++;
    assert.ok(changed<first.width*first.height*.0001,`${mode}: unstable pixels ${changed}`);
    fs.writeFileSync(path.join(out,mode+'.png'),a);
  }
  assert.deepEqual(errors,[]);console.log('PASS coincident-room rendering, stable opaque/transparent frames, top/isometric and dark views without WebGL/script errors');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
