import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE, PageResult } from '../connectors/connectors-api.service';

/** `WorkflowSummary` — рядок списку `GET /api/v1/workflows`. */
export interface WorkflowSummary {
  id: string;
  name: string;
  active: boolean;
  version: number;
  updatedAt: string;
}

/**
 * Визначення сценарію (docs/workflow-format.md). Список сценаріїв працює лише з обов'язковими полями верхнього рівня;
 * повний тип вузлів з'явиться разом із канвасом.
 */
export interface WorkflowDefinition {
  formatVersion: 1;
  name: string;
  settings: {
    timezone: string;
    executionTimeoutSec: number;
    errorWorkflowId: string | null;
    saveSuccessfulData: boolean;
  };
  nodes: unknown[];
  connections: unknown[];
  pinData: Record<string, unknown[]>;
  [extra: string]: unknown;
}

/** `WorkflowDetail` — `GET /api/v1/workflows/{id}` і відповіді змін. */
export interface WorkflowDetail {
  id: string;
  name: string;
  active: boolean;
  version: number;
  versionId: string;
  definition: WorkflowDefinition;
  updatedAt: string;
}

/** Фільтр списку (`search`, `active`). */
export interface WorkflowListFilter {
  search?: string | null;
  active?: boolean | null;
}

/** Порожній сценарій (formatVersion 1) з налаштуваннями за замовчуванням — тіло `POST /workflows`. */
export function emptyWorkflowDefinition(name: string): WorkflowDefinition {
  return {
    formatVersion: 1,
    name,
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [],
    connections: [],
    pinData: {},
  };
}

/** Клієнт `/api/v1/workflows` (docs/architecture.md §10.3). */
@Injectable({ providedIn: 'root' })
export class WorkflowsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/workflows`;

  list(filter: WorkflowListFilter = {}, page = 1, pageSize = 200): Observable<PageResult<WorkflowSummary>> {
    const params: Record<string, string | number | boolean> = { page, pageSize };
    const search = filter.search?.trim();
    if (search) params['search'] = search;
    if (filter.active !== undefined && filter.active !== null) params['active'] = filter.active;
    return this.http.get<PageResult<WorkflowSummary>>(this.base, { params });
  }

  create(name: string): Observable<WorkflowDetail> {
    return this.http.post<WorkflowDetail>(this.base, emptyWorkflowDefinition(name));
  }

  rename(id: string, name: string): Observable<WorkflowDetail> {
    return this.http.patch<WorkflowDetail>(this.url(id), { name });
  }

  activate(id: string): Observable<WorkflowDetail> {
    return this.http.post<WorkflowDetail>(`${this.url(id)}/activate`, null);
  }

  deactivate(id: string): Observable<WorkflowDetail> {
    return this.http.post<WorkflowDetail>(`${this.url(id)}/deactivate`, null);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(this.url(id));
  }

  private url(id: string): string {
    return `${this.base}/${encodeURIComponent(id)}`;
  }
}

/** Рядок списку з деталей (після створення, перейменування, зміни активності). */
export function toSummary(detail: WorkflowDetail): WorkflowSummary {
  return { id: detail.id, name: detail.name, active: detail.active, version: detail.version, updatedAt: detail.updatedAt };
}
