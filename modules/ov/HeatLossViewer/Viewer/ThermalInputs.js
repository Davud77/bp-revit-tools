// Building -> floor -> room -> construction inputs. Empty values inherit.
const scopeLabels={indoor:'Внутри, °C',outdoor:'Наружная температура, °C',cornerBonus:'Угловая добавка, доля',heatGain:'Теплопоступления на помещение, Вт'};
function thermalNumber(label,value='',step='.01') {
  const wrap=document.createElement('label');wrap.textContent=label;
  const input=document.createElement('input');input.type='number';input.step=step;input.value=value;wrap.append(input);return {wrap,input};
}
for(const key of roomParameterKeys) {
  if(!$('room-'+key)) {const field=thermalNumber(scopeLabels[key]);field.input.id='room-'+key;field.input.placeholder='Значение этажа / здания';$('room-parameters').querySelector('.input-grid').append(field.wrap);}
  $('room-'+key).onchange=()=>{
    if(!selectedKey)return;settings.roomOverrides??={};settings.roomOverrides[selectedKey]??={};
    const input=$('room-'+key);if(input.value==='')delete settings.roomOverrides[selectedKey][key];else settings.roomOverrides[selectedKey][key]=Number(input.value);recalculate();
  };
}
function addConstructionEditor(parent,key,kind,area) {
  const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Параметры конструкции';details.append(summary);
  const values=thermalInputsFor(key),fields={};
  function number(name,label,value=values[name]){const f=thermalNumber(label,value??'');details.append(f.wrap);fields[name]=f.input;}
  number('n','Коэффициент положения n');
  const orientation=document.createElement('select');orientation.setAttribute('aria-label','Ориентация конструкции');orientation.append(option('','По нормали поверхности'));
  for(const value of ['С','СВ','В','ЮВ','Ю','ЮЗ','З','СЗ'])orientation.append(option(value,value));orientation.value=values.orientation??'';details.append(orientation);
  let layers,zones;
  if(['window','door'].includes(kind)) {
    number('gn','Gn, кг/(м²·ч)');number('f','Показатель фильтрации f');number('ai','Коэффициент ai');
  } else {
    number('homogeneity','Однородность r');
    const label=document.createElement('label');label.textContent='Слои: толщина, м; λ, Вт/(м·К). Один слой в строке';
    layers=document.createElement('textarea');layers.rows=3;layers.value=(values.layers || []).map(l=>`${l.thickness};${l.lambda}`).join('\n');label.append(layers);details.append(label);
    const ground=document.createElement('label');ground.textContent='Площади зон грунта I; II; III; IV, м²';zones=document.createElement('input');zones.value=values.groundZones?.join(';') || '';ground.append(zones);details.append(ground);
    number('insulationR','Сопротивление утепления грунта, м²·К/Вт');
    if(kind==='floor') {
      const button=document.createElement('button');button.type='button';button.textContent='Зоны прямоугольного пола';
      button.onclick=()=>{
        const room=rooms.find(r=>r.key===selectedKey),p=room?.polygons?.[0],ring=p?.contour;
        if(room?.polygons.length!==1 || p.holes?.length || ring?.length!==4){message.textContent='Для этой формы задайте фактические площади зон вручную.';return;}
        const edges=ring.map((a,i)=>[ring[(i+1)%4][0]-a[0],ring[(i+1)%4][1]-a[1]]);
        if(edges.some((e,i)=>Math.abs(e[0]*edges[(i+1)%4][0]+e[1]*edges[(i+1)%4][1])>1e-6)){message.textContent='Контур не прямоугольный: задайте площади вручную.';return;}
        const w=Math.hypot(...edges[0]),l=Math.hypot(...edges[1]);
        const result=globalThis.PoteriEngine.calculateGroundFloorZones(w,l,Object.fromEntries(Object.entries(activeWalls).map(([k,v])=>[k,v.checked])),Math.max(0,settings.groundElevation-room.baseElevation));
        zones.value=Object.values(result.floor).join(';');message.textContent='Проверьте выбранные наружные стороны и площади перед применением.';
      };
      const activeWalls={};for(const [name,text] of [['top','Сторона 1'],['right','Сторона 2'],['bottom','Сторона 3'],['left','Сторона 4']]) {
        const label=document.createElement('label'),input=document.createElement('input');input.type='checkbox';input.checked=true;label.append(input,document.createTextNode(text));details.append(label);activeWalls[name]=input;
      }
      details.append(button);
    }
  }
  const apply=document.createElement('button');apply.type='button';apply.textContent='Применить';
  const message=document.createElement('p');message.className='note';message.setAttribute('role','status');
  apply.onclick=()=>{
    try {
      const next={...overrides[key]};for(const [name,field] of Object.entries(fields)) {
        if(field.value==='')delete next[name];else {const value=Number(field.value);if(!Number.isFinite(value)||value<0)throw Error('Нужно неотрицательное число');next[name]=value;}
      }
      if(orientation.value)next.orientation=orientation.value;else delete next.orientation;
      if(layers) {
        if(!layers.value.trim())delete next.layers;
        else next.layers=layers.value.trim().split(/\n/).map(line=>{const pair=line.split(';').map(v=>Number(v.trim().replace(',','.')));if(pair.length!==2 || pair.some(v=>!Number.isFinite(v)||v<=0))throw Error('Слой: толщина; λ, оба числа больше нуля');return {thickness:pair[0],lambda:pair[1]};});
      }
      if(zones) {
        if(!zones.value.trim())delete next.groundZones;
        else {const parts=zones.value.split(';').map(v=>Number(v.trim().replace(',','.')));if(parts.length!==4 || parts.some(v=>!Number.isFinite(v)||v<0) || Math.abs(parts.reduce((s,v)=>s+v,0)-area)>.01)throw Error('Четыре площади должны давать площадь конструкции');next.groundZones=parts;}
      }
      overrides[key]=next;recalculate();
    }catch(error){message.textContent=error.message;}
  };
  details.append(apply,message);parent.append(details);
}
