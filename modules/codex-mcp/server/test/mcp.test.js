import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { registerHostedTools } from '../src/hosted-tools.js';
import { registerAnnotationTools } from '../src/annotation-tools.js';
import { registerGraphicsTools } from '../src/graphics-tools.js';
import { registerSheetTools } from '../src/sheet-tools.js';
import { registerRoofTools } from '../src/roof-tools.js';
import { registerMepTools } from '../src/mep-tools.js';
import { registerStructureTools } from '../src/structure-tools.js';
import { registerResourceTools } from '../src/resource-tools.js';
import { registerInventoryTools } from '../src/inventory-tools.js';
import { registerExtensionTools } from '../src/extension-tools.js';

test('MCP initialize/list and C# dispatcher expose matching tool contracts', async t => {
  const child = spawn(process.execPath, [fileURLToPath(new URL('../src/index.js', import.meta.url))], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  t.after(() => child.kill());
  let buffer = '';
  const pending = new Map();
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => { stderr += chunk; });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffer += chunk;
    while (buffer.includes('\n')) {
      const newline = buffer.indexOf('\n');
      const message = JSON.parse(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
      pending.get(message.id)?.(message);
    }
  });
  let nextId = 1;
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    const timeout = setTimeout(() => reject(new Error(`MCP ${method} timed out: ${stderr}`)), 5000);
    pending.set(id, response => { clearTimeout(timeout); pending.delete(id); resolve(response); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const initialized = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'bp-tests', version: '1' } });
  assert.equal(initialized.result.serverInfo.name, 'bp-revit-ai');
  assert.equal(initialized.result.serverInfo.version, '0.9.12');
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listed = await rpc('tools/list', {});
  const tools = listed.result.tools;
  const names = tools.map(tool => tool.name);
  assert.equal(new Set(names).size, names.length);
  const source = await readFile(new URL('../../plugin/Bridge/RevitTools.cs', import.meta.url), 'utf8');
  const routed = [...source.slice(0, source.indexOf('private static object Ping')).matchAll(/"([a-z0-9_]+)"\s*=>/g)].map(match => match[1]);
  const local = ['store_project_data', 'store_room_data', 'query_stored_data'];
  assert.deepEqual(names.filter(name => !local.includes(name)).sort(), [...new Set(routed)].sort());
  for (const name of ['preview_place_hosted_families', 'apply_create_dimensions', 'preview_tag_rooms', 'apply_color_by_parameter'])
    assert.ok(names.includes(name), `Missing tool ${name}`);
  const deletion = tools.find(tool => tool.name === 'apply_delete_plan');
  const documentation = tools.find(tool => tool.name === 'read_documentation');
  assert.ok(documentation, 'Reference capture needs a native documentation reader');
  assert.equal(documentation.annotations.readOnlyHint, true);
  assert.equal(documentation.inputSchema.properties.limit.maximum, 10);
  assert.ok(tools.find(tool => tool.name === 'preview_connect_mep').inputSchema.properties.connectors);
  assert.ok(tools.find(tool => tool.name === 'preview_transform_elements').inputSchema.properties.mode);
  const oversized = await rpc('tools/call', { name: 'read_documentation', arguments: { offset: 0, limit: 11 } });
  assert.ok(oversized.error || oversized.result?.isError, 'Oversized capture must be rejected before bridge access');
  assert.equal(deletion.inputSchema.properties.allowDependentDeletion.default, false);
  assert.equal(deletion.annotations.destructiveHint, true);
  const invalid = await rpc('tools/call', { name: 'preview_place_hosted_families', arguments: { instances: [] } });
  assert.ok(invalid.error || invalid.result?.isError, 'Invalid batch must be rejected before bridge access');
});

const contracts = new Map();
for (const registrar of [registerHostedTools, registerAnnotationTools, registerGraphicsTools, registerSheetTools, registerRoofTools, registerMepTools, registerStructureTools, registerResourceTools, registerInventoryTools, registerExtensionTools])
  registrar((name, description, schema, annotations) => contracts.set(name, { schema, annotations }));
const parse = (name, args) => contracts.get(name).schema.safeParse(args).success;

test('network and model operations expose unambiguous inputs', () => {
  const first = { elementId: 10, connectorId: 0 };
  const second = { elementId: 11, connectorId: 1 };
  assert.ok(parse('preview_connect_mep', { mode: 'union', connectors: [first, second] }));
  assert.ok(parse('preview_connect_mep', { mode: 'takeoff', connectors: [first], hostElementId: 12 }));
  assert.equal(parse('preview_connect_mep', { mode: 'tee', connectors: [first, second] }), false);
  assert.equal(parse('preview_connect_mep', { mode: 'direct', connectors: [first, { ...first, connectorId: 2 }] }), false);
  assert.equal(parse('preview_connect_mep', { mode: 'direct', connectors: [first, second], hostElementId: 12 }), false);
  const origin = { xMeters: 0, yMeters: 0, zMeters: 0 };
  assert.ok(parse('preview_transform_elements', { mode: 'copy', elementIds: [10], translation: origin }));
  assert.equal(parse('preview_transform_elements', { mode: 'rotate', elementIds: [10], translation: origin, degrees: 90 }), false);
  assert.equal(parse('check_element_intersections', { firstIds: [10], secondIds: [11, 11], mode: 'bounds' }), false);
  assert.ok(parse('preview_configure_schedule', { scheduleId: 7, filters: [] }));
  assert.equal(parse('preview_configure_schedule', { scheduleId: 7,
    filters: [{ fieldId: 0, comparison: 'contains', value: { kind: 'integer', value: 1 } }] }), false);
});

test('schedule inventory bounds cell pages and keeps signed parameter identities', () => {
  assert.ok(parse('read_schedule', { scheduleId: 12, section: 'header', rowLimit: 50, columnLimit: 20 }));
  for (const args of [{ rowLimit: 51 }, { columnLimit: 21 }, { rowOffset: -1 }, { section: 'footer' }])
    assert.equal(parse('read_schedule', { scheduleId: 12, ...args }), false);
  assert.ok(parse('preview_schedule_fields', { categoryId: -2000160 }));
  assert.equal(contracts.get('preview_schedule_fields').annotations.readOnlyHint, false);
  assert.ok(parse('preview_create_schedule', { name: 'Rooms', categoryId: -2000160, fields: [{ parameterId: -1006900, fieldType: 'Instance' }] }));
  assert.equal(parse('preview_create_schedule', { name: 'Rooms', categoryId: 0, fields: [] }), false);
  assert.equal(parse('apply_create_schedule', { planId: '' }), false);
});

test('native tag positioning rejects duplicate and unbounded identities and coordinates', () => {
  const input={viewId:1,tags:[{tagId:2,headPosition:{xMeters:3,yMeters:4,zMeters:5}}]};
  assert.ok(parse('preview_position_mep_tags',input));
  assert.ok(parse('preview_position_mep_tags',{...input,tags:[{...input.tags[0],tagTypeId:3,leaderEndPosition:{xMeters:1,yMeters:2,zMeters:3}}]}));
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[{...input.tags[0],leader:false}]}),false);
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[{...input.tags[0],shelfOffsetUpPaperMillimeters:101}]}),false);
  assert.ok(parse('preview_position_mep_tags',{...input,tags:[{...input.tags[0],elbowPosition:{xMeters:1,yMeters:2,zMeters:3}}]}));
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[{...input.tags[0],elbowPosition:{xMeters:Infinity,yMeters:2,zMeters:3}}]}),false);
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[...input.tags,...input.tags]}),false);
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[]}),false);
  assert.equal(parse('preview_position_mep_tags',{...input,tags:[{tagId:2,headPosition:{xMeters:Infinity,yMeters:4,zMeters:5}}]}),false);
  assert.ok(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,scale:200,xMm:1000,yMm:700}]}));
  assert.ok(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,viewportTypeId:3,labelPosition:{xMm:150,yMm:280,alignment:'center'}}]}));
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,xMm:150}]}),false);
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,labelPosition:{xMm:150,yMm:280,alignment:'right'}}]}),false);
  assert.ok(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,scale:200,xMm:1000,yMm:700,labelPosition:{xMm:800,yMm:650}}]}));
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,scale:200,xMm:1000,yMm:700,labelPosition:{xMm:800}}]}),false);
  assert.equal(parse('preview_finish_sheet',{sheetId:1,viewports:[{viewportId:2,scale:200,xMm:1000,yMm:700,labelPosition:{xMm:800,yMm:-1}}]}),false);
});

test('annotation shelf types require bounded names and five millimetre increments', () => {
  const input={types:[{sourceSymbolId:1,name:'Размер_25',shelfLengthMillimeters:25}]};
  assert.ok(parse('preview_duplicate_tag_types',input));
  assert.equal(parse('preview_duplicate_tag_types',{types:[{...input.types[0],shelfLengthMillimeters:27}]}),false);
  assert.equal(parse('preview_duplicate_tag_types',{types:[{...input.types[0],shelfLengthMillimeters:0}]}),false);
  assert.equal(parse('preview_duplicate_tag_types',{types:[{...input.types[0],name:'  '}]}),false);
  assert.ok(parse('read_mep_tag_layout',{viewId:1,tagIds:[2,3]}));
  assert.equal(parse('read_mep_tag_layout',{viewId:1,tagIds:[2,2]}),false);
  assert.ok(parse('preview_merge_mep_tags',{viewId:1,groups:[{tagIds:[2,3]}]}));
  assert.equal(parse('preview_merge_mep_tags',{viewId:1,groups:[{tagIds:[2,3]},{tagIds:[3,4]}]}),false);
  assert.ok(parse('preview_position_mep_tags',{viewId:1,tags:[{tagId:2,headPosition:{xMeters:0,yMeters:0,zMeters:0},leaders:[{elementId:3,endPosition:{xMeters:1,yMeters:1,zMeters:1}}]}]}));
});

test('resource transfers bind source hashes and require explicit bounded unique selections', () => {
  const input = { sourcePath: 'C:/reference.rvt', sourceSha256: 'a'.repeat(64), sourceElementIds: [1, 2] };
  assert.ok(parse('list_documentation_resources', {}));
  assert.ok(parse('list_documentation_resources', { kind: 'sharedParameters', limit: 100 }));
  assert.ok(parse('preview_copy_documentation_resources', input));
  assert.ok(parse('preview_copy_documentation_resources', { ...input, duplicatePolicy: 'reuseDestination' }));
  for (const changes of [{ sourceSha256: '' }, { sourceElementIds: [] }, { sourceElementIds: [1, 1] },
    { sourceElementIds: Array.from({ length: 51 }, (_, i) => i + 1) }, { duplicatePolicy: 'overwrite' }, { sourceElementIds: [Infinity] }])
    assert.equal(parse('preview_copy_documentation_resources', { ...input, ...changes }), false);
  assert.equal(parse('list_documentation_resources', { limit: 101 }), false);
  assert.equal(parse('apply_copy_documentation_resources', {}), false);
  assert.equal(contracts.get('preview_copy_documentation_resources').annotations.readOnlyHint, false);
  assert.equal(contracts.get('list_documentation_resources').annotations.readOnlyHint, true);
});

test('sheet notes preserve an explicit native text type and reject ambiguous style selectors', () => {
  const note = { xMm: 30, yMm: 270, widthMm: 100, text: 'Native note' };
  const input = n => ({ sheetId: 1, notes: [n] });
  assert.ok(parse('preview_finish_sheet', input({ ...note, textTypeId: 2 })));
  assert.ok(parse('preview_finish_sheet', input({ ...note, heightMm: 2.5 })));
  assert.equal(parse('preview_finish_sheet', input(note)), false);
  assert.equal(parse('preview_finish_sheet', input({ ...note, textTypeId: 2, heightMm: 2.5 })), false);
  assert.equal(parse('preview_finish_sheet', input({ ...note, textTypeId: 0 })), false);
});

test('MEP tags require explicit types, unique targets and valid leader geometry', () => {
  const target = { elementId: 10, tagTypeId: 20 };
  const point = { xMeters: 1, yMeters: 2, zMeters: 3 };
  assert.ok(parse('preview_tag_mep', { tags: [target] }));
  assert.ok(parse('preview_tag_mep', { viewId: 30, tags: [{ elementId: 10 }],
    categoryTypes: [{ category: 'OST_DuctTerminal', tagTypeId: 20 }] }));
  assert.ok(parse('preview_tag_mep', { tags: [{ ...target, headPosition: point,
    leaderEndPosition: point, elbowPosition: { ...point, xMeters: 2 } }] }));
  for (const input of [
    { tags: [] }, { tags: Array(51).fill(target) }, { tags: [target, target] },
    { tags: [{ elementId: 10 }] }, { tags: [{ ...target, leader: false, elbowPosition: point }] },
    { tags: [{ ...target, leader: false, leaderEndPosition: point }] },
    { tags: [{ ...target, headPosition: point, offsetRightPaperMillimeters: 15 }] },
    { tags: [{ ...target, headPosition: { ...point, zMeters: Infinity } }] },
    { tags: [{ ...target, offsetUpPaperMillimeters: 101 }] },
    { tags: [{ ...target, orientation: 'diagonal' }] },
    { tags: [target], categoryTypes: [{ category: 'OST_Walls', tagTypeId: 20 }] },
    { tags: [target], categoryTypes: Array(2).fill({ category: 'OST_DuctCurves', tagTypeId: 20 }) }
  ]) assert.equal(parse('preview_tag_mep', input), false, JSON.stringify(input));
  assert.ok(parse('list_annotation_types', { kind: 'mepTags' }));
  assert.equal(contracts.get('apply_tag_mep').annotations.readOnlyHint, false);
  assert.equal(parse('apply_tag_mep', {}), false);
});

test('roofs and sections reject degenerate geometry before bridge access', () => {
  const roof={roofTypeId:1,levelId:2,minX:0,maxX:12,minY:0,maxY:8,offsetMeters:3.15,slopeDegrees:30,ridgeAxis:'x',attachWallIds:[3,4]};
  assert.ok(parse('preview_create_roof',roof));
  for(const changes of [{maxX:0},{slopeDegrees:0},{slopeDegrees:90},{ridgeAxis:'z'},{attachWallIds:[3,3]},{offsetMeters:Infinity}])
    assert.equal(parse('preview_create_roof',{...roof,...changes}),false);
  const section={name:'Section',xMeters:0,yMeters:0,bottomMeters:-1,topMeters:7,widthMeters:15,depthMeters:10,directionX:0,directionY:1};
  assert.ok(parse('preview_create_sections',{views:[section]}));
  for(const changes of [{topMeters:-2},{directionY:0},{depthMeters:0}])
    assert.equal(parse('preview_create_sections',{views:[{...section,...changes}]}),false);
});

test('MEP contracts require a valid segment and matching size shape', () => {
  const point = (x) => ({ xMeters: x, yMeters: 0, zMeters: 2 });
  const pipe = { kind: 'pipe', typeId: 1, systemTypeId: 2, levelId: 3,
    start: point(0), end: point(2), diameterMeters: 0.05 };
  assert.ok(parse('preview_create_mep', { segments: [pipe] }));
  assert.ok(parse('preview_create_mep', { segments: [{ ...pipe, kind: 'duct',
    diameterMeters: undefined, widthMeters: 0.4, heightMeters: 0.2 }] }));
  for (const bad of [{ end: point(0) }, { diameterMeters: 0 },
    { widthMeters: 0.2 }, { kind: 'duct', widthMeters: 0.2, heightMeters: 0.2 }])
    assert.equal(parse('preview_create_mep', { segments: [{ ...pipe, ...bad }] }), false);
});

test('beam system and phase contracts reject invalid layouts and duplicate views', () => {
  const system = { levelId: 1, beamTypeId: 2, minX: 0, maxX: 10, minY: 0, maxY: 8,
    beamDirection: 'x', spacingMeters: 0.6 };
  assert.ok(parse('preview_create_beam_systems', { systems: [system] }));
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, maxX: 0 }] }), false);
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, spacingMeters: 0 }] }), false);
  assert.ok(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 3] }));
  assert.equal(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 2] }), false);
});

test('sheet layout rejects incomplete views and reversed crop boxes', () => {
  const view={sourceViewId:1,viewName:'Plan A3',scale:50,centerXmm:200,centerYmm:165};
  const sheet={number:'АР-01',name:'План этажа',views:[view]};
  assert.ok(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[sheet]}));
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[]}]}),false);
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,scale:0}]}]}),false);
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,crop:{minX:1,maxX:0,minY:0,maxY:1,minZ:0,maxZ:1}}]}]}),false);
  assert.equal(parse('export_sheets_pdf',{sheetIds:[],path:'C:/out.pdf'}),false);
});

test('hosted placement requires one level selector and one sill selector', () => {
  const instance = { hostWallId: 1, familySymbolId: 2, levelId: 3, position: { xMeters: 1, yMeters: 2 } };
  assert.ok(parse('preview_place_hosted_families', { instances: [instance] }));
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, levelName: 'L1' }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, sillHeightMeters: 1, offsetMeters: 2 }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, hostWallId: Number.MAX_SAFE_INTEGER + 1 }] }), false);
});

test('graphics contracts reject incompatible fields, duplicates and missing targets', () => {
  assert.ok(parse('preview_element_graphics', { viewId: 1, action: 'reset_temporary' }));
  for (const args of [
    { viewId: 1, action: 'hide' },
    { viewId: 1, action: 'set_color', elementIds: [2] },
    { viewId: 1, action: 'hide', elementIds: [2, 2] },
    { viewId: 1, action: 'reset_temporary', elementIds: [2] },
    { viewId: 1, action: 'set_transparency', elementIds: [2], transparency: 101 }
  ]) assert.equal(parse('preview_element_graphics', args), false);
});

test('dimensions require complete line and at least two explicit references', () => {
  const dimension = { dimensionTypeId: 2, line: { start: { xMeters: 0, yMeters: 0, zMeters: 0 }, end: { xMeters: 2, yMeters: 0, zMeters: 0 } },
    references: [{ elementId: 3, stableReference: 'a' }, { elementId: 4, stableReference: 'b' }] };
  assert.ok(parse('preview_create_dimensions', { dimensions: [dimension] }));
  assert.equal(parse('preview_create_dimensions', { dimensions: [{ ...dimension, references: [dimension.references[0]] }] }), false);
  assert.equal(parse('preview_tag_rooms', { tagTypeId: 2, offsetUpPaperMillimeters: 101 }), false);
});
