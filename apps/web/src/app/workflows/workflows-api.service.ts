import { HttpClient, HttpErrorResponse } from '@angular/common/http';
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
  /** Група сценарію (F3-63); `null` — без групи. */
  groupId?: string | null;
}

/** `WorkflowGroupSummary` — група сценаріїв з кількістю сценаріїв у ній (`GET /workflow-groups`, F3-63). */
export interface WorkflowGroup {
  id: string;
  name: string;
  sortOrder: number;
  workflowCount: number;
}

/** Вузол сценарію (docs/workflow-format.md, «Обов'язкові поля»). Невідомі поля зберігаються як є. */
export interface WorkflowNode {
  id: string;
  name: string;
  type: string;
  typeVersion: number;
  position: [number, number];
  parameters: Record<string, unknown>;
  timeoutSec?: number;
  retry?: { maxTries: number; waitMs: number; [extra: string]: unknown };
  continueOnFail?: boolean;
  disabled?: boolean;
  notes?: string;
  [extra: string]: unknown;
}

/** Ребро: вихід `from.output` вузла `from.node` → вхід `to.input` вузла `to.node` (індекси від 0). */
export interface WorkflowConnection {
  from: { node: string; output: number; [extra: string]: unknown };
  to: { node: string; input: number; [extra: string]: unknown };
  [extra: string]: unknown;
}

/** Що робити із запуском понад ліміт: поставити в чергу чи відкинути. */
export type LimitAction = 'queue' | 'drop';

/**
 * Визначення сценарію (docs/workflow-format.md). Невідомі поля на будь-якому рівні дозволені —
 * клієнт не відкидає їх при збереженні.
 */
export interface WorkflowDefinition {
  formatVersion: 1;
  name: string;
  settings: {
    timezone: string;
    executionTimeoutSec: number;
    errorWorkflowId: string | null;
    saveSuccessfulData: boolean;
    /** Ліміт одночасних виконань (F6-06). */
    concurrency?: { max: number; onLimit?: LimitAction; [extra: string]: unknown };
    /** Ліміт частоти запусків (F6-06). */
    throttle?: { limit: number; periodSec: number; onLimit?: LimitAction; [extra: string]: unknown };
    /** Скільки помилок тригера підряд вимикає сценарій (F6-06); 0 чи немає — не вимикати. */
    disableAfterErrors?: number;
    [extra: string]: unknown;
  };
  nodes: WorkflowNode[];
  connections: WorkflowConnection[];
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
  groupId?: string | null;
}

/** `WorkflowValidationError` — `{ code, message, nodeId?, path? }` (docs/workflow-format.md, «Валідація при збереженні»). */
export interface WorkflowValidationError {
  code: string;
  message: string;
  nodeId?: string | null;
  path?: string | null;
}

/** Помилки валідації з відповіді 422 (`errors` у ProblemDetails); інша помилка — порожній список. */
export function validationErrorsOf(error: unknown): WorkflowValidationError[] {
  if (!(error instanceof HttpErrorResponse) || error.status !== 422) return [];
  const errors = (error.error as { errors?: unknown } | null)?.errors;
  if (!Array.isArray(errors)) return [];
  return errors.filter(
    (e): e is WorkflowValidationError =>
      !!e && typeof e === 'object' && typeof (e as WorkflowValidationError).code === 'string' && typeof (e as WorkflowValidationError).message === 'string',
  );
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

  get(id: string): Observable<WorkflowDetail> {
    return this.http.get<WorkflowDetail>(this.url(id));
  }

  /** `POST /workflows`; `groupId` — група нового сценарію (F3-63). */
  create(name: string, groupId?: string | null): Observable<WorkflowDetail> {
    const options = groupId ? { params: { groupId } } : {};
    return this.http.post<WorkflowDetail>(this.base, emptyWorkflowDefinition(name), options);
  }

  /** `PUT /workflows/{id}` — зберегти визначення як нову версію; 422 — помилки валідації (див. {@link validationErrorsOf}). */
  save(id: string, definition: WorkflowDefinition): Observable<WorkflowDetail> {
    return this.http.put<WorkflowDetail>(this.url(id), definition);
  }

  rename(id: string, name: string): Observable<WorkflowDetail> {
    return this.http.patch<WorkflowDetail>(this.url(id), { name });
  }

  /** `PATCH /workflows/{id} { groupId }` — перенести в групу; `null` — без групи. Нової версії не створює. */
  move(id: string, groupId: string | null): Observable<WorkflowDetail> {
    return this.http.patch<WorkflowDetail>(this.url(id), { groupId });
  }

  listGroups(): Observable<WorkflowGroup[]> {
    return this.http.get<WorkflowGroup[]>(`${API_BASE}/workflow-groups`);
  }

  createGroup(name: string): Observable<WorkflowGroup> {
    return this.http.post<WorkflowGroup>(`${API_BASE}/workflow-groups`, { name });
  }

  renameGroup(id: string, name: string): Observable<WorkflowGroup> {
    return this.http.patch<WorkflowGroup>(`${API_BASE}/workflow-groups/${encodeURIComponent(id)}`, { name });
  }

  /** Сценарії групи лишаються, без групи. */
  deleteGroup(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/workflow-groups/${encodeURIComponent(id)}`);
  }

  /** `PUT /workflow-groups/order` — усі групи в новому порядку. */
  reorderGroups(ids: readonly string[]): Observable<WorkflowGroup[]> {
    return this.http.put<WorkflowGroup[]>(`${API_BASE}/workflow-groups/order`, { ids });
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
  return { id: detail.id, name: detail.name, active: detail.active, version: detail.version, updatedAt: detail.updatedAt, groupId: detail.groupId ?? null };
}
