import type { FieldUi, FormLayout } from '../forms/form-layout';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { JsonSchema } from '../forms/form-fields';
import { cellView } from './page-format';

/** Тип сторінки застосунку (docs/projects.md §3). */
export type PageKind = 'list' | 'detail' | 'form' | 'metrics' | 'calendar' | 'dashboard';

/** Типи сторінок для вибору в конструкторі. */
export const PAGE_KINDS: readonly PageKind[] = ['list', 'detail', 'form', 'metrics', 'calendar', 'dashboard'];

/** Колір мітки `format: badge` (AC-Kit). */
export type PageColor = 'gray' | 'green' | 'yellow' | 'red' | 'blue' | 'purple';

/** Колонка списку. */
export interface PageColumn {
  column: string;
  label?: string;
  format?: string;
  display?: string;
  /** Підписи значень (`enum`, `badge`, `boolean`, `capacity`). */
  labels?: Record<string, string>;
  /** Кольори міток за значенням (лише `format: badge`). */
  colors?: Record<string, PageColor>;
}

/** Як відкрити сторінку: перехід (типово) чи бокова панель. */
export type PageOpenMode = 'navigate' | 'drawer';

/** Посилання на сторінку застосунку. */
export interface PageLink {
  page: string;
  mode?: PageOpenMode;
}

/** Колонка заголовка, підзаголовка чи кольору події календаря. */
export type PageCalendarColumn = PageColumn;

/** Календар (`calendar`): подія — рядок таблиці з початком і тривалістю чи кінцем (F8-14). */
export interface PageCalendar {
  start: string;
  duration?: string;
  end?: string;
  defaultMinutes?: number;
  title?: PageCalendarColumn;
  subtitle?: PageCalendarColumn;
  color?: PageCalendarColumn;
  views?: ('week' | 'day' | 'month')[];
  default?: 'week' | 'day' | 'month';
  open?: PageLink;
  fromHour?: number;
  toHour?: number;
}

/** Блок головної сторінки (`dashboard`, F8-14): `text`, `tiles`, `list`, `calendar`, `form` чи `actions`. */
export interface PageBlock {
  kind: 'text' | 'tiles' | 'list' | 'calendar' | 'form' | 'actions';
  label?: string;
  page?: string;
  definition?: PageDefinition;
  limit?: number;
  range?: 'today' | 'week';
  actions?: PageAction[];
  text?: string;
  when?: string;
  width?: number;
}

/** Фільтр, яким керує користувач (F8-14). */
export interface PageUserFilter {
  column: string;
  kind: 'select' | 'chips' | 'period' | 'search';
  label?: string;
  display?: string;
  labels?: Record<string, string>;
  default?: unknown;
}

/** Обчислювана колонка списку (F8-14). */
export interface PageComputed {
  key: string;
  label: string;
  table: string;
  by: string;
  aggregate?: 'count' | 'sum';
  column?: string;
  of?: string;
}

/** Дія над рядком: перехід (`page`), видалення (`operation`) або запуск сценарію проєкту (`workflow`, F8-05). */
export interface PageAction {
  label: string;
  page?: string;
  /** Як відкрити `page`; без нього — перехід. */
  mode?: PageOpenMode;
  workflow?: string;
  operation?: 'delete';
  confirm?: string;
  when?: string;
  /** Ролі застосунку, яким дію показано; порожньо — усім, хто бачить сторінку (F8-05). */
  roles?: string[];
}

/** Умова відбору рядків сторінки. */
export interface PageFilter {
  column: string;
  operator: string;
  value?: unknown;
}

/** Агрегат плитки чи діаграми. */
export type PageAggregate = 'count' | 'sum' | 'avg' | 'min' | 'max';

export const PAGE_AGGREGATES: readonly PageAggregate[] = ['count', 'sum', 'avg', 'min', 'max'];

/** Плитка `metrics`: агрегат по таблиці або вираз над іншими плитками (`{{ $tiles.key }}`). */
export interface PageTile {
  label: string;
  key?: string;
  table?: string;
  aggregate?: PageAggregate;
  column?: string;
  where?: PageFilter[];
  expression?: string;
}

/** Діаграма `metrics`: агрегат по групах (`колонка` чи `day|week|month(колонка)`). */
export interface PageChart {
  label?: string;
  table?: string;
  groupBy: string;
  aggregate?: PageAggregate;
  column?: string;
  where?: PageFilter[];
}

/** Поле картки чи форми. */
export interface PageField extends PageColumn {
  required?: boolean;
  description?: string;
  value?: unknown;
  /** Форма: значення з рядка, вибраного в полі `ref` (F8-21); поле не показується. */
  copyFrom?: string;
  /** Як показати поле форми (F11-01) — стає `x-openbp-ui` у схемі. */
  ui?: FieldUi;
}

/** Пов'язаний список картки. */
export interface PageRelated {
  table: string;
  by: string;
  label?: string;
  columns?: PageColumn[];
  actions?: PageAction[];
  /** Кнопка «Додати» підлеглого списку: форма з підставленим ключем батьківського рядка в `by`. */
  create?: PageLink;
  empty?: string;
  sort?: { column: string; direction?: 'asc' | 'desc' };
}

/** Визначення сторінки (`project_pages.definition`). */
export interface PageDefinition {
  table: string;
  columns?: PageColumn[];
  filter?: PageFilter[];
  search?: string[];
  sort?: { column: string; direction?: 'asc' | 'desc' };
  pageSize?: number;
  actions?: PageAction[];
  create?: PageLink;
  fields?: PageField[];
  /** Розмітка форми (F11-01): секції з рядками полів, як `x-openbp-layout`. */
  formLayout?: FormLayout;
  related?: PageRelated[];
  onSubmit?: { operation?: 'insert' | 'upsert' | 'update'; workflow?: string };
  then?: { page: string };
  tiles?: PageTile[];
  charts?: PageChart[];
  /** Панель фільтрів над списком (F8-14). */
  filters?: PageUserFilter[];
  computed?: PageComputed[];
  calendar?: PageCalendar;
  /** Блоки головної сторінки (`dashboard`). */
  blocks?: PageBlock[];
  layout?: 'table' | 'cards';
  card?: { title: string; subtitle?: string };
  /** Підлеглі списки під розгорнутим рядком. */
  expand?: PageRelated[];
  /** Текст порожнього списку. */
  empty?: string;
}

/** `ProjectPageDto`. */
export interface ProjectPage {
  id: string;
  key: string;
  name: string;
  kind: PageKind;
  definition: PageDefinition;
  roles: string[];
  sortOrder: number;
  updatedAt: string;
}

/** Колонка таблиці сторінки з типом. */
export interface PageViewColumn {
  column: string;
  label: string;
  type: string;
  format?: string | null;
}

/** `GET /projects/{id}/pages/{key}`. */
export interface ProjectPageView {
  page: ProjectPage;
  /** Для `metrics` без таблиці за замовчуванням — нульовий guid. */
  tableId: string;
  columns: PageViewColumn[];
  /** JSON Schema форми (лише `form`). */
  schema: JsonSchema | null;
  canEdit: boolean;
  /** Варіанти фільтрів `select`/`chips` за колонкою (F8-16). */
  filterOptions?: Record<string, PageOption[]> | null;
  /** Часовий пояс застосунку (IANA) для показу дат; `UTC`, якщо оболонки немає. */
  timezone?: string;
}

/** Варіант вибору у фільтрі. */
export interface PageOption {
  value: string;
  label: string;
}

/** Значення фільтра користувача: вибрані значення (для `period` — назва періоду) і межі «від–до». */
export interface PageFilterValue {
  values: string[];
  from?: string | null;
  to?: string | null;
}

/** Рядок на сторінці: лише її колонки; `display` — показані значення `ref`; `actions` — індекси доступних дій. */
export interface PageRow {
  key: string;
  data: Record<string, unknown>;
  display: Record<string, string | null>;
  actions: number[];
}

/** `GET …/rows`. */
export interface PageRows {
  items: PageRow[];
  page: number;
  pageSize: number;
  total: number;
  /** Фільтри, що діяли на запит, разом зі значеннями за замовчуванням (F8-16). */
  filters?: Record<string, PageFilterValue> | null;
}

/** Підлеглий список під рядком: `GET …/rows/{rowKey}/expand/{index}`. */
export interface PageExpandRows {
  label: string;
  columns: PageViewColumn[];
  rows: PageRow[];
}

/** Подія календаря: початок, кінець і рядок (`GET …/calendar`). */
export interface PageCalendarEvent {
  start: string;
  end: string;
  row: PageRow;
}

/** `GET …/calendar?from&to`. */
export interface PageCalendarEvents {
  from: string;
  to: string;
  events: PageCalendarEvent[];
  /** Подій більше за ліміт сервера — показано перші. */
  truncated: boolean;
  /** Фільтри, що діяли на запит, разом зі значеннями за замовчуванням. */
  filters?: Record<string, PageFilterValue> | null;
}

/** Блок із даними (`GET …/dashboard`); `source` — ключ сторінки блоку для подальших запитів. */
export interface PageDashboardBlock {
  index: number;
  kind: PageBlock['kind'];
  label?: string | null;
  width: number;
  source?: string | null;
  view?: ProjectPageView | null;
  metrics?: PageMetrics | null;
  rows?: PageRows | null;
  events?: PageCalendarEvents | null;
  text?: string | null;
  /** Індекси кнопок блоку `actions`, доступних користувачу. */
  actions?: number[] | null;
}

/** `GET …/dashboard`. */
export interface PageDashboard {
  blocks: PageDashboardBlock[];
}

/** `POST …/preview` — дані чернетки сторінки без збереження (F8-19). */
export interface PagePreview {
  view: ProjectPageView;
  rows?: PageRows | null;
  metrics?: PageMetrics | null;
  events?: PageCalendarEvents | null;
  dashboard?: PageDashboard | null;
}

/** `GET …/rows/{rowKey}`. */
export interface PageRecord {
  row: PageRow;
  related: { label: string; columns: PageViewColumn[]; rows: PageRow[] }[];
}

/** Результат дії чи форми, що запускала сценарій (F8-05). */
export interface PageActionResult {
  /** `success` | `rejected` (сценарій відмовив: `ok: false`) | `error` | `running` (ще виконується). */
  state: 'success' | 'rejected' | 'error' | 'running';
  message?: string | null;
  field?: string | null;
  data?: Record<string, unknown> | null;
  executionId?: string | null;
}

/** `POST …/submit`; для форми зі сценарієм `row` порожній, а `result` — відповідь сценарію. */
export interface PageSubmitResult {
  row: PageRow | null;
  then: string | null;
  result?: PageActionResult | null;
}

/** `GET …/metrics`. */
export interface PageMetrics {
  tiles: { key?: string | null; label: string; value: number | null }[];
  charts: { label?: string | null; groupBy: string; points: { label: string; value: number | null }[] }[];
}

/** Тіло збереження сторінки (`PUT …/pages/{key}`). */
export interface ProjectPageInput {
  name: string;
  kind: PageKind;
  definition: PageDefinition;
  roles: string[];
  sortOrder?: number | null;
}

/** Параметри списку. */
export interface PageRowsQuery {
  search?: string;
  sort?: string;
  direction?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
  /** Фільтри користувача за колонкою; порожнє значення знімає фільтр, у тому числі заданий за замовчуванням. */
  filters?: Record<string, PageFilterValue>;
}

/** Клієнт `/api/v1/projects/{id}/pages` (F8-04): права й фільтри перевіряє Api. */
@Injectable({ providedIn: 'root' })
export class ProjectPagesApiService {
  private readonly http = inject(HttpClient);

  private base(projectId: string, key?: string): string {
    const root = `${API_BASE}/projects/${encodeURIComponent(projectId)}/pages`;
    return key === undefined ? root : `${root}/${encodeURIComponent(key)}`;
  }

  list(projectId: string): Observable<ProjectPage[]> {
    return this.http.get<ProjectPage[]>(this.base(projectId));
  }

  get(projectId: string, key: string): Observable<ProjectPageView> {
    return this.http.get<ProjectPageView>(this.base(projectId, key));
  }

  rows(projectId: string, key: string, query: PageRowsQuery = {}): Observable<PageRows> {
    let params = new HttpParams();
    for (const [name, value] of Object.entries(query)) {
      if (name !== 'filters' && value !== undefined && value !== null && value !== '') params = params.set(name, String(value));
    }
    params = this.filterParams(params, query.filters ?? {});
    return this.http.get<PageRows>(`${this.base(projectId, key)}/rows`, { params });
  }

  private filterParams(params: HttpParams, filters: Record<string, PageFilterValue>): HttpParams {
    for (const [column, filter] of Object.entries(filters)) {
      const from = filter.from?.trim();
      const to = filter.to?.trim();
      const values = filter.values.filter((v) => v !== '');
      if (from) params = params.set(`filters[${column}][from]`, from);
      if (to) params = params.set(`filters[${column}][to]`, to);
      if (values.length === 0 && !from && !to) params = params.set(`filters[${column}]`, '');
      for (const value of values) params = params.append(`filters[${column}]`, value);
    }
    return params;
  }

  /** Події календаря за діапазон `[from, to)`; фільтри користувача — як у списку. */
  calendar(projectId: string, key: string, from: Date, to: Date, filters: Record<string, PageFilterValue> = {}): Observable<PageCalendarEvents> {
    let params = new HttpParams().set('from', from.toISOString()).set('to', to.toISOString());
    params = this.filterParams(params, filters);
    return this.http.get<PageCalendarEvents>(`${this.base(projectId, key)}/calendar`, { params });
  }

  /** Дані блоків головної; спільні фільтри діють на блоки, таблиця яких має таку колонку. */
  dashboard(projectId: string, key: string, filters: Record<string, PageFilterValue> = {}): Observable<PageDashboard> {
    return this.http.get<PageDashboard>(`${this.base(projectId, key)}/dashboard`, { params: this.filterParams(new HttpParams(), filters) });
  }

  /** Кнопка блоку `actions`: сценарій запускається від імені користувача, перехід виконує браузер. */
  runBlockAction(projectId: string, key: string, block: number, action: number): Observable<PageActionResult> {
    return this.http.post<PageActionResult>(`${this.base(projectId, key)}/blocks/${block}/actions/${action}`, null);
  }

  /** Підлеглий список `expand[index]` під рядком списку (F8-15). */
  expand(projectId: string, key: string, rowKey: string, index: number): Observable<PageExpandRows> {
    return this.http.get<PageExpandRows>(`${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}/expand/${index}`);
  }

  /** Дія над рядком підлеглого списку. */
  runExpandAction(projectId: string, key: string, rowKey: string, index: number, subKey: string, action: number): Observable<PageActionResult> {
    return this.http.post<PageActionResult>(
      `${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}/expand/${index}/${encodeURIComponent(subKey)}/actions/${action}`,
      null,
    );
  }

  row(projectId: string, key: string, rowKey: string): Observable<PageRecord> {
    return this.http.get<PageRecord>(`${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}`);
  }

  submit(projectId: string, key: string, values: Record<string, unknown>, rowKey?: string | null): Observable<PageSubmitResult> {
    return this.http.post<PageSubmitResult>(`${this.base(projectId, key)}/submit`, { values, rowKey: rowKey ?? null });
  }

  metrics(projectId: string, key: string): Observable<PageMetrics> {
    return this.http.get<PageMetrics>(`${this.base(projectId, key)}/metrics`);
  }

  runAction(projectId: string, key: string, rowKey: string, action: number): Observable<PageActionResult> {
    return this.http.post<PageActionResult>(`${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}/actions/${action}`, null);
  }

  /** Створює чи замінює сторінку (editor проєкту). */
  save(projectId: string, key: string, page: ProjectPageInput): Observable<ProjectPage> {
    return this.http.put<ProjectPage>(this.base(projectId, key), page);
  }

  /** Дані чернетки без збереження: та сама перевірка, що й при збереженні (422 з повідомленням). */
  preview(projectId: string, key: string, page: ProjectPageInput): Observable<PagePreview> {
    return this.http.post<PagePreview>(`${this.base(projectId, key)}/preview`, page);
  }

  remove(projectId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.base(projectId, key));
  }
}

/** Значення комірки для людей (без міток і кольорів): див. `cellView`. */
export function cellText(row: PageRow, column: string, type?: string, format?: string | null): string {
  return cellView(row, { column, label: column, type: type ?? 'string', format }, 'UTC').text;
}
