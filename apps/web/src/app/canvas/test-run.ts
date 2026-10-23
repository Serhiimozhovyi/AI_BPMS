import { ExecutionEvent, ExecutionItem, ExecutionState, ExecutionStep, StepStatus, isFinalState } from '../executions/executions-api.service';

/**
 * Стан тестового запуску з канвасу (F2-20): стан виконання і останній крок кожного вузла з SSE.
 * Без Angular — чисті функції; редактор тримає значення в сигналі.
 */
export interface TestRun {
  executionId: string;
  state: ExecutionState;
  /** Помилка виконання (фінальний `error`/`timed_out`) або обрив потоку подій. */
  error: string | null;
  /** Останній крок за id вузла (для Loop — з найбільшим `runIndex`). */
  steps: ReadonlyMap<string, ExecutionStep>;
}

/** Статус вузла на канвасі під час/після тестового запуску. */
export type NodeRunStatus = StepStatus;

/** Новий запуск у стані `queued` (відповідь `POST /executions`). */
export function startedRun(executionId: string, state: ExecutionState = 'queued'): TestRun {
  return { executionId, state, error: null, steps: new Map() };
}

/** Застосовує подію SSE; події чужого виконання ігноруються. */
export function applyEvent(run: TestRun, event: ExecutionEvent): TestRun {
  if (event.kind === 'status') {
    if (event.executionId !== run.executionId) return run;
    return { ...run, state: event.state, error: event.error ?? null };
  }
  const step = event.step;
  const previous = run.steps.get(step.nodeId);
  if (previous && (previous.id === step.id || previous.runIndex > step.runIndex)) return run;
  const steps = new Map(run.steps);
  steps.set(step.nodeId, step);
  // Перший крок означає, що воркер узяв job, навіть якщо подія `status: running` ще не прийшла.
  const state = run.state === 'queued' ? 'running' : run.state;
  return { ...run, state, steps };
}

/** Потік подій обірвався до фінального стану. */
export function streamFailed(run: TestRun, message: string): TestRun {
  return isFinalState(run.state) ? run : { ...run, error: message };
}

/** Запуск ще йде (кнопка «Виконати» недоступна). */
export function isRunning(run: TestRun | null): boolean {
  return !!run && !isFinalState(run.state) && run.error === null;
}

/** Статуси вузлів для канвасу. */
export function nodeStatuses(run: TestRun | null): ReadonlyMap<string, NodeRunStatus> {
  const result = new Map<string, NodeRunStatus>();
  if (!run) return result;
  for (const [nodeId, step] of run.steps) result.set(nodeId, step.status);
  return result;
}

/** Items виходу 0 кожного виконаного вузла — «останній тестовий output» для підказок виразів (F2-19). */
export function runOutputs(run: TestRun | null): Record<string, ExecutionItem[]> {
  const result: Record<string, ExecutionItem[]> = {};
  if (!run) return result;
  for (const [nodeId, step] of run.steps) {
    if (step.status === 'success') result[nodeId] = step.output[0] ?? [];
  }
  return result;
}
