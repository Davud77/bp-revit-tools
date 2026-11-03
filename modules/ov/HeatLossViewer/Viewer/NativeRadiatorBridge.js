// Only included in the local page hosted by Revit; all model writes stay in the native command.
globalThis.botplusRadiatorSnapshot=()=>{
 if(!$('radiator-form').checkValidity()){ $('radiator-form').reportValidity();throw Error('Проверьте параметры подбора радиаторов.'); }
 if(selectedKey)roomRadiatorChange();
 $('radiator-form').dispatchEvent(new Event('submit',{cancelable:true}));
 return {settings:radiatorSettings,custom:customRadiators};
};
globalThis.botplusUpdateLibrary=library=>{DATA.ovLibrary=library;libraryDefaults=globalThis.OvLibraryInputs.create(library,DATA);recalculate();};
$('rad-schedule').onclick=()=>{
 const quote=v=>'"'+(typeof v==='string' && /^[\s]*[=+@-]/.test(v)?"'"+v:String(v??'')).replaceAll('"','""')+'"';
 const rows=[['Помещение','Имя','Этаж','Прибор','Длина мм','Высота мм','Мощность Вт','График','Статус']];
 for(const room of rooms)for(const d of radiatorPlans.get(room.key)?.devices || [])rows.push([room.number,room.name,room.levelName,d.product.model,d.product.lengthMm,d.product.heightMm,d.power.toFixed(1),`${radiatorSettings.supply}/${radiatorSettings.return}`,radiatorPlans.get(room.key).status]);
 chrome.webview.postMessage({type:'export-file',name:'radiator_schedule.csv',text:'\ufeff'+rows.map(r=>r.map(quote).join(';')).join('\r\n')});
};
document.querySelector('.tabs button[data-view=radiators]').click();
document.querySelector('.tabs button[data-view=totals]').hidden=true;document.querySelector('.tabs button[data-view=logs]').hidden=true;
chrome.webview.postMessage({type:'ready'});
