// Pure SI heat balance; no Autodesk, DOM or Three.js dependencies.
globalThis.HeatLossEngine = (() => {
  const defaults = Object.freeze({indoor:20,outdoor:-25,ground:5,wall:.28,window:1.1,door:1.5,roof:.2,floor:.3,reviewed:false,
    method:'poteri',windSpeed:0,buildingHeight:0,groundElevation:0,northAngle:0,orientationAdditions:true,
    windowGn:5,doorGn:7,windowF:.67,doorF:.5,windowAi:1,doorAi:1,cornerBonus:0,heatGain:0});
  const resolve = (room,settings) => ({...settings,...(settings.floorOverrides?.[room.levelKey] || {}),...(settings.roomOverrides?.[room.key] || {}),method:'poteri'});
  function calculate(room, openings, settings, overrides = {}) {
    const missing = [], components = [], surfaces = room.surfaces || [];
    const input = resolve(room,settings);
    const finite = key => typeof input[key] === 'number' && Number.isFinite(input[key]);
    if (!finite('indoor') || !finite('outdoor') || !finite('ground'))
      return {total:null,transmission:null,air:null,missing:['Исходные данные'],components:[],status:'demo'};
    const relevant = openings.filter(o=>o.roomKeys.includes(room.key));
    const hostAreas = new Map();
    for (const s of surfaces) hostAreas.set(s.hostKey,(hostAreas.get(s.hostKey)||0)+s.grossArea);
    for (const s of surfaces) {
      const override = overrides[s.key] || {};
      const exposure = override.exposure || s.exposure;
      if (exposure === 'inside' || exposure === 'ignore') continue;
      if(override.thermalError)missing.push(override.thermalError);
      if (!['outside','ground'].includes(exposure)) { missing.push(s.key); continue; }
      const u = override.u ?? input[s.kind];
      if (!Number.isFinite(u) || u < 0) { missing.push(s.key); continue; }
      const to = exposure === 'ground' ? input.ground : input.outdoor;
      const delta = Math.max(0,input.indoor-to);
      const inserts = s.kind === 'wall' ? relevant.filter(o=>o.hostKey ? o.hostKey===s.hostKey : o.surfaceKeys?.[room.key]===s.key) : [];
      const hostArea = hostAreas.get(s.hostKey) || 0;
      const share = o => o.hostKey ? (hostArea > 0 ? s.grossArea/hostArea : 0) : 1;
      const measured = typeof s.netArea==='number' && Number.isFinite(s.netArea);
      const inserted = inserts.reduce((sum,o)=>sum+o.area*share(o),0);
      if (!measured && inserted > s.grossArea + .01) missing.push('Площадь проёмов превышает стену '+s.key);
      const net = measured ? s.netArea : Math.max(0,s.grossArea-inserted);
      components.push({key:s.key,kind:s.kind,area:net,grossArea:s.grossArea,voidArea:s.grossArea-net,u,delta,q:net*u*delta,exposure,source:s.areaSource || 'beta'});
      if(measured) {
        for(const cut of s.cuts || []) {
          const cutU=overrides[cut.key]?.u ?? (cut.openingKey ? overrides[cut.openingKey]?.u : undefined) ?? input[cut.kind];
          if(!Number.isFinite(cutU) || cutU<0) { missing.push('Вырез без заполнения '+cut.key);continue; }
          components.push({key:cut.key,openingKey:cut.openingKey,kind:cut.kind,area:cut.area,u:cutU,delta,q:cut.area*cutU*delta,exposure,source:'revit-void-geometry'});
        }
      }
      for (const o of measured ? [] : inserts) {
        const openingU = overrides[o.key]?.u ?? input[o.kind];
        if (!Number.isFinite(openingU) || openingU < 0) { missing.push(o.key); continue; }
        const area = o.area*share(o);
        components.push({key:o.key,kind:o.kind,area,u:openingU,delta,q:area*openingU*delta,exposure});
      }
    }
    const volume = room.volume>0 ? room.volume : room.area*room.height;
    let transmission = 0, airTotal=0, infiltration=0;
    {
      if(!globalThis.PoteriEngine) throw new Error('Poteri calculation module is unavailable');
      const legacy=globalThis.PoteriEngine;
      const climate={tempExt:input.outdoor,windSpeed:input.windSpeed || 0,floors:[{height:input.buildingHeight || room.height}]};
      if(input.indoor<=-273 || input.outdoor<=-273 || !Number.isFinite(climate.windSpeed) || climate.windSpeed<0 ||
          !Number.isFinite(input.cornerBonus??0) || (input.cornerBonus??0)<0 || !Number.isFinite(input.heatGain??0) || (input.heatGain??0)<0)missing.push('Параметры Poteri');
      const indoor={tempInt:input.indoor,tempExt:input.outdoor,cornerBonus:input.cornerBonus || 0,area:room.area};
      const directions=['С','СВ','В','ЮВ','Ю','ЮЗ','З','СЗ'];
      const orientation=s=>{
        if(!input.orientationAdditions || !s?.normal || Math.hypot(s.normal[0],s.normal[1])<.5)return '';
        const angle=Math.atan2(s.normal[0],s.normal[1])*180/Math.PI-(input.northAngle || 0);
        return directions[((Math.round(angle/45)%8)+8)%8];
      };
      for(const component of components) {
        const surface=surfaces.find(s=>s.key===component.key || (s.cuts || []).some(c=>c.key===component.key)) ||
          surfaces.find(s=>s.hostKey && s.hostKey===relevant.find(o=>o.key===component.key)?.hostKey);
        const override={...(overrides[component.openingKey || component.key] || {}),...(overrides[component.key] || {})};
        if(override.thermalError)missing.push(override.thermalError);
        const type=component.kind==='roof' || component.kind==='floor'?'slab':component.kind;
        const construction={type,area:component.area,r0:component.u>0?1/component.u:Infinity,n_coef:override.n??1,
          orientation:override.orientation??orientation(surface)};
        if((override.n!=null && (!Number.isFinite(override.n) || override.n<=0)) ||
            (override.homogeneity!=null && (!Number.isFinite(override.homogeneity) || override.homogeneity<=0)))missing.push('Коэффициенты конструкции '+component.key);
        let libraries={};
        if(override.layers!=null && !Array.isArray(override.layers))missing.push('Слои конструкции '+component.key);
        if(Array.isArray(override.layers) && override.layers.length) {
          const template={id:'source',r:override.homogeneity??1,layers:override.layers.map((l,i)=>({material:String(i),thickness:l.thickness}))};
          libraries={materials:override.layers.map((l,i)=>({id:String(i),lambda:l.lambda})),[type==='wall'?'walls':'slabs']:[template]};construction.templateId='source';
          if(override.layers.some(l=>!Number.isFinite(l.thickness) || l.thickness<=0 || !Number.isFinite(l.lambda) || l.lambda<=0))missing.push('Слои конструкции '+component.key);
        }
        if(component.exposure==='ground' && override.groundZones) {
          construction.type='floor_on_ground';const zones=Array.isArray(override.groundZones)?override.groundZones:[];
          if(zones.length!==4 || zones.some(a=>!Number.isFinite(a) || a<0) || Math.abs(zones.reduce((s,a)=>s+a,0)-component.area)>.01)missing.push('Площади зон пола '+component.key);
          zones.forEach((a,i)=>construction['zone'+(i+1)+'Area']=a);
          if(override.insulationR>0)libraries={materials:[{id:'insulation',lambda:1}],groundFloors:[{id:'source',layers:[{material:'insulation',thickness:override.insulationR}]}]},construction.templateId='source';
        }
        const exterior=component.exposure==='ground' && !override.groundZones?input.ground:input.outdoor;
        component.q=Math.max(0,legacy.calculateConstructionHeat(libraries,construction,{...indoor,tempExt:exterior},climate,{}));
        component.orientation=construction.orientation;component.method='poteri';
        component.report={indoor:input.indoor,n:construction.type==='floor_on_ground'?1:construction.n_coef,delta:(input.indoor-exterior)*(construction.type==='floor_on_ground'?1:construction.n_coef),r0:construction.r0};
        if(construction.templateId==='source' && ['wall','slab'].includes(construction.type))
          component.report.r0=legacy.computeWallR0FromTemplate(libraries,(libraries.walls || libraries.slabs)[0]);
        if(construction.type==='floor_on_ground')
          component.report.zones=override.groundZones.map((area,i)=>({area,r0:[2.1,4.3,8.6,14.2][i]+Math.max(0,override.insulationR || 0),q:Math.max(0,area*(input.indoor-exterior)/([2.1,4.3,8.6,14.2][i]+Math.max(0,override.insulationR || 0)))}));
        if(['door','window'].includes(component.kind) && component.exposure==='outside') {
          const opening=relevant.find(o=>o.key===(component.openingKey || component.key));
          const cut=surfaces.flatMap(s=>s.cuts || []).find(c=>c.key===component.key);
          const top=opening?.position?.[2]!=null ? opening.position[2]+opening.height/2 : cut?.boundsMax?.[2];
          const gn=override.gn??input[component.kind+'Gn']??0,f=override.f??input[component.kind+'F'],ai=override.ai??input[component.kind+'Ai'];
          if(![gn,f,ai].every(Number.isFinite) || gn<0 || f<=0 || ai<0)missing.push('Воздухопроницаемость '+component.key);
          if(gn>0 && !Number.isFinite(top))missing.push('Высотная отметка проёма '+component.key);
          const template={id:'opening',gn,f,ai};
          const inf=legacy.getInfiltrationParameters({[component.kind==='window'?'windows':'doors']:[template]},
            {...construction,templateId:'opening',height:(top??room.elevation)-(room.elevation??0)},indoor,climate,
            {baseElevation:(room.elevation??0)-(input.groundElevation || 0)});
          component.infiltration=inf;infiltration+=Math.max(0,inf.qInf);
        }
      }
      transmission=components.reduce((sum,c)=>sum+c.q,0);
      airTotal=infiltration;
    }
    if (!surfaces.length) missing.push('Геометрия ограждений');
    if(room.envelopeStatus==='demo' && surfaces.length) missing.push('Неполная геометрия ограждений');
    // Keep incomplete rooms out of aggregate totals, instead of treating absent surfaces as zero.
    const total = missing.length ? null : Math.max(0,transmission+airTotal-(input.heatGain || 0));
    return {total,transmission,air:airTotal,infiltration,ventilation:0,components,missing,volume,method:'poteri',
      volumeSource:room.volume>0?'revit':'area*height-demo',
      specificArea:total===null ? null : total/room.area,
      specificVolume:total===null ? null : total/volume,
      status:input.reviewed ? 'beta' : 'demo'};
  }
  return {defaults,calculate,resolve};
})();
