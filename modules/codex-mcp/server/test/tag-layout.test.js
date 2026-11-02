import test from'node:test';import assert from'node:assert/strict';
import{segmentsIntersect,shelfLengthMillimeters,tagCollisionReport}from'../src/tag-layout-geometry.js';
test('crossings include collinear overlaps and shared endpoints between different marks',()=>{
 assert.ok(segmentsIntersect({x:0,y:0},{x:10,y:10},{x:0,y:10},{x:10,y:0}));
 assert.ok(segmentsIntersect({x:0,y:0},{x:10,y:0},{x:5,y:0},{x:15,y:0}));
 assert.ok(segmentsIntersect({x:0,y:0},{x:10,y:0},{x:10,y:0},{x:10,y:10}));
 assert.equal(segmentsIntersect({x:0,y:0},{x:10,y:0},{x:0,y:1},{x:10,y:1}),false);
});
test('the widest measured row determines the smallest shelf in five millimetre increments',()=>{
 assert.equal(shelfLengthMillimeters([9.3,28.1]),30);assert.equal(shelfLengthMillimeters([15]),15);assert.equal(shelfLengthMillimeters([15.1]),20);assert.throws(()=>shelfLengthMillimeters([NaN]));
});
test('an isolated text layout can still fail on leaders and protected devices',()=>{
 const a={id:1,rects:[{x0:-10,x1:-5,y0:11,y1:13}],lines:[{a:{x:0,y:0},b:{x:10,y:10},kind:'leader',targetId:20}]};
 const b={id:2,rects:[{x0:15,x1:20,y0:11,y1:13}],lines:[{a:{x:0,y:10},b:{x:10,y:0},kind:'leader',targetId:21}]};
 const r=tagCollisionReport([a,b],[{elementId:22,hard:true,rect:{x0:4,x1:6,y0:4,y1:6}}]);
 assert.equal(r.textText.length,0);assert.equal(r.lineLine.length,1);assert.equal(r.protectedModel.length,2);assert.equal(r.hardCount,3);
 assert.equal(tagCollisionReport([a],[{elementId:20,hard:true,rect:{x0:-1,x1:1,y0:-1,y1:1}}]).hardCount,0);
});
