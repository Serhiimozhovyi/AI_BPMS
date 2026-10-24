import { ExecutionItem } from '../executions/executions-api.service';

/**
 * Закріплений output вузла (`pinData`, F3-51): текст редактора ↔ items. Формат — як output кроку: масив items, у
 * кожного обов'язковий об'єкт `json` (docs/workflow-format.md).
 */

/** Шаблон «Ввести дані вручну»: один порожній item. */
export const EMPTY_PINNED_ITEMS: readonly ExecutionItem[] = [{ json: {} }];

/** Результат розбору тексту: items або повідомлення про першу помилку. */
export type PinnedParseResult = { items: ExecutionItem[] } | { error: string };

/** Items як текст для редактора: JSON з відступом 2. */
export function formatPinnedItems(items: readonly unknown[]): string {
  return JSON.stringify(items, null, 2);
}

/** Розбирає текст редактора: JSON-масив, кожен елемент — об'єкт з об'єктом `json`. */
export function parsePinnedItems(text: string): PinnedParseResult {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (e) {
    return { error: $localize`:@@pinned.invalidJson:Некоректний JSON: ${(e as Error).message}:message:` };
  }
  if (!Array.isArray(value)) return { error: $localize`:@@pinned.notArray:Очікується масив items: [{ "json": { … } }].` };
  for (let i = 0; i < value.length; i++) {
    const item: unknown = value[i];
    if (!isObject(item) || !isObject(item['json'])) {
      return { error: $localize`:@@pinned.itemWithoutJson:Item ${i}:index: має бути об'єктом з полем «json»-об'єктом.` };
    }
  }
  return { items: value as ExecutionItem[] };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
