import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const signedId = z.number().int().min(-Number.MAX_SAFE_INTEGER).max(Number.MAX_SAFE_INTEGER).refine(value => value !== 0);
const offset = z.number().int().min(0).max(1_000_000).optional();
const page = { offset, limit: z.number().int().min(1).max(100).optional() };
const nameContains = z.string().trim().min(1).max(200).optional();
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };

export function registerInventoryTools(register) {
  register('list_views', 'Страница видов проекта с ID, типом, уровнем, шаблоном и масштабом. Фильтр viewType принимает имя Revit ViewType, например FloorPlan или ThreeD.',
    z.object({ ...page, nameContains, viewType: z.string().trim().min(1).max(80).optional(), includeTemplates: z.boolean().optional() }).strict());
  register('list_schedules', 'Страница спецификаций проекта с ID, категориями и типом спецификации.',
    z.object({ ...page, nameContains, categoryId: signedId.optional() }).strict());
  register('read_schedule', 'Прочитать поля и ограниченную страницу отображаемых ячеек Header или Body спецификации по ID. Смещения строк и столбцов считаются от начала секции.',
    z.object({ scheduleId: id, section: z.enum(['header', 'body']).optional(), rowOffset: offset, columnOffset: offset,
      rowLimit: z.number().int().min(1).max(50).optional(), columnLimit: z.number().int().min(1).max(20).optional() }).strict());
  register('list_schedule_categories', 'Категории, доступные для создания обычной спецификации. Возвращает ID категорий для preview_create_schedule.',
    z.object({ ...page, nameContains }).strict());
  register('list_schedule_fields', 'Доступные поля существующей спецификации. Используй пару parameterId и fieldType для однозначного выбора поля.',
    z.object({ ...page, scheduleId: id, nameContains }).strict());
  register('preview_schedule_fields', 'Определить доступные поля для категории без существующей спецификации. Создаёт пробную спецификацию с полным откатом и возвращает страницу полей.',
    z.object({ ...page, categoryId: signedId, nameContains }).strict(), write);
  register('preview_create_schedule', 'Проверить создание обычной спецификации с выбранными полями в указанном порядке. Операция полностью откатывается. Временный ID из preview использовать нельзя.',
    z.object({ categoryId: signedId, name: z.string().trim().min(1).max(200), fields: z.array(z.object({ parameterId: signedId,
      fieldType: z.string().trim().min(1).max(80).optional(), heading: z.string().trim().min(1).max(200).optional() }).strict()).min(1).max(30) }).strict(), write);
  register('apply_create_schedule', 'Создать проверенную спецификацию по действующему planId и вернуть постоянный ID.',
    z.object({ planId: z.string().min(1).max(80) }).strict(), write);
  register('get_model_warnings', 'Прочитать страницу сохранённых предупреждений Revit, описания и ID затронутых элементов. Фильтр elementId ищет и среди основных, и среди дополнительных элементов.',
    z.object({ ...page, elementId: id.optional() }).strict());
  register('list_worksets', 'Прочитать пользовательские рабочие наборы открытого проекта, активный набор и состояние worksharing.',
    z.object({ ...page, nameContains }).strict());
}
