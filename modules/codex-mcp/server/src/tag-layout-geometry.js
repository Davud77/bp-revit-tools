// Paper-space primitives. Geometry belongs to the caller's current view and document.
const EPS=1e-7;
const cross=(a,b,c)=>(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
const on=(a,b,p)=>Math.abs(cross(a,b,p))<=EPS&&p.x>=Math.min(a.x,b.x)-EPS&&p.x<=Math.max(a.x,b.x)+EPS&&p.y>=Math.min(a.y,b.y)-EPS&&p.y<=Math.max(a.y,b.y)+EPS;
export function segmentsIntersect(a,b,c,d){
 const x=cross(a,b,c),y=cross(a,b,d),u=cross(c,d,a),v=cross(c,d,b);
 return (x*y< -EPS&&u*v< -EPS)||on(a,b,c)||on(a,b,d)||on(c,d,a)||on(c,d,b);
}
export function segmentRectIntersects(a,b,r){
 const inside=p=>p.x>=r.x0-EPS&&p.x<=r.x1+EPS&&p.y>=r.y0-EPS&&p.y<=r.y1+EPS;
 if(inside(a)||inside(b))return true;
 const p=[{x:r.x0,y:r.y0},{x:r.x1,y:r.y0},{x:r.x1,y:r.y1},{x:r.x0,y:r.y1}];
 return p.some((q,i)=>segmentsIntersect(a,b,q,p[(i+1)%4]));
}
export const rectanglesIntersect=(a,b)=>a.x0<=b.x1+EPS&&a.x1>=b.x0-EPS&&a.y0<=b.y1+EPS&&a.y1>=b.y0-EPS;
export function shelfLengthMillimeters(rowWidths){
 if(!rowWidths.length||rowWidths.some(w=>!Number.isFinite(w)||w<0))throw Error('Supply measured paper widths');
 return Math.max(5,Math.ceil((Math.max(...rowWidths)-EPS)/5)*5);
}
export function tagCollisionReport(tags,obstacles=[]){
 const result={textText:[],textLine:[],lineLine:[],protectedModel:[],softModel:[]};
 for(let i=0;i<tags.length;i++)for(let j=i+1;j<tags.length;j++){
  const a=tags[i],b=tags[j];
  if(a.rects.some(r=>b.rects.some(s=>rectanglesIntersect(r,s))))result.textText.push([a.id,b.id]);
  if(a.rects.some(r=>b.lines.some(l=>segmentRectIntersects(l.a,l.b,r)))||b.rects.some(r=>a.lines.some(l=>segmentRectIntersects(l.a,l.b,r))))result.textLine.push([a.id,b.id]);
  if(a.lines.some(l=>b.lines.some(k=>segmentsIntersect(l.a,l.b,k.a,k.b))))result.lineLine.push([a.id,b.id]);
 }
 for(const tag of tags)for(const o of obstacles){
  const labelHit=tag.rects.some(r=>rectanglesIntersect(r,o.rect))||tag.lines.filter(l=>l.kind==='shelf').some(l=>segmentRectIntersects(l.a,l.b,o.rect));
  const leaderHit=tag.lines.filter(l=>l.kind!=='shelf').some(l=>l.targetId!==o.elementId&&segmentRectIntersects(l.a,l.b,o.rect));
  if(labelHit||leaderHit)(o.hard?result.protectedModel:result.softModel).push([tag.id,o.elementId]);
 }
 result.hardCount=result.textText.length+result.textLine.length+result.lineLine.length+result.protectedModel.length;
 return result;
}
