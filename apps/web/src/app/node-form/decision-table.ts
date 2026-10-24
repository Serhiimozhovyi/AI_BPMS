import { isPlainObject } from './node-value';

/**
 * Таблиця рішень `core.decisionTable` (F6-19): чисті функції табличного редактора. Рядки — правила, колонки — умови на
 * входи (`when[i]`: `"-"` або `{ operator, right }`) і значення виходів (`then[j]`). Формат параметрів — docs/workflow-format.md.
 */

/** Комірка «будь-яке значення». */
export const ANY_CELL = '-';

/** Умова комірки. */
export interface DecisionCell {
  operator: string;
  right?: unknown;
}

/** Правило таблиці. */
export interface DecisionRule {
  when: (DecisionCell | typeof ANY_CELL)[];
  then: unknown[];
}

/** Масив правил: схема `items` з властивостями `when` і `then`. */
export function isDecisionRulesSchema(items: Record<string, unknown>): boolean {
  const props = isPlainObject(items['properties']) ? items['properties'] : {};
  return 'when' in props && 'then' in props;
}

/** Назви колонок з `inputs`/`outputs` (`[{ name }]`); безіменна — `#номер`. */
export function columnNames(list: unknown): string[] {
  return (Array.isArray(list) ? list : []).map((entry, i) => {
    const name = isPlainObject(entry) ? entry['name'] : undefined;
    return typeof name === 'string' && name.trim() ? name : `#${i + 1}`;
  });
}

/** Правила з моделі, доповнені до кількості входів (`"-"`) і виходів (`undefined`); зайві комірки лишаються. */
export function normalizeRules(value: unknown, inputs: number, outputs: number): DecisionRule[] {
  return (Array.isArray(value) ? value : []).map((raw) => {
    const rule = isPlainObject(raw) ? raw : {};
    const when = (Array.isArray(rule['when']) ? rule['when'] : []).map(toCell);
    const then = Array.isArray(rule['then']) ? [...(rule['then'] as unknown[])] : [];
    while (when.length < inputs) when.push(ANY_CELL);
    while (then.length < outputs) then.push(undefined);
    return { when, then };
  });
}

function toCell(raw: unknown): DecisionCell | typeof ANY_CELL {
  if (isPlainObject(raw) && typeof raw['operator'] === 'string') {
    return 'right' in raw ? { operator: raw['operator'], right: raw['right'] } : { operator: raw['operator'] };
  }
  return ANY_CELL;
}

/** Нове правило: «будь-яке» на всіх входах і порожні виходи. */
export function emptyRule(inputs: number, outputs: number): DecisionRule {
  return { when: Array.from({ length: inputs }, () => ANY_CELL), then: Array.from({ length: outputs }, () => undefined) };
}

/** Інший оператор комірки (`"-"` — будь-яке); праве значення зберігається. */
export function withOperator(cell: DecisionCell | typeof ANY_CELL, operator: string): DecisionCell | typeof ANY_CELL {
  if (operator === ANY_CELL) return ANY_CELL;
  return cell === ANY_CELL ? { operator } : { ...cell, operator };
}

/** Інше праве значення комірки; для «будь-яке» — умова «дорівнює». */
export function withRight(cell: DecisionCell | typeof ANY_CELL, right: unknown): DecisionCell {
  const base = cell === ANY_CELL ? { operator: 'equals' } : cell;
  if (right === undefined) {
    const { operator } = base;
    return { operator };
  }
  return { ...base, right };
}

/** Копія правил з заміною одного правила. */
export function replaceRule(rules: readonly DecisionRule[], index: number, rule: DecisionRule): DecisionRule[] {
  return rules.map((r, i) => (i === index ? rule : r));
}

/** Переміщує правило на `delta` позицій (порядок важливий для `first`); за межами — без змін. */
export function moveRule(rules: readonly DecisionRule[], index: number, delta: number): DecisionRule[] {
  const target = index + delta;
  if (target < 0 || target >= rules.length) return [...rules];
  const copy = [...rules];
  [copy[index], copy[target]] = [copy[target], copy[index]];
  return copy;
}

/** Правила для збереження: `undefined` у `then` стає `null` (JSON-масив без дірок). */
export function serializeRules(rules: readonly DecisionRule[]): DecisionRule[] {
  return rules.map((r) => ({ when: r.when, then: r.then.map((v) => (v === undefined ? null : v)) }));
}

/** Оператори комірки — ті самі, що в `core.if` (`ConditionEvaluator`). */
export const CELL_OPERATORS: readonly string[] = [
  'equals',
  'notEquals',
  'gt',
  'gte',
  'lt',
  'lte',
  'contains',
  'notContains',
  'startsWith',
  'endsWith',
  'isEmpty',
  'isNotEmpty',
  'isTrue',
  'isFalse',
];
