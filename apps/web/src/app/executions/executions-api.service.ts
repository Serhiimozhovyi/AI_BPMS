import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE, PageResult } from '../connectors/connectors-api.service';

/**
 * `ExecutionState` (snake_case, як у БД): `queued → running → success | error | cancelled | timed_out`;
 * `running ⇄ waiting` — довге очікування (F5-02).
 */
export type ExecutionState = 'queued' | 'running' | 'waiting' | 'cancelling' | 'success' | 'error' | 'cancelled' | 'timed_out';

/** `StepStatus` кроку (camelCase); `waiting` — вузол чекає до `resumeAt` (F5-02). */
export type StepStatus = 'success' | 'error' | 'skipped' | 'cancelled' | 'waiting';

/** Item між вузлами (docs/workflow-format.md, «Item і BinaryRef»). */
export interface ExecutionItem {
  json: Record<string, unknown>;
  binary?: Record<string, unknown> | null;
  [extra: string]: unknown;
}

/** `ExecutionSummary` — відповідь запуску і рядок списку. */
export interface ExecutionSummary {
  id: string;
  workflowId: string;
  versionId: string;
  /** `error` — запуск сценарію помилок (`core.errorTrigger`, F4-06); `resume` — продовження з вузла, що впав (F6-08). */
  mode: 'manual' | 'trigger' | 'retry' | 'error' | 'resume';
  state: ExecutionState;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  /** Для `resume` — виконання, яке це продовжує. */
  resumedFromExecutionId?: string | null;
  /** Ключ кореляції (F5-04): бізнес-ідентифікатор запуску, напр. id угоди. */
  correlationKey?: string | null;
  /** Для `waiting` — коли виконання прокинеться (F5-02). */
  resumeAt?: string | null;
  /** Для `waiting` — вузол, що чекає. */
  waitingNodeId?: string | null;
}

/** `StepError` — помилка вузла (F3-57): `message` — що сталося і що зробити, `details` — технічні подробиці. */
export interface StepError {
  message: string;
  type?: string | null;
  attempts?: number;
  details?: string | null;
  /** Індекс item'а на вході, на якому впав вузол; `null` — невідомо. */
  itemIndex?: number | null;
}

/** `SubStep` — підкрок вузла (F6-12): запит до моделі (`llm`) або виклик інструмента агентом (`tool`). */
export interface SubStep {
  kind: 'llm' | 'tool' | string;
  /** Модель або «конектор / дія». */
  name: string;
  startedAt: string;
  finishedAt: string;
  input?: unknown;
  output?: unknown;
  error?: string | null;
  itemIndex?: number;
  iteration?: number;
  usage?: { input?: number | null; output?: number | null } | null;
}

/** `ExecutionStepDto` — крок виконання (вже очищений від секретів). */
export interface ExecutionStep {
  id: string;
  nodeId: string;
  runIndex: number;
  status: StepStatus;
  /** Items на входах — по масиву на порт. */
  input: ExecutionItem[][];
  /** Items на виходах — по масиву на порт. */
  output: ExecutionItem[][];
  error: StepError | null;
  startedAt: string;
  finishedAt: string;
  /** Підкроки агента (F6-12); зазвичай немає. */
  subSteps?: SubStep[] | null;
  /** Для кроку `waiting` — коли вузол прокинеться (F5-02). */
  resumeAt?: string | null;
}

/** `ExecutionDetail` — `GET /executions/{id}`. */
export interface ExecutionDetail {
  summary: ExecutionSummary;
  steps: ExecutionStep[];
}

/** `StartExecutionRequest` — тіло ручного запуску. */
export interface StartExecutionRequest {
  startNodeId?: string | null;
  startItems?: ExecutionItem[] | null;
  /** «Виконати вузол»: лише цей вузол і його попередники (сам вузол виконується навіть із `pinData`). */
  destinationNodeId?: string | null;
  /** Виходи вузлів (по портах) з попереднього запуску: такі вузли не виконуються. */
  runData?: Record<string, ExecutionItem[][]> | null;
  /** Ключ кореляції запуску (F5-04). */
  correlationKey?: string | null;
}

/** Подія SSE `/executions/{id}/events` (docs/architecture.md §10.5). */
export type ExecutionEvent =
  | { kind: 'step'; step: ExecutionStep }
  | { kind: 'status'; executionId: string; state: ExecutionState; error: string | null };

/** Фінальний стан: виконання більше не зміниться. */
export function isFinalState(state: ExecutionState): boolean {
  return state === 'success' || state === 'error' || state === 'cancelled' || state === 'timed_out';
}

/** Фільтр журналу `GET /executions` (усі поля необов'язкові). */
export interface ExecutionFilter {
  workflowId?: string | null;
  status?: ExecutionState | null;
  /** ISO 8601: `started_at >= from`. */
  from?: string | null;
  /** ISO 8601: `started_at <= to`. */
  to?: string | null;
  /** Точний збіг ключа кореляції (F5-04). */
  correlationKey?: string | null;
}

/** Мінімум `EventSource`, який потрібен клієнту; у тестах підміняється. */
export interface EventSourceLike {
  addEventListener(type: string, listener: (e: MessageEvent<string>) => void): void;
  onerror: ((e: Event) => void) | null;
  close(): void;
}

/** Фабрика `EventSource` (за замовчуванням — браузерна; cookie того самого origin ідуть автоматично). */
export const EVENT_SOURCE_FACTORY = new InjectionToken<(url: string) => EventSourceLike>('EVENT_SOURCE_FACTORY', {
  providedIn: 'root',
  factory: () => (url: string) => new EventSource(url) as unknown as EventSourceLike,
});

/** Клієнт виконань (docs/architecture.md §10.3, §10.5). */
@Injectable({ providedIn: 'root' })
export class ExecutionsApiService {
  private readonly http = inject(HttpClient);
  private readonly eventSource = inject(EVENT_SOURCE_FACTORY);

  /** `POST /workflows/{id}/executions` — ручний (тестовий) запуск поточної версії з `pinData`. */
  start(workflowId: string, request: StartExecutionRequest = {}): Observable<ExecutionSummary> {
    return this.http.post<ExecutionSummary>(`${API_BASE}/workflows/${encodeURIComponent(workflowId)}/executions`, request);
  }

  /** `GET /executions` — журнал, новіші першими (для `queued` — за id). */
  list(filter: ExecutionFilter = {}, page = 1, pageSize = 50): Observable<PageResult<ExecutionSummary>> {
    const params: Record<string, string | number> = { page, pageSize };
    for (const key of ['workflowId', 'status', 'from', 'to', 'correlationKey'] as const) {
      const value = filter[key];
      if (value) params[key] = value;
    }
    return this.http.get<PageResult<ExecutionSummary>>(`${API_BASE}/executions`, { params });
  }

  get(id: string): Observable<ExecutionDetail> {
    return this.http.get<ExecutionDetail>(this.url(id));
  }

  cancel(id: string): Observable<ExecutionSummary> {
    return this.http.post<ExecutionSummary>(`${this.url(id)}/cancel`, null);
  }

  /** `POST /executions/{id}/retry` — нове виконання тієї самої версії з тими самими стартовими даними. */
  retry(id: string): Observable<ExecutionSummary> {
    return this.http.post<ExecutionSummary>(`${this.url(id)}/retry`, null);
  }

  /**
   * `POST /executions/{id}/resume` (F6-08) — продовжити помилкове виконання з вузла, що впав; `items` — нові items на
   * вхід 0 цього вузла, без них — ті самі, що були.
   */
  resume(id: string, items?: ExecutionItem[] | null): Observable<ExecutionSummary> {
    return this.http.post<ExecutionSummary>(`${this.url(id)}/resume`, items ? { items } : {});
  }

  /**
   * Події виконання з SSE: кроки і зміни стану; фінальний стан завершує потік. Обрив з'єднання `EventSource`
   * відновлює сам (з `Last-Event-ID`, тож кроки не дублюються); якщо з'єднання закрите остаточно — помилка потоку.
   */
  events(id: string): Observable<ExecutionEvent> {
    return new Observable<ExecutionEvent>((subscriber) => {
      const source = this.eventSource(`${this.url(id)}/events`);
      source.addEventListener('step', (e) => subscriber.next({ kind: 'step', step: JSON.parse(e.data) as ExecutionStep }));
      source.addEventListener('status', (e) => {
        const data = JSON.parse(e.data) as { executionId: string; state: ExecutionState; error: string | null };
        subscriber.next({ kind: 'status', executionId: data.executionId, state: data.state, error: data.error ?? null });
        if (isFinalState(data.state)) {
          source.close();
          subscriber.complete();
        }
      });
      source.onerror = () => {
        // readyState 2 (CLOSED) — браузер не перепідключатиметься (напр. 404/403 до першої події).
        if ((source as unknown as { readyState?: number }).readyState === 2) {
          subscriber.error(new Error($localize`:@@executions.streamClosed:З'єднання з потоком подій виконання закрито.`));
        }
      };
      return () => source.close();
    });
  }

  private url(id: string): string {
    return `${API_BASE}/executions/${encodeURIComponent(id)}`;
  }
}

/** Усі стани для фільтра журналу. */
export const EXECUTION_STATES: readonly ExecutionState[] = ['queued', 'running', 'waiting', 'cancelling', 'success', 'error', 'cancelled', 'timed_out'];

/** Назва стану українською. */
export function executionStateLabel(state: ExecutionState): string {
  switch (state) {
    case 'queued':
      return $localize`:@@executions.state.queued:У черзі`;
    case 'running':
      return $localize`:@@executions.state.running:Виконується`;
    case 'waiting':
      return $localize`:@@executions.state.waiting:Очікує`;
    case 'cancelling':
      return $localize`:@@executions.state.cancelling:Скасовується`;
    case 'success':
      return $localize`:@@executions.state.success:Успіх`;
    case 'error':
      return $localize`:@@executions.state.error:Помилка`;
    case 'cancelled':
      return $localize`:@@executions.state.cancelled:Скасовано`;
    case 'timed_out':
      return $localize`:@@executions.state.timedOut:Перевищено час`;
  }
}

/** Назва режиму запуску українською. */
export function executionModeLabel(mode: ExecutionSummary['mode']): string {
  switch (mode) {
    case 'manual':
      return $localize`:@@executions.mode.manual:Вручну`;
    case 'trigger':
      return $localize`:@@executions.mode.trigger:Тригер`;
    case 'retry':
      return $localize`:@@executions.mode.retry:Повтор`;
    case 'error':
      return $localize`:@@executions.mode.error:Сценарій помилок`;
    case 'resume':
      return $localize`:@@executions.mode.resume:Продовження`;
  }
}

/** Крок вузла, що впав: останній з помилкою чи перерваний (`null`, якщо таймаут настав між вузлами). */
export function failedStep(detail: ExecutionDetail): ExecutionStep | null {
  return [...detail.steps].reverse().find((s) => s.status === 'error' || s.status === 'cancelled') ?? null;
}

/** Розбір тексту редактора вхідних items: масив об'єктів `{ json: {...} }`. */
export function parseItems(text: string): { items: ExecutionItem[] } | { error: string } {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { error: $localize`:@@executions.resume.invalidJson:Некоректний JSON.` };
  }
  const valid =
    Array.isArray(value) &&
    value.every((i) => typeof i === 'object' && i !== null && !Array.isArray(i) && typeof (i as { json?: unknown }).json === 'object' && (i as { json?: unknown }).json !== null);
  return valid ? { items: value as ExecutionItem[] } : { error: $localize`:@@executions.resume.notItems:Очікується масив items виду [{ "json": { … } }].` };
}
