import { isDesignableSchema, summarizeSchema } from './form-summary';

describe('короткий опис схеми форми (F11-04)', () => {
  it('порожнє значення — без полів', () => {
    for (const v of [undefined, null, {}, { type: 'object', properties: {} }]) expect(summarizeSchema(v)).toEqual({ count: 0, labels: [], more: 0 });
  });

  it('перші 5 підписів у порядку розмітки, решта — «ще N»; без підпису — ключ', () => {
    const properties = Object.fromEntries(['a', 'b', 'c', 'd', 'e', 'f', 'g'].map((k) => [k, k === 'b' ? { type: 'string' } : { type: 'string', title: k.toUpperCase() }]));
    const summary = summarizeSchema({
      type: 'object',
      properties,
      'x-openbp-layout': { sections: [{ rows: [[{ field: 'g' }], [{ field: 'b' }, { field: 'a' }], [{ field: 'c' }], [{ field: 'd' }], [{ field: 'e' }], [{ field: 'f' }]] }] },
    });
    expect(summary).toEqual({ count: 7, labels: ['G', 'b', 'A', 'C', 'D'], more: 2 });
  });

  it('вираз чи не-об’єкт — не схема для конструктора', () => {
    expect(isDesignableSchema('{{ $json.schema }}')).toBe(false);
    expect(isDesignableSchema([])).toBe(false);
    expect(isDesignableSchema({ type: 'object' })).toBe(true);
    expect(summarizeSchema('{{ x }}').count).toBe(0);
  });
});
