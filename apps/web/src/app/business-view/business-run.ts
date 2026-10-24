import { CanvasModel } from '../canvas/canvas-model';
import { ExecutionState, ExecutionStep } from '../executions/executions-api.service';
import { BusinessView } from './business-model';

/**
 * Виконання на бізнес-вигляді (F3-69): статус кожного кроку схеми за кроками виконання. Технічні вузли згорнуті,
 * тож їхній статус переходить на найближчий видимий крок перед ними (немає такого — після них); збій видно завжди.
 */

export type BusinessRunStatus = 'done' | 'running' | 'waiting' | 'error' | 'pending';

export interface BusinessRunInfo {
  status: BusinessRunStatus;
  startedAt?: string;
  durationMs?: number;
}

/** Що знаємо про виконання: стан і останній крок кожного вузла. */
export interface BusinessRun {
  state: ExecutionState | 'waiting';
  steps: readonly ExecutionStep[];
}

const ACTIVE_STATES = new Set<string>(['queued', 'running', 'cancelling', 'waiting']);

/** Сильніший статус перемагає, коли кілька вузлів падають на один крок. */
const RANK: Record<BusinessRunStatus, number> = { pending: 0, done: 1, running: 2, waiting: 3, error: 4 };

export function businessRunStatuses(view: BusinessView, model: CanvasModel, run: BusinessRun | null): ReadonlyMap<string, BusinessRunInfo> {
  const result = new Map<string, BusinessRunInfo>();
  if (!run) return result;

  const lastByNode = new Map<string, ExecutionStep>();
  for (const step of run.steps) {
    const prev = lastByNode.get(step.nodeId);
    if (!prev || step.runIndex >= prev.runIndex) lastByNode.set(step.nodeId, step);
  }

  const visible = new Set(view.steps.filter((s) => s.nodeId).map((s) => s.id));
  const preds = new Map<string, string[]>();
  const succs = new Map<string, string[]>();
  for (const e of model.edges) {
    (preds.get(e.target) ?? preds.set(e.target, []).get(e.target)!).push(e.source);
    (succs.get(e.source) ?? succs.set(e.source, []).get(e.source)!).push(e.target);
  }
  const nearestVisible = (start: string, next: ReadonlyMap<string, string[]>): string[] => {
    const found: string[] = [];
    const seen = new Set<string>([start]);
    const queue = [...(next.get(start) ?? [])];
    while (queue.length > 0) {
      const id = queue.shift()!;
      if (seen.has(id)) continue;
      seen.add(id);
      if (visible.has(id)) found.push(id);
      else queue.push(...(next.get(id) ?? []));
    }
    return found;
  };

  const put = (id: string, info: BusinessRunInfo): void => {
    const current = result.get(id);
    if (!current || RANK[info.status] > RANK[current.status]) result.set(id, info);
  };

  for (const [nodeId, step] of lastByNode) {
    const status: BusinessRunStatus = step.status === 'success' ? 'done' : step.status === 'error' ? 'error' : 'pending';
    if (status === 'pending') continue;
    const info: BusinessRunInfo = { status, startedAt: step.startedAt, ...duration(step) };
    if (visible.has(nodeId)) {
      put(nodeId, info);
      continue;
    }
    // Згорнутий вузол: пройдений — нічого не додає; збій — на найближчий видимий крок перед ним (або після).
    if (status !== 'error') continue;
    const targets = nearestVisible(nodeId, preds);
    for (const id of targets.length > 0 ? targets : nearestVisible(nodeId, succs)) put(id, { status: 'error' });
  }

  // Поки виконання триває, кроки без статусу, до яких дійшли items (вихід попереднього вузла по цьому ребру не
  // порожній), — «виконується»/«чекає». Гілка, у яку items не пішли, лишається «не дійшло».
  if (ACTIVE_STATES.has(run.state)) {
    const active: BusinessRunStatus = run.state === 'waiting' ? 'waiting' : 'running';
    for (const e of model.edges) {
      if (!visible.has(e.target) || result.has(e.target)) continue;
      const source = lastByNode.get(e.source);
      if (source?.status === 'success' && (source.output[e.sourceOutput]?.length ?? 0) > 0) put(e.target, { status: active });
    }
  }

  // Дія людини в іншій системі пройдена, коли пройдено вузол, що її читає чи записує.
  for (const step of view.steps) {
    if (step.nodeId) continue;
    const linked = view.edges.filter((e) => e.from === step.id || e.to === step.id).map((e) => (e.from === step.id ? e.to : e.from));
    if (linked.some((id) => result.get(id)?.status === 'done')) put(step.id, { status: 'done' });
  }

  for (const step of view.steps) if (!result.has(step.id)) result.set(step.id, { status: 'pending' });
  return result;
}

function duration(step: ExecutionStep): { durationMs?: number } {
  const ms = Date.parse(step.finishedAt) - Date.parse(step.startedAt);
  return Number.isFinite(ms) && ms >= 0 ? { durationMs: ms } : {};
}
