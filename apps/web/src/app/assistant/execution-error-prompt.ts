import { ExecutionDetail, ExecutionStep } from '../executions/executions-api.service';

/** Найдовший фрагмент тексту помилки в запиті до моделі. */
const MAX_TEXT = 1000;

/** Запит до помічника з помилкового виконання (F3-112). */
export interface ExecutionErrorPrompt {
  text: string;
  /** Вузол, що впав, — іде моделі в контексті сторінки. */
  nodeId: string | null;
}

function clip(text: string): string {
  return text.length > MAX_TEXT ? `${text.slice(0, MAX_TEXT)}…` : text;
}

/** Перший крок, що впав. */
export function failedStep(detail: ExecutionDetail): ExecutionStep | undefined {
  return detail.steps.find((s) => s.status === 'error' || s.error);
}

/**
 * Запит «розбери помилку»: сценарій, виконання, крок і текст помилки. Дані кроків (input/output) у запит не йдуть — модель
 * сама читає виконання інструментом `get_execution`, а Api віддає кроки очищеними від секретів (docs/architecture.md §8).
 * Текст помилки теж береться з очищеного виконання.
 */
export function executionErrorPrompt(detail: ExecutionDetail, workflowName: string): ExecutionErrorPrompt {
  const step = failedStep(detail);
  const lines = [$localize`:@@assistant.explainError.head:Розбери помилку виконання ${detail.summary.id}:execution: сценарію «${workflowName}:workflow:».`];
  if (detail.summary.error) lines.push($localize`:@@assistant.explainError.summary:Помилка виконання: ${clip(detail.summary.error)}:error:`);
  if (step?.error) {
    const text = [step.error.message, step.error.details].filter(Boolean).join('\n');
    lines.push($localize`:@@assistant.explainError.step:Упав вузол ${step.nodeId}:node: (запуск ${step.runIndex}:run:): ${clip(text)}:error:`);
  }
  lines.push($localize`:@@assistant.explainError.ask:Поясни причину простими словами. Якщо це виправляється зміною сценарію — запропонуй виправлення.`);
  return { text: lines.join('\n'), nodeId: step?.nodeId ?? null };
}
