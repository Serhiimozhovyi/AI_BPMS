import { HttpClient } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** `ExecutionState` (snake_case, як у БД): `queued → running → success | error | cancelled | timed_out`. */
export type ExecutionState = 'queued' | 'running' | 'cancelling' | 'success' | 'error' | 'cancelled' | 'timed_out';

/** `StepStatus` кроку (camelCase). */
export type StepStatus = 'success' | 'error' | 'skipped' | 'cancelled';

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
  mode: 'manual' | 'trigger' | 'retry';
  state: ExecutionState;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
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
  error: { message: string; type?: string | null; attempts?: number } | null;
  startedAt: string;
  finishedAt: string;
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
}

/** Подія SSE `/executions/{id}/events` (docs/architecture.md §10.5). */
export type ExecutionEvent =
  | { kind: 'step'; step: ExecutionStep }
  | { kind: 'status'; executionId: string; state: ExecutionState; error: string | null };

/** Фінальний стан: виконання більше не зміниться. */
export function isFinalState(state: ExecutionState): boolean {
  return state === 'success' || state === 'error' || state === 'cancelled' || state === 'timed_out';
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

  get(id: string): Observable<ExecutionDetail> {
    return this.http.get<ExecutionDetail>(this.url(id));
  }

  cancel(id: string): Observable<ExecutionSummary> {
    return this.http.post<ExecutionSummary>(`${this.url(id)}/cancel`, null);
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
