import * as z from 'zod/v4';
import { executeCode } from './code-runtime.js';

export const codeToolNames = ['search_tools', 'describe_tools', 'execute_code'];
export function registerCodeTools(server, catalog, denied = new Set()) {
  const read = { readOnlyHint: true, destructiveHint: false, openWorldHint: false };
  const register = (name, description, inputSchema, annotations, handler) => !denied.has(name) && server.registerTool(name,
    { description, inputSchema, annotations, outputSchema: z.object({}).loose() }, async (args, context) => {
      try {
        const result = await handler(args, context?.mcpReq?.signal);
        return { ...(result.failed ? { isError: true } : {}), structuredContent: result,
          content: [{ type: 'text', text: JSON.stringify(result) }] };
      } catch (e) { return { isError: true, content: [{ type: 'text', text: String(e.message).slice(0, 4000) }] }; }
    });
  register('search_tools', 'Найти команды BP по имени и описанию без загрузки всех схем. Все слова запроса должны совпасть. Пустой query перечисляет каталог постранично.',
    z.object({ query: z.string().max(200).default(''), offset: z.number().int().min(0).max(10000).default(0),
      limit: z.number().int().min(1).max(20).default(10) }).strict(), read, args => catalog.search(args));
  register('describe_tools', 'Получить точные схемы выбранных команд перед вызовом из JavaScript. Ограничения Zod дополнительно проверяются во время вызова.',
    z.object({ names: z.array(z.string().min(1).max(100)).min(1).max(10) }).strict(), read,
    args => ({ tools: catalog.describe(args.names) }));
  register('execute_code', 'Выполнить JavaScript: await tools.call(name,args), циклы, фильтрация, return итог; print выводит выбранные данные. Укажите toolNames. Нет файлов, сети, Node, C# или Revit API. Все вызовы валидируются и выполняются последовательно через обычные preview/apply. Это не атомарная транзакция: при ошибке ранее выполненные записи сохраняются. Лимиты: 64 MB, 1 s CPU, 64 KB итог, 8 KB print.',
    z.object({ code: z.string().min(1).max(32000), toolNames: z.array(z.string().min(1).max(100)).max(20),
      timeoutMs: z.number().int().min(100).max(310000).default(30000),
      maxCalls: z.number().int().min(1).max(100).default(100) }).strict(),
    { readOnlyHint: false, destructiveHint: true, openWorldHint: false },
    (args, signal) => executeCode(args, catalog, { signal }));
}
