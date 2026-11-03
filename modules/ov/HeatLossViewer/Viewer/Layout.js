// Persistent desktop split between model/list and the room side panel.
{
  const app=$('app'),handle=$('panel-resizer'),key='heatloss-panel-ratio';
  let ratio=.4;
  try{const saved=Number(localStorage.getItem(key));if(saved>.15 && saved<.8)ratio=saved;}catch{}
  function apply(next,persist=true){
    const width=app.clientWidth,min=280/width,max=1-320/width;
    ratio=Math.max(min,Math.min(max,next));app.style.setProperty('--panel-width',`${ratio*100}%`);
    handle.setAttribute('aria-valuenow',String(Math.round(ratio*100)));
    handle.setAttribute('aria-valuemin',String(Math.ceil(min*100)));handle.setAttribute('aria-valuemax',String(Math.floor(max*100)));
    if(persist)try{localStorage.setItem(key,String(ratio));}catch{}
    requestAnimationFrame(position);
  }
  function position(){
    if(innerWidth<=1100 || ['totals','radiators','logs'].includes(document.body.dataset.view))return;
    const box=app.getBoundingClientRect(),right=$('room-card').getBoundingClientRect(),header=document.querySelector('.sheet').getBoundingClientRect();
    handle.style.left=`${right.left-box.left-5}px`;handle.style.top=`${header.bottom-box.top+8}px`;handle.style.height=`${Math.max(100,Math.max($('stage').getBoundingClientRect().bottom,$('table-panel').getBoundingClientRect().bottom,right.bottom)-header.bottom-8)}px`;
  }
  handle.onpointerdown=event=>{if(event.button!==0)return;handle.setPointerCapture(event.pointerId);handle.dataset.dragging='true';event.preventDefault();};
  handle.onpointermove=event=>{if(handle.dataset.dragging!=='true')return;const box=app.getBoundingClientRect();apply((box.right-event.clientX)/box.width);};
  handle.onpointerup=event=>{delete handle.dataset.dragging;if(handle.hasPointerCapture(event.pointerId))handle.releasePointerCapture(event.pointerId);};
  handle.onpointercancel=()=>delete handle.dataset.dragging;
  handle.onkeydown=event=>{if(!['ArrowLeft','ArrowRight','Home'].includes(event.key))return;event.preventDefault();apply(event.key==='Home'?.4:ratio+(event.key==='ArrowLeft'?20:-20)/app.clientWidth);};
  handle.ondblclick=()=>apply(.4);
  new ResizeObserver(position).observe(app);new ResizeObserver(position).observe($('room-card'));
  addEventListener('resize',()=>{if(innerWidth>1100)apply(ratio,false);});
  document.querySelectorAll('.tabs button').forEach(button=>button.addEventListener('click',()=>requestAnimationFrame(position)));
  if(innerWidth>1100)apply(ratio,false);
}
