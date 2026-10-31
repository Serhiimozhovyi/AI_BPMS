import type { PageField } from '../../projects/project-pages-api.service';
import { designerSchema, fromDesignerSchema, hiddenFields, hiddenRows, isHiddenField, parseHiddenValue } from './page-form-model';

const columns = [
  { name: 'name', type: 'string' },
  { name: 'starts_at', type: 'datetime' },
  { name: 'status', type: 'enum', values: ['active', 'cancelled'] },
  { name: 'author', type: 'user' },
];

describe('форма сторінки ↔ конструктор (F11-05)', () => {
  const def = {
    fields: [
      { column: 'name', label: 'Ім’я', required: true, ui: { placeholder: 'Як звати' } },
      { column: 'starts_at', description: 'Коли' },
      { column: 'status', format: 'badge' },
      { column: 'author', value: '{{ $user.id }}' },
    ] as PageField[],
    formLayout: { sections: [{ title: 'Запис', rows: [[{ field: 'name', width: 8 }, { field: 'starts_at', width: 4 }], [{ field: 'status' }]] }] },
  };

  it('схема для конструктора: видимі поля з типом колонки, підписом, підказкою, обов’язковістю, ui і розміткою; приховані поза нею', () => {
    const schema = designerSchema(def, columns);
    expect(Object.keys(schema['properties'] as object)).toEqual(['name', 'starts_at', 'status']);
    const props = schema['properties'] as Record<string, Record<string, unknown>>;
    expect(props['name']).toEqual({ type: 'string', title: 'Ім’я', 'x-openbp-ui': { placeholder: 'Як звати' } });
    expect(props['starts_at']).toEqual({ type: 'string', title: 'starts_at', format: 'date-time', description: 'Коли' });
    expect(props['status']['oneOf']).toEqual([{ const: 'active', title: 'active' }, { const: 'cancelled', title: 'cancelled' }]);
    expect(schema['required']).toEqual(['name']);
    expect((schema['x-openbp-layout'] as { sections: unknown[] }).sections).toHaveLength(1);
  });

  it('круг сторінка → схема → сторінка без змін зберігає все, чого конструктор не знає (format, value)', () => {
    const hidden = hiddenFields(hiddenRows(def.fields), def.fields);
    const result = fromDesignerSchema(designerSchema(def, columns), def.fields, hidden);
    expect(result.fields).toEqual(def.fields);
    expect(result.formLayout).toEqual(def.formLayout);
  });

  it('зміни в схемі: підпис, обов’язковість, прибране поле, ui; розмітка без нетривіальних частин не пишеться', () => {
    const schema = designerSchema(def, columns);
    const props = schema['properties'] as Record<string, Record<string, unknown>>;
    props['name']['title'] = 'name';
    delete props['starts_at'];
    delete schema['required'];
    delete (props['name'] as Record<string, unknown>)['x-openbp-ui'];
    delete schema['x-openbp-layout'];
    const result = fromDesignerSchema(schema, def.fields, []);
    expect(result.fields).toEqual([{ column: 'name' }, { column: 'status', format: 'badge' }]);
    expect(result.formLayout).toBeUndefined();
  });

  it('приховані поля: рядки списку ↔ value / copyFrom, значення розбираються', () => {
    const rows = hiddenRows([{ column: 'a', value: 5 }, { column: 'b', copyFrom: 'psychologist_id.user_id' }, { column: 'c', value: '{{ $user.id }}' }, { column: 'd' }]);
    expect(rows).toEqual([
      { column: 'a', mode: 'value', text: '5' },
      { column: 'b', mode: 'copy', text: 'psychologist_id.user_id' },
      { column: 'c', mode: 'value', text: '{{ $user.id }}' },
    ]);
    expect(hiddenFields(rows, [])).toEqual([{ column: 'a', value: 5 }, { column: 'b', copyFrom: 'psychologist_id.user_id' }, { column: 'c', value: '{{ $user.id }}' }]);
    expect(parseHiddenValue('true')).toBe(true);
    expect(parseHiddenValue('{{ 5 }}')).toBe('{{ 5 }}');
    expect(parseHiddenValue('active')).toBe('active');
    expect(parseHiddenValue('{ "a": 1 }')).toEqual({ a: 1 });
    expect(parseHiddenValue('[1')).toBe('[1');
    expect(isHiddenField({ column: 'x', value: '' })).toBe(true);
    expect(hiddenFields([{ column: '', mode: 'value', text: 'x' }], [])).toEqual([]);
  });
});
