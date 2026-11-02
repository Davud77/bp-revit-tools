const fs=require('node:fs');const path=require('node:path');const assert=require('node:assert/strict');
require('node:vm').runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/Calculation.js','utf8'));
const directory=process.argv[2] || 'artifacts/revit-final/Блок 5 от 13.07.26';
const data=JSON.parse(fs.readFileSync(path.join(directory,'building.json'),'utf8'));
assert.equal(data.rooms.length,142);assert.equal(data.openings.length,1006);
assert.ok(Math.abs(data.rooms.reduce((sum,r)=>sum+r.area,0)-2058.4251797707484)<1e-6);
assert.equal(new Set(data.openings.map(o=>o.key)).size,data.openings.length);
let measured=0,cuts=0,filled=0,gross=0,net=0,voidArea=0,complete=0,total=0;
const incomplete=[];
for(const room of data.rooms){
 for(const s of room.surfaces){
  if(s.netArea==null)continue;
  measured++;cuts+=s.cuts.length;filled+=s.cuts.filter(c=>c.openingKey).length;gross+=s.grossArea;net+=s.netArea;
  const area=s.cuts.reduce((sum,c)=>sum+c.area,0);voidArea+=area;
  assert.ok(s.netArea>=0 && s.netArea<=s.grossArea+.005);
  assert.ok(Math.abs(s.grossArea-s.netArea-area)<.01,'gross = net + union of actual cuts');
  for(const cut of s.cuts){assert.ok(cut.area>0);assert.ok(cut.triangles.length>0);if(cut.openingKey)assert.ok(data.openings.some(o=>o.key===cut.openingKey));}
 }
 const r=HeatLossEngine.calculate(room,data.openings,{...HeatLossEngine.defaults});
 if(r.total===null)incomplete.push({key:room.key,number:room.number,missing:r.missing});
 else{complete++;total+=r.total;assert.ok(r.total>=0);}
}
assert.ok(measured>=1443);assert.ok(cuts>=370);assert.ok(complete>0);
const report={project:data.projectName,rooms:data.rooms.length,openings:data.openings.length,measuredWallFaces:measured,cuts,filledCuts:filled,
 grossWallArea:gross,netWallArea:net,voidArea,calculatedDemoRooms:complete,unresolvedRooms:incomplete.length,demoHeatLossWatts:total,status:'demo',incomplete};
fs.writeFileSync('artifacts/geometry-and-calculation-report.json',JSON.stringify(report,null,2));
console.log(JSON.stringify({...report,incomplete:undefined},null,2));
