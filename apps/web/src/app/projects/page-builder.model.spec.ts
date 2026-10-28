import {
  emptyAction,
  emptyChart,
  emptyFilter,
  emptyTile,
  fromActions,
  fromCharts,
  fromFilters,
  fromTiles,
  parseFilterValue,
  toActions,
  toCharts,
  toFilters,
  toTiles,
} from './page-builder.model';

describe('page-builder.model (F8-05)', () => {
  it('значення умови: число, логічне, вираз і текст', () => {
    expect(parseFilterValue('42')).toBe(42);
    expect(parseFilterValue('-1.5')).toBe(-1.5);
    expect(parseFilterValue('true')).toBe(true);
    expect(parseFilterValue('{{ $user.id }}')).toBe('{{ $user.id }}');
    expect(parseFilterValue('Олена')).toBe('Олена');
  });

  it('умови: порожні колонки відкидаються, isEmpty без значення, повернення у рядки', () => {
    const filters = toFilters([
      { column: 'status', operator: 'eq', value: 'active' },
      { column: 'phone', operator: 'isEmpty', value: 'ignored' },
      emptyFilter(),
    ]);
    expect(filters).toEqual([
      { column: 'status', operator: 'eq', value: 'active' },
      { column: 'phone', operator: 'isEmpty' },
    ]);
    expect(fromFilters(filters)).toEqual([
      { column: 'status', operator: 'eq', value: 'active' },
      { column: 'phone', operator: 'isEmpty', value: '' },
    ]);
  });

  it('дії: перехід, сценарій і видалення зі своїми полями; недописані рядки відкидаються', () => {
    const rows = [
      { ...emptyAction(), label: 'Відкрити', type: 'page' as const, target: 'booking' },
      { ...emptyAction(), label: 'Скасувати', type: 'workflow' as const, target: 'cancel-booking', confirm: 'Скасувати запис?', when: "{{ $row.status === 'active' }}", roles: ['coordinator'] },
      { ...emptyAction(), label: 'Видалити', type: 'delete' as const },
      { ...emptyAction(), label: 'Без цілі', type: 'page' as const },
      { ...emptyAction(), label: '', type: 'delete' as const },
    ];
    const actions = toActions(rows);
    expect(actions).toEqual([
      { label: 'Відкрити', page: 'booking' },
      { label: 'Скасувати', workflow: 'cancel-booking', confirm: 'Скасувати запис?', when: "{{ $row.status === 'active' }}", roles: ['coordinator'] },
      { label: 'Видалити', operation: 'delete' },
    ]);
    expect(fromActions(actions).map((a) => [a.type, a.target])).toEqual([
      ['page', 'booking'],
      ['workflow', 'cancel-booking'],
      ['delete', ''],
    ]);
  });

  it('плитки: агрегат з умовою і вираз над плитками', () => {
    const tiles = toTiles([
      { ...emptyTile(), label: 'Онлайн', key: 'online', where: { column: 'kind', operator: 'eq', value: 'online' } },
      { ...emptyTile(), label: 'Хвилин', aggregate: 'sum', column: 'minutes', table: 'visits' },
      { ...emptyTile(), label: 'Частка', mode: 'expression', expression: '{{ $tiles.online / $tiles.all }}' },
      emptyTile(),
    ]);
    expect(tiles).toEqual([
      { label: 'Онлайн', key: 'online', aggregate: 'count', where: [{ column: 'kind', operator: 'eq', value: 'online' }] },
      { label: 'Хвилин', aggregate: 'sum', table: 'visits', column: 'minutes' },
      { label: 'Частка', expression: '{{ $tiles.online / $tiles.all }}' },
    ]);
    expect(fromTiles(tiles).map((t) => t.mode)).toEqual(['aggregate', 'aggregate', 'expression']);
  });

  it('діаграми: потрібне групування, count без колонки', () => {
    const charts = toCharts([
      { ...emptyChart(), label: 'За місяцями', groupBy: 'month(day)' },
      { ...emptyChart(), groupBy: 'kind', aggregate: 'sum', column: 'minutes' },
      emptyChart(),
    ]);
    expect(charts).toEqual([
      { groupBy: 'month(day)', aggregate: 'count', label: 'За місяцями' },
      { groupBy: 'kind', aggregate: 'sum', column: 'minutes' },
    ]);
    expect(fromCharts(charts)[1].column).toBe('minutes');
  });
});
