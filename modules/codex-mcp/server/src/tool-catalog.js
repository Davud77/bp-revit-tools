import * as z from 'zod/v4';

export class ToolCatalog {
  #tools = new Map();
  #schemas = new Map();
  add(name, description, schema, annotations, handler) {
    if (this.#tools.has(name)) throw new Error(`Duplicate tool: ${name}`);
    this.#tools.set(name, { name, description, schema, annotations, handler,
      searchText: `${name} ${description}`.toLocaleLowerCase() });
  }
  has(name) { return this.#tools.has(name); }
  search({ query = '', offset = 0, limit = 10 } = {}) {
    const words = query.toLocaleLowerCase().split(/\s+/u).filter(Boolean);
    const items = [...this.#tools.values()].filter(t => words.every(w =>
      t.searchText.includes(w)));
    return { total: items.length, offset, nextOffset: offset + limit < items.length ? offset + limit : null,
      tools: items.slice(offset, offset + limit).map(t => ({ name: t.name,
        description: t.description, annotations: t.annotations })) };
  }
  describe(names) {
    return names.map(name => {
      const t = this.#tools.get(name);
      if (!t) throw new Error(`Unknown tool: ${name}`);
      if (!this.#schemas.has(name))
        this.#schemas.set(name, JSON.stringify(z.toJSONSchema(t.schema, { unrepresentable: 'any' })));
      return { name, description: t.description, annotations: t.annotations,
        // A fresh value prevents callers from mutating the cached contract.
        inputSchema: JSON.parse(this.#schemas.get(name)) };
    });
  }
  async call(name, args, context) {
    const t = this.#tools.get(name);
    if (!t) throw new Error(`Unknown tool: ${name}`);
    // The same Zod contract is used by direct MCP and code calls. Refinements run here too.
    return await t.handler(t.schema.parse(args), context);
  }
}
