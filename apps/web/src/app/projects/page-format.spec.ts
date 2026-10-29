import { cellView, formatPhone } from './page-format';
import { PageRow } from './project-pages-api.service';

const row = (data: Record<string, unknown>, display: Record<string, string | null> = {}): PageRow => ({ key: 'r', data, display, actions: [] });
const col = (column: string, type: string, extra: Record<string, unknown> = {}) => ({ column, label: column, type, ...extra });

describe('cellView (F8-16, docs/projects.md §3.6)', () => {
  it('порожнє значення — тире, показане значення ref має перевагу над ключем', () => {
    expect(cellView(row({ a: null }), col('a', 'string')).text).toBe('—');
    expect(cellView(row({ slot: 's1' }, { slot: 'пн, 2 лис 10:00' }), col('slot', 'ref')).text).toBe('пн, 2 лис 10:00');
  });

  it('datetime — у часовому поясі застосунку, а не браузера', () => {
    const value = '2026-11-02T08:00:00.0000000Z';
    expect(cellView(row({ at: value }), col('at', 'datetime'), 'Europe/Kyiv').text).toBe('пн, 2 лист., 10:00');
    expect(cellView(row({ at: value }), col('at', 'datetime'), 'UTC').text).toBe('пн, 2 лист., 08:00');
    expect(cellView(row({ at: value }), col('at', 'datetime', { format: 'time' }), 'Europe/Kyiv').text).toBe('10:00');
  });

  it('date не зсувається поясом, weekday — число 1–7 і дата', () => {
    expect(cellView(row({ d: '2026-11-02' }), col('d', 'date'), 'Pacific/Auckland').text).toBe('пн, 2 лист.');
    expect(cellView(row({ n: 3 }), col('n', 'number', { format: 'weekday' })).text).toBe('середа');
    expect(cellView(row({ d: '2026-11-02' }), col('d', 'date', { format: 'weekday' })).text).toBe('понеділок');
  });

  it('enum і boolean — підписи, badge — підпис і колір (за замовчуванням сірий)', () => {
    const labels = { online: 'Онлайн', offline: 'Наживо' };
    expect(cellView(row({ f: 'online' }), col('f', 'enum', { format: 'enum', labels })).text).toBe('Онлайн');
    expect(cellView(row({ f: 'other' }), col('f', 'enum', { format: 'enum', labels })).text).toBe('other');
    const badge = col('s', 'enum', { format: 'badge', labels: { open: 'Вільно' }, colors: { open: 'green' } });
    expect(cellView(row({ s: 'open' }), badge)).toEqual({ text: 'Вільно', color: 'green' });
    expect(cellView(row({ s: 'closed' }), badge)).toEqual({ text: 'closed', color: 'gray' });
    expect(cellView(row({ b: true }), col('b', 'boolean', { labels: { true: 'Активний' } })).text).toBe('Активний');
    expect(cellView(row({ b: false }), col('b', 'boolean')).text).toBe('ні');
  });

  it('phone — маска, capacity — «без ліміту» для 0', () => {
    expect(formatPhone('380671234567')).toBe('+38 (067) 123-45-67');
    expect(formatPhone('0671234567')).toBe('+38 (067) 123-45-67');
    expect(formatPhone('12345')).toBe('12345');
    expect(cellView(row({ p: '380671234567' }), col('p', 'string', { format: 'phone' })).text).toBe('+38 (067) 123-45-67');
    expect(cellView(row({ c: 0 }), col('c', 'number', { format: 'capacity' })).text).toBe('без ліміту');
    expect(cellView(row({ c: 0 }), col('c', 'number', { format: 'capacity', labels: { 0: 'Без обмежень' } })).text).toBe('Без обмежень');
    expect(cellView(row({ c: 8 }), col('c', 'number', { format: 'capacity' })).text).toBe('8');
  });

  it('обчислювана колонка: число, «3 / 8» з of і «3 / без ліміту» для 0', () => {
    const computed = { key: 'registered', label: 'Записано', table: 'regs', by: 'event', of: 'capacity' };
    const column = col('registered', 'number', { computed });
    expect(cellView(row({ registered: 3, capacity: 8 }), column).text).toBe('3 / 8');
    expect(cellView(row({ registered: 3, capacity: 0 }), column).text).toBe('3 / без ліміту');
    expect(cellView(row({ registered: 0 }), col('registered', 'number', { computed: { ...computed, of: undefined } })).text).toBe('0');
  });
});
