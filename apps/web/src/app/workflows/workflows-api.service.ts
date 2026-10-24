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

/** Доріжка бізнес-вигляду (F3-67, docs/business-view.md): хто виконує кроки. */
export interface BusinessLane {
  id: string;
  name: string;
  /** Підпис під назвою («люди у веб-формах»). */
  description?: string;
  [extra: string]: unknown;
}

/** Зв'язок зовнішнього кроку з вузлом: `in` — вузол читає результат кроку, `out` — вузол ставить людині задачу. */
export interface BusinessLink {
  node: string;
  direction: 'in' | 'out';
  label?: string;
  [extra: string]: unknown;
}

/** Зовнішній крок: дія людини в іншій системі, якої немає в графі; має хоча б один зв'язок. */
export interface BusinessStep {
  id: string;
  title: string;
  subtitle?: string;
  /** id доріжки з `business.lanes`. */
  lane: string;
  links: BusinessLink[];
  [extra: string]: unknown;
}

/** Бізнес-розмітка сценарію (F3-67). Усе необов'язкове; рушій ці поля ігнорує. */
export interface WorkflowBusiness {
  lanes?: BusinessLane[];
  steps?: BusinessStep[];
  [extra: string]: unknown;
}

/** Як вузол показується в бізнес-вигляді; без `title` — `name`, без `lane` — доріжка за типом вузла. */
export interface WorkflowNodeBusiness {
  title?: string;
  subtitle?: string;
  lane?: string;
  /** `true` — сховати; `false` — показати технічний вузол, який інакше згорнувся б. */
  hidden?: boolean;
  [extra: string]: unknown;
}

/** Бізнес-атрибути ребра: підпис стрілки; без нього — назва виходу. */
export interface WorkflowConnectionBusiness {
  label?: string;
  [extra: string]: unknown;
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
  business?: WorkflowNodeBusiness;
  [extra: string]: unknown;
}

/** Ребро: вихід `from.output` вузла `from.node` → вхід `to.input` вузла `to.node` (індекси від 0). */
export interface WorkflowConnection {
  from: { node: string; output: number; [extra: string]: unknown };
  to: { node: string; input: number; [extra: string]: unknown };
  business?: WorkflowConnectionBusiness;
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
  /** Бізнес-розмітка для бізнес-вигляду (F3-67); немає — правила за замовчуванням. */
  business?: WorkflowBusiness;
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

/** Тіло `POST /workflows/{id}/expressions/evaluate` (F3-56). */
export interface EvaluateExpressionRequest {
  expression: string;
  nodeId?: string;
  /** Вхідний item (`$json`); без нього — `pinData` попереднього вузла. */
  item?: { json: unknown };
  /** Виходи вузлів за назвою (`$node["…"]`); без них — `pinData`. */
  nodes?: Record<string, { json: unknown }[]>;
}

/** Результат попереднього перегляду: значення або помилка виразу. */
export interface ExpressionPreview {
  value: unknown;
  error: string | null;
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

  /** Обчислює вираз для попереднього перегляду (F3-56): ліміти виконання, без `$env`. */
  evaluateExpression(id: string, body: EvaluateExpressionRequest): Observable<ExpressionPreview> {
    return this.http.post<ExpressionPreview>(`${this.url(id)}/expressions/evaluate`, body);
  }

  /** Поточна версія з маніфестом залежностей — файл для іншого середовища (F6-09). */
  exportWorkflow(id: string): Observable<WorkflowExport> {
    return this.http.get<WorkflowExport>(`${this.url(id)}/export`);
  }

  /** Новий неактивний сценарій з експорту; незмаплені посилання — 422 `IMPORT_UNMAPPED` ({@link unmappedOf}). */
  importWorkflow(request: WorkflowImportRequest): Observable<WorkflowImportResult> {
    return this.http.post<WorkflowImportResult>(`${this.base}/import`, request);
  }

  private url(id: string): string {
    return `${this.base}/${encodeURIComponent(id)}`;
  }
}

/** Рядок списку з деталей (після створення, перейменування, зміни активності). */
export function toSummary(detail: WorkflowDetail): WorkflowSummary {
  return { id: detail.id, name: detail.name, active: detail.active, version: detail.version, updatedAt: detail.updatedAt, groupId: detail.groupId ?? null };
}

/** Маніфест залежностей експорту (F6-09): id середовища-джерела з назвами й типами, без секретів. */
export interface WorkflowManifest {
  connectors: { ref: string; name: string; transport: string }[];
  credentials: { ref: string; name: string; type: string }[];
  variables: string[];
  workflows?: { ref: string; name: string }[] | null;
}

/** `GET /workflows/{id}/export`. */
export interface WorkflowExport {
  definition: WorkflowDefinition;
  manifest: WorkflowManifest;
}

/** Явні відповідники «ref джерела → id цього середовища». */
export interface ImportMapping {
  connectors?: Record<string, string> | null;
  credentials?: Record<string, string> | null;
  workflows?: Record<string, string> | null;
}

/** Тіло `POST /workflows/import`. */
export interface WorkflowImportRequest {
  definition: WorkflowDefinition;
  manifest?: WorkflowManifest | null;
  mapping?: ImportMapping | null;
  groupId?: string | null;
}

/** Вид посилання в імпорті. */
export type ReferenceKind = 'connector' | 'credential' | 'workflow';

/** Посилання без відповідника: `not_found` · `ambiguous` · `invalid` (явний відповідник не існує). */
export interface UnmappedReference {
  kind: ReferenceKind;
  ref: string;
  name: string | null;
  type: string | null;
  reason: 'not_found' | 'ambiguous' | 'invalid';
}

/** Результат імпорту: новий сценарій, застосовані відповідники і відсутні змінні. */
export interface WorkflowImportResult {
  workflow: WorkflowDetail;
  mapping: ImportMapping;
  missingVariables: string[];
}

/** `unmapped[]` з відповіді 422 `IMPORT_UNMAPPED`; інша помилка — `null`. */
export function unmappedOf(error: unknown): UnmappedReference[] | null {
  if (error instanceof HttpErrorResponse && error.status === 422) {
    const body = error.error as { code?: unknown; unmapped?: unknown } | null;
    if (body?.code === 'IMPORT_UNMAPPED' && Array.isArray(body.unmapped)) return body.unmapped as UnmappedReference[];
  }
  return null;
}
