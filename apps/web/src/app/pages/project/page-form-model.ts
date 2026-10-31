import type { JsonSchema } from '../../forms/form-fields';
import { TableColumn, fieldOrder, fromSchema, propertyForColumn, toSchema } from '../../forms/designer/form-designer.model';
import { LAYOUT_KEY, UI_KEY, writeLayout } from '../../forms/form-layout';
import type { FormLayout } from '../../forms/form-layout';
import type { PageDefinition, PageField } from '../../projects/project-pages-api.service';

/**
 * Поля форми сторінки застосунку ↔ схема для конструктора (F11-05). Конструктор бачить лише поля, які вводить
 * користувач: поля з `value` (ставить сервер) і `copyFrom` (копія з пов'язаного рядка) на формі не показуються й
 * редагуються окремим списком «Приховані поля» — їх конструктор не чіпає.
 */

/** Поле не показується на формі: значення ставить сервер. */
export function isHiddenField(field: PageField): boolean {
  return field.value !== undefined || field.copyFrom !== undefined;
}

function sameName(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

/** Схема форми для конструктора з визначення сторінки: властивість на кожне видиме поле, тип — за колонкою. */
export function designerSchema(def: Pick<PageDefinition, 'fields' | 'formLayout'>, columns: readonly TableColumn[]): JsonSchema {
  const properties: Record<string, JsonSchema> = {};
  const required: string[] = [];
  for (const field of def.fields ?? []) {
    if (isHiddenField(field)) continue;
    const column = columns.find((c) => sameName(c.name, field.column)) ?? { name: field.column, type: 'string' };
    const property = propertyForColumn(column, field.label || field.column);
    if (field.description) property['description'] = field.description;
    if (field.ui && Object.keys(field.ui).length) property[UI_KEY] = { ...field.ui };
    properties[field.column] = property;
    if (field.required) required.push(field.column);
  }
  const schema: JsonSchema = { type: 'object', properties, ...(required.length ? { required } : {}) };
  return def.formLayout ? writeLayout(schema, def.formLayout as FormLayout) : schema;
}

/** Результат конструктора для визначення сторінки: поля (видимі за розміткою, далі приховані) і розмітка. */
export interface FormFromDesigner {
  fields: PageField[];
  formLayout?: FormLayout;
}

/**
 * Схема конструктора → `fields[]` і `formLayout` сторінки. Параметри поля, яких конструктор не знає (`format`, `display`…),
 * беруться з `base`; підпис (`label`), підказка, `required` і `ui` — зі схеми. `hidden` — поля, які конструктор не показує.
 */
export function fromDesignerSchema(schema: JsonSchema, base: readonly PageField[], hidden: readonly PageField[]): FormFromDesigner {
  const state = fromSchema(schema);
  const out = toSchema(state);
  const fields = fieldOrder(state).map((column): PageField => {
    const property = state.properties[column] ?? {};
    const field: PageField = { ...(base.find((f) => f.column === column && !isHiddenField(f)) ?? { column }) };
    delete field.label;
    delete field.description;
    delete field.required;
    delete field.ui;
    const title = property['title'];
    if (typeof title === 'string' && title.trim() && title !== column) field.label = title;
    const description = property['description'];
    if (typeof description === 'string' && description.trim()) field.description = description;
    if (state.required.includes(column)) field.required = true;
    const ui = property[UI_KEY];
    if (typeof ui === 'object' && ui !== null && Object.keys(ui).length) field.ui = ui as PageField['ui'];
    return field;
  });
  const layout = out[LAYOUT_KEY];
  return { fields: [...fields, ...hidden], ...(layout ? { formLayout: layout as FormLayout } : {}) };
}

/** Рядок списку «Приховані поля». */
export interface HiddenRow {
  column: string;
  mode: 'value' | 'copy';
  /** `value`: вираз чи значення; `copy`: `<колонка ref>.<колонка>`. */
  text: string;
}

export function hiddenRows(fields: readonly PageField[] | undefined): HiddenRow[] {
  return (fields ?? []).filter(isHiddenField).map((f) =>
    f.copyFrom !== undefined
      ? { column: f.column, mode: 'copy', text: f.copyFrom }
      : { column: f.column, mode: 'value', text: typeof f.value === 'string' ? f.value : JSON.stringify(f.value) },
  );
}

/** Текст значення → значення поля: числа, `true/false/null`, масиви й об'єкти — як JSON; вирази `{{ }}` і решта — рядком. */
export function parseHiddenValue(text: string): unknown {
  const trimmed = text.trim();
  if (trimmed.startsWith('{{') || !/^(-?\d|true$|false$|null$|\[|\{)/.test(trimmed)) return text;
  try {
    return JSON.parse(trimmed);
  } catch {
    return text;
  }
}

/** Приховані поля сторінки з рядків списку; параметри, яких список не показує (`display`…), беруться з `base`. */
export function hiddenFields(rows: readonly HiddenRow[], base: readonly PageField[]): PageField[] {
  return rows
    .filter((r) => r.column)
    .map((r): PageField => {
      const kept = base.find((f) => f.column === r.column && isHiddenField(f));
      const field: PageField = { ...(kept ?? { column: r.column }) };
      delete field.value;
      delete field.copyFrom;
      if (r.mode === 'copy') field.copyFrom = r.text;
      else field.value = parseHiddenValue(r.text);
      return field;
    });
}
