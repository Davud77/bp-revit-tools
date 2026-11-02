import * as z from 'zod/v4';

export function registerResourceTools(register) {
  const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
  const path = z.string().min(1).max(240);
  const read = { readOnlyHint: true, destructiveHint: false, openWorldHint: false };
  const write = { ...read, readOnlyHint: false };
  register('list_documentation_resources', 'Читать типы аннотаций, штампы, шаблоны, фильтры и GUID общих параметров. Без sourcePath читает текущий проект; с локальным RVT открывает эталон в фоне и закрывает без сохранения. Не читает размещённые элементы. Эталон не должен быть открыт в Revit.', z.object({
    sourcePath: path.optional(),
    kind: z.enum(['all', 'annotationTypes', 'titleBlockTypes', 'viewTemplates', 'filters', 'sharedParameters']).default('all'),
    query: z.string().max(200).default(''),
    offset: z.number().int().min(0).max(100000).default(0),
    limit: z.number().int().min(1).max(100).default(50)
  }).strict(), read);
  register('preview_copy_documentation_resources', 'Пробный перенос выбранных нативных ресурсов из локального RVT с полным откатом. SHA-256 берётся из list_documentation_resources. По умолчанию конфликты имён/семейств отменяют пакет. reuseDestination сохраняет определения целевого проекта, НЕ гарантируя совпадение с эталоном; результат перечисляет повторно используемые ресурсы. Зависимости переносит Revit; GUID и привязки нужно сверять после переноса. Отдельное управление привязками общих параметров не реализовано.', z.object({
    sourcePath: path,
    sourceSha256: z.string().regex(/^[a-fA-F0-9]{64}$/),
    sourceElementIds: z.array(id).min(1).max(50).refine(ids => new Set(ids).size === ids.length, 'Source IDs must be unique.'),
    duplicatePolicy: z.enum(['abort', 'reuseDestination']).default('abort')
  }).strict(), write);
  register('apply_copy_documentation_resources', 'Применить проверенный перенос ресурсов. Повторно проверяет файл эталона, целевой документ и ревизию. Один атомарный пакет; эталон закрывается без сохранения.', z.object({ planId: z.string().min(1).max(100) }).strict(), write);
}
