import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const categoryId = z.number().int().min(-Number.MAX_SAFE_INTEGER).max(Number.MAX_SAFE_INTEGER).refine(value => value !== 0);
const offset = z.number().int().min(0).max(1_000_000).optional();
const page = { offset, limit: z.number().int().min(1).max(100).optional() };
const name = z.string().trim().min(1).max(200);
const filePath = z.string().min(1).max(240);
const exportDirectory = z.string().min(1).max(185);
const plan = z.object({ planId: z.string().min(1).max(80) }).strict();
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const ids = maximum => z.array(id).min(1).max(maximum).refine(values => new Set(values).size === values.length);
const connector = z.object({ elementId: id, connectorId: z.number().int().min(0).max(2_147_483_647) }).strict();
const connectors = (min, max) => z.array(connector).min(min).max(max)
  .refine(values => new Set(values.map(value => value.elementId)).size === values.length);
const coordinate = z.number().finite().min(-100_000).max(100_000);
const point = z.object({ xMeters: coordinate, yMeters: coordinate, zMeters: coordinate }).strict();
const dimension = z.number().finite().min(0.001).max(3);
const raceway = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('conduit'), typeId: id, levelId: id, start: point, end: point, diameterMeters: dimension }).strict(),
  z.object({ kind: z.literal('cableTray'), typeId: id, levelId: id, start: point, end: point, widthMeters: dimension, heightMeters: dimension }).strict()
]);
const sheetCoordinate = z.number().finite().min(0).max(10_000);
const viewChanges = z.object({ viewId: id, name: name.optional(), scale: z.number().int().min(1).max(1000).optional(),
  templateId: id.nullable().optional() }).strict().refine(value => value.name !== undefined || value.scale !== undefined || value.templateId !== undefined);
const fieldId = z.number().int().min(0).max(2_147_483_647);
const scheduleFilter = z.object({ fieldId, comparison: z.enum(['equal', 'notEqual', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual', 'contains', 'beginsWith', 'endsWith']),
  value: z.discriminatedUnion('kind', [
    z.object({ kind: z.literal('text'), value: z.string().max(500) }).strict(),
    z.object({ kind: z.literal('integer'), value: z.number().int().min(-2_147_483_648).max(2_147_483_647) }).strict(),
    z.object({ kind: z.literal('internalDouble'), value: z.number().finite() }).strict(),
    z.object({ kind: z.literal('elementId'), value: categoryId }).strict()
  ]) }).strict().refine(filter => !['contains', 'beginsWith', 'endsWith'].includes(filter.comparison) || filter.value.kind === 'text');
const changeFilter = z.discriminatedUnion('action', [
  z.object({ action: z.literal('remove'), filterId: id }).strict(),
  z.object({ action: z.literal('set'), filterId: id, visible: z.boolean().optional(), enabled: z.boolean().optional() }).strict()
]);
const portCounts = { direct: 2, elbow: 2, union: 2, transition: 2, tee: 3, cross: 4, takeoff: 1 };
const connection = z.object({ mode: z.enum(['direct', 'elbow', 'union', 'transition', 'tee', 'cross', 'takeoff']),
  connectors: connectors(1, 4), hostElementId: id.optional() }).strict().refine(value =>
  value.connectors.length === portCounts[value.mode] && (value.mode === 'takeoff') === (value.hostElementId !== undefined),
  { message: 'The connector count and takeoff host must match the selected mode.' });
const transform = z.object({ mode: z.enum(['move', 'copy', 'rotate']), elementIds: ids(50),
  translation: point.optional(), axisStart: point.optional(), axisEnd: point.optional(),
  degrees: z.number().finite().min(-360).max(360).refine(value => value !== 0).optional() }).strict().refine(value =>
  value.mode === 'rotate'
    ? !value.translation && !!value.axisStart && !!value.axisEnd && value.degrees !== undefined
    : !!value.translation && !value.axisStart && !value.axisEnd && value.degrees === undefined,
  { message: 'Use only translation for move/copy, or both axis points and degrees for rotate.' });

export function registerExtensionTools(register) {
  register('read_mep_connectors', 'Прочитать коннекторы элементов, размеры, направление, системы и связи. Координаты в метрах. Для записи используй пару elementId и connectorId.',
    z.object({ elementIds: ids(20) }).strict());
  register('list_mep_systems', 'Постраничный список систем ОВ, ВК и электрических цепей текущего проекта.',
    z.object({ ...page, kind: z.enum(['mechanical', 'piping', 'electrical']).optional(), nameContains: name.optional() }).strict());
  register('read_mep_system', 'Прочитать состав выбранной MEP-системы и базовое оборудование. Пагинация по элементам системы.',
    z.object({ ...page, systemId: id }).strict());
  register('list_mep_routing_preferences', 'Правила выбора фитингов и деталей в заданных типах труб или воздуховодов.',
    z.object({ typeIds: ids(10) }).strict());
  register('preview_connect_mep', 'Проверить соединение свободных концевых коннекторов ОВ/ВК. direct может подобрать фитинг; явные варианты elbow, union, transition, tee, cross или takeoff. У tee первые два коннектора относятся к магистрали, у takeoff укажи ID магистрального сегмента.',
    connection, write);
  register('apply_connect_mep', 'Применить проверенное соединение или создание фитинга по planId.', plan, write);
  register('preview_disconnect_mep', 'Проверить отсоединение двух непосредственно соединённых физических коннекторов без явного удаления фитингов.',
    z.object({ connectors: connectors(2, 2) }).strict(), write);
  register('apply_disconnect_mep', 'Применить проверенное отсоединение коннекторов по planId.', plan, write);
  register('list_raceway_types', 'Типы кабельных лотков и труб электропроводки.', z.object({}).strict());
  register('preview_create_raceways', 'Проверить создание прямых кабельных лотков и труб электропроводки. Координаты и размеры в метрах.',
    z.object({ segments: z.array(raceway).min(1).max(30) }).strict(), write);
  register('apply_create_raceways', 'Создать проверенный пакет прямых кабельных лотков и труб электропроводки.', plan, write);

  register('list_model_links', 'Список экземпляров RVT-связей с состоянием загрузки и преобразованием из координат связи во внутренние координаты текущей модели.',
    z.object(page).strict());
  register('list_link_elements', 'Читать элементы загруженной RVT-связи. ID принадлежат документу связи; габариты возвращаются в координатах текущей модели.',
    z.object({ ...page, linkId: id, categoryId, nameContains: name.optional() }).strict());
  register('check_element_intersections', 'Проверить пересечения между двумя явными наборами элементов текущей модели. bounds даёт кандидатов по габаритам; interference использует штатную проверку Revit и отдельно возвращает неподдерживаемые пары.',
    z.object({ firstIds: ids(50), secondIds: ids(50), mode: z.enum(['bounds', 'interference']),
      limit: z.number().int().min(1).max(500).optional() }).strict());
  register('list_project_parameters', 'Страница привязок проектных и общих параметров с ID, GUID, группой и категориями.',
    z.object({ ...page, nameContains: name.optional() }).strict());
  register('list_view_filters', 'Фильтры проекта либо фильтры указанного вида с видимостью и состоянием применения.',
    z.object({ ...page, viewId: id.optional(), nameContains: name.optional() }).strict());
  register('list_levels', 'Уровни модели с ID и отметками в метрах.', z.object({ ...page, nameContains: name.optional() }).strict());
  register('list_grids', 'Оси модели с ID и геометрией кривой. Координаты в метрах.', z.object({ ...page, nameContains: name.optional() }).strict());
  register('preview_set_element_worksets', 'Проверить перенос указанных элементов на существующий пользовательский рабочий набор.',
    z.object({ worksetId: z.number().int().min(0).max(2_147_483_647), elementIds: ids(100) }).strict(), write);
  register('apply_set_element_worksets', 'Применить проверенную смену рабочего набора по planId.', plan, write);

  register('preview_create_empty_sheets', 'Проверить создание пустых листов с выбранной основной надписью или без неё.',
    z.object({ sheets: z.array(z.object({ number: z.string().trim().min(1).max(50), name, titleBlockTypeId: id.optional() }).strict()).min(1).max(20) }).strict(), write);
  register('apply_create_empty_sheets', 'Создать проверенный пакет листов.', plan, write);
  register('preview_place_sheet_items', 'Проверить размещение существующих видов и спецификаций на существующем листе. xMm/yMm от нижнего левого угла, для вида это центр экрана, для спецификации точка вставки.',
    z.object({ sheetId: id, items: z.array(z.object({ viewId: id, xMm: sheetCoordinate, yMm: sheetCoordinate }).strict()).min(1).max(20) }).strict(), write);
  register('apply_place_sheet_items', 'Разместить проверенные виды и спецификации на листе.', plan, write);
  register('preview_duplicate_views', 'Проверить дублирование существующих видов с указанными именами и режимом Revit.',
    z.object({ views: z.array(z.object({ viewId: id, name, mode: z.enum(['duplicate', 'withDetailing', 'asDependent']).optional() }).strict()).min(1).max(20) }).strict(), write);
  register('apply_duplicate_views', 'Создать проверенные дубликаты видов.', plan, write);
  register('preview_update_views', 'Проверить изменение имени, масштаба и назначенного шаблона существующих видов. templateId:null снимает шаблон.',
    z.object({ views: z.array(viewChanges).min(1).max(20).refine(values => new Set(values.map(value => value.viewId)).size === values.length) }).strict(), write);
  register('apply_update_views', 'Применить проверенные изменения свойств видов.', plan, write);
  register('export_schedule_csv', 'Экспортировать отображаемые данные спецификации в новый локальный CSV с запятыми и кавычками. Существующий файл сохраняется.',
    z.object({ scheduleId: id, path: filePath }).strict(), write);
  register('preview_configure_schedule', 'Проверить замену фильтров, сортировки и настройку детализации спецификации. Пустой массив очищает соответствующие настройки. internalDouble означает внутренние единицы Revit.',
    z.object({ scheduleId: id, filters: z.array(scheduleFilter).max(8).optional(),
      sort: z.array(z.object({ fieldId, order: z.enum(['ascending', 'descending']), blankLine: z.boolean().optional() }).strict()).max(4).optional(),
      itemized: z.boolean().optional() }).strict().refine(value => value.filters !== undefined || value.sort !== undefined || value.itemized !== undefined), write);
  register('apply_configure_schedule', 'Применить проверенную конфигурацию спецификации.', plan, write);
  register('preview_configure_view_filters', 'Проверить назначение, видимость, активность или снятие существующих фильтров указанного вида либо шаблона.',
    z.object({ viewId: id, filters: z.array(changeFilter).min(1).max(30).refine(values => new Set(values.map(value => value.filterId)).size === values.length) }).strict(), write);
  register('apply_configure_view_filters', 'Применить проверенные настройки фильтров вида.', plan, write);
  register('list_view_family_types', 'Типы семейств видов, включая трёхмерные виды и чертёжные виды.',
    z.object({ ...page, nameContains: name.optional() }).strict());
  register('preview_create_3d_views', 'Проверить создание ортографических 3D-видов с необязательной секущей рамкой. Координаты рамки в метрах.',
    z.object({ views: z.array(z.object({ typeId: id, name, sectionBox: z.object({ min: point, max: point }).strict().optional() }).strict()).min(1).max(20) }).strict(), write);
  register('apply_create_3d_views', 'Создать проверенные ортографические 3D-виды.', plan, write);
  register('preview_transform_elements', 'Проверить перенос, копирование с переносом или поворот элементов. Смещение и ось в метрах, угол в градусах.',
    transform, write);
  register('apply_transform_elements', 'Применить проверенный перенос, копирование или поворот.', plan, write);
  register('list_dwg_export_setups', 'Имена доступных настроек экспорта DWG в открытом проекте.', z.object({}).strict());
  register('export_views_dwg', 'Экспортировать печатаемые виды или листы в отдельную новую папку DWG. Можно использовать сохранённую настройку проекта.',
    z.object({ viewIds: ids(30), directory: exportDirectory, setupName: name.optional() }).strict(), write);
  register('export_project_ifc', 'Экспортировать текущую модель в отдельную новую папку IFC. Допустим фильтр по 3D-виду. Транзакция экспортера откатывается после выпуска файла.',
    z.object({ directory: exportDirectory, version: z.enum(['IFC2x3', 'IFC4', 'IFC4RV']).optional(), viewId: id.optional(), baseQuantities: z.boolean().optional() }).strict(), write);
}
