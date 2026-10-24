/**
 * Значення поля форми вузла (F2-18). Будь-яке поле параметрів приймає рядок `{{ }}` замість свого типу
 * (docs/workflow-format.md, «Валідація при збереженні», п. 5), тож поле редагується як текст і перетворюється
 * на значення потрібного типу тут — без Angular, щоб легко тестувати.
 */

/** Як поле перетворює текст на значення: тип зі схеми або `json` — довільне JSON-значення. */
export type ValueKind = 'string' | 'number' | 'integer' | 'boolean' | 'enum' | 'json';

/** Обмеження значення з JSON Schema, які перевіряє форма. */
export interface ValueRules {
  kind: ValueKind;
  /** Для `enum` — допустимі значення. */
  options?: readonly unknown[];
  /** Для `enum` — підписи значень у тому ж порядку (напр. назви credentials для їхніх id); немає — саме значення. */
  labels?: readonly string[];
  /** Для `enum` — значення, що показуються в списку, але є помилкою `missing` (напр. id видаленого credential'а). */
  missing?: readonly unknown[];
  /** Для `json` — очікуваний тип (`object` / `array`), якщо схема його задає. */
  jsonType?: 'object' | 'array';
  minimum?: number;
  /** `false` — поле не приймає `{{ }}` (загальні поля вузла: `timeoutSec`, `retry`…). */
  allowExpression?: boolean;
}

const EXPRESSION = /\{\{[\s\S]*\}\}/;

/** Рядок з виразом `{{ … }}` (повністю або частково — шаблон рядка). */
export function isExpression(value: unknown): value is string {
  return typeof value === 'string' && EXPRESSION.test(value);
}

/** Значення → текст у полі. */
export function formatValue(value: unknown, kind: ValueKind): string {
  if (value === undefined || value === null) return '';
  if (typeof value === 'string') return value;
  if (kind === 'json' && typeof value === 'object') return JSON.stringify(value, null, 2);
  return JSON.stringify(value);
}

/**
 * Текст у полі → значення. Вираз лишається рядком; порожній текст — `undefined` (поле не задано).
 * Текст, який не перетворюється на потрібний тип, лишається рядком — його позначить {@link valueError}.
 */
export function parseValue(text: string, rules: ValueRules): unknown {
  if (rules.allowExpression !== false && isExpression(text)) return text;
  const trimmed = text.trim();
  if (trimmed === '') return undefined;
  switch (rules.kind) {
    case 'string':
      return text;
    case 'number':
    case 'integer': {
      const n = Number(trimmed);
      return Number.isFinite(n) ? n : text;
    }
    case 'boolean':
      return trimmed === 'true' ? true : trimmed === 'false' ? false : text;
    case 'enum': {
      const option = (rules.options ?? []).find((o) => (typeof o === 'string' ? o : JSON.stringify(o)) === trimmed);
      return option !== undefined ? option : text;
    }
    case 'json':
      try {
        return JSON.parse(trimmed) as unknown;
      } catch {
        return text;
      }
  }
}

/** Код помилки значення або `null`. Порожнє значення не помилка — обов'язковість перевіряє `required`. */
export type ValueErrorCode = 'expression' | 'number' | 'integer' | 'boolean' | 'enum' | 'missing' | 'object' | 'array' | 'minimum';

export function valueError(value: unknown, rules: ValueRules): ValueErrorCode | null {
  if (value === undefined || value === null) return null;
  if (isExpression(value)) return rules.allowExpression === false ? 'expression' : null;
  switch (rules.kind) {
    case 'string':
      return null;
    case 'number':
    case 'integer':
      if (typeof value !== 'number') return 'number';
      if (rules.kind === 'integer' && !Number.isInteger(value)) return 'integer';
      return rules.minimum !== undefined && value < rules.minimum ? 'minimum' : null;
    case 'boolean':
      return typeof value === 'boolean' ? null : 'boolean';
    case 'enum': {
      const key = JSON.stringify(value);
      if ((rules.missing ?? []).some((o) => JSON.stringify(o) === key)) return 'missing';
      return (rules.options ?? []).some((o) => JSON.stringify(o) === key) ? null : 'enum';
    }
    case 'json':
      if (rules.jsonType === 'object') return isPlainObject(value) ? null : 'object';
      if (rules.jsonType === 'array') return Array.isArray(value) ? null : 'array';
      return null;
  }
}

export function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Текст помилки для користувача. */
export function valueErrorMessage(code: string, rules?: ValueRules): string {
  switch (code) {
    case 'required':
      return $localize`:@@nodeform.error.required:Обов'язкове поле`;
    case 'expression':
      return $localize`:@@nodeform.error.expression:Вираз {{ }} тут не підтримується`;
    case 'number':
      return $localize`:@@nodeform.error.number:Очікується число або вираз {{ }}`;
    case 'integer':
      return $localize`:@@nodeform.error.integer:Очікується ціле число`;
    case 'boolean':
      return $localize`:@@nodeform.error.boolean:Очікується true, false або вираз {{ }}`;
    case 'enum':
      return $localize`:@@nodeform.error.enum:Значення не з переліку`;
    case 'missing':
      return $localize`:@@nodeform.error.missing:Не знайдено: облікові дані видалено або недоступні`;
    case 'object':
      return $localize`:@@nodeform.error.object:Очікується JSON-об'єкт або вираз {{ }}`;
    case 'array':
      return $localize`:@@nodeform.error.array:Очікується JSON-масив або вираз {{ }}`;
    case 'minimum':
      return $localize`:@@nodeform.error.minimum:Мінімум — ${rules?.minimum}:minimum:`;
    default:
      return $localize`:@@nodeform.error.invalid:Некоректне значення`;
  }
}
