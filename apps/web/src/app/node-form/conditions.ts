import { expressionFor } from '../canvas/data-schema';
import { isPlainObject } from './node-value';

/**
 * Конструктор умов і таблиця присвоєнь (F3-53): впізнавання форм схеми `core.if`/`core.switch`/`core.set`, підписи
 * операторів і присвоєння з полів входу. Без Angular — чисті функції; значення в сценарії не змінюють формат
 * (docs/workflow-format.md).
 */

export type JsonSchema = Record<string, unknown>;

/** Оператори, яким не потрібне праве значення: поле «порівняти з» ховається (значення в моделі лишається). */
export const UNARY_OPERATORS: readonly string[] = ['isEmpty', 'isNotEmpty', 'isTrue', 'isFalse'];

/** Підписи операторів умов (`ConditionEvaluator`) українською; невідомий оператор показується як є. */
export function operatorLabel(operator: string): string {
  switch (operator) {
    case 'equals':
      return $localize`:@@cond.op.equals:дорівнює`;
    case 'notEquals':
      return $localize`:@@cond.op.notEquals:не дорівнює`;
    case 'gt':
      return $localize`:@@cond.op.gt:більше`;
    case 'gte':
      return $localize`:@@cond.op.gte:більше або дорівнює`;
    case 'lt':
      return $localize`:@@cond.op.lt:менше`;
    case 'lte':
      return $localize`:@@cond.op.lte:менше або дорівнює`;
    case 'contains':
      return $localize`:@@cond.op.contains:містить`;
    case 'notContains':
      return $localize`:@@cond.op.notContains:не містить`;
    case 'startsWith':
      return $localize`:@@cond.op.startsWith:починається з`;
    case 'endsWith':
      return $localize`:@@cond.op.endsWith:закінчується на`;
    case 'isEmpty':
      return $localize`:@@cond.op.isEmpty:порожнє`;
    case 'isNotEmpty':
      return $localize`:@@cond.op.isNotEmpty:не порожнє`;
    case 'isTrue':
      return $localize`:@@cond.op.isTrue:істина`;
    case 'isFalse':
      return $localize`:@@cond.op.isFalse:хиба`;
    default:
      return operator;
  }
}

/** Праве значення не потрібне. */
export function isUnary(operator: unknown): boolean {
  return typeof operator === 'string' && UNARY_OPERATORS.includes(operator);
}

function properties(schema: JsonSchema): Record<string, unknown> {
  return isPlainObject(schema['properties']) ? schema['properties'] : {};
}

/** Умова: об'єкт з `left` і `operator`-переліком (`$defs.condition` `core.if`/`core.switch`). */
export function isConditionSchema(schema: JsonSchema): boolean {
  const props = properties(schema);
  const operator = props['operator'];
  return 'left' in props && isPlainObject(operator) && Array.isArray(operator['enum']) && operator['enum'].includes('equals');
}

/** Правило `core.switch`: об'єкт з масивом `conditions`. */
export function isRuleSchema(schema: JsonSchema): boolean {
  const conditions = properties(schema)['conditions'];
  return isPlainObject(conditions) && conditions['type'] === 'array';
}

/** Присвоєння `core.set`: об'єкт з `name` і `value`. */
export function isAssignmentSchema(schema: JsonSchema): boolean {
  const props = properties(schema);
  return Object.keys(props).length === 2 && 'name' in props && 'value' in props;
}

/** Перемикач «усі / будь-яка»: перелік рівно з `and` і `or`. */
export function isCombineSchema(schema: JsonSchema): boolean {
  const values = schema['enum'];
  return Array.isArray(values) && values.length === 2 && values.includes('and') && values.includes('or');
}

/** Присвоєння `{ name, value }`. */
export interface Assignment {
  name: string;
  value: unknown;
}

/**
 * «Додати всі поля зі входу»: присвоєння `поле = {{ $json.поле }}` для полів верхнього рівня першого вхідного item,
 * яких ще немає серед `existing` (порядок — як у вході).
 */
export function assignmentsFromInput(input: unknown, existing: readonly unknown[]): Assignment[] {
  if (!isPlainObject(input)) return [];
  const taken = new Set(existing.map((a) => (isPlainObject(a) ? a['name'] : undefined)).filter((n): n is string => typeof n === 'string'));
  return Object.keys(input)
    .filter((key) => !taken.has(key))
    .map((key) => ({ name: key, value: expressionFor([key]) }));
}

/** Імена полів входу для автодоповнення назви присвоєння: починаються з введеного (без регістру), крім точного збігу. */
export function inputFieldNames(input: unknown, typed: string): string[] {
  if (!isPlainObject(input)) return [];
  const prefix = typed.trim().toLowerCase();
  return Object.keys(input).filter((key) => key.toLowerCase().startsWith(prefix) && key !== typed);
}
