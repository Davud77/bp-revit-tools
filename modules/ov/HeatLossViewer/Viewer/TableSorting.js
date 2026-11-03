// Sort displayed rows without replacing controls, row handlers or calculation data.
{
 const states=new WeakMap(),collator=new Intl.Collator('ru',{numeric:true,sensitivity:'base'});
 function value(cell){
  const control=cell?.querySelector('input:not([type=checkbox]),select');
  const text=String(cell?.dataset.sortValue ?? (control?.tagName==='SELECT'?control.selectedOptions[0]?.text:control?control.value:cell?.textContent) ?? '').trim();
  if(!text || /^(—|нет данных)$/i.test(text))return null;
  const compact=text.replace(/[\s\u00a0\u202f]/g,'').replace(',','.');
  if(/^[+-]?\d+(?:\.\d+)?(?:%|Вт|кВт|м²|м³|м|°C)?$/.test(compact))return Number.parseFloat(compact);
  return text;
 }
 function apply(table){
  const state=states.get(table);if(!state)return;
  for(const body of table.tBodies){
   const rows=[...body.rows];
   const sorted=rows.map((row,index)=>({row,index,v:value(row.cells[state.column])})).sort((a,b)=>{
    if(a.v===null || b.v===null)return a.v===b.v?a.index-b.index:a.v===null?1:-1;
    const order=typeof a.v==='number'&&typeof b.v==='number'?a.v-b.v:collator.compare(String(a.v),String(b.v));
    return order*state.direction || a.index-b.index;
   }).map(x=>x.row);
   if(rows.some((row,i)=>row!==sorted[i]))body.append(...sorted);
  }
 }
 function sort(header){
  const table=header.closest('table'),old=states.get(table),column=header.cellIndex;
  const direction=old?.column===column?-old.direction:1;
  states.set(table,{column,direction});
  table.querySelectorAll('thead th').forEach(h=>h.setAttribute('aria-sort',h===header?(direction===1?'ascending':'descending'):'none'));
  apply(table);
 }
 function initialise(){
  for(const table of document.querySelectorAll('table')){
   if(table.id==='room-table' || table.dataset.sortableReady)continue;
   table.dataset.sortableReady='true';
   table.querySelectorAll('thead th').forEach(h=>{h.classList.add('sortable');h.tabIndex=0;h.setAttribute('aria-sort','none');h.title='Сортировать по столбцу';});
   for(const body of table.tBodies)new MutationObserver(()=>apply(table)).observe(body,{childList:true,subtree:true,characterData:true});
  }
 }
 document.addEventListener('click',event=>{const h=event.target.closest('th.sortable');if(h)sort(h);});
 document.addEventListener('keydown',event=>{if(event.target.matches('th.sortable')&&['Enter',' '].includes(event.key)){event.preventDefault();sort(event.target);}});
 document.addEventListener('change',event=>{const table=event.target.closest('table');if(table)queueMicrotask(()=>apply(table));});
 initialise();new MutationObserver(initialise).observe($('app'),{childList:true,subtree:true});
}
