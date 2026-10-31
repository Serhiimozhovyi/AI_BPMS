import type { JsonSchema } from '../form-fields';
import { fromSchema, fieldOrder } from './form-designer.model';

/** Скільки підписів полів показує кнопка «Налаштувати поля» в панелі вузла. */
export const SUMMARY_LABELS = 5;

export interface FormSummary {
  /** Кількість полів форми. */
  count: number;
  /** Підписи перших полів у порядку розмітки (без підпису — ключ). */
  labels: string[];
  /** Скільки полів не вмістилось у `labels` («ще N»). */
  more: number;
}

/** Чи значення параметра — схема форми, яку вміє показати конструктор (об'єкт); вираз `{{ }}` чи інше — ні. */
export function isDesignableSchema(value: unknown): value is JsonSchema {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Короткий опис схеми для панелі вузла: кількість полів і перші підписи. */
export function summarizeSchema(schema: unknown): FormSummary {
  if (!isDesignableSchema(schema)) return { count: 0, labels: [], more: 0 };
  const state = fromSchema(schema);
  const keys = fieldOrder(state);
  const labels = keys.slice(0, SUMMARY_LABELS).map((key) => {
    const title = state.properties[key]?.['title'];
    return typeof title === 'string' && title.trim() ? title : key;
  });
  return { count: keys.length, labels, more: Math.max(0, keys.length - SUMMARY_LABELS) };
}
