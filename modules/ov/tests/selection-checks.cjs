const assert=require('node:assert/strict'),fs=require('node:fs');require('node:vm').runInThisContext(fs.readFileSync('HeatLossViewer/Viewer/Selection.js','utf8'));
const state=new ObjectSelection();const room={key:'room',roomKey:'room',kind:'room'},door={key:'door',roomKey:'room',kind:'door'},other={key:'other',roomKey:'other',kind:'room'};
state.apply(room);state.apply(door,{ctrlKey:true});state.apply(other,{ctrlKey:true});assert.equal(state.items.size,3);
state.apply(door,{shiftKey:true});assert.equal(state.items.size,2);assert.equal(state.current().key,'other');
state.apply(other,{shiftKey:true,ctrlKey:true});assert.equal(state.current().key,'room');assert.equal(state.items.size,1);
state.apply(null,{ctrlKey:true});assert.equal(state.items.size,1);state.apply(null);assert.equal(state.items.size,0);
state.apply(room);state.apply(other);assert.deepEqual([...state.items.keys()],['other']);state.apply(other,{shiftKey:true});assert.equal(state.current(),null);
console.log('PASS additive selection, Shift subtraction, mixed objects, active fallback, empty click and replacement');
