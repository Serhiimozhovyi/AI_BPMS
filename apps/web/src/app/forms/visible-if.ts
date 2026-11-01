import type { JsonSchema } from './form-fields';
import { hasLayout, readLayout, type VisibleIf } from './form-layout';

/**
 * Умова «Показувати, якщо» (F11-06, docs/forms-designer.md §3.1). Та сама семантика на сервері —
 * `OpenBP.Application/Forms/VisibleIf.cs`; обидві реалізації перевіряє одна таблиця кейсів `visible-if.cases.json`.
 *
 * - Порожнє значення: немає, `null`, порожній рядок, порожній масив (`0` і `false` — не порожні).
 * - `eq`/`ne`: два числа (або рядки-числа) порівнюються як числа, решта — як текст із урахуванням регістру;
 *   масив (кілька варіантів) дорівнює значенню, якщо містить його. Порожнє поле не дорівнює нічому.
 * - `contains`: масив — містить значення; текст — містить підрядок без урахування регістру.
 * - `gt`/`lt`: числа — як числа, інакше як текст (дати `YYYY-MM-DD` порівнюються правильно); порожнє — ні.
 * - Невідомий оператор — поле показується (валідатор схеми таких не пропускає).
 */

const NUMBER = /^-?\d+(\.\d+)?$/;

/** Чи значення порожнє. */
export function isEmptyValue(value: unknown): boolean {
  return value === undefined || value === null || value === '' || (Array.isArray(value) && value.length === 0);
}

function asNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && NUMBER.test(value.trim())) return Number(value.trim());
  return null;
}

function asText(value: unknown): string | null {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return null;
}

function equals(a: unknown, b: unknown): boolean {
  const x = asNumber(a);
  const y = asNumber(b);
  if (x !== null && y !== null) return x === y;
  const s = asText(a);
  const t = asText(b);
  return s !== null && t !== null && s === t;
}

function compare(a: unknown, b: unknown): number | null {
  const x = asNumber(a);
  const y = asNumber(b);
  if (x !== null && y !== null) return x === y ? 0 : x < y ? -1 : 1;
  const s = asText(a);
  const t = asText(b);
  if (s === null || t === null) return null;
  return s === t ? 0 : s < t ? -1 : 1;
}

/** Чи виконується умова для значень форми. */
export function isVisible(rule: VisibleIf, values: Record<string, unknown>): boolean {
  const value = values[rule.field];
  const empty = isEmptyValue(value);
  switch (rule.operator) {
    case 'isEmpty':
      return empty;
    case 'isNotEmpty':
      return !empty;
    case 'eq':
    case 'ne': {
      const same = !empty && (Array.isArray(value) ? value.some((v) => equals(v, rule.value)) : equals(value, rule.value));
      return rule.operator === 'eq' ? same : !same;
    }
    case 'contains': {
      if (empty || isEmptyValue(rule.value)) return false;
      if (Array.isArray(value)) return value.some((v) => equals(v, rule.value));
      const text = asText(value);
      const part = asText(rule.value);
      return text !== null && part !== null && text.toLowerCase().includes(part.toLowerCase());
    }
    case 'gt':
    case 'lt': {
      if (empty || isEmptyValue(rule.value) || Array.isArray(value)) return false;
      const order = compare(value, rule.value);
      return order !== null && (rule.operator === 'gt' ? order > 0 : order < 0);
    }
    default:
      return true;
  }
}

/** Що приховано умовами: індекси секцій і ключі полів (поля прихованих секцій — теж). */
export interface HiddenState {
  sections: Set<number>;
  fields: Set<string>;
}

/**
 * Приховані секції й поля форми за значеннями. Умови перевіряються в порядку розмітки, і значення вже прихованих
 * полів вважаються порожніми — так ланцюжок «A ховає B, B ховає C» дає той самий результат, що й на сервері.
 */
export function hiddenState(schema: JsonSchema | null | undefined, values: Record<string, unknown>): HiddenState {
  const state: HiddenState = { sections: new Set(), fields: new Set() };
  if (!hasLayout(schema)) return state;
  const effective: Record<string, unknown> = { ...values };
  const hide = (field: string) => {
    state.fields.add(field);
    delete effective[field];
  };
  readLayout(schema).sections.forEach((section, index) => {
    const sectionHidden = !!section.visibleIf && !isVisible(section.visibleIf, effective);
    if (sectionHidden) state.sections.add(index);
    for (const row of section.rows) {
      for (const cell of row) {
        if (sectionHidden || (cell.visibleIf && !isVisible(cell.visibleIf, effective))) hide(cell.field);
      }
    }
  });
  return state;
}

/** Ключі полів, прихованих умовами. */
export function hiddenFields(schema: JsonSchema | null | undefined, values: Record<string, unknown>): Set<string> {
  return hiddenState(schema, values).fields;
}
