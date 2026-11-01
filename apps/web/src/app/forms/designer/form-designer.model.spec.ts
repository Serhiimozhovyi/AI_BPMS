import {
  DesignerHistory,
  addFieldToRow,
  canJoinPrevious,
  canMerge,
  joinPrevious,
  mergeIntoRow,
  normalizeWidths,
  placeInRow,
  resizeBoundary,
  resizeField,
  setRowWidths,
  splitFromRow,
  widthPresets,
  addField,
  addSection,
  duplicateField,
  fieldOrder,
  fromSchema,
  keyError,
  keyFromLabel,
  kindOf,
  moveField,
  moveRow,
  moveSection,
  nudgeField,
  optionsOf,
  placeOf,
  propertyFor,
  removeField,
  removeSection,
  renameKey,
  setFieldProps,
  setFieldUi,
  setOptions,
  setRequired,
  setSectionProps,
  toSchema,
  uniqueKey,
  rulesUsing,
  ruleSources,
  setFieldVisibleIf,
} from './form-designer.model';

describe('модель конструктора форми (F11-02)', () => {
  const schema = {
    type: 'object',
    title: 'Заява',
    required: ['name', 'from'],
    properties: {
      name: { type: 'string', title: 'ПІБ', minLength: 2, 'x-custom': 1 },
      from: { type: 'string', format: 'date', title: 'З' },
      to: { type: 'string', format: 'date', title: 'По' },
      reason: { type: 'string', format: 'textarea', title: 'Причина' },
    },
    'x-openbp-layout': {
      sections: [
        { title: 'Хто', rows: [[{ field: 'name' }]] },
        { title: 'Коли', rows: [[{ field: 'from', width: 6 }, { field: 'to', width: 6 }], [{ field: 'reason', visibleIf: { field: 'from', operator: 'isNotEmpty' } }]] },
      ],
    },
  };

  it('схема → модель → схема без втрат, зокрема невідомих ключів і visibleIf', () => {
    expect(toSchema(fromSchema(schema))).toEqual(schema);
  });

  it('порожня або не об’єктна схема — одна секція без полів', () => {
    const state = fromSchema(null);
    expect(state.layout.sections).toEqual([{ rows: [] }]);
    expect(toSchema(state)).toEqual({ type: 'object', properties: {} });
  });

  it('ключ з підпису: транслітерація й унікальність', () => {
    expect(keyFromLabel('Прізвище та ім’я')).toBe('prizvyshche_ta_imia');
    expect(keyFromLabel('Дата й час')).toBe('data_i_chas');
    expect(keyFromLabel('2 дні')).toBe('f_2_dni');
    expect(keyFromLabel('!!!')).toBe('field');
    expect(uniqueKey('tekst', ['tekst', 'tekst_2'])).toBe('tekst_3');
    expect(keyError(fromSchema(schema), 'name', 'from')).toBeTruthy();
    expect(keyError(fromSchema(schema), 'name', '1x')).toBeTruthy();
    expect(keyError(fromSchema(schema), 'name', 'full_name')).toBeNull();
  });

  it('додає поле в кінець секції або на вказане місце', () => {
    let state = fromSchema(schema);
    const first = addField(state, 'text');
    expect(first.key).toBe('tekst');
    expect(placeOf(first.state, 'tekst')).toEqual({ section: 1, row: 2, cell: 0 });
    state = addField(first.state, 'text', 0, 0).state;
    expect(placeOf(state, 'tekst_2')).toEqual({ section: 0, row: 0, cell: 0 });
    expect(state.properties['tekst_2']).toEqual({ type: 'string', title: 'Текст' });
  });

  it('типи палітри дають властивості, які конструктор розпізнає назад', () => {
    for (const kind of ['text', 'textarea', 'number', 'email', 'phone', 'address', 'date', 'datetime', 'boolean', 'select', 'multiselect', 'file'] as const) {
      expect(kindOf(propertyFor(kind, 'Поле'))).toBe(kind);
    }
  });

  it('видаляє поле разом з обов’язковістю; рядок з одним полем, що лишилось, втрачає ширину', () => {
    const state = removeField(fromSchema(schema), 'to');
    expect(state.layout.sections[1].rows[0]).toEqual([{ field: 'from' }]);
    expect(removeField(fromSchema(schema), 'name').required).toEqual(['from']);
    expect(toSchema(removeField(fromSchema(schema), 'name'))['properties']).not.toHaveProperty('name');
  });

  it('переносить поле й рядок між секціями', () => {
    let state = moveField(fromSchema(schema), 'to', 0, 0);
    expect(fieldOrder(state)).toEqual(['to', 'name', 'from', 'reason']);
    expect(state.layout.sections[1].rows[0]).toEqual([{ field: 'from' }]);
    state = moveRow(fromSchema(schema), 1, 0, 0, 1);
    expect(state.layout.sections[0].rows).toEqual([[{ field: 'name' }], [{ field: 'from', width: 6 }, { field: 'to', width: 6 }]]);
  });

  it('↑/↓: рядок міняється з сусіднім, на межі переходить у сусідню секцію, поле з рядка виходить окремим', () => {
    let state = nudgeField(fromSchema(schema), 'reason', -1);
    expect(fieldOrder(state)).toEqual(['name', 'reason', 'from', 'to']);
    state = nudgeField(state, 'reason', -1);
    expect(placeOf(state, 'reason')).toEqual({ section: 0, row: 1, cell: 0 });
    expect(nudgeField(fromSchema(schema), 'name', -1)).toEqual(fromSchema(schema));
    state = nudgeField(fromSchema(schema), 'to', 1);
    expect(state.layout.sections[1].rows.map((r) => r.map((c) => c.field))).toEqual([['from'], ['to'], ['reason']]);
  });

  it('дублює поле під оригіналом з новим ключем і обов’язковістю', () => {
    const { state, key } = duplicateField(fromSchema(schema), 'name');
    expect(key).toBe('name_2');
    expect(placeOf(state, 'name_2')).toEqual({ section: 0, row: 1, cell: 0 });
    expect(state.properties['name_2']).toEqual(state.properties['name']);
    expect(state.required).toContain('name_2');
  });

  it('властивості, підказки показу, обов’язковість', () => {
    let state = setFieldProps(fromSchema(schema), 'name', { title: 'Ім’я', minLength: undefined, description: '' });
    expect(state.properties['name']).toEqual({ type: 'string', title: 'Ім’я', 'x-custom': 1 });
    state = setFieldUi(state, 'name', { placeholder: 'Як звати' });
    expect(state.properties['name']['x-openbp-ui']).toEqual({ placeholder: 'Як звати' });
    state = setFieldUi(state, 'name', { placeholder: '' });
    expect(state.properties['name']).not.toHaveProperty('x-openbp-ui');
    expect(setRequired(state, 'to', true).required).toEqual(['name', 'from', 'to']);
    expect(setRequired(state, 'name', false).required).toEqual(['from']);
  });

  it('перейменування ключа оновлює розмітку, обов’язковість і умови', () => {
    const state = renameKey(fromSchema(schema), 'from', 'start');
    expect(Object.keys(state.properties)).toEqual(['name', 'start', 'to', 'reason']);
    expect(state.required).toEqual(['name', 'start']);
    expect(state.layout.sections[1].rows[0][0]).toEqual({ field: 'start', width: 6 });
    expect(state.layout.sections[1].rows[1][0].visibleIf?.field).toBe('start');
    expect(renameKey(fromSchema(schema), 'from', 'to')).toEqual(fromSchema(schema));
  });

  it('секції: додати, налаштувати, перенести, видалити з перенесенням полів', () => {
    let state = addSection(fromSchema(schema));
    expect(state.layout.sections[2]).toEqual({ title: 'Нова секція', rows: [] });
    state = setSectionProps(state, 2, { title: 'Документи', collapsible: true, collapsed: true });
    expect(state.layout.sections[2]).toEqual({ title: 'Документи', collapsible: true, collapsed: true, rows: [] });
    state = setSectionProps(state, 2, { collapsible: false });
    expect(state.layout.sections[2]).toEqual({ title: 'Документи', rows: [] });
    state = moveSection(state, 2, 0);
    expect(state.layout.sections.map((s) => s.title)).toEqual(['Документи', 'Хто', 'Коли']);
    state = removeSection(state, 0);
    expect(state.layout.sections.map((s) => s.title)).toEqual(['Хто', 'Коли']);
    state = removeSection(state, 1);
    expect(fieldOrder(state)).toEqual(['name', 'from', 'to', 'reason']);
    expect(removeSection(state, 0)).toBe(state);
  });

  it('варіанти списку з підписами записуються як oneOf, для масиву — в items', () => {
    const base = addField(addField(fromSchema(schema), 'select').state, 'multiselect').state;
    let state = setOptions(base, 'vybir', [
      { value: 'annual', label: 'Щорічна' },
      { value: 'unpaid', label: '' },
    ]);
    expect(state.properties['vybir']['oneOf']).toEqual([
      { const: 'annual', title: 'Щорічна' },
      { const: 'unpaid', title: 'unpaid' },
    ]);
    expect(optionsOf(state.properties['vybir']).map((o) => o.label)).toEqual(['Щорічна', 'unpaid']);
    state = setOptions(state, 'kilka_variantiv', [{ value: 'a', label: 'А' }]);
    expect(state.properties['kilka_variantiv']['items']).toEqual({ type: 'string', oneOf: [{ const: 'a', title: 'А' }] });
    expect(optionsOf({ type: 'string', enum: ['x', 'y'] })).toEqual([
      { value: 'x', label: 'x' },
      { value: 'y', label: 'y' },
    ]);
  });

  it('історія: undo/redo, нова дія очищає redo, ліміт кроків', () => {
    const history = new DesignerHistory<number>(3);
    history.push(1);
    history.push(2);
    expect(history.undo(3)).toBe(2);
    expect(history.redo(2)).toBe(3);
    expect(history.undo(3)).toBe(2);
    history.push(2);
    expect(history.canRedo).toBe(false);
    for (let i = 10; i < 20; i++) history.push(i);
    let steps = 0;
    let current = 99;
    for (let prev = history.undo(current); prev !== null; prev = history.undo(current)) {
      current = prev;
      steps++;
    }
    expect(steps).toBe(3);
  });

  describe('рядки з кількох полів і ширини (F11-03)', () => {
    const cellsOf = (state: ReturnType<typeof fromSchema>, section: number) =>
      state.layout.sections[section].rows.map((r) => r.map((c) => (c.width ? `${c.field}:${c.width}` : c.field)));

    it('злиття: поле стає зліва чи справа від іншого, ширини рівні, порожній рядок зникає', () => {
      let state = mergeIntoRow(fromSchema(schema), 'name', 'from', 'before');
      expect(cellsOf(state, 0)).toEqual([]);
      expect(cellsOf(state, 1)).toEqual([['name', 'from', 'to'], ['reason']]);
      state = mergeIntoRow(fromSchema(schema), 'reason', 'to', 'after');
      expect(cellsOf(state, 1)).toEqual([['from', 'to', 'reason']]);
      expect(toSchema(state)['x-openbp-layout']).toEqual({
        sections: [
          { title: 'Хто', rows: [[{ field: 'name' }]] },
          { title: 'Коли', rows: [[{ field: 'from' }, { field: 'to' }, { field: 'reason', visibleIf: { field: 'from', operator: 'isNotEmpty' } }]] },
        ],
      });
    });

    it('у межах свого рядка — лише порядок, ширини йдуть з полями', () => {
      let state = setRowWidths(fromSchema(schema), 1, 0, [4, 8]);
      state = mergeIntoRow(state, 'to', 'from', 'before');
      expect(cellsOf(state, 1)[0]).toEqual(['to:8', 'from:4']);
      expect(placeInRow(state, 'to', 1, 0, 2)).toEqual(setRowWidths(fromSchema(schema), 1, 0, [4, 8]));
    });

    it('ліміт 4 поля в рядку', () => {
      let state = fromSchema(schema);
      state = mergeIntoRow(state, 'reason', 'to', 'after');
      state = mergeIntoRow(state, 'name', 'to', 'after');
      expect(cellsOf(state, 1)).toEqual([['from', 'to', 'name', 'reason']]);
      const extra = addField(state, 'text', 0).state;
      expect(canMerge(extra, 'tekst', 'from')).toBe(false);
      expect(mergeIntoRow(extra, 'tekst', 'from', 'after')).toBe(extra);
      expect(canMerge(extra, 'name', 'from')).toBe(true);
      const added = addFieldToRow(state, 'text', 1, 0, 1);
      expect(cellsOf(added.state, 1)).toEqual([['from', 'to', 'name', 'reason'], ['tekst']]);
    });

    it('вихід з рядка: окремим рядком під ним; решта ширин — до суми 12', () => {
      let state = mergeIntoRow(fromSchema(schema), 'reason', 'to', 'after');
      state = setRowWidths(state, 1, 0, [3, 6, 3]);
      state = splitFromRow(state, 'to');
      expect(cellsOf(state, 1)).toEqual([['from', 'reason'], ['to']]);
      expect(splitFromRow(state, 'to')).toBe(state);
      state = setRowWidths(mergeIntoRow(fromSchema(schema), 'reason', 'to', 'after'), 1, 0, [2, 6, 4]);
      expect(cellsOf(splitFromRow(state, 'to'), 1)).toEqual([['from:4', 'reason:8'], ['to']]);
    });

    it('«в один рядок із попереднім»', () => {
      const state = fromSchema(schema);
      expect(canJoinPrevious(state, 'reason')).toBe(true);
      expect(cellsOf(joinPrevious(state, 'reason'), 1)).toEqual([['from', 'to', 'reason']]);
      expect(canJoinPrevious(state, 'from')).toBe(false);
      expect(canJoinPrevious(state, 'name')).toBe(false);
    });

    it('нормалізація й пресети ширин', () => {
      expect(normalizeWidths([6, 6, 6])).toEqual([4, 4, 4]);
      expect(normalizeWidths([1, 11])).toEqual([2, 10]);
      expect(normalizeWidths([5])).toEqual([12]);
      expect(normalizeWidths([1, 1, 1, 9])).toEqual([2, 2, 2, 6]);
      expect(normalizeWidths([4, 4, 4, 4]).reduce((a, b) => a + b)).toBe(12);
      expect(widthPresets(2)).toContainEqual([4, 8]);
      expect(widthPresets(3)).toContainEqual([3, 6, 3]);
      expect(widthPresets(1)).toEqual([]);
      let state = setRowWidths(fromSchema(schema), 1, 0, [8, 4]);
      expect(cellsOf(state, 1)[0]).toEqual(['from:8', 'to:4']);
      state = setRowWidths(state, 1, 0, [6, 6]);
      expect(cellsOf(state, 1)[0]).toEqual(['from', 'to']);
      expect(cellsOf(setRowWidths(state, 1, 0, [9, 9]), 1)[0]).toEqual(['from', 'to']);
    });

    it('межа між клітинками й Alt+←/→: крок 1, мінімум 2', () => {
      let state = resizeBoundary(fromSchema(schema), 1, 0, 0, 2);
      expect(cellsOf(state, 1)[0]).toEqual(['from:8', 'to:4']);
      state = resizeBoundary(state, 1, 0, 0, 10);
      expect(cellsOf(state, 1)[0]).toEqual(['from:10', 'to:2']);
      state = resizeField(state, 'to', 1);
      expect(cellsOf(state, 1)[0]).toEqual(['from:9', 'to:3']);
      state = resizeField(state, 'from', 1);
      expect(cellsOf(state, 1)[0]).toEqual(['from:10', 'to:2']);
      expect(resizeField(state, 'from', 1)).toBe(state);
      expect(resizeField(fromSchema(schema), 'name', 1)).toEqual(fromSchema(schema));
    });

    it('undo повертає рядок як був', () => {
      const history = new DesignerHistory<ReturnType<typeof fromSchema>>();
      const before = fromSchema(schema);
      history.push(before);
      const after = mergeIntoRow(before, 'reason', 'from', 'before');
      expect(history.undo(after)).toEqual(before);
    });
  });

  describe('«Показувати, якщо» (F11-06)', () => {
    const schema = {
      type: 'object',
      properties: { kind: { type: 'string' }, reason: { type: 'string' }, note: { type: 'string' } },
      'x-openbp-layout': {
        sections: [
          { rows: [[{ field: 'kind' }, { field: 'reason', visibleIf: { field: 'kind', operator: 'eq', value: 'other' } }]] },
          { title: 'Ще', visibleIf: { field: 'kind', operator: 'isNotEmpty' }, rows: [[{ field: 'note' }]] },
        ],
      },
    };

    it('ставить і прибирає умову поля; на саме поле чи неіснуюче — ні', () => {
      const state = fromSchema(schema);
      const set = setFieldVisibleIf(state, 'note', { field: 'reason', operator: 'isNotEmpty' });
      expect(set.layout.sections[1].rows[0][0].visibleIf).toEqual({ field: 'reason', operator: 'isNotEmpty' });
      expect(setFieldVisibleIf(set, 'note', undefined).layout.sections[1].rows[0][0].visibleIf).toBeUndefined();
      expect(setFieldVisibleIf(state, 'note', { field: 'note', operator: 'isEmpty' })).toBe(state);
      expect(setFieldVisibleIf(state, 'note', { field: 'ghost', operator: 'isEmpty' })).toBe(state);
    });

    it('джерела: для поля — усі інші, для секції — поля поза нею', () => {
      const state = fromSchema(schema);
      expect(ruleSources(state, { field: 'reason' })).toEqual(['kind', 'note']);
      expect(ruleSources(state, { section: 1 })).toEqual(['kind', 'reason']);
    });

    it('видалення поля-джерела прибирає умови, що на нього посилались', () => {
      const state = fromSchema(schema);
      expect(rulesUsing(state, 'kind')).toEqual({ fields: ['reason'], sections: [1] });
      const next = removeField(state, 'kind');
      expect(rulesUsing(next, 'kind')).toEqual({ fields: [], sections: [] });
      expect(next.layout.sections[0].rows[0]).toEqual([{ field: 'reason' }]);
      expect(next.layout.sections[1].visibleIf).toBeUndefined();
      expect(state.layout.sections[1].visibleIf).toBeDefined();
    });
  });
});
