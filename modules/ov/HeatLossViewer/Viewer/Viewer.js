const $ = id => document.getElementById(id);
const DATA = JSON.parse($('building-data').textContent);
const fmt = (v, digits = 2) => Number(v).toLocaleString('ru-RU', {maximumFractionDigits: digits});
const rooms = DATA.rooms;
const openings = DATA.openings || [];
const roomKeys=new Set(rooms.map(r=>r.key));
const unboundOpenings=openings.filter(o=>!o.roomKeys?.some(key=>roomKeys.has(key)));
const unboundKeys=new Set(unboundOpenings.map(o=>o.key));
const engine = globalThis.HeatLossEngine;
function projectDefaults() {
  const calculation=DATA.projectConditions?.calculation;
  const values={...engine.defaults,method:'poteri',buildingHeight:Math.max(0,...rooms.map(r=>r.baseElevation+r.height)),...(calculation?.building || {}),floorOverrides:calculation?.floorOverrides || {},roomOverrides:calculation?.roomOverrides || {},reviewed:calculation?.reviewed??false,orientationAdditions:calculation?.orientationAdditions??true};
  const conditions=DATA.projectConditions;
  for(const [source,target] of [['outdoorTemperature','outdoor'],['indoorTemperature','indoor'],['windSpeed','windSpeed'],['buildingHeight','buildingHeight']]) {
    if(Number.isFinite(conditions?.[source]))values[target]=conditions[source];
  }
  return values;
}
let settings = projectDefaults(), overrides = {}, results = new Map(), sortKey = 'number', sortDirection = 1;
let libraryDefaults=globalThis.OvLibraryInputs.create(DATA.ovLibrary,DATA);
const currentThermalOverrides=()=>globalThis.OvLibraryInputs.merge(libraryDefaults,overrides);
const thermalInputsFor=key=>globalThis.OvLibraryInputs.merge({[key]:libraryDefaults[key]},{[key]:overrides[key]})[key];
const roomParameterKeys=['indoor','outdoor','cornerBonus','heatGain'];
const storageKey = 'heatloss-inputs:' + DATA.projectName + ':' + DATA.exportedAt;
try { const saved = JSON.parse(localStorage.getItem(storageKey)); if (saved) { settings = {...settings,...saved.settings,method:'poteri'}; overrides = saved.overrides || {}; } } catch {}
const save = () => { try { localStorage.setItem(storageKey,JSON.stringify({settings,overrides})); } catch {} };
const watts = v => v == null || !Number.isFinite(v) ? 'нет данных' : `${fmt(v,0)} Вт`;
const kindNames = {wall:'Стена',window:'Окно',door:'Дверь',roof:'Покрытие',floor:'Пол',cut:'Вырез'};
function calculateAll() { const inputs=currentThermalOverrides();results = new Map(rooms.map(r=>[r.key,engine.calculate(r,openings,settings,inputs)])); }
calculateAll();
const levels = [...new Map(rooms.map(r => [r.levelKey, {key:r.levelKey, name:r.levelName, elevation:r.elevation}])).values()]
  .sort((a,b) => a.elevation - b.elevation || a.name.localeCompare(b.name, 'ru'));
const levelIndex = new Map(levels.map((l,i) => [l.key,i]));
let selectedKey = null, colorMode = 'area', sliceMode = 'all', selectedLevelIdx = Math.max(0,levels.length - 1);
const selection=new globalThis.ObjectSelection();
const slider = $('level-slider');
slider.max = Math.max(0, levels.length - 1);
slider.value = selectedLevelIdx;
$('project-title').textContent = DATA.projectName;
if(DATA.projectConditions) {
  const conditions=DATA.projectConditions, details=document.createElement('details'),summary=document.createElement('summary');
  details.id='project-conditions';summary.textContent='Сведения о проекте из Revit';details.append(summary);
  for(const [key,label,unit] of [['number','Номер',''],['address','Адрес',''],['climateRegion','Климатический район / зона',''],['climateSubregion','Подрайон',''],['outdoorTemperature','Наружная температура',' °C'],['indoorTemperature','Внутренняя температура',' °C'],['windSpeed','Ветер',' м/с'],['heatingPeriodTemperature','Температура отопительного периода',' °C'],['heatingPeriodDays','Отопительный период',' суток'],['buildingHeight','Высота здания',' м'],['firstFloorHeight','От земли до первого этажа',' м'],['notes','Примечания','']]) {
    const line=document.createElement('div'),value=conditions[key];line.textContent=label+': '+(value==null || value===''?'не задано':value+unit);details.append(line);
  }
  const note=document.createElement('p');note.textContent='Заданные в Revit температуры, ветер и высота использованы как исходные параметры здания. Пустые поля используют значения по умолчанию расчёта. Изменения в браузере сохраняются только в этой выгрузке.';details.append(note);
  $('totals-panel').append(details);
}
if(!rooms.length) document.querySelectorAll('.color-btn,.slice-btn').forEach(b=>b.disabled=true);
const option = (value,text) => { const o=document.createElement('option');o.value=value;o.textContent=text;return o; };
for(const level of levels) $('floor-filter').append(option(level.key,level.name));
for(const name of [...new Set(rooms.map(r=>r.name))].sort((a,b)=>a.localeCompare(b,'ru'))) $('type-filter').append(option(name,name));
const valueFor = (r,key) => key in r ? r[key] : results.get(r.key)?.[key];
if (DATA.warnings?.length) {
  $('no-warnings').hidden=true;
  $('warnings').hidden = false;
  $('warning-count').textContent = `Замечания экспорта (${DATA.warnings.length})`;
  for (const text of DATA.warnings) {
    const li = document.createElement('li'); li.textContent = text; $('warning-list').append(li);
  }
}
function visible(room) {
  const idx = levelIndex.get(room.levelKey);
  if (sliceMode === 'single') return idx === selectedLevelIdx;
  // Include different linked levels at the same physical elevation.
  return sliceMode === 'all' || room.elevation <= (levels[selectedLevelIdx]?.elevation??-Infinity) + 1e-6;
}
function renderTable() {
  const q = $('search-box').value.trim().toLocaleLowerCase('ru');
  const fragment = document.createDocumentFragment();
  let count = 0;
  const ordered = [...rooms].sort((a,b)=>{
    const av=valueFor(a,sortKey),bv=valueFor(b,sortKey);
    return sortDirection*(typeof av==='number' ? av-(bv??0) : String(av??'').localeCompare(String(bv??''),'ru',{numeric:true}));
  });
  for (const room of ordered) {
    if (!visible(room) || ($('floor-filter').value && $('floor-filter').value!==room.levelKey) ||
        ($('type-filter').value && $('type-filter').value!==room.name) || !`${room.number} ${room.name} ${room.levelName}`.toLocaleLowerCase('ru').includes(q)) continue;
    count++;
    const row = document.createElement('tr');
    row.className = selection.has(room.key) ? 'selected' : '';
    row.dataset.key = room.key;
    row.tabIndex = 0;
    row.setAttribute('aria-selected', String(selection.has(room.key)));
    const loss = results.get(room.key);
    for (const value of [room.number, room.name, room.levelName, fmt(room.area), loss.total==null?'нет данных':fmt(loss.total,0), loss.specificArea==null?'нет данных':fmt(loss.specificArea,1)]) {
      const cell = document.createElement('td'); cell.textContent = value; row.append(cell);
    }
    row.onclick = e => selectRoom(room.key,false,e);
    row.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectRoom(room.key,false,e); } };
    fragment.append(row);
  }
  $('room-list').replaceChildren(fragment);
  $('table-count').textContent = `${count} помещений`;
  $('empty-table').hidden = count > 0;
}
let refresh3D = () => {}, focusRoom = () => {}, focusOpening = () => {}, theme3D = () => {}, captureScene = null;
let radiatorPlans = new Map(), refreshRadiatorScene = () => {};
function selectRoom(key, fromScene = false,event={},object=null) {
  const active=selection.apply(key ? object || {key,roomKey:key,kind:'room'}:null,event);
  selectedKey = active?.roomKey??null;
  const room = rooms.find(r => r.key === selectedKey);
  $('btn-view-focus').disabled = !room && !active?.key;
  $('btn-view-focus').onclick=()=>room?focusRoom(room.key):active?.key && focusOpening(active.key);
  $('card-title').textContent = room ? `№ ${room.number} · ${room.name}` : 'Выберите помещение';
  $('card-sub').textContent = room ? `${room.levelName} · Отметка ${fmt(room.elevation)} м` : 'Кликните на объём в 3D или выберите строку в таблице';
  if(active && !room){$('card-title').textContent=`${kindNames[active.kind] || active.kind} · ${active.name || active.key}`;$('card-sub').textContent='Связь с помещением не определена; элемент сохранён в экспорте.';}
  const opening = !room && active ? openings.find(o=>o.key===active.key) : null;
  const fields = room ? {area:`${fmt(room.area)} м²`, volume:room.volume > 0 ? `${fmt(room.volume)} м³` : 'нет данных',
    height:`${fmt(room.height)} м`, bounds:room.boundariesCount, points:`${room.contour.length} точек`, id:room.idText} :
    opening ? {area:`${fmt(opening.area)} м²`,height:`${fmt(opening.height)} м`,id:opening.idText} : {};
  for (const field of ['area','volume','height','bounds','points','id']) $('card-'+field).textContent = fields[field] ?? '—';
  $('card-source').textContent = '';
  renderRoomCalculation(room);
  renderRadiatorCard(room);
  refresh3D(); renderTable();
  if (fromScene) {
    // Ensure a model selection remains discoverable when a text filter excluded it.
    if (!$('room-list').querySelector('tr.selected')) { $('search-box').value = ''; $('floor-filter').value=''; $('type-filter').value=''; renderTable(); }
    $('room-list').querySelector('tr.selected')?.scrollIntoView({block:'nearest'});
  }
}
function update() {
  if (selectedKey && !visible(rooms.find(r => r.key === selectedKey))) selectRoom(null);
  const shown = rooms.filter(visible);
  $('level-output').textContent = sliceMode === 'all' || !levels.length ? 'Все этажи' : `${levels[selectedLevelIdx].name} · ${fmt((levels[selectedLevelIdx]?.elevation??-Infinity))} м`;
  $('stats-bar').textContent = `В срезе: ${shown.length} / ${rooms.length} помещений · ${fmt(shown.reduce((s,r)=>s+r.area,0))} м² · ${shown.reduce((s,r)=>s+r.boundariesCount,0)} сегментов ограждений`;
  refresh3D(); renderTable();
}

function cell(row,text) { const td=document.createElement('td');td.textContent=text;row.append(td);return td; }
$('unbound-opening-count').textContent=`Окна и двери без помещения: ${unboundOpenings.length}`;
$('unbound-openings').open=false;
for(const opening of unboundOpenings) {
  const row=document.createElement('tr');row.dataset.key=opening.key;row.tabIndex=0;
  cell(row,opening.idText || opening.key);cell(row,`${kindNames[opening.kind] || opening.kind} · ${opening.name}`);cell(row,Number.isFinite(opening.position?.[2])?fmt(opening.position[2]):'не задана');
  const button=document.createElement('button');button.textContent='В 3D';cell(row,'').append(button);
  const find=e=>{document.querySelector('.slice-btn[data-slice="all"]').click();$('show-openings').checked=true;document.querySelector('[data-view="model"]').click();selectRoom(opening.key,false,e,{key:opening.key,roomKey:null,kind:opening.kind,name:opening.name});update();focusOpening(opening.key);};
  row.onclick=find;row.onkeydown=e=>{if(e.key==='Enter' || e.key===' '){e.preventDefault();find(e);}};
  $('unbound-opening-list').append(row);
}
function renderRoomCalculation(room) {
  const result = room && results.get(room.key);
  for(const [field,key] of [['loss','total'],['trans','transmission'],['air','air']]) $('card-'+field).textContent=watts(result?.[key]);
  $('surface-list').replaceChildren(); $('opening-list').replaceChildren();
  $('surface-summary').textContent=`Ограждения · ${room?.surfaces?.length || 'нет данных'} · ${result?.missing.length ? 'проверьте границы' : 'расчёт'}`;
  const roomOpenings = room ? openings.filter(o=>o.roomKeys.includes(room.key)) : [];
  $('opening-summary').textContent=`Окна и двери · ${roomOpenings.length || 'нет данных'}`;
  $('room-parameters').hidden=!room;
  for(const key of roomParameterKeys) $('room-'+key).value=room ? settings.roomOverrides?.[room.key]?.[key] ?? '' : '';
  if(!room) return;
  for(const s of room.surfaces || []) {
    const row=document.createElement('tr');
    const desc=cell(row,`${kindNames[s.kind] || s.kind} · ${s.elementId || 'нет данных'}`);desc.title=s.typeName+' · '+s.classificationSource;
    addConstructionEditor(desc,s.key,s.kind,s.netArea??s.grossArea);
    const areaCell=cell(row,s.netArea==null?fmt(s.grossArea):`${fmt(s.netArea)} / ${fmt(s.grossArea)}`);areaCell.dataset.sortValue=s.netArea??s.grossArea;areaCell.title=s.netArea==null?'площадь до вычета проёмов. '+(s.areaNote || ''):`Нетто / брутто; вырезы ${fmt(s.grossArea-s.netArea)} м² · ${s.areaSource}`;
    const exposure=document.createElement('select');exposure.setAttribute('aria-label','Граница '+s.elementId);
    for(const [v,t] of [['outside','Наружная'],['inside','Внутренняя'],['ground','Грунт / низ'],['unknown','нет данных'],['ignore','Исключить']]) exposure.append(option(v,t));
    exposure.value=overrides[s.key]?.exposure || s.exposure;
    const td=document.createElement('td');td.append(exposure);row.append(td);
    const u=document.createElement('input');u.type='number';u.min=0;u.step='.01';u.placeholder=String(settings[s.kind]);u.value=overrides[s.key]?.u ?? '';u.setAttribute('aria-label','U поверхности '+s.elementId);
    const ucell=document.createElement('td');ucell.append(u);row.append(ucell);
    cell(row,watts(result.components.find(c=>c.key===s.key)?.q));
    const change=()=>{overrides[s.key]={...overrides[s.key],exposure:exposure.value};if(u.value!=='') overrides[s.key].u=Number(u.value);else delete overrides[s.key].u;recalculate();};
    exposure.onchange=change;u.onchange=change;
    $('surface-list').append(row);
    for(const cut of s.cuts || []) {
      const line=document.createElement('tr');const cutDescription=cell(line,kindNames[cut.kind]+' · '+(cut.openingKey?.split('/').pop() || 'нет данных'));addConstructionEditor(cutDescription,cut.key,cut.kind,cut.area);cell(line,fmt(cut.area));cell(line,'Вырез из стены');
      const input=document.createElement('input');input.type='number';input.min=0;input.step='.01';input.placeholder=settings[cut.kind] ?? 'нет данных';input.value=overrides[cut.key]?.u ?? '';input.setAttribute('aria-label','U выреза '+cut.key);
      const td=document.createElement('td');td.append(input);line.append(td);input.onchange=()=>{if(input.value==='') delete overrides[cut.key];else overrides[cut.key]={u:Number(input.value)};recalculate();};
      cell(line,watts(result.components.find(c=>c.key===cut.key)?.q));$('surface-list').append(line);
    }
  }
  for(const o of roomOpenings) {
    const measured=(room.surfaces || []).flatMap(s=>s.cuts || []).filter(c=>c.openingKey===o.key).reduce((sum,c)=>sum+c.area,0);
    const row=document.createElement('tr');cell(row,o.idText);const name=cell(row,kindNames[o.kind]+' · '+o.name);name.title=o.geometrySource;addConstructionEditor(name,o.key,o.kind,measured || o.area);const area=cell(row,fmt(measured || o.area));area.title=measured?'Площадь фактического выреза':'Параметры семейства';$('opening-list').append(row);
  }
}
function renderTotals() {
  const ready=rooms.filter(r=>results.get(r.key).total!==null);
  const sum=key=>ready.reduce((s,r)=>s+results.get(r.key)[key],0);
  const complete=ready.length===rooms.length && rooms.length>0;
  const total=ready.length ? sum('total') : null;
  const area=ready.reduce((s,r)=>s+r.area,0);
  $('calc-state').textContent='Poteri'+` · ${ready.length} / ${rooms.length} помещений`;
  $('total-loss').textContent=complete ? watts(total) : total===null?'нет данных':`${watts(total)} · часть`;
  $('total-trans').textContent=ready.length?watts(sum('transmission')):'нет данных';
  $('total-air').textContent=ready.length?watts(sum('air')):'нет данных';
  $('total-specific').textContent=total!==null && area>0 ? `${fmt(total/area,1)} Вт/м²`:'нет данных';
  $('total-area').textContent=`${fmt(rooms.reduce((s,r)=>s+r.area,0))} м²`;
  $('total-rooms').textContent=`${rooms.length} помещений · ${levels.length} уровней`;
  $('total-openings').textContent=openings.length?`${openings.filter(o=>o.kind==='window').length} / ${openings.filter(o=>o.kind==='door').length}`:'нет данных';
  $('total-coverage').textContent=`Рассчитано ${ready.length} из ${rooms.length} помещений. ${complete?'Все помещения включены.':'Неполные помещения исключены из сумм.'} ${settings.reviewed?'Исходные данные проверены.':'Проверьте исходные данные и границы ограждений.'}`;
  $('floor-totals').replaceChildren();
  for(const l of levels) {
    const group=rooms.filter(r=>r.levelKey===l.key), valid=group.filter(r=>results.get(r.key).total!==null);
    const q=valid.reduce((s,r)=>s+results.get(r.key).total,0),a=valid.reduce((s,r)=>s+r.area,0);
    const row=document.createElement('tr');
    for(const v of [l.name,`${valid.length} / ${group.length}`,fmt(group.reduce((s,r)=>s+r.area,0)),valid.length?fmt(q,0):'нет данных',valid.length?fmt(q/a,1):'нет данных']) cell(row,v);
    const bar=cell(row,'');bar.className='bar-cell';const i=document.createElement('i');i.style.width=total>0?`${q/total*100}%`:'0%';bar.append(i);$('floor-totals').append(row);
  }
}
function recalculate() { calculateAll();save();renderTotals();renderRoomCalculation(rooms.find(r=>r.key===selectedKey));recalculateRadiators();update(); }
for(const key of ['indoor']) $('room-'+key).onchange=()=>{
  if(!selectedKey) return;settings.roomOverrides??={};settings.roomOverrides[selectedKey]??={};
  const input=$('room-'+key);if(input.value==='') delete settings.roomOverrides[selectedKey][key];else settings.roomOverrides[selectedKey][key]=Number(input.value);recalculate();
};
$('export-results').onclick=()=>{
  const payload={schemaVersion:'heatloss-beta-1',projectName:DATA.projectName,exportedAt:new Date().toISOString(),status:settings.reviewed?'beta':'demo',settings,overrides,rooms:rooms.map(r=>({key:r.key,id:r.idText,...results.get(r.key)}))};
  const url=URL.createObjectURL(new Blob([JSON.stringify(payload,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='heat_loss_results.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
};
function themeLabel(){ $('btn-theme').textContent=document.documentElement.dataset.theme==='dark'?'Светлая тема':'Тёмная тема'; }
themeLabel();
$('btn-theme').onclick=()=>{const theme=document.documentElement.dataset.theme==='dark'?'light':'dark';document.documentElement.dataset.theme=theme;try{localStorage.setItem('heatloss-theme',theme);}catch{}themeLabel();theme3D();refresh3D();};
const tableHome=document.createComment('room table in model view');$('table-panel').before(tableHome);
$('unbound-openings').hidden=unboundOpenings.length===0;
$('show-unbound-only').closest('label').hidden=unboundOpenings.length===0;
document.querySelectorAll('.tabs button').forEach(button=>button.onclick=()=>{
  document.body.dataset.view=button.dataset.view;
  document.querySelectorAll('.tabs button').forEach(b=>b.setAttribute('aria-pressed',String(b===button)));
  $('totals-panel').hidden=button.dataset.view!=='totals';
  $('logs-panel').hidden=button.dataset.view!=='logs';
  if(button.dataset.view==='totals')$('totals-panel').append($('table-panel'));
  else tableHome.after($('table-panel'));
  if($('radiators-panel'))$('radiators-panel').hidden=button.dataset.view!=='radiators';
});
$('card-expand').onclick=()=>{document.body.classList.toggle('expanded-card');$('card-expand').textContent=document.body.classList.contains('expanded-card')?'Свернуть':'Развернуть';};
$('card-clear').onclick=()=>selectRoom(null);
document.addEventListener('keydown',event=>{if(event.key==='Escape' && !['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName))selectRoom(null);});
for(const id of ['floor-filter','type-filter']) $(id).onchange=renderTable;
$('reset-filters').onclick=()=>{$('search-box').value='';$('floor-filter').value='';$('type-filter').value='';renderTable();};
document.querySelectorAll('th[data-sort]').forEach(th=>{
  th.tabIndex=0;
  th.onkeydown=e=>{if(['Enter',' '].includes(e.key)){e.preventDefault();th.click();}};
  th.onclick=()=>{if(sortKey===th.dataset.sort) sortDirection*=-1;else{sortKey=th.dataset.sort;sortDirection=1;}
    document.querySelectorAll('th[data-sort]').forEach(h=>h.setAttribute('aria-sort',h===th?(sortDirection===1?'ascending':'descending'):'none'));renderTable();};
});
document.querySelectorAll('.color-btn').forEach(btn => btn.onclick = () => {
  document.querySelectorAll('.color-btn').forEach(b => { b.classList.toggle('active', b === btn); b.setAttribute('aria-pressed',String(b === btn)); });
  colorMode = btn.dataset.mode; update();
});
document.querySelectorAll('.slice-btn').forEach(btn => btn.onclick = () => {
  document.querySelectorAll('.slice-btn').forEach(b => { b.classList.toggle('active', b === btn); b.setAttribute('aria-pressed',String(b === btn)); });
  sliceMode = btn.dataset.slice; slider.disabled = sliceMode === 'all'; update();
});
slider.oninput = () => { selectedLevelIdx = Number(slider.value); update(); };
$('search-box').addEventListener('input', renderTable);
$('btn-view-focus').onclick = () => { if (selectedKey) focusRoom(selectedKey); };
renderTotals();
update();

async function start3D() {
  if(!rooms.length){$('load-state').textContent='В модели нет размещённых помещений';return;}
  const [THREE, {OrbitControls}] = await Promise.all([import('three'), import('three/addons/controls/OrbitControls.js')]);
  const container = $('canvas-container');
  const scene = new THREE.Scene(); scene.background = new THREE.Color('#f5f5f2');
  const camera = new THREE.PerspectiveCamera(45, 1, .01, 10000);
  const renderer = new THREE.WebGLRenderer({antialias:true});
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.domElement.setAttribute('aria-label','Интерактивная 3D-модель помещений');
  container.append(renderer.domElement);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  scene.add(new THREE.HemisphereLight(0xffffff, 0x9b9b8e, 2));
  const light = new THREE.DirectionalLight(0xffffff, 2); light.position.set(30,80,50); scene.add(light);
  const bounds = {minX:Infinity,maxX:-Infinity,minY:Infinity,maxY:-Infinity,minZ:Infinity,maxZ:-Infinity};
  for (const r of rooms) {
    for (const p of r.polygons) for (const [x,y] of p.contour) {
      bounds.minX = Math.min(bounds.minX,x); bounds.maxX = Math.max(bounds.maxX,x);
      bounds.minY = Math.min(bounds.minY,y); bounds.maxY = Math.max(bounds.maxY,y);
    }
    bounds.minZ = Math.min(bounds.minZ,r.baseElevation); bounds.maxZ = Math.max(bounds.maxZ,r.baseElevation+r.height);
  }
  const cx = (bounds.minX+bounds.maxX)/2, cy = (bounds.minY+bounds.maxY)/2, z0 = bounds.minZ;
  const span = Math.max(bounds.maxX-bounds.minX,bounds.maxY-bounds.minY,bounds.maxZ-bounds.minZ,1);
  camera.far = span*30; camera.near = Math.max(.001,span/1000); camera.updateProjectionMatrix();
  controls.maxDistance = span*20; controls.minDistance = .05;
  const grid = new THREE.GridHelper(Math.ceil(span*1.6), 30, 0xb9b9ad, 0xdadad2);
  grid.position.y = -.03; scene.add(grid);
  const meshes = new Map(), pickable = [];
  const radiatorGroup=new THREE.Group(),radiatorMeshes=[];scene.add(radiatorGroup);
  const rangeFor = () => {
    let min=Infinity,max=-Infinity;
    for(const r of rooms){const v=valueFor(r,colorMode);if(typeof v==='number' && Number.isFinite(v)){min=Math.min(min,v);max=Math.max(max,v);}}
    return {min,max};
  };
  let range=rangeFor();
  const dark=()=>document.documentElement.dataset.theme==='dark';
  function color(r) {
    if (selection.has(r.key)) return new THREE.Color('#7b42f6');
    const value=valueFor(r,colorMode);
    if(colorMode!=='level' && value==null) return new THREE.Color(dark()?'#69665f':'#b3b1a8');
    const t = colorMode === 'level' ? levelIndex.get(r.levelKey)/Math.max(1,levels.length-1) :
      range.max === range.min ? .5 : (value-range.min)/(range.max-range.min);
    return new THREE.Color(dark()?'#894b21':'#ea9e70').lerp(new THREE.Color(dark()?'#ffc7a5':'#813d02'),t);
  }
  function path(points, shape) {
    points.forEach(([x,y],i) => i ? shape.lineTo(x-cx,y-cy) : shape.moveTo(x-cx,y-cy));
    shape.closePath(); return shape;
  }
  for (const r of rooms) {
    const shapes = r.polygons.map(p => {
      const shape = path(p.contour,new THREE.Shape());
      shape.holes = p.holes.map(h => path(h,new THREE.Path())); return shape;
    });
    let geometry = new THREE.ExtrudeGeometry(shapes,{depth:r.height, bevelEnabled:false, steps:1});
    geometry.rotateX(-Math.PI/2); // Revit (X,Y,Z) -> Three (X,Z,-Y).
    const walls=(r.surfaces || []).filter(s=>s.kind==='wall');
    if(walls.length && walls.every(s=>s.triangles?.length || s.netArea===0)) {
      const positions=[];const cap=geometry.groups.find(g=>g.materialIndex===0);const attr=geometry.getAttribute('position');
      if(cap) for(let i=cap.start;i<cap.start+cap.count;i++) positions.push(attr.getX(i),attr.getY(i),attr.getZ(i));
      for(const wall of walls) for(const t of wall.triangles) {
        const u=[t[3]-t[0],t[4]-t[1],t[5]-t[2]],v=[t[6]-t[0],t[7]-t[1],t[8]-t[2]];
        const n=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]];
        const order=wall.normal && n.reduce((sum,c,i)=>sum+c*wall.normal[i],0)<0?[0,6,3]:[0,3,6];
        for(const i of order)positions.push(t[i]-cx,t[i+2]-r.baseElevation,-(t[i+1]-cy));
      }
      geometry.dispose();geometry=new THREE.BufferGeometry();geometry.setAttribute('position',new THREE.Float32BufferAttribute(positions,3));geometry.computeVertexNormals();
    }
    // Repeated boundary subfaces must not be drawn twice in the same room.
    const attribute=geometry.getAttribute('position'),uniqueTriangles=new Set(),indices=[];
    for(let start=0;start<attribute.count;start+=3) {
      const vertices=[0,1,2].map(i=>[attribute.getX(start+i),attribute.getY(start+i),attribute.getZ(start+i)].map(v=>Math.round(v*1e6)).join(','));
      const key=[...vertices].sort().join('|');if(uniqueTriangles.has(key))continue;uniqueTriangles.add(key);indices.push(start,start+1,start+2);
    }
    geometry.setIndex(indices);geometry.clearGroups();
    // Opposite faces of adjacent room volumes occupy the same plane. Draw only the outward face.
    const material = new THREE.MeshStandardMaterial({color:color(r),side:THREE.FrontSide,roughness:.82,forceSinglePass:true,polygonOffset:true,polygonOffsetFactor:1,polygonOffsetUnits:1});
    const mesh = new THREE.Mesh(geometry, material);
    mesh.position.y = r.baseElevation-z0; mesh.userData.room = r;
    mesh.userData.opaqueOrder=meshes.size;mesh.renderOrder=meshes.size;material.polygonOffsetUnits=meshes.size+1;
    mesh.userData.triangles=[];
    for(let n=0;n<indices.length;n+=3) {
      const ids=indices.slice(n,n+3),points=ids.map(i=>new THREE.Vector3(attribute.getX(i),attribute.getY(i)+mesh.position.y,attribute.getZ(i)));
      const normal=points[1].clone().sub(points[0]).cross(points[2].clone().sub(points[0])).normalize();
      const key=points.map(p=>[p.x,p.y,p.z].map(v=>Math.round(v*1e6)).join(',')).sort().join('|')+'n'+[normal.x,normal.y,normal.z].map(v=>Math.round(v*1000)).join(',');
      mesh.userData.triangles.push({key,ids});
    }
    mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry),new THREE.LineBasicMaterial({color:'#373630',depthWrite:false})));
    scene.add(mesh); meshes.set(r.key,mesh); pickable.push(mesh);
  }
  const openingMeshes=[];
  const drawnOpenings=new Set();
  for(const room of rooms) for(const surface of room.surfaces || []) for(const cut of surface.cuts || []) {
    if(!cut.openingKey || !cut.triangles?.length) continue;
    const opening=openings.find(o=>o.key===cut.openingKey);if(!opening) continue;
    const points=[];
    for(const t of cut.triangles) for(let i=0;i<9;i+=3) points.push(t[i]-cx,t[i+2]-z0,-(t[i+1]-cy));
    const geometry=new THREE.BufferGeometry();geometry.setAttribute('position',new THREE.Float32BufferAttribute(points,3));geometry.computeVertexNormals();
    const material=new THREE.MeshBasicMaterial({color:opening.kind==='window'?'#bfe0f0':'#ff0000',side:THREE.DoubleSide,polygonOffset:true,polygonOffsetFactor:-2,polygonOffsetUnits:-2});
    const mesh=new THREE.Mesh(geometry,material);mesh.userData={room,opening};mesh.renderOrder=2;
    mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry),new THREE.LineBasicMaterial({color:'#16222b'})));
    scene.add(mesh);openingMeshes.push(mesh);drawnOpenings.add(room.key+'|'+opening.key);
  }
  // Draw a framed rectangle on each associated room boundary. This is a beta approximation,
  // not a CSG cut or a facsimile of the complete window/door family.
  function nearestBoundary(room,x,y){
    let best=null;
    for(const p of room.polygons) for(const ring of [p.contour,...p.holes]) for(let i=0;i<ring.length;i++){
      const a=ring[i],b=ring[(i+1)%ring.length],dx=b[0]-a[0],dy=b[1]-a[1],length=Math.hypot(dx,dy);if(length<1e-8) continue;
      const t=Math.max(0,Math.min(1,((x-a[0])*dx+(y-a[1])*dy)/(length*length)));
      const px=a[0]+t*dx,py=a[1]+t*dy,distance=Math.hypot(px-x,py-y);
      if(!best || distance<best.distance) best={x:px,y:py,tx:dx/length,ty:dy/length,distance};
    }
    return best;
  }
  for(const o of openings){
    for(const key of o.roomKeys){
      const room=rooms.find(r=>r.key===key);if(!room) continue;
      if(drawnOpenings.has(room.key+'|'+o.key)) continue;
      const nearest=nearestBoundary(room,o.position[0],o.position[1]);
      if(!nearest || nearest.distance>1) continue;
      const base=Math.max(o.position[2]-o.height/2,room.baseElevation),top=Math.min(o.position[2]+o.height/2,room.baseElevation+room.height);
      if(top<=base) continue;
      const geometry=new THREE.BoxGeometry(o.width,top-base,.035);
      const mesh=new THREE.Mesh(geometry,new THREE.MeshBasicMaterial({color:o.kind==='window'?'#bfe0f0':'#ff0000',polygonOffset:true,polygonOffsetFactor:-2,polygonOffsetUnits:-2}));mesh.renderOrder=2;
      mesh.rotation.y=Math.atan2(nearest.ty,nearest.tx);
      mesh.position.set(nearest.x-cx,(base+top)/2-z0,-(nearest.y-cy));
      mesh.userData={room,opening:o};
      const frame=new THREE.LineSegments(new THREE.EdgesGeometry(geometry),new THREE.LineBasicMaterial({color:'#16222b'}));mesh.add(frame);
      scene.add(mesh);openingMeshes.push(mesh);
    }
    if(!o.roomKeys.some(key=>rooms.some(r=>r.key===key)) && o.position?.length===3 && o.width>0 && o.height>0) {
      const geometry=new THREE.BoxGeometry(o.width,o.height,.035);
      const mesh=new THREE.Mesh(geometry,new THREE.MeshBasicMaterial({color:o.kind==='door'?'#ff0000':'#bfe0f0',polygonOffset:true,polygonOffsetFactor:-2,polygonOffsetUnits:-2}));
      mesh.position.set(o.position[0]-cx,o.position[2]-z0,-(o.position[1]-cy));mesh.rotation.y=Math.atan2(o.tangent?.[1] || 0,o.tangent?.[0] || 1);mesh.renderOrder=2;
      mesh.userData={room:null,opening:o};mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry),new THREE.LineBasicMaterial({color:'#f07818',depthWrite:false})));
      scene.add(mesh);openingMeshes.push(mesh);
    }
  }
  theme3D=()=>{
    scene.background.set(dark()?'#202934':'#f5f5f2');
    const colors=[dark()?'#383835':'#b9b9ad',dark()?'#292927':'#dadad2'];
    const attribute=grid.geometry.getAttribute('color');
    // Recreate grid colors on theme changes; preserve geometry placement.
    const replacement=new THREE.GridHelper(Math.ceil(span*1.6),30,...colors.map(c=>new THREE.Color(c)));
    attribute.copy(replacement.geometry.getAttribute('color'));attribute.needsUpdate=true;replacement.geometry.dispose();replacement.material.dispose();
    for(const mesh of pickable) mesh.children[0].material.color.set(dark()?'#d8d7cf':'#373630');
    for(const mesh of openingMeshes){mesh.material.color.set(mesh.userData.opening.kind==='window'?(dark()?'#4a93b8':'#bfe0f0'):'#ff0000');mesh.children[0].material.color.set(unboundKeys.has(mesh.userData.opening.key)?'#f07818':dark()?'#c4dfef':'#16222b');}
  };
  theme3D();
  refreshRadiatorScene=()=>{
    radiatorGroup.traverse(object=>{if(object.geometry)object.geometry.dispose();if(object.material)object.material.dispose();});radiatorGroup.clear();radiatorMeshes.length=0;
    for(const room of rooms)for(const [deviceIndex,d] of (radiatorPlans.get(room.key)?.devices || []).entries()){
      const length=d.product.lengthMm/1000,height=d.product.heightMm/1000,depth=(d.product.depthMm || 100)/1000;
      const geometry=new THREE.BoxGeometry(length,height,depth);
      const material=new THREE.MeshStandardMaterial({color:'#16b8c4',roughness:.65});
      const mesh=new THREE.Mesh(geometry,material);mesh.position.set(d.position[0]-cx,d.position[2]-z0,-(d.position[1]-cy));mesh.rotation.y=Math.atan2(d.tangent[1],d.tangent[0]);mesh.userData={room,radiator:d,selectionKey:`radiator:${room.key}:${deviceIndex}`};
      mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry),new THREE.LineBasicMaterial({color:dark()?'#c9f8ff':'#084954'})));
      radiatorGroup.add(mesh);radiatorMeshes.push(mesh);
    }
  };
  refreshRadiatorScene();
  const allBox = new THREE.Box3(); for (const m of [...pickable,...openingMeshes,...radiatorMeshes]) allBox.expandByObject(m);
  function fit(box, top = false) {
    if (box.isEmpty()) return;
    const center = box.getCenter(new THREE.Vector3()), size = box.getSize(new THREE.Vector3());
    const radius = Math.max(size.length()/2,.3);
    const halfFov = Math.atan(Math.tan(THREE.MathUtils.degToRad(camera.fov/2))*Math.min(1,camera.aspect));
    const distance = radius/Math.sin(halfFov)*1.15;
    const direction = top ? new THREE.Vector3(0,1,.00001) : new THREE.Vector3(1,.8,1).normalize();
    controls.target.copy(center); camera.position.copy(center).addScaledVector(direction,distance); controls.update();
  }
  function visibleBox() { const box = new THREE.Box3(); for (const m of [...pickable,...openingMeshes,...radiatorMeshes]) if (m.visible) box.expandByObject(m); return box; }
  $('btn-view-iso').onclick = () => fit(visibleBox());
  $('btn-view-top').onclick = () => fit(visibleBox(), true);
  focusRoom = key => fit(new THREE.Box3().setFromObject(meshes.get(key)));
  focusOpening = key => {const mesh=openingMeshes.find(m=>m.userData.opening.key===key);if(mesh)fit(new THREE.Box3().setFromObject(mesh));};
  refresh3D = () => {
    range=rangeFor();
    for (const r of rooms) { const mesh = meshes.get(r.key); mesh.visible = visible(r); mesh.material.color.copy(color(r));mesh.material.transparent=$('show-shell').checked;mesh.material.opacity=$('show-shell').checked?.28:1;mesh.material.depthWrite=!$('show-shell').checked;mesh.material.side=$('show-shell').checked?THREE.DoubleSide:THREE.FrontSide;mesh.material.forceSinglePass=false;mesh.renderOrder=$('show-shell').checked?0:mesh.userData.opaqueOrder;mesh.material.needsUpdate=true; }
    // One visible owner per coincident face with the same orientation. Hiding an owner restores the next one.
    const facesDrawn=new Set();
    for(const mesh of [...meshes.values()].sort((a,b)=>Number(selection.has(b.userData.room.key))-Number(selection.has(a.userData.room.key)))) {
      const draw=[];
      if(mesh.visible)for(const triangle of mesh.userData.triangles)if(!facesDrawn.has(triangle.key)){facesDrawn.add(triangle.key);draw.push(...triangle.ids);}
      mesh.geometry.setIndex(draw);
    }
    for(const mesh of openingMeshes){
      const room=mesh.userData.room,o=mesh.userData.opening;
      const upper=levels.filter(l=>l.elevation>(levels[selectedLevelIdx]?.elevation??-Infinity)+.001).map(l=>l.elevation).sort((a,b)=>a-b)[0]??Infinity;
      const show=room?visible(room):sliceMode==='all' || o.position[2]<upper && (sliceMode!=='single' || o.position[2]>=(levels[selectedLevelIdx]?.elevation??-Infinity));
      mesh.visible=$('show-openings').checked && show && (!$('show-unbound-only').checked || unboundKeys.has(o.key));mesh.material.color.set(selection.has(o.key)?'#a35cff':o.kind==='door'?'#ff0000':dark()?'#4a93b8':'#bfe0f0');
    }
    for(const mesh of radiatorMeshes){mesh.visible=$('show-radiators').checked && visible(mesh.userData.room);mesh.material.color.set(selection.has(mesh.userData.selectionKey)?'#a35cff':'#16b8c4');}
    const unit={area:'м²',volume:'м³',specificArea:'Вт/м²',specificVolume:'Вт/м³',total:'Вт'}[colorMode];
    const label = colorMode === 'level' ? `${levels.length} уровней` : Number.isFinite(range.min)?`${fmt(range.min)} — ${fmt(range.max)} ${unit}`:'нет данных';
    $('legend').textContent = `${label} · Серый — нет полного расчёта`;
  };
  $('show-unbound-only').onchange=refresh3D;$('show-openings').onchange=refresh3D;$('show-shell').onchange=refresh3D;
  $('show-radiators').onchange=refresh3D;
  const ray = new THREE.Raycaster(), mouse = new THREE.Vector2();
  let down = null;
  renderer.domElement.addEventListener('pointerdown',e => { down = {x:e.clientX,y:e.clientY,button:e.button,id:e.pointerId}; });
  renderer.domElement.addEventListener('pointercancel',() => down = null);
  renderer.domElement.addEventListener('pointerup',e => {
    if (!down || down.button !== 0 || down.id !== e.pointerId || Math.hypot(e.clientX-down.x,e.clientY-down.y)>4) { down = null; return; }
    down = null;
    const rect = renderer.domElement.getBoundingClientRect();
    mouse.set((e.clientX-rect.left)/rect.width*2-1,-(e.clientY-rect.top)/rect.height*2+1);
    ray.setFromCamera(mouse,camera);
    const hit = ray.intersectObjects([...pickable,...openingMeshes,...radiatorMeshes].filter(m=>m.visible),false)[0];
    if (hit) {const d=hit.object.userData;selectRoom(d.room?.key || d.opening?.key,true,e,{key:d.opening?.key || d.selectionKey || d.room.key,roomKey:d.room?.key??null,kind:d.opening?.kind || (d.radiator?'radiator':'room'),name:d.opening?.name});}
    else selectRoom(null,false,e);
  });
  const resize = () => {
    const w = container.clientWidth, h = container.clientHeight;
    if(w<=0 || h<=0) return;
    const old=camera.aspect;camera.aspect = w/h;
    if(old>0) camera.position.sub(controls.target).multiplyScalar(Math.min(1,old)/Math.min(1,camera.aspect)).add(controls.target);
    camera.updateProjectionMatrix(); renderer.setSize(w,h);
  };
  new ResizeObserver(resize).observe(container); resize();
  update(); fit(allBox);
  captureScene=()=>{
    const size=renderer.getSize(new THREE.Vector2()),background=scene.background.clone(),position=camera.position.clone(),target=controls.target.clone(),aspect=camera.aspect,damping=controls.enableDamping;
    const saved=[...meshes.values()].map(mesh=>({mesh,index:mesh.geometry.getIndex(),visible:mesh.visible,transparent:mesh.material.transparent,opacity:mesh.material.opacity,depthWrite:mesh.material.depthWrite,side:mesh.material.side,order:mesh.renderOrder}));
    const openingsVisible=openingMeshes.map(mesh=>mesh.visible),radiatorsVisible=radiatorGroup.visible;
    try{
      const faces=new Set(),box=new THREE.Box3();
      for(const {mesh} of saved){
        mesh.visible=true;mesh.material.transparent=false;mesh.material.opacity=1;mesh.material.depthWrite=true;mesh.material.side=THREE.FrontSide;mesh.material.needsUpdate=true;
        const indices=[];for(const triangle of mesh.userData.triangles)if(!faces.has(triangle.key)){faces.add(triangle.key);indices.push(...triangle.ids);}mesh.geometry.setIndex(indices);box.expandByObject(mesh);
      }
      for(const mesh of openingMeshes)mesh.visible=!unboundKeys.has(mesh.userData.opening.key);
      radiatorGroup.visible=false;scene.background.set('#ffffff');controls.enableDamping=false;
      renderer.setSize(1600,750,false);camera.aspect=1600/750;camera.updateProjectionMatrix();fit(box);
      renderer.render(scene,camera);return renderer.domElement.toDataURL('image/png');
    }finally{
      for(const s of saved){s.mesh.geometry.setIndex(s.index);s.mesh.visible=s.visible;Object.assign(s.mesh.material,{transparent:s.transparent,opacity:s.opacity,depthWrite:s.depthWrite,side:s.side,needsUpdate:true});s.mesh.renderOrder=s.order;}
      openingMeshes.forEach((mesh,i)=>mesh.visible=openingsVisible[i]);radiatorGroup.visible=radiatorsVisible;
      scene.background.copy(background);renderer.setSize(size.x,size.y,false);camera.aspect=aspect;camera.position.copy(position);controls.target.copy(target);camera.updateProjectionMatrix();controls.update();controls.enableDamping=damping;
      renderer.render(scene,camera);
    }
  };
  $('print-report').disabled=false;
  $('load-state').hidden = true;
  renderer.setAnimationLoop(() => {
    controls.update();const distance=camera.position.distanceTo(controls.target),near=Math.max(.001,Math.min(span/1000,distance/100)),far=Math.max(span*2,distance+span*3);
    if(Math.abs(camera.near-near)>near*.01 || Math.abs(camera.far-far)>far*.01){camera.near=near;camera.far=far;camera.updateProjectionMatrix();}
    const angle=Math.atan2(camera.position.x-controls.target.x,camera.position.z-controls.target.z);$('compass').style.transform=`rotate(${angle}rad)`;renderer.render(scene,camera);
  });
}
start3D().catch(error => {
  $('load-state').textContent = '3D недоступен: проверьте WebGL и доступ к CDN unpkg.com. Таблица и карточки доступны. ' + error.message;
  console.error(error);
});
