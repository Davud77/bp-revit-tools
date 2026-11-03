globalThis.OvLibraryInputs=(()=>{
 function create(library,data){
  const result={};if(!library)return result;
  const types=new Map((library.constructions || []).map(t=>[t.key,t])),materials=new Map((library.materials || []).map(m=>[m.key,m]));
  const value=t=>{
   const input={n:t.n??1};
   if(t.useLayers){
    const layers=(t.layers || []).filter(l=>l.thickness>0);
    if(!layers.length || layers.some(l=>!(materials.get(l.materialKey)?.lambda>0)))input.thermalError='Не заданы теплопроводности слоёв типа «'+t.name+'»';
    else{input.layers=layers.map(l=>({thickness:l.thickness,lambda:materials.get(l.materialKey).lambda}));input.homogeneity=t.homogeneity??1;}
   }else if(t.r0>0)input.u=1/t.r0;else input.thermalError='Не задано R₀ типа «'+t.name+'»';
   if(['window','door'].includes(t.kind))Object.assign(input,{gn:t.gn,f:t.f,ai:t.ai});
   return input;
  };
  const openings=new Map((data.openings || []).map(o=>[o.key,o]));
  for(const o of data.openings || []){const t=types.get(o.typeKey);if(t?.useInCalculation)result[o.key]=value(t);}
  for(const room of data.rooms || [])for(const surface of room.surfaces || []){
   const t=types.get(surface.typeKey);
   if(t?.useInCalculation){result[surface.key]=value(t);if(t.groundFloor && surface.kind==='floor' && surface.exposure!=='inside')result[surface.key].exposure='ground';}
   for(const cut of surface.cuts || []){const ot=types.get(openings.get(cut.openingKey)?.typeKey);if(ot?.useInCalculation)result[cut.key]=value(ot);}
  }
  return result;
 }
 const merge=(base,overrides)=>Object.fromEntries([...new Set([...Object.keys(base),...Object.keys(overrides)])].map(key=>{
  const manual=overrides[key] || {},value={...base[key],...manual};
  if(manual.u>0){value.layers=undefined;value.thermalError=null;}
  else if(manual.layers?.length && manual.layers.every(l=>l.thickness>0 && l.lambda>0)){value.u=undefined;value.thermalError=null;}
  return [key,value];
 }));
 return {create,merge};
})();
