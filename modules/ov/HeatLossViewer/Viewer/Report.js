// Print-ready Poteri report. No recalculation or invented geometry in the report.
$('print-report').onclick=()=>{
  const target=window.open('','_blank');
  if(!target){$('report-message').textContent='Разрешите открытие окна отчёта в браузере.';return;}
  const doc=target.document;
  doc.open();doc.write('<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>Расчёт тепловых потерь</title></head><body></body></html>');doc.close();
  const style=doc.createElement('style');style.textContent=`
    @page{size:A4 landscape;margin:10mm}*{box-sizing:border-box}body{font:10pt Arial,sans-serif;color:#161616;margin:0;background:#eee}h1{font-size:21pt;margin:0 0 5mm}h2{font-size:13pt;margin:7mm 0 3mm}p{margin:2mm 0;line-height:1.4}.toolbar{position:sticky;top:0;background:white;padding:12px;box-shadow:0 1px 5px #aaa;z-index:2;display:flex;gap:12px;align-items:center}.toolbar button{padding:8px 15px;cursor:pointer;font:inherit}.cover,.report-pages,.appendix{background:white;padding:10mm;margin:10px auto;max-width:297mm}.cover{break-after:page;page-break-after:always}.cover img{display:block;width:100%;height:125mm;object-fit:contain;margin:4mm 0}.meta{display:flex;gap:6mm;flex-wrap:wrap;font-size:10pt}.report-table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:7.5pt}.report-table th,.report-table td{border:.2mm solid #999;padding:1.5mm 1mm;overflow-wrap:anywhere;vertical-align:middle;text-align:center}.report-table th{background:#f1f1f1;font-weight:bold}.report-table td:first-child{text-align:left}.report-table .level td{font-size:10pt;background:#e7e7e7;font-weight:bold;text-align:left}.report-table .room td{background:#f5f5f5;font-weight:bold;text-align:left}.report-table .sum td{text-align:right;font-weight:bold}.report-table .missing td{color:#923900;text-align:left}.report-table thead{display:table-header-group}.report-table tr{break-inside:avoid;page-break-inside:avoid}.report-table col:first-child{width:19%}.report-table col:not(:first-child){width:5.7857%}.notes{font-size:8pt}.appendix{break-before:page}.report-table .gains td{text-align:right}.toolbar span{font-size:10pt}
    @media print{body{background:white}.toolbar{display:none}.cover,.report-pages,.appendix{padding:0;margin:0;max-width:none}.cover{height:185mm}.cover img{height:123mm}.report-pages>h2{margin-top:0}.report-table th{-webkit-print-color-adjust:exact;print-color-adjust:exact}}
  `;doc.head.append(style);
  const add=(parent,tag,text,className)=>{const el=doc.createElement(tag);if(text!=null)el.textContent=text;if(className)el.className=className;parent.append(el);return el;};
  const format=(value,digits=2)=>Number.isFinite(value)?value.toLocaleString('ru-RU',{maximumFractionDigits:digits}):'—';
  const toolbar=add(doc.body,'div',null,'toolbar');
  const print=add(toolbar,'button','Печать / Сохранить PDF');print.id='report-print';print.onclick=()=>target.print();
  const close=add(toolbar,'button','Закрыть');close.onclick=()=>target.close();
  add(toolbar,'span','A4, альбомная ориентация. Выберите «Сохранить в PDF» в окне печати.');
  const cover=add(doc.body,'section',null,'cover');cover.id='report-cover';
  add(cover,'h1','РАСЧЁТ ТЕПЛОВЫХ ПОТЕРЬ');add(cover,'h2',DATA.projectName);
  const ready=rooms.filter(r=>results.get(r.key).total!==null),sum=key=>ready.reduce((s,r)=>s+results.get(r.key)[key],0);
  const meta=add(cover,'div',null,'meta');
  for(const text of [`Методика: Poteri`,`Наружная температура: ${format(settings.outdoor)} °C`,`Ветер: ${format(settings.windSpeed)} м/с`,`Рассчитано: ${ready.length} / ${rooms.length} помещений`])add(meta,'span',text);
  try{const image=add(cover,'img');image.alt='3D-модель всех помещений';image.src=captureScene();}catch(error){add(cover,'p','Не удалось получить 3D-изображение: '+error.message);}
  add(cover,'p',`${ready.length===rooms.length?'Итого':'Частичный итог'}: ${ready.length?format(sum('total'),0)+' Вт':'нет данных'}. Ограждения: ${ready.length?format(sum('transmission'),0):'—'} Вт; инфильтрация: ${ready.length?format(sum('infiltration'),0):'—'} Вт.`);
  add(cover,'p','3D показывает все помещения. Проёмы без помещений перечислены в приложении. Отдельная вентиляционная нагрузка не добавляется.', 'notes');
  add(cover,'p','Нерассчитанные помещения исключены из сумм. Проверьте исходные параметры и классификацию ограждений.','notes');
  const section=add(doc.body,'section',null,'report-pages');add(section,'h2','Теплотехнический расчёт ограждающих конструкций');
  const table=add(section,'table',null,'report-table');table.id='report-table';
  const cols=add(table,'colgroup');for(let i=0;i<15;i++)add(cols,'col');
  const thead=add(table,'thead'),head=add(thead,'tr');
  for(const [text,span,rows] of [['Наименование конструкции',1,2],['Ор.',1,2],['Размеры, м',2,1],['S, м²',1,2],['n',1,2],['tвн, °C',1,2],['Δt, °C',1,2],['R₀, м²·К/Вт',1,2],['Параметры инфильтрации',3,1],['Потери, Вт',3,1]]){const th=add(head,'th',text);th.colSpan=span;th.rowSpan=rows;}
  const sub=add(thead,'tr');for(const text of ['b','h','ΔP, Па','Gн, кг/(м²·ч)','Gинф, кг/ч','Qтр','Qинф','Qобщ'])add(sub,'th',text);
  const body=add(table,'tbody');
  const wide=(text,kind)=>{const tr=add(body,'tr',null,kind);const td=add(tr,'td',text);td.colSpan=15;};
  const row=values=>{const tr=add(body,'tr',null,'construction');for(const value of values)add(tr,'td',value);};
  for(const level of levels){
    wide(level.name,'level');
    for(const room of rooms.filter(r=>r.levelKey===level.key).sort((a,b)=>a.number.localeCompare(b.number,'ru',{numeric:true}))){
      const result=results.get(room.key),input=engine.resolve(room,settings);
      wide(`Помещение № ${room.number} — ${room.name} · S = ${format(room.area)} м² · tвн = ${format(input.indoor)} °C`,'room');
      if(result.missing.length)wide('Не рассчитано: '+result.missing.join('; '),'missing');
      for(const c of result.components){
        const surface=(room.surfaces || []).find(s=>s.key===c.key),opening=openings.find(o=>o.key===(c.openingKey || c.key)),info=c.report || {},inf=c.infiltration;
        const name=`${kindNames[c.kind] || c.kind} · ${surface?.typeName || opening?.name || ''}${surface?.elementId || opening?.idText?' ['+(surface?.elementId || opening?.idText)+']':''}`;
        if(info.zones){info.zones.forEach((zone,i)=>{if(zone.area>0)row([`Пол по грунту · зона ${i+1}`,'—','—','—',format(zone.area),1,format(info.indoor),format(info.delta),format(zone.r0),'—','—','—',format(zone.q,0),'—',format(zone.q,0)]);});continue;}
        row([name,c.orientation || '—',format(opening?.width),format(opening?.height),format(c.area),format(info.n),format(info.indoor),format(info.delta),info.r0===Infinity?'∞':format(info.r0),format(inf?.deltaP),format(inf?.gn),format(inf?.gInf),format(c.q,0),inf?format(Math.max(0,inf.qInf),0):'—',format(c.q+Math.max(0,inf?.qInf || 0),0)]);
      }
      if(input.heatGain)wide(`Теплопоступления: −${format(input.heatGain,0)} Вт`,'gains');
      wide(`∑Q помещения = ${result.total===null?'нет данных':format(result.total,0)+' Вт'}`,'sum');
    }
    const valid=rooms.filter(r=>r.levelKey===level.key && results.get(r.key).total!==null);
    wide(`ИТОГО ПО ЭТАЖУ: ${valid.length?format(valid.reduce((s,r)=>s+results.get(r.key).total,0),0)+' Вт':'нет данных'} · учтено ${valid.length} / ${rooms.filter(r=>r.levelKey===level.key).length} помещений`,'sum');
  }
  wide(`ИТОГО ПО ЗДАНИЮ: ${ready.length?format(sum('total'),0)+' Вт':'нет данных'} · учтено ${ready.length} / ${rooms.length} помещений`,'sum');
  add(section,'p','b и h приведены по параметрам заполнений; неизвестные размеры обозначены «—». Qтр включает коэффициент n и принятые добавки. Площади проёмов учитываются один раз. Параметры и результаты соответствуют текущей выгрузке.','notes');
  if(unboundOpenings.length){
    const appendix=add(doc.body,'section',null,'appendix');add(appendix,'h2',`Окна и двери без связи с помещением — ${unboundOpenings.length}`);
    add(appendix,'p','Эти элементы показаны в 3D, но не добавлены к произвольному тепловому балансу.','notes');
    const t=add(appendix,'table',null,'report-table'),h=add(add(t,'thead'),'tr');for(const title of ['ElementId','Тип','Название','Отметка центра, м'])add(h,'th',title);
    const b=add(t,'tbody');for(const o of unboundOpenings){const tr=add(b,'tr');for(const value of [o.idText,kindNames[o.kind] || o.kind,o.name,format(o.position?.[2])])add(tr,'td',value);}
  }
  target.focus();$('report-message').textContent='Отчёт открыт в отдельном окне.';
};
