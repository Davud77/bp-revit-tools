const radiatorEngine=globalThis.RadiatorSelection;
const radiatorCatalog=JSON.parse($('radiator-catalog').textContent);
let radiatorSettings={...radiatorEngine.defaults,roomOverrides:{}},customRadiators=[];
const radiatorStorage=storageKey+':radiators';
try{const saved=JSON.parse(localStorage.getItem(radiatorStorage));if(saved){radiatorSettings={...radiatorSettings,...saved.settings};customRadiators=saved.custom || [];}}catch{}
const radiatorSave=()=>{try{localStorage.setItem(radiatorStorage,JSON.stringify({settings:radiatorSettings,custom:customRadiators}));}catch{}};
const mountNames={wall:'Настенный панельный',floor:'Напольный на опорах',vertical:'Вертикальный настенный',trench:'Внутрипольный, естественная конвекция','trench-fan':'Внутрипольный с вентилятором',sectional:'Секционный / свой прибор'};
const radiatorStatus={selected:'Подобран · бета',insufficient:'Недостаточно мощности / места',demo:'демо',restricted:'Нужна проверка размещения','catalog-missing':'Нужен каталог','placement-missing':'Место вручную',excluded:'Исключён',zero:'Нагрузка 0'};
for(const [index,row] of (radiatorCatalog.referenceLoads?.rooms || []).entries())$('rad-source-load').append(option(index,`Этаж ${row.floor} · ${row.number}. ${row.name} · ${row.watts} Вт · стр. ${row.page}`));
for(const [value,label] of Object.entries(mountNames)){$('rad-mount').append(option(value,label));$('rad-room-mount').append(option(value,label));}
function radiatorProducts(){return [...radiatorCatalog.products,...customRadiators];}
function fillRadiatorSeries(){
 const value=radiatorSettings.series;$('rad-series').replaceChildren(option('','Все серии'));
 for(const series of [...new Set(radiatorProducts().filter(p=>p.mounts.includes(radiatorSettings.mount)).map(p=>p.series))])$('rad-series').append(option(series,series));
 if([...$('rad-series').options].some(o=>o.value===value))$('rad-series').value=value;else{radiatorSettings.series='';$('rad-series').value='';}
}
function fillRadiatorInputs(){
 for(const key of ['buildingType','policyMode','mount','panelType','supply','return','reservePercent','coveragePercent','maxDevicesPerOpening','maxHeightMm','floorGapMm','topGapMm','wallGapMm','sideGapMm','floorRecessMm','installationFactor'])$('rad-'+key).value=radiatorSettings[key];
 fillRadiatorSeries();
}
function recalculateRadiators(){
 radiatorPlans=new Map(rooms.map(room=>[room.key,radiatorEngine.select(room,results.get(room.key),openings,radiatorProducts(),radiatorSettings,settings,overrides)]));
 radiatorSave();renderRadiatorTable();renderRadiatorCard(rooms.find(r=>r.key===selectedKey));refreshRadiatorScene();refresh3D();
}
function renderRadiatorTable(){
 const fragment=document.createDocumentFragment();let count=0,wattsInstalled=0,selected=0;
 for(const room of rooms){
  const plan=radiatorPlans.get(room.key);if(!plan)continue;
  count+=plan.devices.length;wattsInstalled+=plan.installed || 0;if(plan.status==='selected')selected++;
  const row=document.createElement('tr');row.dataset.key=room.key;row.className=room.key===selectedKey?'selected':'';row.tabIndex=0;
  for(const value of [room.number,room.name,room.levelName,watts(plan.required),plan.devices.length,watts(plan.installed),radiatorStatus[plan.status]])cell(row,value);
  row.onclick=()=>{selectRoom(room.key);$('rad-selected-room').textContent=`Выбрано: № ${room.number} · ${room.name}. Индивидуальные настройки — в карточке.`;};
  row.onkeydown=e=>{if(e.key==='Enter'){row.click();}};fragment.append(row);
 }
 $('radiator-rows').replaceChildren(fragment);
 $('rad-summary').textContent=`${selected} / ${rooms.length} помещений · ${count} приборов · ${fmt(wattsInstalled/1000,2)} кВт установлено · Предварительная схема`;
 const rule=radiatorSettings.policyMode==='legacy'?'Архивная схема СП 60.13330.2016: 50% / 75%':radiatorSettings.policyMode==='project'?`Правило проекта: ${radiatorSettings.coveragePercent}%`:
 ['residential','public'].includes(radiatorSettings.buildingType)?`Текущий СП + правило проекта: не менее ${radiatorSettings.coveragePercent}% ширины окна`:'Текущий СП: максимально возможная длина под каждым окном';
 $('rad-rule').textContent=rule;
 $('rad-catalog-info').textContent=`${radiatorCatalog.products.length} позиций Kermi из «${radiatorCatalog.source}» + ${customRadiators.length} своих. График ${radiatorSettings.supply}/${radiatorSettings.return} °C. Мощность пересчитывается через логарифмический температурный напор и каталожный n.`;
}
function renderRadiatorCard(room){
 const plan=room && radiatorPlans.get(room.key),o=room && radiatorSettings.roomOverrides?.[room.key];
 $('rad-room-state').textContent=plan ? `${radiatorStatus[plan.status]} · ${plan.reason}`:'Выберите помещение';
 $('rad-room-q').textContent=plan?`${watts(plan.required)} требуется / ${watts(plan.installed)} установлено`:'демо';
 $('rad-room-mount').value=o?.mount || '';$('rad-room-target').value=o?.targetWatts ?? '';
 $('rad-room-reviewed').checked=!!o?.accessReviewed;$('rad-room-excluded').checked=!!o?.excluded;
 $('rad-room-wall').replaceChildren(option('','Автоматически по наружным стенам'));
 for(const surface of room?.surfaces || [])if(surface.kind==='wall' && (overrides[surface.key]?.exposure || surface.exposure)==='outside')$('rad-room-wall').append(option(surface.key,`${surface.typeName} · ${surface.elementId}`));
 $('rad-room-wall').value=o?.wallKey || '';
 const sourceIndex=o?.sourceLoad ? (radiatorCatalog.referenceLoads?.rooms || []).findIndex(r=>r.floor===o.sourceLoad.floor && r.number===o.sourceLoad.number) : -1;
 $('rad-source-load').value=sourceIndex>=0?String(sourceIndex):'';
 $('rad-source-state').textContent=o?.sourceLoad?`Источник: ${o.sourceLoad.source}, стр. ${o.sourceLoad.page}; соответствие выбрано вручную.`:'Нагрузка из PDF автоматически к Room не привязывается.';
 $('rad-device-list').replaceChildren();
 for(const d of plan?.devices || []){
  const row=document.createElement('tr');
  for(const v of [d.product.model,`${d.product.lengthMm} × ${d.product.heightMm}`,watts(d.power),d.coverage==null?'Наружная стена':d.groupSize>1?`${fmt(d.coverage*100,1)}% · группа ${fmt(d.groupCoverage*100,1)}%`:`${fmt(d.coverage*100,1)}% окна`])cell(row,v);
  row.title=`Источник: ${d.product.source}, ${d.product.sheet || ''} ${d.product.sourceCells || ''}`;$('rad-device-list').append(row);
 }
 $('rad-room-warnings').replaceChildren();
 for(const warning of plan?.warnings || []){const li=document.createElement('li');li.textContent=warning;$('rad-room-warnings').append(li);}
}
fillRadiatorInputs();
$('rad-mount').onchange=()=>{radiatorSettings.mount=$('rad-mount').value;if(radiatorSettings.mount==='vertical'){$('rad-maxHeightMm').value=2200;radiatorSettings.maxHeightMm=2200;}fillRadiatorSeries();};
$('rad-buildingType').onchange=()=>{
 const special=!['residential','public'].includes($('rad-buildingType').value);
 if(special){$('rad-floorGapMm').value=Math.max(100,Number($('rad-floorGapMm').value));$('rad-wallGapMm').value=Math.max(60,Number($('rad-wallGapMm').value));}
};
$('radiator-form').onsubmit=e=>{
 e.preventDefault();
 for(const key of ['buildingType','policyMode','mount','series','panelType'])radiatorSettings[key]=$('rad-'+key).value;
 for(const key of ['supply','return','reservePercent','coveragePercent','maxDevicesPerOpening','maxHeightMm','floorGapMm','topGapMm','wallGapMm','sideGapMm','floorRecessMm','installationFactor'])radiatorSettings[key]=Number($('rad-'+key).value);
 recalculateRadiators();$('rad-message').textContent='Подбор обновлён. Неразрешённые помещения перечислены в таблице.';
};
function roomRadiatorChange(event){
 if(!selectedKey)return;
 radiatorSettings.roomOverrides??={};const row=radiatorSettings.roomOverrides[selectedKey]??={};
 for(const [id,key] of [['rad-room-mount','mount'],['rad-room-wall','wallKey']]){if($(id).value)row[key]=$(id).value;else delete row[key];}
 if($('rad-room-target').value==='')delete row.targetWatts;else row.targetWatts=Number($('rad-room-target').value);
 if(event?.target?.id==='rad-room-target')delete row.sourceLoad;
 row.accessReviewed=$('rad-room-reviewed').checked;row.excluded=$('rad-room-excluded').checked;
 // A room-specific mounting type should not inherit an incompatible global series filter.
 if(row.mount && row.mount!==radiatorSettings.mount)row.series='';else delete row.series;
 recalculateRadiators();
}
for(const id of ['rad-room-mount','rad-room-wall','rad-room-target','rad-room-reviewed','rad-room-excluded'])$(id).onchange=roomRadiatorChange;
$('rad-apply-source-load').onclick=()=>{
 if(!selectedKey || $('rad-source-load').value===''){ $('rad-source-state').textContent='Сначала выберите помещение и строку PDF.';return; }
 const reference=radiatorCatalog.referenceLoads.rooms[Number($('rad-source-load').value)];
 radiatorSettings.roomOverrides??={};radiatorSettings.roomOverrides[selectedKey]??={};
 radiatorSettings.roomOverrides[selectedKey].targetWatts=reference.watts;
 radiatorSettings.roomOverrides[selectedKey].sourceLoad={type:'user-mapped-pdf',source:radiatorCatalog.referenceLoads.source,...reference};
 recalculateRadiators();$('rad-source-state').textContent=`Нагрузка ${reference.watts} Вт применена только к выбранному Room. Привязку выбрал пользователь.`;
};
$('rad-reset').onclick=()=>{radiatorSettings={...radiatorEngine.defaults,roomOverrides:{}};fillRadiatorInputs();recalculateRadiators();};
function radiatorDownload(name,data){const url=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
$('rad-export').onclick=()=>radiatorDownload('radiator_selection.json',{schemaVersion:'radiator-selection-1',projectName:DATA.projectName,exportedAt:new Date().toISOString(),
 status:'beta-proposal',coordinateSystem:DATA.coordinateSystem,units:{length:'m',power:'W'},settings:radiatorSettings,heatLossSettings:settings,
 normative:radiatorCatalog.normative,catalogSource:radiatorCatalog.source,catalogSha256:radiatorCatalog.sha256,
 rooms:[...radiatorPlans.values()].map(p=>({...p,devices:p.devices.map(d=>({productId:d.product.id,model:d.product.model,mount:radiatorEngine.effective(rooms.find(r=>r.key===p.roomKey),radiatorSettings).mount,
  power:d.power,position:d.position,tangent:d.tangent,normal:d.normal,lengthMm:d.product.lengthMm,heightMm:d.product.heightMm,depthMm:d.product.depthMm,
  depthIsSchematic:!!d.product.depthIsSchematic,floorElevation:d.floorElevation,floorReferenceSource:d.floorReferenceSource,
  qNominal:d.product.qNominal,n:d.product.n,referenceSupply:d.product.referenceSupply,referenceReturn:d.product.referenceReturn,referenceRoom:d.product.referenceRoom,referenceDelta:d.product.referenceDelta,
  indoor:settings.roomOverrides?.[p.roomKey]?.indoor ?? settings.indoor,coverage:d.coverage,groupCoverage:d.groupCoverage,groupSize:d.groupSize,windowKey:d.windowKey,surfaceKey:d.surfaceKey,source:d.product.source,sheet:d.product.sheet,sourceCells:d.product.sourceCells,fanMode:d.product.fanMode}))}))});
$('rad-schedule').onclick=()=>{
 const quote=v=>'"'+(typeof v==='string' && /^[\s]*[=+@-]/.test(v)?"'"+v:String(v??'')).replaceAll('"','""')+'"';
 const rows=[['Помещение','Имя','Этаж','Прибор','Длина мм','Высота мм','Мощность Вт','График','Статус']];
 for(const room of rooms)for(const d of radiatorPlans.get(room.key)?.devices || [])rows.push([room.number,room.name,room.levelName,d.product.model,d.product.lengthMm,d.product.heightMm,d.power.toFixed(1),`${radiatorSettings.supply}/${radiatorSettings.return}`,radiatorPlans.get(room.key).status]);
 const url=URL.createObjectURL(new Blob(['\ufeff'+rows.map(r=>r.map(quote).join(';')).join('\r\n')],{type:'text/csv;charset=utf-8'}));const a=document.createElement('a');a.href=url;a.download='radiator_schedule.csv';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
};
$('rad-custom-form').onsubmit=e=>{
 e.preventDefault();
 const model=$('custom-model').value.trim(),source=$('custom-source').value.trim(),mount=$('custom-mount').value;
 const p={id:'custom-'+Date.now(),manufacturer:'По данным пользователя',series:'Свой каталог',model,deviceType:mount,mounts:[mount],panelType:'',
  lengthMm:Number($('custom-length').value),heightMm:Number($('custom-height').value),depthMm:Number($('custom-depth').value),
  qNominal:Number($('custom-q').value),n:Number($('custom-n').value),referenceSupply:Number($('custom-supply').value),referenceReturn:Number($('custom-return').value),referenceRoom:Number($('custom-room').value),source,catalogStatus:'manual',fanMode:$('custom-fan').value};
 if(!model||!source||!['lengthMm','heightMm','depthMm','qNominal','n'].every(k=>Number.isFinite(p[k])&&p[k]>0)||radiatorEngine.logMean(p.referenceSupply,p.referenceReturn,p.referenceRoom)===null){$('rad-custom-message').textContent='Проверьте размеры, мощность, n и исходный график.';return;}
 if(mount==='trench-fan'&&!p.fanMode){$('rad-custom-message').textContent='Укажите каталожную скорость вентилятора.';return;}
 customRadiators.push(p);radiatorSettings.mount=mount;radiatorSettings.series='Свой каталог';radiatorSettings.panelType='';fillRadiatorInputs();recalculateRadiators();$('rad-custom-message').textContent='Прибор добавлен. Проверьте допустимую высоту и глубину ниши.';
};
for(const [value,label] of Object.entries(mountNames))$('custom-mount').append(option(value,label));
$('rad-catalog-import').onchange=async e=>{
 try{
  const file=JSON.parse(await e.target.files[0].text());const entries=Array.isArray(file)?file:file.products;
  if(!Array.isArray(entries)||entries.length>10000)throw new Error('catalog');
  const ids=new Set(radiatorProducts().map(p=>p.id));
  for(const p of entries){if(!p.id||ids.has(p.id)||!p.model||!p.source||!Array.isArray(p.mounts)||!p.mounts.length||p.mounts.some(m=>!(m in mountNames))||!['lengthMm','heightMm','depthMm','qNominal','n'].every(k=>typeof p[k]==='number'&&Number.isFinite(p[k])&&p[k]>0)||radiatorEngine.logMean(p.referenceSupply,p.referenceReturn,p.referenceRoom)===null)throw new Error('record');ids.add(p.id);}
  customRadiators.push(...entries);fillRadiatorSeries();recalculateRadiators();$('rad-custom-message').textContent=`Добавлено ${entries.length} позиций.`;
 }catch{$('rad-custom-message').textContent='Каталог не загружен: проверьте уникальные id, типы, размеры, мощность, n, график и источник.';}
};
$('rad-view-3d').onclick=()=>{document.querySelector('.tabs button[data-view=model]').click();$('show-radiators').checked=true;$('show-shell').checked=true;refresh3D();};
$('rad-show-card').onclick=()=>{document.querySelector('.tabs button[data-view=model]').click();$('show-radiators').checked=true;$('show-shell').checked=true;refresh3D();$('radiator-room-details').open=true;$('radiator-room-details').scrollIntoView({block:'nearest'});};
recalculateRadiators();
