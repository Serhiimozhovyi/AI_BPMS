import { ExecutionEvent, ExecutionItem, ExecutionState, ExecutionStep, StartExecutionRequest, StepStatus, isFinalState } from '../executions/executions-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';

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
  /** Вузли, кроки яких уже прийшли в цьому виконанні; решта `steps` — з попереднього запуску. */
  current: ReadonlySet<string>;
}

/** Статус вузла на канвасі під час/після тестового запуску. */
export type NodeRunStatus = StepStatus;

/**
 * Новий запуск у стані `queued` (відповідь `POST /executions`). `previous` — кроки попереднього запуску, що лишаються
 * видимими, поки новий їх не замінить (запуск одного вузла не стирає решту графа).
 */
export function startedRun(executionId: string, state: ExecutionState = 'queued', previous?: ReadonlyMap<string, ExecutionStep>): TestRun {
  return { executionId, state, error: null, steps: new Map(previous ?? []), current: new Set() };
}

/** Застосовує подію SSE; події чужого виконання ігноруються. */
export function applyEvent(run: TestRun, event: ExecutionEvent): TestRun {
  if (event.kind === 'status') {
    if (event.executionId !== run.executionId) return run;
    return { ...run, state: event.state, error: event.error ?? null };
  }
  const step = event.step;
  const previous = run.steps.get(step.nodeId);
  if (previous && run.current.has(step.nodeId) && (previous.id === step.id || previous.runIndex > step.runIndex)) return run;
  const steps = new Map(run.steps);
  steps.set(step.nodeId, step);
  const current = new Set(run.current).add(step.nodeId);
  // Перший крок означає, що воркер узяв job, навіть якщо подія `status: running` ще не прийшла.
  const state = run.state === 'queued' ? 'running' : run.state;
  return { ...run, state, steps, current };
}

/** Потік подій обірвався до фінального стану. */
export function streamFailed(run: TestRun, message: string): TestRun {
  return isFinalState(run.state) ? run : { ...run, error: message };
}

/** Запуск ще йде (кнопка «Виконати» недоступна). */
export function isRunning(run: TestRun | null): boolean {
  return !!run && !isFinalState(run.state) && run.error === null;
}

/** Id усіх попередників вузла за ребрами сценарію (без самого вузла). */
export function ancestorIds(definition: WorkflowDefinition, nodeId: string): Set<string> {
  const result = new Set<string>();
  const stack = [nodeId];
  while (stack.length > 0) {
    const id = stack.pop()!;
    for (const c of definition.connections) {
      if (c.to.node === id && !result.has(c.from.node) && c.from.node !== nodeId) {
        result.add(c.from.node);
        stack.push(c.from.node);
      }
    }
  }
  return result;
}

/**
 * Запит «Виконати вузол» (F2-21): лише цей вузол і попередники; попередники, що завершились в останньому запуску
 * (успіх або згасла гілка), не виконуються — їхні виходи (усі порти) йдуть як `runData`. Решта — з `pinData` або
 * виконуються.
 */
export function singleNodeRequest(definition: WorkflowDefinition, run: TestRun | null, nodeId: string): StartExecutionRequest {
  const runData: Record<string, ExecutionItem[][]> = {};
  if (run) {
    for (const id of ancestorIds(definition, nodeId)) {
      const step = run.steps.get(id);
      if (step && (step.status === 'success' || step.status === 'skipped')) runData[id] = step.output;
    }
  }
  return { destinationNodeId: nodeId, runData };
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
