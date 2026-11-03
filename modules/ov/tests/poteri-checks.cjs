const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
for(const file of ['Poteri.js','Calculation.js'])vm.runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/'+file,'utf8'));
const engine=HeatLossEngine,legacy=PoteriEngine;
const near=(a,b)=>assert.ok(Math.abs(a-b)<1e-8,`${a} != ${b}`);
const layers={materials:[{id:1,lambda:.76},{id:2,lambda:.035}],walls:[{id:1,r:.92,layers:[{material:1,thickness:.2},{material:2,thickness:.1}]}]};
near(legacy.computeWallR0FromTemplate(layers,layers.walls[0]),.92*(.115+.2/.76+.1/.035+.043));
for(const depth of [0,1,3,7]){
 const result=legacy.calculateGroundFloorZones(10,8,{top:true,left:true},depth);
 near(Object.values(result.floor).reduce((s,a)=>s+a,0),80);
 near(Object.values(result.walls).reduce((s,a)=>s+a,0),18*depth);
}
const room={key:'r',levelKey:'L',area:20,volume:60,height:3,elevation:0,baseElevation:0,
 surfaces:[{key:'wall',hostKey:'w',kind:'wall',exposure:'outside',normal:[0,1,0],grossArea:10,netArea:8,
 cuts:[{key:'cut',openingKey:'window',kind:'window',area:2,boundsMax:[2,0,2.1]}]}]};
const openings=[{key:'window',hostKey:'w',kind:'window',roomKeys:['r'],area:2,position:[1,0,1.5],height:1.2}];
const input={...engine.defaults,method:'poteri',indoor:20,outdoor:-20,wall:.3,window:1.2,buildingHeight:10,windSpeed:5,cornerBonus:.05,heatGain:10};
const result=engine.calculate(room,openings,input);
near(result.transmission,(8*.3+2*1.2)*40*1.15);
const pressure=(10-2.1)*9.81*353*(1/253-1/293)+.5*1.2*25*1.4;
const infiltration=.28*2*Math.pow(pressure/10,.67)*5*40;
near(result.infiltration,infiltration);near(result.air,infiltration);
near(result.total,result.transmission+infiltration-10);near(result.ventilation,0);
const scoped={...input,floorOverrides:{L:{indoor:18,airChanges:.7}},roomOverrides:{r:{indoor:22}}};
assert.equal(engine.resolve(room,scoped).indoor,22);assert.equal(engine.resolve(room,scoped).airChanges,.7);
delete scoped.roomOverrides.r.indoor;assert.equal(engine.resolve(room,scoped).indoor,18);
delete scoped.floorOverrides.L.indoor;assert.equal(engine.resolve(room,scoped).indoor,20);
const zero=engine.calculate(room,openings,input,{window:{gn:0}});near(zero.infiltration,0);
const air=engine.calculate(room,openings,{...input,airMode:'airChanges'});near(air.air,result.infiltration);near(air.ventilation,0);
const floor={...room,surfaces:[{key:'floor',kind:'floor',exposure:'ground',grossArea:20}]};
const ground=engine.calculate(floor,[],{...input,heatGain:0,airMode:'airChanges',airChanges:0,cornerBonus:0},{floor:{groundZones:[5,5,5,5],insulationR:1}});
near(ground.transmission,40*[2.1,4.3,8.6,14.2].reduce((s,r)=>s+5/(r+1),0));
assert.equal(engine.calculate(floor,[],input,{floor:{groundZones:[1,1,1,1]}}).total,null);
const layered=engine.calculate({...room,surfaces:[{...room.surfaces[0],cuts:[],netArea:10}]},[],{...input,cornerBonus:0,heatGain:0},{wall:{layers:[{thickness:.2,lambda:.76},{thickness:.1,lambda:.035}],homogeneity:.92}});
near(layered.transmission,10*40*1.1/(.92*(.115+.2/.76+.1/.035+.043)));
near(layered.components[0].report.r0,.92*(.115+.2/.76+.1/.035+.043));
near(ground.components[0].report.zones.reduce((sum,z)=>sum+z.q,0),ground.transmission);
const half=engine.calculate(room,openings,input,{wall:{n:.5}});near(half.components[0].report.n,.5);near(half.components[0].report.delta,20);
console.log('PASS Poteri layered R, orientation, corner additions, infiltration, single Poteri method (legacy air settings ignored), gains, ground zones and building/floor/room inheritance');
