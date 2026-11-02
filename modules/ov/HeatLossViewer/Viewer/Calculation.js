// Pure SI heat balance; no Autodesk, DOM or Three.js dependencies.
globalThis.HeatLossEngine = (() => {
  const defaults = Object.freeze({indoor:20,outdoor:-25,ground:5,airChanges:.5,wall:.28,window:1.1,door:1.5,roof:.2,floor:.3,reviewed:false});
  function calculate(room, openings, settings, overrides = {}) {
    const missing = [], components = [], surfaces = room.surfaces || [];
    const input = {...settings,...(settings.roomOverrides?.[room.key] || {})};
    const finite = key => typeof input[key] === 'number' && Number.isFinite(input[key]);
    if (!finite('indoor') || !finite('outdoor') || !finite('ground') || !finite('airChanges') || input.airChanges < 0)
      return {total:null,transmission:null,air:null,missing:['Исходные данные'],components:[],status:'demo'};
    const relevant = openings.filter(o=>o.roomKeys.includes(room.key));
    const hostAreas = new Map();
    for (const s of surfaces) hostAreas.set(s.hostKey,(hostAreas.get(s.hostKey)||0)+s.grossArea);
    for (const s of surfaces) {
      const override = overrides[s.key] || {};
      const exposure = override.exposure || s.exposure;
      if (exposure === 'inside' || exposure === 'ignore') continue;
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
    const air = .33*input.airChanges*volume*Math.max(0,input.indoor-input.outdoor);
    const transmission = components.reduce((s,c)=>s+c.q,0);
    if (!surfaces.length) missing.push('Геометрия ограждений');
    if(room.envelopeStatus==='demo' && surfaces.length) missing.push('Неполная геометрия ограждений');
    // Keep incomplete rooms out of aggregate totals, instead of treating absent surfaces as zero.
    const total = missing.length ? null : transmission+air;
    return {total,transmission,air,components,missing,volume,volumeSource:room.volume>0?'revit':'area*height-demo',
      specificArea:total===null ? null : total/room.area,
      specificVolume:total===null ? null : total/volume,
      status:input.reviewed ? 'beta' : 'demo'};
  }
  return {defaults,calculate};
})();
