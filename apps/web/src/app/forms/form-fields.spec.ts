import type { FormlyFieldConfig } from '@ngx-formly/core';
import { PF_CHECKBOX, PF_FILE, PF_INPUT, PF_SECTION, PF_SELECT, PF_TEXTAREA, PublicFieldProps, SectionProps, formFields, toPayload } from './form-fields';

describe('formFields (F6-03)', () => {
  const schema = {
    type: 'object',
    required: ['name', 'days'],
    properties: {
      name: { type: 'string', title: 'ПІБ', minLength: 2 },
      days: { type: 'integer', title: 'Днів', minimum: 1, maximum: 28 },
      kind: { type: 'string', enum: ['annual', 'unpaid'] },
      level: { type: 'string', oneOf: [{ const: 'l1', title: 'Перший' }, { const: 'l2', title: 'Другий' }] },
      tags: { type: 'array', items: { enum: ['a', 'b'] } },
      email: { type: 'string', format: 'email', description: 'Робоча пошта' },
      start: { type: 'string', format: 'date' },
      at: { type: 'string', format: 'date-time' },
      comment: { type: 'string', format: 'textarea' },
      agree: { type: 'boolean', title: 'Погоджуюсь' },
      scan: { type: 'string', format: 'binary', title: 'Скан' },
    },
  };

  it('кожна властивість — поле свого типу з підписом, обов’язковістю й межами', () => {
    const fields = formFields(schema);
    const byKey = Object.fromEntries(fields.map((f) => [f.key as string, f]));
    const props = (key: string) => byKey[key].props as PublicFieldProps;

    expect(fields.map((f) => f.key)).toEqual(Object.keys(schema.properties));
    expect([byKey['name'].type, props('name').label, props('name').required, props('name').minLength]).toEqual([PF_INPUT, 'ПІБ', true, 2]);
    expect([props('days').type, props('days').min, props('days').max, props('days').required]).toEqual(['number', 1, 28, true]);
    expect(byKey['kind'].type).toBe(PF_SELECT);
    expect(props('kind').options).toEqual([
      { value: 'annual', label: 'annual' },
      { value: 'unpaid', label: 'unpaid' },
    ]);
    expect(props('level').options?.map((o) => o.label)).toEqual(['Перший', 'Другий']);
    expect([byKey['tags'].type, props('tags').multiple]).toEqual([PF_SELECT, true]);
    expect([props('email').type, props('email').description]).toEqual(['email', 'Робоча пошта']);
    expect(props('start').type).toBe('date');
    expect(props('at').type).toBe('datetime-local');
    expect(byKey['comment'].type).toBe(PF_TEXTAREA);
    expect([byKey['agree'].type, byKey['agree'].defaultValue]).toEqual([PF_CHECKBOX, false]);
    expect(byKey['scan'].type).toBe(PF_FILE);
  });

  it('не об’єкт або без properties — порожня форма', () => {
    expect(formFields(null)).toEqual([]);
    expect(formFields({ type: 'object' })).toEqual([]);
  });

  it('модель → тіло: порожні відкинуто, числа числами, дата й час — ISO, файли окремо', () => {
    const file = new File(['x'], 'заява.pdf', { type: 'application/pdf' });
    const payload = toPayload(schema, {
      name: 'Олена',
      days: '5',
      kind: '',
      email: null,
      tags: [],
      at: '2026-10-26T10:30',
      agree: false,
      scan: file,
      extra: 'не з форми',
    });

    expect(payload.fields).toEqual({ name: 'Олена', days: 5, at: new Date('2026-10-26T10:30').toISOString(), agree: false });
    expect(payload.files).toEqual({ scan: file });
  });

  it('розмітка (F11-01): секції з рядками на сітці 12, поза розміткою — у кінець, модель плоска', () => {
    const fields = formFields({
      type: 'object',
      required: ['from'],
      properties: {
        name: { type: 'string', title: 'ПІБ', 'x-openbp-ui': { placeholder: 'Прізвище та ім’я' } },
        from: { type: 'string', format: 'date' },
        to: { type: 'string', format: 'date' },
        reason: { type: 'string', format: 'textarea', 'x-openbp-ui': { rows: 2 } },
      },
      'x-openbp-layout': {
        sections: [
          { rows: [[{ field: 'name' }]] },
          { title: 'Дати', description: 'Включно', collapsible: true, collapsed: true, rows: [[{ field: 'from', width: 8 }, { field: 'to' }]] },
        ],
      },
    });

    expect(fields.map((f) => f.wrappers)).toEqual([[PF_SECTION], [PF_SECTION]]);
    expect(fields[1].props as SectionProps).toEqual({ label: 'Дати', description: 'Включно', collapsible: true, collapsed: true });
    const [first, second] = fields.map((s) => s.fieldGroup!.map((row) => row.fieldGroup!.map((f) => [f.key, f.className])));
    expect(first).toEqual([[['name', 'pf-col pf-col-12']]]);
    expect(second).toEqual([
      [
        ['from', 'pf-col pf-col-8'],
        ['to', 'pf-col pf-col-4'],
      ],
      [['reason', 'pf-col pf-col-12']],
    ]);
    expect(fields.flatMap((s) => s.fieldGroup!.map((row) => row.fieldGroupClassName))).toEqual(['pf-row', 'pf-row', 'pf-row']);
    expect(fields.every((s) => s.key === undefined && s.fieldGroup!.every((row) => row.key === undefined))).toBe(true);
    const name = fields[0].fieldGroup![0].fieldGroup![0];
    const reason = fields[1].fieldGroup![1].fieldGroup![0];
    expect((name.props as PublicFieldProps).placeholder).toBe('Прізвище та ім’я');
    expect([reason.type, (reason.props as PublicFieldProps).rows]).toEqual([PF_TEXTAREA, 2]);
    expect((fields[1].fieldGroup![0].fieldGroup![0].props as PublicFieldProps).required).toBe(true);
  });

  it('умова «Показувати, якщо» (F11-06): hide-вираз поля й секції за моделлю; toPayload не надсилає приховане', () => {
    const schema = {
      type: 'object',
      properties: { kind: { type: 'string' }, reason: { type: 'string' }, doctor: { type: 'string' } },
      'x-openbp-layout': {
        sections: [
          { rows: [[{ field: 'kind' }, { field: 'reason', visibleIf: { field: 'kind', operator: 'eq', value: 'other' } }]] },
          { visibleIf: { field: 'kind', operator: 'eq', value: 'sick' }, rows: [[{ field: 'doctor' }]] },
        ],
      },
    };
    const fields = formFields(schema);
    const reason = fields[0].fieldGroup![0].fieldGroup![1];
    const kind = fields[0].fieldGroup![0].fieldGroup![0];
    const hide = (f: FormlyFieldConfig, model: Record<string, unknown>) =>
      (f.expressions!['hide'] as (field: FormlyFieldConfig) => boolean)({ ...f, model });

    expect(kind.expressions).toBeUndefined();
    expect(fields[0].expressions).toBeUndefined();
    expect([hide(reason, { kind: 'annual' }), hide(reason, { kind: 'other' })]).toEqual([true, false]);
    expect([hide(fields[1], { kind: 'annual' }), hide(fields[1], { kind: 'sick' })]).toEqual([true, false]);
    expect(toPayload(schema, { kind: 'annual', reason: 'x', doctor: 'y' }).fields).toEqual({ kind: 'annual' });
    expect(toPayload(schema, { kind: 'other', reason: 'x', doctor: 'y' }).fields).toEqual({ kind: 'other', reason: 'x' });
  });
});
