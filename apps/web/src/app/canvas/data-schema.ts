import { ExecutionItem } from '../executions/executions-api.service';
import { isPlainObject } from '../node-form/node-value';

/**
 * Дані вузла в панелі «Вхід | Параметри | Вихід» (F3-52): схема полів з items, таблиця, вираз `{{ }}` для шляху поля
 * і вставка в позицію курсора. Без Angular — чисті функції.
 */

/** Сегмент шляху до поля: ключ об'єкта або індекс масиву. */
export type PathSegment = string | number;

export type FieldType = 'string' | 'number' | 'boolean' | 'null' | 'object' | 'array';

/** Поле схеми: шлях від `json` item, типи серед усіх items і перший приклад значення. */
export interface SchemaField {
  key: string;
  path: PathSegment[];
  /** Типи значення в різних items, у порядку появи; більше одного — поле має різні типи. */
  types: FieldType[];
  /** Приклад простого значення (перший, що трапився); для об'єктів і масивів — `undefined`. */
  sample?: unknown;
  children: SchemaField[];
}

/** Схема: поля `json` усіх items, злиті за ключами (порядок — перша поява). */
export function buildSchema(items: readonly ExecutionItem[]): SchemaField[] {
  const root = newField('', []);
  for (const item of items) merge(root, item?.json ?? {});
  return root.children;
}

function newField(key: string, path: PathSegment[]): SchemaField {
  return { key, path, types: [], children: [] };
}

function typeOf(value: unknown): FieldType {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  if (isPlainObject(value)) return 'object';
  if (typeof value === 'number' || typeof value === 'bigint') return 'number';
  if (typeof value === 'boolean') return 'boolean';
  return 'string';
}

function merge(field: SchemaField, value: unknown): void {
  const type = typeOf(value);
  if (!field.types.includes(type)) field.types.push(type);
  if (type === 'object') {
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) merge(childField(field, key), child);
  } else if (type === 'array') {
    // Елементи масиву зливаються в одне поле `[0]` — шлях до першого елемента.
    const elements = value as unknown[];
    if (elements.length > 0) {
      const element = childField(field, 0);
      for (const e of elements) merge(element, e);
    }
  } else if (field.sample === undefined && type !== 'null') {
    field.sample = value;
  }
}

function childField(parent: SchemaField, key: PathSegment): SchemaField {
  const name = typeof key === 'number' ? `[${key}]` : key;
  let child = parent.children.find((c) => c.path.at(-1) === key);
  if (!child) {
    child = newField(name, [...parent.path, key]);
    parent.children.push(child);
  }
  return child;
}

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;

/** Звертання до сегмента шляху: `.key`, `["ключ"]` для нелатинських і незвичних ключів, `[n]` для індексів. */
export function accessorPath(path: readonly PathSegment[]): string {
  return path.map((s) => (typeof s === 'number' ? `[${s}]` : IDENTIFIER.test(s) ? `.${s}` : `[${JSON.stringify(s)}]`)).join('');
}

/**
 * Вираз для поля: `{{ $json.шлях }}` — поле поточного вхідного item; з `nodeName` — `{{ $node["Назва"].json.шлях }}`
 * (output іншого вузла).
 */
export function expressionFor(path: readonly PathSegment[], nodeName?: string): string {
  const base = nodeName === undefined ? '$json' : `$node[${JSON.stringify(nodeName)}].json`;
  return `{{ ${base}${accessorPath(path)} }}`;
}

/** Текст після вставки `insert` замість діапазону `start..end` і позиція курсора за вставкою. */
export function insertAt(text: string, start: number, end: number, insert: string): { text: string; cursor: number } {
  const from = clampIndex(Math.min(start, end), text.length);
  const to = clampIndex(Math.max(start, end), text.length);
  return { text: text.slice(0, from) + insert + text.slice(to), cursor: from + insert.length };
}

function clampIndex(value: number, length: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.min(Math.trunc(value), length)) : length;
}

/** Таблиця items: колонки — ключі верхнього рівня `json` (перша поява), клітинки — короткий текст значення. */
export interface ItemsTable {
  columns: string[];
  rows: { cells: TableCell[] }[];
}

export interface TableCell {
  text: string;
  /** Вкладений об'єкт чи масив: показується згорнутим, повний JSON — у підказці. */
  nested: boolean;
  /** Повне значення як JSON (для підказки). */
  title: string;
}

export function itemsTable(items: readonly ExecutionItem[]): ItemsTable {
  const columns: string[] = [];
  for (const item of items) {
    const json = isPlainObject(item?.json) ? item.json : {};
    for (const key of Object.keys(json)) if (!columns.includes(key)) columns.push(key);
  }
  return {
    columns,
    rows: items.map((item) => {
      const json: Record<string, unknown> = isPlainObject(item?.json) ? item.json : {};
      return { cells: columns.map((key) => cell(Object.hasOwn(json, key) ? json[key] : undefined)) };
    }),
  };
}

function cell(value: unknown): TableCell {
  if (value === undefined) return { text: '', nested: false, title: '' };
  const title = JSON.stringify(value, null, 2) ?? '';
  if (Array.isArray(value)) return { text: `[${value.length}]`, nested: true, title };
  if (isPlainObject(value)) return { text: `{${Object.keys(value).length}}`, nested: true, title };
  return { text: typeof value === 'string' ? value : JSON.stringify(value), nested: false, title };
}

/** Короткий приклад значення для схеми (рядки — в лапках, довгі — обрізані). */
export function sampleText(value: unknown, max = 40): string {
  if (value === undefined) return '';
  const text = typeof value === 'string' ? JSON.stringify(value) : String(value);
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}
