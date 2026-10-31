import type { JsonSchema } from './form-fields';

/**
 * Розмітка форми (F11-01, docs/forms-designer.md §3.1): анотація `x-openbp-layout` у JSON Schema форми — секції з
 * рядками, рядок — до 4 полів на сітці з 12 колонок. Дані форми лишаються плоскими (`properties` верхнього рівня),
 * розмітка лише каже, як поля стоять на сторінці. Поля, яких немає в розмітці, стоять у кінці по одному в рядок.
 */

/** Ключ анотації розмітки на корені схеми. */
export const LAYOUT_KEY = 'x-openbp-layout';
/** Ключ підказок показу на властивості. */
export const UI_KEY = 'x-openbp-ui';
/** Скільки колонок у сітці рядка. */
export const GRID_COLUMNS = 12;
/** Найбільше полів в одному рядку. */
export const MAX_ROW_CELLS = 4;

/** Оператори умови «Показувати, якщо» (F11-06). */
export type VisibleIfOperator = 'eq' | 'ne' | 'contains' | 'isEmpty' | 'isNotEmpty' | 'gt' | 'lt';

export interface VisibleIf {
  field: string;
  operator: VisibleIfOperator;
  value?: unknown;
}

/** Клітинка рядка: поле і його ширина в колонках сітки (без ширини — порівну). */
export interface FormCell {
  field: string;
  width?: number;
  visibleIf?: VisibleIf;
}

export interface FormSection {
  id?: string;
  title?: string;
  description?: string;
  collapsible?: boolean;
  collapsed?: boolean;
  visibleIf?: VisibleIf;
  rows: FormCell[][];
}

export interface FormLayout {
  sections: FormSection[];
}

/** Підказки показу поля (`x-openbp-ui`). */
export interface FieldUi {
  widget?: 'radio' | 'chips';
  placeholder?: string;
  rows?: number;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function propertyKeys(schema: JsonSchema | null | undefined): string[] {
  const properties = schema?.['properties'];
  return isObject(properties) ? Object.keys(properties) : [];
}

function readVisibleIf(value: unknown): VisibleIf | undefined {
  if (!isObject(value) || typeof value['field'] !== 'string' || typeof value['operator'] !== 'string') return undefined;
  const rule: VisibleIf = { field: value['field'], operator: value['operator'] as VisibleIfOperator };
  if ('value' in value) rule.value = value['value'];
  return rule;
}

function readWidth(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= GRID_COLUMNS ? value : undefined;
}

/** Чи схема має власну розмітку. */
export function hasLayout(schema: JsonSchema | null | undefined): boolean {
  return isObject(schema?.[LAYOUT_KEY]);
}

/**
 * Розмітка зі схеми. Посилання на поле, якого немає у `properties`, і повтори поля відкидаються, порожні рядки теж;
 * поля без місця в розмітці додаються в кінець останньої секції по одному в рядок. Схема без розмітки — одна секція
 * без заголовка з усіма полями по одному в рядок.
 */
export function readLayout(schema: JsonSchema | null | undefined): FormLayout {
  const keys = propertyKeys(schema);
  const known = new Set(keys);
  const placed = new Set<string>();
  const raw = schema?.[LAYOUT_KEY];
  const sections: FormSection[] = [];

  if (isObject(raw) && Array.isArray(raw['sections'])) {
    for (const s of raw['sections']) {
      if (!isObject(s)) continue;
      const section: FormSection = { rows: [] };
      if (typeof s['id'] === 'string') section.id = s['id'];
      if (typeof s['title'] === 'string') section.title = s['title'];
      if (typeof s['description'] === 'string') section.description = s['description'];
      if (s['collapsible'] === true) section.collapsible = true;
      if (s['collapsed'] === true) section.collapsed = true;
      const visibleIf = readVisibleIf(s['visibleIf']);
      if (visibleIf) section.visibleIf = visibleIf;
      for (const r of Array.isArray(s['rows']) ? s['rows'] : []) {
        const row: FormCell[] = [];
        for (const c of Array.isArray(r) ? r : []) {
          const field = isObject(c) ? c['field'] : undefined;
          if (typeof field !== 'string' || !known.has(field) || placed.has(field) || row.length >= MAX_ROW_CELLS) continue;
          placed.add(field);
          const cell: FormCell = { field };
          const width = readWidth((c as Record<string, unknown>)['width']);
          if (width) cell.width = width;
          const cellIf = readVisibleIf((c as Record<string, unknown>)['visibleIf']);
          if (cellIf) cell.visibleIf = cellIf;
          row.push(cell);
        }
        if (row.length) section.rows.push(row);
      }
      sections.push(section);
    }
  }

  if (!sections.length) sections.push({ rows: [] });
  const last = sections[sections.length - 1];
  for (const key of keys) {
    if (!placed.has(key)) last.rows.push([{ field: key }]);
  }
  return { sections };
}

/** Ширини клітинок рядка в колонках сітки: задані — як є, решта ділить залишок порівну (не менше 1). */
export function rowWidths(row: readonly FormCell[]): number[] {
  const fixed = row.reduce((sum, c) => sum + (c.width ?? 0), 0);
  const free = row.filter((c) => !c.width).length;
  const share = free ? Math.max(1, Math.floor((GRID_COLUMNS - fixed) / free)) : 0;
  return row.map((c) => c.width ?? share);
}

function sectionJson(section: FormSection): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  if (section.id) out['id'] = section.id;
  if (section.title) out['title'] = section.title;
  if (section.description) out['description'] = section.description;
  if (section.collapsible) out['collapsible'] = true;
  if (section.collapsible && section.collapsed) out['collapsed'] = true;
  if (section.visibleIf) out['visibleIf'] = section.visibleIf;
  out['rows'] = section.rows.map((row) =>
    row.map((cell) => {
      const c: Record<string, unknown> = { field: cell.field };
      if (cell.width) c['width'] = cell.width;
      if (cell.visibleIf) c['visibleIf'] = cell.visibleIf;
      return c;
    }),
  );
  return out;
}

/** Чи розмітка нічого не додає до порядку полів: одна секція без налаштувань, кожне поле окремим рядком без умов. */
function isTrivial(layout: FormLayout): boolean {
  if (layout.sections.length !== 1) return false;
  const [s] = layout.sections;
  return !s.title && !s.description && !s.collapsible && !s.visibleIf && s.rows.every((r) => r.length === 1 && !r[0].width && !r[0].visibleIf);
}

/**
 * Нова схема з розміткою: `x-openbp-layout` записано (або прибрано, якщо розмітка тривіальна), ключі `properties`
 * переставлено в порядку розмітки, щоб JSON читався так само, як форма. Решта схеми не змінюється.
 */
export function writeLayout(schema: JsonSchema, layout: FormLayout): JsonSchema {
  const properties = isObject(schema['properties']) ? schema['properties'] : {};
  const ordered: Record<string, unknown> = {};
  for (const section of layout.sections) {
    for (const row of section.rows) {
      for (const cell of row) {
        if (cell.field in properties && !(cell.field in ordered)) ordered[cell.field] = properties[cell.field];
      }
    }
  }
  for (const [key, value] of Object.entries(properties)) {
    if (!(key in ordered)) ordered[key] = value;
  }

  const result: JsonSchema = { ...schema, properties: ordered };
  const order = Object.keys(ordered);
  const trivial = isTrivial(layout) && layout.sections[0].rows.every((r, i) => r[0].field === order[i]);
  if (trivial) {
    delete result[LAYOUT_KEY];
  } else {
    result[LAYOUT_KEY] = { sections: layout.sections.map(sectionJson) };
  }
  return result;
}

/** Підказки показу поля зі схеми властивості. */
export function fieldUi(property: JsonSchema): FieldUi {
  const raw = property[UI_KEY];
  if (!isObject(raw)) return {};
  const ui: FieldUi = {};
  if (raw['widget'] === 'radio' || raw['widget'] === 'chips') ui.widget = raw['widget'];
  if (typeof raw['placeholder'] === 'string') ui.placeholder = raw['placeholder'];
  if (typeof raw['rows'] === 'number' && raw['rows'] >= 1 && raw['rows'] <= 20) ui.rows = Math.round(raw['rows']);
  return ui;
}
