// Pure catalog sizing and proposed placement. Coordinates remain in the Revit host system, in meters.
globalThis.RadiatorSelection = (() => {
 const defaults=Object.freeze({buildingType:'residential',policyMode:'current',mount:'wall',series:'X2 Profil',panelType:'',supply:70,return:55,
  reservePercent:0,coveragePercent:50,maxDevicesPerOpening:3,maxHeightMm:600,floorGapMm:100,topGapMm:50,wallGapMm:25,sideGapMm:20,
  floorRecessMm:0,installationFactor:1,roomOverrides:{}});
 const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0);
 function logMean(supply,ret,room){
  const a=supply-room,b=ret-room;
  if(![a,b].every(Number.isFinite)||a<=0||b<=0||supply<ret)return null;
  return Math.abs(a-b)<1e-8?(a+b)/2:(a-b)/Math.log(a/b);
 }
 function output(product,supply,ret,room,factor=1){
  const dt=logMean(supply,ret,room);
  const ref=product.referenceDelta ?? logMean(product.referenceSupply,product.referenceReturn,product.referenceRoom);
  if(dt===null || !(ref>0) || !(product.qNominal>0) || !(product.n>0) || !(factor>0 && factor<=1))return null;
  return product.qNominal*Math.pow(dt/ref,product.n)*factor;
 }
 function inside(room,x,y){
  const ring=(points)=>{let yes=false;for(let i=0,j=points.length-1;i<points.length;j=i++){
   const a=points[i],b=points[j];if((a[1]>y)!=(b[1]>y)&&x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0])yes=!yes;
  }return yes;};
  return room.polygons.some(p=>ring(p.contour)&&!p.holes.some(ring));
 }
 function intervalUnion(intervals){
  const sorted=intervals.filter(i=>i[1]-i[0]>1e-7).sort((a,b)=>a[0]-b[0]),result=[];
  for(const i of sorted){const last=result[result.length-1];if(last && i[0]<=last[1]+1e-5)last[1]=Math.max(last[1],i[1]);else result.push([...i]);}
  return result;
 }
 function intersect(a,b){const out=[];for(const x of a)for(const y of b){const i=[Math.max(x[0],y[0]),Math.min(x[1],y[1])];if(i[1]-i[0]>1e-7)out.push(i);}return intervalUnion(out);}
 function section(surface,tangent,z){
  const intervals=[];
  for(const t of surface.triangles || []){
   const p=[[t[0],t[1],t[2]],[t[3],t[4],t[5]],[t[6],t[7],t[8]]],crossings=[];
   for(let i=0;i<3;i++){
    const a=p[i],b=p[(i+1)%3];if(z<Math.min(a[2],b[2])-1e-7||z>Math.max(a[2],b[2])+1e-7)continue;
    if(Math.abs(a[2]-b[2])<1e-8){crossings.push(dot(a,tangent),dot(b,tangent));continue;}
    const k=(z-a[2])/(b[2]-a[2]);if(k>=-1e-6&&k<=1+1e-6)crossings.push(dot(a,tangent)+(dot(b,tangent)-dot(a,tangent))*k);
   }
   if(crossings.length>=2)intervals.push([Math.min(...crossings),Math.max(...crossings)]);
  }
  return intervalUnion(intervals);
 }
 function surfaceIntervals(surface,tangent,base,height){
  if(surface.triangles?.length){
   // Changes can occur at any vertex, not only the radiator's top and bottom.
   const levels=new Set([base+.0001,base+height-.0001,base+height/2]);
   for(const t of surface.triangles)for(let i=2;i<9;i+=3)if(t[i]>base+.0002&&t[i]<base+height-.0002){levels.add(t[i]-.0001);levels.add(t[i]+.0001);}
   let allowed=null;for(const z of levels){const ranges=section(surface,tangent,z);allowed=allowed===null?ranges:intersect(allowed,ranges);if(!allowed.length)return [];}
   return allowed || [];
  }
  if(!surface.boundsMin?.length || !surface.boundsMax?.length)return [];
  const values=[];for(const x of [surface.boundsMin[0],surface.boundsMax[0]])for(const y of [surface.boundsMin[1],surface.boundsMax[1]])values.push(dot([x,y,0],tangent));
  return [[Math.min(...values),Math.max(...values)]];
 }
 function effective(room,settings){return {...defaults,...settings,...(settings.roomOverrides?.[room.key]||{})};}
 function slots(room,openings,settings,surfaceOverrides={}){
  const s=effective(room,settings),surfaces=(room.surfaces||[]).filter(w=>w.kind==='wall' && w.hostKey &&
   (surfaceOverrides[w.key]?.exposure || w.exposure)==='outside' && w.normal?.length===3 && Math.abs(w.normal[2])<.2);
  const windows=openings.filter(o=>o.kind==='window'&&o.roomKeys.includes(room.key)&&surfaces.some(w=>w.hostKey===o.hostKey || o.surfaceKeys?.[room.key]===w.key));
  const result=[];
  for(const window of windows){
   const matches=surfaces.filter(w=>w.hostKey===window.hostKey || window.surfaceKeys?.[room.key]===w.key);
   const surface=matches.sort((a,b)=>Math.abs(dot(a.normal,window.position.map((v,i)=>v-a.point[i])))-Math.abs(dot(b.normal,window.position.map((v,i)=>v-b.point[i]))))[0];
   if(!surface)continue;
   const tangent=[surface.normal[1],-surface.normal[0],0];const length=Math.hypot(...tangent);for(let i=0;i<3;i++)tangent[i]/=length;
   const center=[...window.position];const distance=dot(surface.normal,center.map((v,i)=>v-surface.point[i]));for(let i=0;i<3;i++)center[i]-=surface.normal[i]*distance;
   result.push({key:window.key,windowKey:window.key,location:'under-window',width:window.width,center,tangent,normal:surface.normal,surface,
    sill:window.position[2]-window.height/2-room.baseElevation,maxLength:Math.max(0,window.width-2*s.sideGapMm/1000)});
  }
  if(!windows.length || s.mount==='vertical'){
   for(const surface of surfaces){
    if(s.wallKey && surface.key!==s.wallKey)continue;
    const tangent=[surface.normal[1],-surface.normal[0],0],length=Math.hypot(...tangent);for(let i=0;i<3;i++)tangent[i]/=length;
    const bounds=[];for(const x of [surface.boundsMin[0],surface.boundsMax[0]])for(const y of [surface.boundsMin[1],surface.boundsMax[1]])bounds.push(dot([x,y,0],tangent));
    result.push({key:surface.key,location:'external-wall',width:Infinity,center:surface.point,tangent,normal:surface.normal,surface,sill:Infinity,maxLength:Math.max(...bounds)-Math.min(...bounds)-2*s.sideGapMm/1000});
   }
  }
  return result;
 }
 function fit(room,slot,product,settings,doors=[]){
  const s=effective(room,settings),length=product.lengthMm/1000,height=product.heightMm/1000,depth=(product.depthMm||100)/1000;
  const special=s.buildingType!=='residential'&&s.buildingType!=='public';
  const gap=Math.max(s.floorGapMm,special?100:60)/1000,back=Math.max(s.wallGapMm,special?60:25)/1000,top=Math.max(s.topGapMm,50)/1000;
  const trench=['trench','trench-fan'].includes(s.mount);
  if(trench && s.floorRecessMm<product.heightMm)return null;
  if(!trench && height>s.maxHeightMm/1000+.00001)return null;
  if(!trench && height+gap>room.height)return null;
  if(s.mount==='wall'&&slot.windowKey && height+gap+top>slot.sill+.001)return null;
  if(length>slot.maxLength+.001)return null;
  const wallBottom=slot.surface.boundsMin?.[2] ?? room.baseElevation;
  const floorReference=Math.max(room.baseElevation,wallBottom);
  if(floorReference-room.baseElevation>.4)return null;
  const base=floorReference+gap;
  if(!trench && base+height>room.baseElevation+room.height+.001)return null;
  if(s.mount==='wall'&&slot.windowKey && base+height+top>room.baseElevation+slot.sill+.001)return null;
  let allowed=(s.mount==='wall' || (s.mount==='vertical'))?surfaceIntervals(slot.surface,slot.tangent,base,height):surfaceIntervals(slot.surface,slot.tangent,floorReference+.15,.05);
  // A freestanding / trench device may stand in front of glazing; no solid wall support is required.
  if(s.mount==='floor'||trench){
   if(slot.windowKey){const c=dot(slot.center,slot.tangent);allowed=[[c-slot.width/2,c+slot.width/2]];}
  }
  const preferred=dot(slot.center,slot.tangent);let best=null;
  for(const interval of allowed){
   const lo=interval[0]+length/2+s.sideGapMm/1000,hi=interval[1]-length/2-s.sideGapMm/1000;if(lo>hi+.00001)continue;
   const u=Math.max(lo,Math.min(hi,preferred));const center=slot.center.map((v,i)=>v+slot.tangent[i]*(u-preferred)-slot.normal[i]*(back+depth/2));
   center[2]=trench?floorReference-height/2:base+height/2;
   let fits=true;
   for(const a of [-1,1])for(const b of [-1,1]){
    const p=center.map((v,i)=>v+slot.tangent[i]*a*length/2+slot.normal[i]*b*depth/2);
    if(!inside(room,p[0],p[1]))fits=false;
   }
   if(!fits)continue;
   if(doors.some(door=>{
    const delta=door.position.map((v,i)=>v-center[i]);
    const along=Math.abs(dot(delta,slot.tangent)),across=Math.abs(dot(delta,slot.normal));
    return across<depth/2+.4 && along<(length+door.width)/2+.05 && center[2]+height/2>door.position[2]-door.height/2 && center[2]-height/2<door.position[2]+door.height/2;
   }))continue;
   const coverage=slot.windowKey?Math.max(0,Math.min(u+length/2,preferred+slot.width/2)-Math.max(u-length/2,preferred-slot.width/2))/slot.width:null;
   const candidate={position:center,tangent:slot.tangent,normal:slot.normal,coverage,location:slot.location,windowKey:slot.windowKey,surfaceKey:slot.surface.key,
    floorElevation:floorReference,floorReferenceSource:floorReference>room.baseElevation+.001?'wall-base-beta':'room-base'};
   if(!best || Math.abs(u-preferred)<best.shift)best={...candidate,shift:Math.abs(u-preferred)};
  }
  return best;
 }
 function select(room,heat,openings,catalog,settings,thermalSettings,surfaceOverrides={}){
  const s=effective(room,settings),indoor=thermalSettings.roomOverrides?.[room.key]?.indoor ?? thermalSettings.floorOverrides?.[room.levelKey]?.indoor ?? thermalSettings.indoor;
  const requiredRaw=s.targetWatts!==undefined?s.targetWatts:heat?.total;
  const special=!['residential','public'].includes(s.buildingType);
  const result={roomKey:room.key,requiredRaw,required:null,devices:[],warnings:[],status:'demo',reason:'Нужна тепловая нагрузка',coverageRule:s.policyMode==='current'&&special?'maximum-feasible':'project-percent'};
  result.targetSource=s.sourceLoad || {type:s.targetWatts!==undefined?'manual':'heat-loss-calculation',status:heat?.status || 'demo'};
  const coveragePercent=s.policyMode==='legacy'?(special?75:50):s.coveragePercent;
  if(s.excluded){result.status='excluded';result.reason='Исключено пользователем';return result;}
  if(!Number.isFinite(requiredRaw)||requiredRaw<0)return result;
  if(logMean(s.supply,s.return,indoor)===null || !Number.isFinite(s.reservePercent)||s.reservePercent<0 || !(s.installationFactor>0&&s.installationFactor<=1) || !(coveragePercent>=0&&coveragePercent<=100) || !(s.maxHeightMm>0)) {result.reason='Проверьте температуры и коэффициенты';return result;}
  result.required=requiredRaw*(1+s.reservePercent/100);
  if(result.required===0){result.status='zero';result.reason='Тепловая нагрузка равна нулю';return result;}
  if(/тамбур/i.test(room.name)&&openings.some(o=>o.kind==='door'&&o.isExterior&&o.roomKeys.includes(room.key))){result.status='restricted';result.reason='Тамбур с наружной дверью: размещение ограничено п. 6.4.9 СП 60';return result;}
  if(/лестнич|коридор|холл|вестибюл/i.test(room.name)&&!s.accessReviewed){result.status='restricted';result.reason='Нужно проверить эвакуационные проходы и ограждение прибора';return result;}
  const products=catalog.filter(p=>p.mounts?.includes(s.mount)&&(!s.series||p.series===s.series)&&(!s.panelType||p.panelType===s.panelType));
  if(!products.length){result.status='catalog-missing';result.reason='Добавьте каталог для выбранного типа';return result;}
  const available=slots(room,openings,s,surfaceOverrides);
  if(!available.length){result.status='placement-missing';result.reason='Нет доступного окна или наружной стены: требуется место вручную';return result;}
  const windows=available.filter(x=>x.windowKey),used=new Set();
  const selectedSlots=windows.length?windows:available.sort((a,b)=>b.surface.grossArea-a.surface.grossArea);
  let remaining=result.required;
  const windowWeight=windows.reduce((sum,w)=>sum+w.width,0);
  const doors=openings.filter(o=>o.kind==='door'&&o.roomKeys.includes(room.key));
  for(const slot of selectedSlots){
   if(!windows.length && remaining<=.1)break;
   const need=windows.length?result.required*slot.width/windowWeight:remaining;
   const candidates=[];
   const families=new Map();
   for(const p of products){
    const family=[p.series,p.deviceType,p.panelType,p.heightMm,p.depthMm,p.n,p.referenceDelta,p.referenceSupply,p.referenceReturn,p.referenceRoom,
     (p.qNominal/p.lengthMm).toFixed(6),p.source,p.fanMode].join('|');
    if(!families.has(family))families.set(family,[]);families.get(family).push(p);
   }
   for(const family of families.values()){
    const arrangements=new Map(),ordered=family.sort((a,b)=>a.lengthMm-b.lengthMm),separation=.05;
    let states=new Map([[0,[]]]);
    for(let count=1;count<=Math.max(1,Math.min(6,Math.trunc(s.maxDevicesPerOpening || 3)));count++){
     const next=new Map();
     for(const [activeLength,members] of states)for(const product of ordered){
      const sum=activeLength+product.lengthMm,total=sum+(count-1)*separation*1000;
      if(total>slot.maxLength*1000+.001)break;
      const key=Math.round(sum*1000)/1000;
      if(!next.has(key))next.set(key,[...members,product]);
     }
     for(const [sum,members] of next)arrangements.set(count+'|'+sum,members);
     states=next;if(!states.size)break;
    }
    for(const members of arrangements.values()){
     const p=members[0],count=members.length,groupLength=members.reduce((sum,p)=>sum+p.lengthMm,0)+(count-1)*separation*1000;
     const placement=fit(room,slot,{...p,lengthMm:groupLength},s,doors);if(!placement)continue;
     const preferred=dot(slot.center,slot.tangent),parts=[];
     let cursor=-groupLength/2000;
     for(let index=0;index<count;index++){
      const product=members[index],q=output(product,s.supply,s.return,indoor,s.installationFactor);if(q===null)break;
      const shift=cursor+product.lengthMm/2000;cursor+=product.lengthMm/1000+separation;
      const position=placement.position.map((v,i)=>v+slot.tangent[i]*shift),center=dot(position,slot.tangent);
      const actualHalf=product.lengthMm/2000;
      const coverage=slot.windowKey?Math.max(0,Math.min(center+actualHalf,preferred+slot.width/2)-Math.max(center-actualHalf,preferred-slot.width/2))/slot.width:null;
      parts.push({product,power:q,...placement,position,coverage,slotKey:slot.key,groupSize:count});
     }
     if(parts.length!==count)continue;
     const coverage=slot.windowKey?parts.reduce((sum,d)=>sum+d.coverage,0):null;
     if(slot.windowKey && result.coverageRule==='project-percent' && coverage*100+.001<coveragePercent)continue;
     for(const part of parts)part.groupCoverage=coverage;
     candidates.push({product:p,power:parts.reduce((sum,p)=>sum+p.power,0),...placement,coverage,slotKey:slot.key,parts,count});
    }
   }
   if(!candidates.length){result.warnings.push('Нет подходящего габарита: '+slot.key);continue;}
   let allowed=candidates;
   if(slot.windowKey && result.coverageRule==='maximum-feasible'){
    const maximum=Math.max(...candidates.map(c=>c.coverage));allowed=candidates.filter(c=>c.coverage>=maximum-.001);
   }
   const adequate=allowed.filter(c=>c.power>=need);
   const sorted=(adequate.length?adequate:allowed).sort((a,b)=>adequate.length?a.count-b.count||(a.power-need)-(b.power-need)||a.product.heightMm-b.product.heightMm:b.power-a.power);
   const chosen=sorted[0];
   // Multiple free wall subfaces must not result in overlapping devices at the same wall location.
   const overlaps=(a,b)=>{
    const an=[a.tangent[1],-a.tangent[0],0],bn=[b.tangent[1],-b.tangent[0],0],delta=a.position.map((v,i)=>v-b.position[i]);
    for(const axis of [a.tangent,an,b.tangent,bn]){
     const ar=Math.abs(dot(axis,a.tangent))*a.product.lengthMm/2000+Math.abs(dot(axis,an))*(a.product.depthMm || 100)/2000;
     const br=Math.abs(dot(axis,b.tangent))*b.product.lengthMm/2000+Math.abs(dot(axis,bn))*(b.product.depthMm || 100)/2000;
     if(Math.abs(dot(delta,axis))>ar+br+.01)return false;
    }return true;
   };
   if(chosen.parts.some(part=>result.devices.some(previous=>overlaps(part,previous))))continue;
   result.devices.push(...chosen.parts);used.add(slot.key);remaining-=chosen.power;
  }
  result.installed=result.devices.reduce((sum,d)=>sum+d.power,0);
  result.deficit=Math.max(0,result.required-result.installed);
  result.powerCovered=result.deficit<.1;
  result.windowCoverageComplete=windows.every(w=>used.has(w.key));
  result.status=result.powerCovered&&result.windowCoverageComplete?'selected':'insufficient';
  result.reason=result.status==='selected'?'Предварительный подбор':'Не хватает мощности или места под частью окон';
  if(result.devices.some(d=>d.product.depthIsSchematic))result.warnings.push('Глубина прибора условная; проверьте монтажный габарит и крепления');
  if(result.devices.some(d=>d.floorReferenceSource==='wall-base-beta'))result.warnings.push('Отметка пола уточнена по низу стены; проверьте чистый пол и подоконную доску');
  if(s.buildingType!=='residential'&&s.buildingType!=='public')result.warnings.push('Проверьте защитное ограждение, температуру поверхности и применимость типа прибора');
  if(s.mount==='floor')result.warnings.push('Проверьте совместимость напольных опор с выбранным типоразмером');
  if(s.mount==='trench-fan')result.warnings.push('Мощность действительна только для каталожной скорости вентилятора; проверьте питание и шум');
  result.loadStatus=heat?.status || 'manual';
  return result;
 }
 return {defaults,logMean,output,inside,section,surfaceIntervals,slots,fit,select,effective};
})();
