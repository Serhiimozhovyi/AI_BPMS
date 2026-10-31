import { FormLayout, LAYOUT_KEY, fieldUi, hasLayout, readLayout, rowWidths, writeLayout } from './form-layout';

describe('розмітка форми (F11-01)', () => {
  const properties = {
    name: { type: 'string', title: 'ПІБ' },
    from: { type: 'string', format: 'date' },
    to: { type: 'string', format: 'date' },
    reason: { type: 'string', format: 'textarea' },
  };

  it('без розмітки — одна секція, кожне поле окремим рядком у порядку properties', () => {
    const schema = { type: 'object', properties };
    expect(hasLayout(schema)).toBe(false);
    expect(readLayout(schema)).toEqual({ sections: [{ rows: [[{ field: 'name' }], [{ field: 'from' }], [{ field: 'to' }], [{ field: 'reason' }]] }] });
  });

  it('читає секції й рядки; невідомі поля, повтори й зайві клітинки відкидає, пропущені поля — у кінець', () => {
    const schema = {
      type: 'object',
      properties,
      [LAYOUT_KEY]: {
        sections: [
          { title: 'Хто', rows: [[{ field: 'name' }, { field: 'ghost' }]] },
          {
            title: 'Коли',
            collapsible: true,
            collapsed: true,
            visibleIf: { field: 'name', operator: 'isNotEmpty' },
            rows: [[{ field: 'from', width: 6 }, { field: 'to', width: 6 }, { field: 'name' }], [], [{ field: 'nope' }]],
          },
        ],
      },
    };
    expect(readLayout(schema)).toEqual({
      sections: [
        { title: 'Хто', rows: [[{ field: 'name' }]] },
        {
          title: 'Коли',
          collapsible: true,
          collapsed: true,
          visibleIf: { field: 'name', operator: 'isNotEmpty' },
          rows: [[{ field: 'from', width: 6 }, { field: 'to', width: 6 }], [{ field: 'reason' }]],
        },
      ],
    });
  });

  it('не більше 4 полів у рядку, ширина поза 1–12 ігнорується', () => {
    const many = Object.fromEntries(['a', 'b', 'c', 'd', 'e'].map((k) => [k, { type: 'string' }]));
    const schema = { properties: many, [LAYOUT_KEY]: { sections: [{ rows: [['a', 'b', 'c', 'd', 'e'].map((field) => ({ field, width: 13 }))] }] } };
    const layout = readLayout(schema);
    expect(layout.sections[0].rows).toEqual([[{ field: 'a' }, { field: 'b' }, { field: 'c' }, { field: 'd' }], [{ field: 'e' }]]);
  });

  it('ширини: задані як є, решта ділить залишок', () => {
    expect(rowWidths([{ field: 'a' }])).toEqual([12]);
    expect(rowWidths([{ field: 'a' }, { field: 'b' }, { field: 'c' }])).toEqual([4, 4, 4]);
    expect(rowWidths([{ field: 'a', width: 8 }, { field: 'b' }])).toEqual([8, 4]);
  });

  it('запис: розмітка в схемі, ключі properties у порядку розмітки, решта схеми без змін', () => {
    const schema = { type: 'object', required: ['name'], properties };
    const layout: FormLayout = {
      sections: [
        { title: 'Коли', rows: [[{ field: 'from', width: 6 }, { field: 'to' }]] },
        { rows: [[{ field: 'name' }], [{ field: 'reason' }]] },
      ],
    };
    const result = writeLayout(schema, layout);
    expect(Object.keys(result['properties'] as object)).toEqual(['from', 'to', 'name', 'reason']);
    expect(result['required']).toEqual(['name']);
    expect(result[LAYOUT_KEY]).toEqual({
      sections: [{ title: 'Коли', rows: [[{ field: 'from', width: 6 }, { field: 'to' }]] }, { rows: [[{ field: 'name' }], [{ field: 'reason' }]] }],
    });
    expect(readLayout(result)).toEqual(layout);
    expect(schema).not.toHaveProperty(LAYOUT_KEY);
  });

  it('тривіальна розмітка прибирається — схема лишається як без конструктора', () => {
    const schema = { type: 'object', properties, [LAYOUT_KEY]: { sections: [{ title: 'Стара', rows: [] }] } };
    const result = writeLayout(schema, { sections: [{ rows: [[{ field: 'to' }], [{ field: 'name' }], [{ field: 'from' }], [{ field: 'reason' }]] }] });
    expect(result).not.toHaveProperty(LAYOUT_KEY);
    expect(Object.keys(result['properties'] as object)).toEqual(['to', 'name', 'from', 'reason']);
  });

  it('підказки показу поля', () => {
    expect(fieldUi({ 'x-openbp-ui': { widget: 'radio', placeholder: 'Коротко', rows: 3 } })).toEqual({ widget: 'radio', placeholder: 'Коротко', rows: 3 });
    expect(fieldUi({ 'x-openbp-ui': { widget: 'slider', rows: 50 } })).toEqual({});
    expect(fieldUi({})).toEqual({});
  });
});
