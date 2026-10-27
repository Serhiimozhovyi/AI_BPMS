import { ExecutionDetail, ExecutionState, ExecutionStep, ExecutionSummary } from '../executions/executions-api.service';
import { TestRun } from './test-run';

/** Останнє виконання сценарію, показане на канвасі при відкритті редактора (F3-76). */
export interface LastRun {
  summary: ExecutionSummary;
  /** Той самий вигляд, що й у тестового запуску, тож статуси й помилки на вузлах рахують ті самі функції. */
  run: TestRun;
}

/** Деталі виконання → стан для канвасу: для кожного вузла лишається крок з найбільшим `runIndex` (Loop). */
export function lastRunFromDetail(detail: ExecutionDetail): LastRun {
  const steps = new Map<string, ExecutionStep>();
  for (const step of detail.steps) {
    const previous = steps.get(step.nodeId);
    if (!previous || previous.runIndex <= step.runIndex) steps.set(step.nodeId, step);
  }
  const { summary } = detail;
  return { summary, run: { executionId: summary.id, state: summary.state, error: summary.error, steps, current: new Set(steps.keys()) } };
}

/** Час завершення кроку для підпису на картці вузла: `18:10:42`. */
export function lastRunTimes(run: TestRun | null): ReadonlyMap<string, string> {
  const result = new Map<string, string>();
  if (!run) return result;
  for (const [nodeId, step] of run.steps) {
    const at = step.finishedAt || step.startedAt;
    if (at) result.set(nodeId, formatTime(at));
  }
  return result;
}

const STATE_LABEL: Record<ExecutionState, string> = {
  queued: $localize`:@@lastrun.state.queued:у черзі`,
  running: $localize`:@@lastrun.state.running:виконується`,
  waiting: $localize`:@@lastrun.state.waiting:очікує`,
  cancelling: $localize`:@@lastrun.state.cancelling:скасовується`,
  success: $localize`:@@lastrun.state.success:успішно`,
  error: $localize`:@@lastrun.state.error:помилка`,
  cancelled: $localize`:@@lastrun.state.cancelled:скасовано`,
  timed_out: $localize`:@@lastrun.state.timedOut:перевищено час`,
};

/** `18:10, помилка` (сьогодні) або `26.10 18:10, успішно`. */
export function lastRunLabel(summary: Pick<ExecutionSummary, 'state' | 'startedAt' | 'finishedAt'>, now: Date = new Date()): string {
  const at = summary.finishedAt ?? summary.startedAt;
  const state = STATE_LABEL[summary.state];
  if (!at) return state;
  const date = new Date(at);
  const sameDay = date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate();
  const hm = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return sameDay ? `${hm}, ${state}` : `${pad(date.getDate())}.${pad(date.getMonth() + 1)} ${hm}, ${state}`;
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}
