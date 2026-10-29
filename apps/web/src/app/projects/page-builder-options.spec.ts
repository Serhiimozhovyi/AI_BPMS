import { describe, expect, it } from 'vitest';
import { DataTableSummary } from '../tables/data-tables-api.service';
import { labelsText, parseLabels, sampleFor } from './page-builder-options';

const table = {
  id: 't1',
  name: 'events',
  rowCount: 0,
  updatedAt: '',
  columns: [
    { name: 'title', type: 'string' },
    { name: 'starts_at', type: 'datetime' },
    { name: 'duration_min', type: 'number' },
    { name: 'status', type: 'enum', values: ['planned', 'done'] },
    { name: 'contact', type: 'string', sensitive: true },
  ],
} as DataTableSummary;

describe('page-builder-options', () => {
  it('підписи й кольори читаються з тексту й повертаються назад', () => {
    const parsed = parseLabels('planned=Заплановано|green\ndone=Проведено|blue\ncancelled=Скасовано|навіть-не-колір\nпорожній рядок\n=x');
    expect(parsed.labels).toEqual({ planned: 'Заплановано', done: 'Проведено', cancelled: 'Скасовано' });
    expect(parsed.colors).toEqual({ planned: 'green', done: 'blue' });
    expect(labelsText(parsed.labels, parsed.colors)).toBe('planned=Заплановано|green\ndone=Проведено|blue\ncancelled=Скасовано');
    expect(parseLabels('')).toEqual({ labels: undefined, colors: undefined });
  });

  it('зразок списку: колонки без чутливих, мітка стану, чипи, період і пошук', () => {
    const sample = sampleFor('list', table, []);
    expect(sample.kind).toBe('list');
    expect(sample.definition.columns?.map((c) => c.column)).toEqual(['title', 'starts_at', 'duration_min', 'status']);
    expect(sample.definition.columns?.find((c) => c.column === 'status')).toMatchObject({ format: 'badge', colors: { planned: 'green', done: 'blue' } });
    expect(sample.definition.filters?.map((f) => f.kind)).toEqual(['chips', 'period', 'search']);
  });

  it('зразок календаря: початок, тривалість, заголовок і колір за станом', () => {
    const sample = sampleFor('calendar', table, ['calendar']);
    expect(sample.key).toBe('calendar-2');
    expect(sample.definition.calendar).toMatchObject({ start: 'starts_at', duration: 'duration_min', title: { column: 'title' }, color: { column: 'status' }, default: 'week' });
  });

  it('зразок головної: привітання й короткий список з власним визначенням', () => {
    const sample = sampleFor('dashboard', table, []);
    expect(sample.definition.blocks?.map((b) => b.kind)).toEqual(['text', 'list']);
    expect(sample.definition.blocks?.[1].definition?.table).toBe('events');
  });

  it('без таблиці зразок усе одно будується', () => {
    expect(sampleFor('calendar', undefined, []).definition.calendar?.start).toBe('');
  });
});
