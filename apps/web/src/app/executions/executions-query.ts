import { ParamMap, Params } from '@angular/router';
import { EXECUTION_STATES, ExecutionState } from './executions-api.service';

/** Фільтри журналу виконань: те, що зберігається в query-параметрах адреси `/executions` (F3-81). */
export interface ExecutionsQuery {
  workflowId: string | null;
  status: ExecutionState | null;
  /** `yyyy-MM-dd` (локальний день) або порожньо. */
  from: string;
  to: string;
  correlationKey: string;
  hasOpenViolations: boolean;
  /** З 1. */
  page: number;
}

const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;

/** Фільтри з адреси; невідомі чи зіпсовані значення ігноруються. */
export function parseExecutionsQuery(params: ParamMap): ExecutionsQuery {
  const status = params.get('status');
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const page = Number(params.get('page'));
  return {
    workflowId: params.get('workflowId') || null,
    status: EXECUTION_STATES.includes(status as ExecutionState) ? (status as ExecutionState) : null,
    from: ISO_DAY.test(from) ? from : '',
    to: ISO_DAY.test(to) ? to : '',
    correlationKey: (params.get('correlationKey') ?? '').trim(),
    hasOpenViolations: params.get('hasOpenViolations') === 'true',
    page: Number.isInteger(page) && page > 1 ? page : 1,
  };
}

/** Query-параметри адреси для фільтрів: значення за замовчуванням — `null` (параметр прибирається при `merge`). */
export function executionsQueryParams(query: ExecutionsQuery): Params {
  return {
    workflowId: query.workflowId || null,
    status: query.status,
    from: query.from || null,
    to: query.to || null,
    correlationKey: query.correlationKey || null,
    hasOpenViolations: query.hasOpenViolations ? 'true' : null,
    page: query.page > 1 ? query.page : null,
  };
}

/** Чи однакові фільтри. */
export function sameExecutionsQuery(a: ExecutionsQuery, b: ExecutionsQuery): boolean {
  return JSON.stringify(executionsQueryParams(a)) === JSON.stringify(executionsQueryParams(b));
}

/** Локальний день `Date` → `yyyy-MM-dd`. */
export function toIsoDay(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** `yyyy-MM-dd` → `Date` (локальна північ); порожньо чи зіпсовано — `null`. */
export function fromIsoDay(day: string): Date | null {
  if (!ISO_DAY.test(day)) return null;
  const [y, m, d] = day.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  return date.getMonth() === m - 1 ? date : null;
}

/** Тривалість виконання: «до секунди» — мс, далі секунди, хвилини, години; без завершення — `—`. */
export function formatExecutionDuration(startedAt: string | null, finishedAt: string | null): string {
  if (!startedAt || !finishedAt) return '—';
  const ms = new Date(finishedAt).getTime() - new Date(startedAt).getTime();
  if (!Number.isFinite(ms) || ms < 0) return '—';
  if (ms < 1000) return $localize`:@@executions.duration.ms:${ms}:ms: мс`;
  const seconds = ms / 1000;
  if (seconds < 60) {
    const value = seconds.toFixed(1).replace('.', ',').replace(/,0$/, '');
    return $localize`:@@executions.duration.s:${value}:s: с`;
  }
  const minutes = Math.floor(seconds / 60);
  const restSeconds = Math.round(seconds - minutes * 60);
  if (minutes < 60) return $localize`:@@executions.duration.m:${minutes}:m: хв ${restSeconds}:s: с`;
  const hours = Math.floor(minutes / 60);
  const restMinutes = minutes % 60;
  return $localize`:@@executions.duration.h:${hours}:h: год ${restMinutes}:m: хв`;
}
