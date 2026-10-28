import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { JsonSchema } from '../forms/form-fields';

/** Тип сторінки застосунку (docs/projects.md §3). */
export type PageKind = 'list' | 'detail' | 'form';

/** Колонка списку. */
export interface PageColumn {
  column: string;
  label?: string;
  format?: string;
  display?: string;
}

/** Дія над рядком: перехід (`page`), видалення (`operation`) або сценарій (з F8-05). */
export interface PageAction {
  label: string;
  page?: string;
  workflow?: string;
  operation?: 'delete';
  confirm?: string;
  when?: string;
}

/** Поле картки чи форми. */
export interface PageField extends PageColumn {
  required?: boolean;
  description?: string;
  value?: unknown;
}

/** Пов'язаний список картки. */
export interface PageRelated {
  table: string;
  by: string;
  label?: string;
  columns?: PageColumn[];
  actions?: PageAction[];
}

/** Визначення сторінки (`project_pages.definition`). */
export interface PageDefinition {
  table: string;
  columns?: PageColumn[];
  filter?: { column: string; operator: string; value?: unknown }[];
  search?: string[];
  sort?: { column: string; direction?: 'asc' | 'desc' };
  pageSize?: number;
  actions?: PageAction[];
  create?: { page: string };
  fields?: PageField[];
  related?: PageRelated[];
  onSubmit?: { operation?: 'insert' | 'upsert' | 'update'; workflow?: string };
  then?: { page: string };
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
  tableId: string;
  columns: PageViewColumn[];
  /** JSON Schema форми (лише `form`). */
  schema: JsonSchema | null;
  canEdit: boolean;
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
}

/** `GET …/rows/{rowKey}`. */
export interface PageRecord {
  row: PageRow;
  related: { label: string; columns: PageViewColumn[]; rows: PageRow[] }[];
}

/** `POST …/submit`. */
export interface PageSubmitResult {
  row: PageRow;
  then: string | null;
}

/** Параметри списку. */
export interface PageRowsQuery {
  search?: string;
  sort?: string;
  direction?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
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
      if (value !== undefined && value !== null && value !== '') params = params.set(name, String(value));
    }
    return this.http.get<PageRows>(`${this.base(projectId, key)}/rows`, { params });
  }

  row(projectId: string, key: string, rowKey: string): Observable<PageRecord> {
    return this.http.get<PageRecord>(`${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}`);
  }

  submit(projectId: string, key: string, values: Record<string, unknown>, rowKey?: string | null): Observable<PageSubmitResult> {
    return this.http.post<PageSubmitResult>(`${this.base(projectId, key)}/submit`, { values, rowKey: rowKey ?? null });
  }

  runAction(projectId: string, key: string, rowKey: string, action: number): Observable<void> {
    return this.http.post<void>(`${this.base(projectId, key)}/rows/${encodeURIComponent(rowKey)}/actions/${action}`, null);
  }
}

/** Значення комірки для людей: показане значення `ref`, дата й час — локально, `null` — тире. */
export function cellText(row: PageRow, column: string, type?: string, format?: string | null): string {
  const shown = row.display?.[column];
  if (shown !== undefined && shown !== null) return shown;
  const value = row.data[column];
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? $localize`:@@app.page.yes:так` : $localize`:@@app.page.no:ні`;
  const kind = format ?? type;
  if (typeof value === 'string' && (kind === 'datetime' || kind === 'date')) {
    const date = new Date(value);
    if (!Number.isNaN(date.getTime())) {
      return kind === 'date'
        ? date.toLocaleDateString('uk-UA')
        : date.toLocaleString('uk-UA', { dateStyle: 'short', timeStyle: 'short' });
    }
  }
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}
