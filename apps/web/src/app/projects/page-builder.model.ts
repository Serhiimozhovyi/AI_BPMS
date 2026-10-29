import { PageAction, PageAggregate, PageChart, PageFilter, PageTile } from './project-pages-api.service';

/** Рядок умови в конструкторі: значення — текст (число, `true`/`false` або вираз `{{ $user.id }}`). */
export interface FilterRow {
  column: string;
  operator: string;
  value: string;
}

/** Рядок дії в конструкторі: перехід на сторінку, запуск сценарію проєкту або видалення рядка. */
export interface ActionRow {
  label: string;
  type: 'page' | 'workflow' | 'delete';
  /** Ключ сторінки або сценарій; для `delete` порожньо. */
  target: string;
  confirm: string;
  when: string;
  roles: string[];
  /** Як відкрити сторінку: перехід чи бокова панель (лише `page`). */
  mode: 'navigate' | 'drawer';
}

/** Рядок плитки `metrics`: агрегат по таблиці або вираз над іншими плитками. */
export interface TileRow {
  label: string;
  key: string;
  mode: 'aggregate' | 'expression';
  table: string;
  aggregate: PageAggregate;
  column: string;
  where: FilterRow;
  expression: string;
}

/** Рядок діаграми `metrics`. */
export interface ChartRow {
  label: string;
  table: string;
  groupBy: string;
  aggregate: PageAggregate;
  column: string;
  where: FilterRow;
}

/** Оператори, які не мають значення. */
export const VALUELESS_OPERATORS: readonly string[] = ['isEmpty', 'isNotEmpty'];

export const FILTER_OPERATORS: readonly string[] = ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'contains', 'isEmpty', 'isNotEmpty'];

export const emptyFilter = (): FilterRow => ({ column: '', operator: 'eq', value: '' });

export const emptyAction = (): ActionRow => ({ label: '', type: 'page', target: '', confirm: '', when: '', roles: [], mode: 'navigate' });

export const emptyTile = (): TileRow => ({
  label: '',
  key: '',
  mode: 'aggregate',
  table: '',
  aggregate: 'count',
  column: '',
  where: emptyFilter(),
  expression: '',
});

export const emptyChart = (): ChartRow => ({ label: '', table: '', groupBy: '', aggregate: 'count', column: '', where: emptyFilter() });

/** Текст поля значення → значення умови: число й логічні — як є, решта (зокрема вирази) — рядок. */
export function parseFilterValue(text: string): unknown {
  const trimmed = text.trim();
  if (/^-?\d+(\.\d+)?$/.test(trimmed)) return Number(trimmed);
  if (trimmed === 'true') return true;
  if (trimmed === 'false') return false;
  return text;
}

export function filterValueText(value: unknown): string {
  return value === undefined || value === null ? '' : typeof value === 'string' ? value : JSON.stringify(value);
}

/** Заповнені рядки умов → `filter`/`where` сторінки. */
export function toFilters(rows: FilterRow[]): PageFilter[] {
  return rows
    .filter((r) => r.column)
    .map((r) => (VALUELESS_OPERATORS.includes(r.operator) ? { column: r.column, operator: r.operator } : { column: r.column, operator: r.operator, value: parseFilterValue(r.value) }));
}

export function fromFilters(filters: PageFilter[] | undefined): FilterRow[] {
  return (filters ?? []).map((f) => ({ column: f.column, operator: f.operator, value: filterValueText(f.value) }));
}

export function toActions(rows: ActionRow[]): PageAction[] {
  return rows
    .filter((r) => r.label.trim() && (r.type === 'delete' || r.target))
    .map((r) => {
      const action: PageAction = { label: r.label.trim() };
      if (r.type === 'page') {
        action.page = r.target;
        if (r.mode === 'drawer') action.mode = 'drawer';
      }
      else if (r.type === 'workflow') action.workflow = r.target;
      else action.operation = 'delete';
      if (r.confirm.trim()) action.confirm = r.confirm.trim();
      if (r.when.trim()) action.when = r.when.trim();
      if (r.roles.length) action.roles = r.roles;
      return action;
    });
}

export function fromActions(actions: PageAction[] | undefined): ActionRow[] {
  return (actions ?? []).map((a) => ({
    label: a.label,
    type: a.workflow ? 'workflow' : a.operation ? 'delete' : 'page',
    target: a.workflow ?? a.page ?? '',
    confirm: a.confirm ?? '',
    when: a.when ?? '',
    roles: a.roles ?? [],
    mode: a.mode ?? 'navigate',
  }));
}

export function toTiles(rows: TileRow[]): PageTile[] {
  return rows
    .filter((r) => r.label.trim())
    .map((r) => {
      const tile: PageTile = { label: r.label.trim() };
      if (r.key.trim()) tile.key = r.key.trim();
      if (r.mode === 'expression') {
        tile.expression = r.expression.trim();
        return tile;
      }
      tile.aggregate = r.aggregate;
      if (r.table) tile.table = r.table;
      if (r.aggregate !== 'count' && r.column) tile.column = r.column;
      const where = toFilters([r.where]);
      if (where.length) tile.where = where;
      return tile;
    });
}

export function fromTiles(tiles: PageTile[] | undefined): TileRow[] {
  return (tiles ?? []).map((t) => ({
    label: t.label,
    key: t.key ?? '',
    mode: t.expression ? 'expression' : 'aggregate',
    table: t.table ?? '',
    aggregate: t.aggregate ?? 'count',
    column: t.column ?? '',
    where: fromFilters(t.where)[0] ?? emptyFilter(),
    expression: t.expression ?? '',
  }));
}

export function toCharts(rows: ChartRow[]): PageChart[] {
  return rows
    .filter((r) => r.groupBy.trim())
    .map((r) => {
      const chart: PageChart = { groupBy: r.groupBy.trim(), aggregate: r.aggregate };
      if (r.label.trim()) chart.label = r.label.trim();
      if (r.table) chart.table = r.table;
      if (r.aggregate !== 'count' && r.column) chart.column = r.column;
      const where = toFilters([r.where]);
      if (where.length) chart.where = where;
      return chart;
    });
}

export function fromCharts(charts: PageChart[] | undefined): ChartRow[] {
  return (charts ?? []).map((c) => ({
    label: c.label ?? '',
    table: c.table ?? '',
    groupBy: c.groupBy,
    aggregate: c.aggregate ?? 'count',
    column: c.column ?? '',
    where: fromFilters(c.where)[0] ?? emptyFilter(),
  }));
}
