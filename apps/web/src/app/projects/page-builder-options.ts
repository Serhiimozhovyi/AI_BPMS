import { DataTableSummary } from '../tables/data-tables-api.service';
import { PageColor, PageDefinition, PageKind } from './project-pages-api.service';

/** Кольори міток по колу для зразків. */
const CYCLE: PageColor[] = ['green', 'blue', 'yellow', 'red', 'purple', 'gray'];

export const PAGE_COLORS: readonly PageColor[] = ['gray', 'green', 'yellow', 'red', 'blue', 'purple'];

/** Формати колонок, які можна вибрати в конструкторі (docs/projects.md §3.6). */
export const COLUMN_FORMATS: readonly string[] = ['', 'text', 'number', 'date', 'datetime', 'time', 'weekday', 'enum', 'badge', 'phone', 'capacity', 'boolean'];

/**
 * Підписи й кольори значень колонки одним текстом: рядок `значення=Підпис|колір`, колір (`green`, `blue`…) не обов'язковий.
 * Порожні рядки пропускаються, невідомий колір відкидається.
 */
export function parseLabels(text: string): { labels?: Record<string, string>; colors?: Record<string, PageColor> } {
  const labels: Record<string, string> = {};
  const colors: Record<string, PageColor> = {};
  for (const line of text.split('\n')) {
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const value = line.slice(0, eq).trim();
    const [label, color] = line.slice(eq + 1).split('|').map((x) => x.trim());
    if (!value) continue;
    labels[value] = label || value;
    if (color && (PAGE_COLORS as readonly string[]).includes(color)) colors[value] = color as PageColor;
  }
  return { labels: Object.keys(labels).length ? labels : undefined, colors: Object.keys(colors).length ? colors : undefined };
}

export function labelsText(labels: Record<string, string> | undefined, colors: Record<string, PageColor> | undefined): string {
  const keys = [...new Set([...Object.keys(labels ?? {}), ...Object.keys(colors ?? {})])];
  return keys.map((k) => `${k}=${labels?.[k] ?? k}${colors?.[k] ? `|${colors[k]}` : ''}`).join('\n');
}

/** Зразок нової сторінки: ключ, назва, тип і визначення за таблицею проєкту. */
export interface PageSample {
  key: string;
  name: string;
  kind: PageKind;
  definition: PageDefinition;
}

export type SampleKind = 'list' | 'calendar' | 'dashboard';

/**
 * Зразки сторінок на колонках вибраної таблиці: «список з панеллю фільтрів», «календар» і «головна». Колонки добираються
 * за типами (перша дата, перший перелік, перший текст…); чутливі колонки не використовуються.
 */
export function sampleFor(sample: SampleKind, table: DataTableSummary | undefined, existing: string[]): PageSample {
  const columns = (table?.columns ?? []).filter((c) => !c.sensitive);
  const first = (type: string) => columns.find((c) => c.type === type);
  const text = columns.find((c) => c.type === 'string');
  const enumColumn = first('enum');
  const date = first('datetime') ?? first('date');
  const name = table?.name ?? '';
  const free = (base: string) => {
    let key = base;
    for (let i = 2; existing.includes(key); i++) key = `${base}-${i}`;
    return key;
  };
  const colors = (values: string[] | null | undefined) => Object.fromEntries((values ?? []).map((v, i) => [v, CYCLE[i % CYCLE.length]]));

  if (sample === 'calendar') {
    const minutes = columns.find((c) => c.type === 'number' && /dur|min|хв/i.test(c.name));
    return {
      key: free('calendar'),
      name: 'Календар',
      kind: 'calendar',
      definition: {
        table: name,
        calendar: {
          start: date?.name ?? '',
          ...(minutes ? { duration: minutes.name } : { defaultMinutes: 60 }),
          ...(text ? { title: { column: text.name } } : {}),
          ...(enumColumn ? { color: { column: enumColumn.name, format: 'badge', colors: colors(enumColumn.values) } } : {}),
          views: ['week', 'day', 'month'],
          default: 'week',
          fromHour: 8,
          toHour: 20,
        },
        ...(enumColumn ? { filters: [{ column: enumColumn.name, kind: 'chips' as const, label: enumColumn.name }] } : {}),
      },
    };
  }

  if (sample === 'dashboard') {
    return {
      key: free('home'),
      name: 'Головна',
      kind: 'dashboard',
      definition: {
        table: '',
        blocks: [
          { kind: 'text', text: 'Вітаємо, {{ $user.email }}!', width: 12 },
          {
            kind: 'list',
            label: name,
            limit: 5,
            width: 12,
            definition: { table: name, columns: columns.slice(0, 4).map((c) => ({ column: c.name })) },
          },
        ],
      },
    };
  }

  return {
    key: free('list'),
    name: name || 'Список',
    kind: 'list',
    definition: {
      table: name,
      columns: columns.slice(0, 5).map((c) => (c.type === 'enum' ? { column: c.name, format: 'badge', colors: colors(c.values) } : { column: c.name })),
      filters: [
        ...(enumColumn ? [{ column: enumColumn.name, kind: 'chips' as const, label: enumColumn.name }] : []),
        ...(date ? [{ column: date.name, kind: 'period' as const, label: date.name }] : []),
        ...(text ? [{ column: text.name, kind: 'search' as const, label: 'Пошук' }] : []),
      ],
    },
  };
}
