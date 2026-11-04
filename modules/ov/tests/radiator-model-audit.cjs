const fs=require('node:fs'),vm=require('node:vm');
vm.runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/Calculation.js','utf8'));
vm.runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/RadiatorSelection.js','utf8'));
const building=JSON.parse(fs.readFileSync(process.argv[2] || require('node:path').join(require('node:os').homedir(),'Documents','RevitHeatLossExport','building.json'),'utf8'));
const catalog=JSON.parse(fs.readFileSync('HeatLossViewer/Viewer/RadiatorCatalog.json','utf8'));
const settings={...RadiatorSelection.defaults},thermal={...HeatLossEngine.defaults};
const start=Date.now();const rooms=building.rooms.map(room=>RadiatorSelection.select(room,HeatLossEngine.calculate(room,building.openings,thermal),building.openings,catalog.products,settings,thermal));
const status=rooms.reduce((s,r)=>(s[r.status]=(s[r.status] || 0)+1,s),{});
const output={projectName:building.projectName,status:'beta-proposal',durationMs:Date.now()-start,settings,byStatus:status,
 totalDevices:rooms.reduce((s,r)=>s+r.devices.length,0),installedWatts:rooms.reduce((s,r)=>s+(r.installed || 0),0),
 rooms};
fs.writeFileSync('../../Выходные данные/Проверки ОВ/radiator-model-audit.json',JSON.stringify(output,null,2));
console.log(JSON.stringify({...output,rooms:undefined},null,2));
