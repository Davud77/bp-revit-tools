const fs=require('node:fs');const path=require('node:path');
require('node:vm').runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/Calculation.js','utf8'));
const source=path.resolve(process.argv[2]);const building=JSON.parse(fs.readFileSync(source,'utf8'));
const settings={...HeatLossEngine.defaults};
const rooms=building.rooms.map(room=>({key:room.key,id:room.idText,...HeatLossEngine.calculate(room,building.openings || [],settings)}));
const complete=rooms.filter(r=>r.total!==null);
const result={schemaVersion:'heatloss-beta-1',status:'demo',projectName:building.projectName,exportedAt:new Date().toISOString(),
 units:{power:'W',temperature:'C',heatTransferCoefficient:'W/(m2*K)'},settings,overrides:{},
 coverage:{totalRooms:rooms.length,calculatedRooms:complete.length,complete:complete.length===rooms.length},
 partialTotals:{total:complete.reduce((s,r)=>s+r.total,0),transmission:complete.reduce((s,r)=>s+r.transmission,0),air:complete.reduce((s,r)=>s+r.air,0)},rooms};
const target=path.join(path.dirname(source),'heat_loss_results.demo.json');fs.writeFileSync(target,JSON.stringify(result,null,2));console.log(target);
