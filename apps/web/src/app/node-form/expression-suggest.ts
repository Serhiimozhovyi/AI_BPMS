import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { isPlainObject } from './node-value';

/**
 * Підказки в полі виразу `{{ }}` (F2-19): змінні з docs/workflow-format.md і `OpenBP.Expressions`
 * (`$json`, `$binary`, `$input`, `$node["…"]`, `$now`, `$env`, `$execution`), назви вузлів сценарію і поля з останнього
 * тестового output. Без Angular — чисті функції.
 */

/** Що відомо полю виразу про сценарій. */
export interface ExpressionContext {
  /** Інші вузли сценарію (для `$node["…"]`) і перший item їхнього останнього output, якщо є. */
  nodes: readonly { name: string; sample?: unknown }[];
  /** `json` першого вхідного item вузла (для `$json` і `$input`), якщо відомий. */
  input?: unknown;
  /** Назви змінних середовища (`$env.…`), якщо відомі. */
  env?: readonly string[];
}

export const EMPTY_EXPRESSION_CONTEXT: ExpressionContext = { nodes: [] };

export type SuggestionKind = 'variable' | 'node' | 'field' | 'method';

export interface Suggestion {
  /** Що показати у списку. */
  label: string;
  /** Текст, яким замінюється діапазон `from..to`. */
  insert: string;
  kind: SuggestionKind;
  /** Тип значення чи коротке пояснення. */
  detail?: string;
}

export interface SuggestResult {
  /** Діапазон тексту, який замінює підказка. */
  from: number;
  to: number;
  items: Suggestion[];
}

/** Кореневі змінні виразу. */
export const ROOT_VARIABLES: readonly { name: string; detail: string }[] = [
  { name: '$json', detail: $localize`:@@expr.var.json:поточний item` },
  { name: '$binary', detail: $localize`:@@expr.var.binary:файли поточного item` },
  { name: '$input', detail: $localize`:@@expr.var.input:вхідні items` },
  { name: '$node', detail: $localize`:@@expr.var.node:output вузла за назвою` },
  { name: '$now', detail: $localize`:@@expr.var.now:поточний час (Date)` },
  { name: '$env', detail: $localize`:@@expr.var.env:змінні середовища` },
  { name: '$execution', detail: $localize`:@@expr.var.execution:поточне виконання` },
];

const INPUT_MEMBERS = ['item', 'all()', 'first()', 'last()'];
const ITEM_MEMBERS = ['json', 'binary'];
const EXECUTION_MEMBERS = ['id'];
const NOW_MEMBERS = ['toISOString()', 'getTime()', 'getFullYear()', 'getMonth()', 'getDate()', 'getHours()', 'getMinutes()', 'getDay()'];

/** Максимум підказок у списку. */
const LIMIT = 50;

/**
 * Підказки для позиції курсора. `null` — курсор не у виразі `{{ }}` або на місці, де нічого підказати
 * (напр. після `]` чи `()`).
 */
export function suggestExpression(text: string, cursor: number, ctx: ExpressionContext): SuggestResult | null {
  const before = text.slice(0, Math.max(0, Math.min(cursor, text.length)));
  const open = before.lastIndexOf('{{');
  if (open < 0 || before.lastIndexOf('}}') > open) return null;
  const body = before.slice(open + 2);
  const offset = open + 2;

  // Посилання — від останнього `$`, з якого розбирається весь хвіст (`$` усередині рядка в дужках не підходить).
  for (let i = body.lastIndexOf('$'); i >= 0; i = i === 0 ? -1 : body.lastIndexOf('$', i - 1)) {
    if (i > 0 && /[\w$]/.test(body[i - 1])) continue;
    const ref = parseReference(body.slice(i));
    if (!ref) continue;
    const items = candidates(ref, ctx);
    if (items === null) return null;
    const result = items
      .filter((c) => matches(c.name, ref.partial.prefix))
      .slice(0, LIMIT)
      .map((c) => ({ label: c.label ?? c.name, insert: c.insert ?? accessor(c.name, ref.partial.kind), kind: c.kind, detail: c.detail }));
    return result.length > 0 ? { from: offset + i + ref.partial.start, to: before.length, items: result } : null;
  }
  return null;
}

/** Текст після вибору підказки і позиція курсора за вставкою. */
export function applySuggestion(text: string, result: SuggestResult, suggestion: Suggestion): { text: string; cursor: number } {
  return {
    text: text.slice(0, result.from) + suggestion.insert + text.slice(result.to),
    cursor: result.from + suggestion.insert.length,
  };
}

/**
 * Контекст для вузла `nodeId`: інші вузли, вхід — перший item output вузла з першого вхідного ребра.
 * `outputs` — items (`{ json }`) за id вузла: останній тестовий output (поки — `pinData` сценарію).
 */
export function expressionContext(
  definition: WorkflowDefinition,
  nodeId: string,
  outputs: Readonly<Record<string, readonly unknown[] | undefined>>,
  env?: readonly string[],
): ExpressionContext {
  const sample = (id: string): unknown => {
    const items = outputs[id];
    const first: unknown = Array.isArray(items) ? items[0] : undefined;
    return isPlainObject(first) ? first['json'] : undefined;
  };
  const incoming = definition.connections
    .filter((c) => c.to.node === nodeId)
    .sort((a, b) => a.to.input - b.to.input);
  const withData = incoming.find((c) => sample(c.from.node) !== undefined) ?? incoming[0];
  return {
    nodes: definition.nodes.filter((n) => n.id !== nodeId).map((n) => ({ name: n.name, sample: sample(n.id) })),
    input: withData ? sample(withData.from.node) : undefined,
    env,
  };
}

// ---------- розбір посилання ----------

/** Недописана частина в кінці посилання. */
interface PartialStep {
  kind: 'root' | 'dot' | 'bracket';
  /** Те, що вже введено (без лапок). */
  prefix: string;
  /** Початок частини, яку замінює підказка, відносно `$`: `$`, крапка або `[`; `-1` — посилання завершене. */
  start: number;
}

interface Reference {
  root: string;
  /** Повністю введені кроки шляху: ключі, індекси (`"0"`), виклики (`"all()"`). */
  path: string[];
  partial: PartialStep;
}

const IDENT_START = /[A-Za-z_$]/;
const IDENT = /[\w$]/;

/** Розбирає `$root(.key|["key"]|[0]|.fn())*` з недописаним кроком у кінці; інакше `null`. */
function parseReference(s: string): Reference | null {
  let i = 1;
  while (i < s.length && /[A-Za-z_]/.test(s[i])) i++;
  const root = s.slice(0, i);
  if (i === s.length) return { root: '$', path: [], partial: { kind: 'root', prefix: root, start: 0 } };
  if (!ROOT_VARIABLES.some((v) => v.name === root)) return null;

  const path: string[] = [];
  while (i < s.length) {
    if (s[i] === '.') {
      const dot = i;
      let j = i + 1;
      if (j < s.length && !IDENT_START.test(s[j])) return null;
      while (j < s.length && IDENT.test(s[j])) j++;
      const name = s.slice(dot + 1, j);
      if (j === s.length) return { root, path, partial: { kind: 'dot', prefix: name, start: dot } };
      if (s.startsWith('()', j)) {
        path.push(`${name}()`);
        j += 2;
      } else {
        path.push(name);
      }
      i = j;
    } else if (s[i] === '[') {
      const open = i;
      let j = i + 1;
      while (j < s.length && s[j] === ' ') j++;
      if (j === s.length) return { root, path, partial: { kind: 'bracket', prefix: '', start: open } };
      const quote = s[j];
      let key: string;
      if (quote === '"' || quote === "'") {
        let k = j + 1;
        let value = '';
        while (k < s.length && s[k] !== quote) {
          if (s[k] === '\\' && k + 1 < s.length) k++;
          value += s[k];
          k++;
        }
        if (k >= s.length) return { root, path, partial: { kind: 'bracket', prefix: value, start: open } };
        key = value;
        j = k + 1;
      } else if (/\d/.test(quote)) {
        let k = j;
        while (k < s.length && /\d/.test(s[k])) k++;
        key = s.slice(j, k);
        j = k;
      } else {
        return null;
      }
      while (j < s.length && s[j] === ' ') j++;
      if (s[j] !== ']') return null;
      path.push(key);
      i = j + 1;
    } else {
      return null;
    }
  }
  // Посилання завершене (`]` чи `()` у кінці) — підказувати нічого.
  return { root, path, partial: { kind: 'dot', prefix: '', start: -1 } };
}

// ---------- кандидати ----------

interface Candidate {
  name: string;
  kind: SuggestionKind;
  detail?: string;
  label?: string;
  insert?: string;
}

/** Кандидати для посилання; `null` — нічого підказати. */
function candidates(ref: Reference, ctx: ExpressionContext): Candidate[] | null {
  const { root, path, partial } = ref;
  if (partial.start < 0) return null;
  if (partial.kind === 'root') {
    return ROOT_VARIABLES.map((v) => ({ name: v.name, kind: 'variable', detail: v.detail, insert: v.name }));
  }

  switch (root) {
    case '$json':
      return fields(at(ctx.input, path));
    case '$node': {
      if (path.length === 0) {
        if (partial.kind !== 'bracket') return null;
        return ctx.nodes.map((n) => ({ name: n.name, kind: 'node', label: n.name, insert: `[${JSON.stringify(n.name)}]` }));
      }
      const node = ctx.nodes.find((n) => n.name === path[0]);
      if (!node) return null;
      if (path.length === 1) return members(ITEM_MEMBERS);
      return path[1] === 'json' ? fields(at(node.sample, path.slice(2))) : null;
    }
    case '$input': {
      if (path.length === 0) return members(INPUT_MEMBERS);
      // `$input.item` / `.first()` / `.last()` / `.all()[i]` → item.
      const itemSteps = path[0] === 'all()' ? (path.length > 1 && /^\d+$/.test(path[1]) ? 2 : -1) : ['item', 'first()', 'last()'].includes(path[0]) ? 1 : -1;
      if (itemSteps < 0 || path.length < itemSteps) return null;
      if (path.length === itemSteps) return members(ITEM_MEMBERS);
      return path[itemSteps] === 'json' ? fields(at(ctx.input, path.slice(itemSteps + 1))) : null;
    }
    case '$execution':
      return path.length === 0 ? members(EXECUTION_MEMBERS) : null;
    case '$now':
      return path.length === 0 && partial.kind === 'dot' ? members(NOW_MEMBERS, 'method') : null;
    case '$env':
      return path.length === 0 ? (ctx.env ?? []).map((name) => ({ name, kind: 'field', detail: 'string' })) : null;
    default:
      return null;
  }
}

function members(names: readonly string[], kind: SuggestionKind = 'variable'): Candidate[] {
  return names.map((name) => ({ name, kind }));
}

/** Ключі об'єкта з типом значення. */
function fields(value: unknown): Candidate[] | null {
  if (!isPlainObject(value)) return null;
  return Object.entries(value).map(([name, v]) => ({ name, kind: 'field', detail: typeName(v) }));
}

/** Значення за шляхом зі зразка; не знайдено — `undefined`. */
function at(value: unknown, path: readonly string[]): unknown {
  let current = value;
  for (const step of path) {
    if (Array.isArray(current) && /^\d+$/.test(step)) current = current[Number(step)];
    else if (isPlainObject(current) && Object.prototype.hasOwnProperty.call(current, step)) current = current[step];
    else return undefined;
  }
  return current;
}

function typeName(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

/** Як вставити крок: `.name` або `["name"]` (ключ не ідентифікатор чи введено `[`). */
function accessor(name: string, kind: PartialStep['kind']): string {
  const plain = /^[A-Za-z_$][\w$]*(\(\))?$/.test(name);
  return kind === 'dot' && plain ? `.${name}` : `[${JSON.stringify(name)}]`;
}

function matches(name: string, prefix: string): boolean {
  return prefix === '' || name.toLowerCase().startsWith(prefix.toLowerCase());
}
