import { PF_CHECKBOX, PF_FILE, PF_INPUT, PF_SELECT, PF_TEXTAREA, PublicFieldProps, formFields, toPayload } from './form-fields';

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
});
