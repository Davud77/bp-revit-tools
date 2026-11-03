const radiatorEngine=globalThis.RadiatorSelection;
const radiatorCatalog=JSON.parse($('radiator-catalog').textContent);
const radiatorSettings={...radiatorEngine.defaults,...(DATA.ovLibrary?.radiators?.settings || {})};
function recalculateRadiators(){
 const products=[...radiatorCatalog.products,...(DATA.ovLibrary?.radiators?.custom || [])],inputs=currentThermalOverrides();
 radiatorPlans=new Map(rooms.map(room=>[room.key,radiatorEngine.select(room,results.get(room.key),openings,products,radiatorSettings,settings,inputs)]));
 renderRadiatorCard(rooms.find(r=>r.key===selectedKey));refreshRadiatorScene();refresh3D();
}
function renderRadiatorCard(room){
 const plan=room && radiatorPlans.get(room.key);$('rad-preview-devices').replaceChildren();
 $('rad-preview-state').textContent=plan?`${plan.reason || plan.status} · Требуется ${watts(plan.required)} / подобрано ${watts(plan.installed)}. Настройки — в Revit: ОВ → Библиотека → Радиаторы.`:'Выберите помещение. Настройки подбора находятся в Revit: ОВ → Библиотека → Радиаторы.';
 for(const d of plan?.devices || []){const row=document.createElement('tr');for(const value of [d.product.model,watts(d.power),d.windowKey?'Под окном':'Наружная стена'])cell(row,value);$('rad-preview-devices').append(row);}
}
recalculateRadiators();
