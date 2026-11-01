import { FormlyFieldConfig } from '@ngx-formly/core';
import { fieldUi, hasLayout, readLayout, rowWidths } from './form-layout';
import { PHONE_FORMAT, isPhone, normalizePhone } from './phone';
import { hiddenFields, hiddenState } from './visible-if';

/**
 * JSON Schema полів публічної форми (F6-03) → поля ngx-formly і назад — у тіло надсилання. Підтримується те, що
 * описує `core.formTrigger`/`core.wait` (docs/workflow-format.md): властивості верхнього рівня `string` (формати
 * `email`, `date`, `date-time`, `uri`, `textarea`, `binary` — файл), `number`, `integer`, `boolean`, `enum` (або
 * `oneOf` з `const`/`title`) — список, масив з `items.enum` — кілька значень. `title` — підпис, `description` — підказка,
 * `required`, `minLength`/`maxLength`, `minimum`/`maximum`, `pattern` — перевірки ще до надсилання.
 */

export type JsonSchema = Record<string, unknown>;

/** Типи полів, які реєструє `providePublicForm()`. */
export const PF_INPUT = 'pf-input';
export const PF_TEXTAREA = 'pf-textarea';
export const PF_SELECT = 'pf-select';
export const PF_CHECKBOX = 'pf-checkbox';
export const PF_FILE = 'pf-file';
/** Варіанти кнопками (`x-openbp-ui.widget: "radio"`, F11-07). */
export const PF_RADIO = 'pf-radio';
/** Кілька варіантів чипами (`x-openbp-ui.widget: "chips"`, F11-07). */
export const PF_CHIPS = 'pf-chips';
/** Найбільше варіантів, які показуються кнопками; більше — списком. */
export const RADIO_MAX_OPTIONS = 6;
/** Обгортка секції форми з розміткою (F11-01). */
export const PF_SECTION = 'pf-section';

/** Варіант списку. */
export interface FormOption {
  value: unknown;
  label: string;
}

/** Властивості props, які читають типи полів форми. */
export interface PublicFieldProps {
  label?: string;
  description?: string;
  required?: boolean;
  /** HTML-тип `<input>`: text, email, number, date, datetime-local, url. */
  type?: string;
  options?: FormOption[];
  multiple?: boolean;
  minLength?: number;
  maxLength?: number;
  min?: number;
  max?: number;
  pattern?: string;
  /** Текст-підказка всередині порожнього поля (`x-openbp-ui.placeholder`). */
  placeholder?: string;
  /** Висота багаторядкового поля в рядках (`x-openbp-ui.rows`). */
  rows?: number;
  /** Поле-телефон (`format: "phone"`): маска під час введення, у даних — цифри (F11-07). */
  phone?: boolean;
}

/** Властивості обгортки секції. */
export interface SectionProps {
  label?: string;
  description?: string;
  collapsible?: boolean;
  collapsed?: boolean;
}

/** Значення формату поля-файлу. */
export const BINARY_FORMAT = 'binary';

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function propertiesOf(schema: JsonSchema | null | undefined): [string, JsonSchema][] {
  const properties = schema && isObject(schema['properties']) ? schema['properties'] : {};
  return Object.entries(properties).map(([key, value]) => [key, isObject(value) ? value : {}]);
}

function requiredOf(schema: JsonSchema | null | undefined): Set<string> {
  const required = schema?.['required'];
  return new Set(Array.isArray(required) ? required.filter((r): r is string => typeof r === 'string') : []);
}

function typeOf(property: JsonSchema): string | undefined {
  const type = property['type'];
  if (typeof type === 'string') return type;
  return Array.isArray(type) ? type.find((t): t is string => typeof t === 'string' && t !== 'null') : undefined;
}

function optionsOf(property: JsonSchema): FormOption[] | null {
  if (Array.isArray(property['enum'])) {
    return property['enum'].map((value) => ({ value, label: String(value) }));
  }
  if (Array.isArray(property['oneOf']) && property['oneOf'].every((o) => isObject(o) && 'const' in o)) {
    return (property['oneOf'] as JsonSchema[]).map((o) => ({
      value: o['const'],
      label: typeof o['title'] === 'string' ? o['title'] : String(o['const']),
    }));
  }
  return null;
}

function numberOf(property: JsonSchema, name: string): number | undefined {
  const value = property[name];
  return typeof value === 'number' ? value : undefined;
}

/** Чи властивість — поле-файл (`format: binary`). */
export function isFileProperty(property: JsonSchema): boolean {
  return property['format'] === BINARY_FORMAT;
}

/**
 * Поля форми для об'єктної схеми. Не об'єкт — порожній список. Без розмітки — поля одне під одним; з розміткою
 * `x-openbp-layout` (F11-01) — секції (обгортка `pf-section`) з рядками на сітці з 12 колонок (`pf-row`, `pf-col-N`).
 * Групи без `key`, тож модель форми лишається плоскою.
 */
export function formFields(schema: JsonSchema | null | undefined): FormlyFieldConfig[] {
  const required = requiredOf(schema);
  const properties = new Map(propertiesOf(schema));
  const field = (key: string) => fieldFor(key, properties.get(key) ?? {}, required.has(key));
  if (!hasLayout(schema)) {
    return [...properties.keys()].map(field);
  }
  // Умови «Показувати, якщо» (F11-06): та сама функція, що й у toPayload і на сервері; приховане поле formly не
  // валідує, а його значення прибирає з моделі (resetFieldOnHide).
  const model = (f: FormlyFieldConfig) => (f.model ?? {}) as Record<string, unknown>;
  return readLayout(schema).sections.map((section, index): FormlyFieldConfig => {
    const rows = section.rows.map((row): FormlyFieldConfig => {
      const widths = rowWidths(row);
      return {
        fieldGroupClassName: 'pf-row',
        fieldGroup: row.map((cell, i): FormlyFieldConfig => {
          const config: FormlyFieldConfig = { ...field(cell.field), className: `pf-col pf-col-${widths[i]}` };
          if (cell.visibleIf || section.visibleIf) {
            config.expressions = { hide: (f: FormlyFieldConfig) => hiddenFields(schema, model(f)).has(cell.field) };
          }
          return config;
        }),
      };
    });
    const props: SectionProps = {
      label: section.title,
      description: section.description,
      collapsible: section.collapsible,
      collapsed: section.collapsible && section.collapsed,
    };
    const group: FormlyFieldConfig = { wrappers: [PF_SECTION], props, fieldGroup: rows };
    if (section.visibleIf) {
      group.expressions = { hide: (f: FormlyFieldConfig) => hiddenState(schema, model(f)).sections.has(index) };
    }
    return group;
  });
}

function fieldFor(key: string, property: JsonSchema, required: boolean): FormlyFieldConfig {
  const ui = fieldUi(property);
  const props: PublicFieldProps = {
    label: typeof property['title'] === 'string' ? property['title'] : key,
    description: typeof property['description'] === 'string' ? property['description'] : undefined,
    required,
  };
  if (ui.placeholder) props.placeholder = ui.placeholder;
  if (ui.rows) props.rows = ui.rows;
  const type = typeOf(property);
  const options = optionsOf(property);
  const items = isObject(property['items']) ? property['items'] : null;
  const defaultValue = property['default'];
  const base = { key, defaultValue, props } satisfies FormlyFieldConfig;

  if (isFileProperty(property)) {
    return { ...base, type: PF_FILE };
  }
  if (options) {
    const radio = ui.widget === 'radio' && options.length <= RADIO_MAX_OPTIONS;
    return { ...base, type: radio ? PF_RADIO : PF_SELECT, props: { ...props, options } };
  }
  if (type === 'array' && items && optionsOf(items)) {
    return { ...base, type: ui.widget === 'chips' ? PF_CHIPS : PF_SELECT, props: { ...props, options: optionsOf(items)!, multiple: true } };
  }
  if (type === 'boolean') {
    // Обов'язковий «так/ні» — це «треба погодитись»: requiredTrue не потрібен, false теж відповідь.
    return { ...base, type: PF_CHECKBOX, defaultValue: defaultValue ?? false, props: { ...props, required: false } };
  }
  if (type === 'number' || type === 'integer') {
    return {
      ...base,
      type: PF_INPUT,
      props: { ...props, type: 'number', min: numberOf(property, 'minimum'), max: numberOf(property, 'maximum') },
    };
  }
  const format = property['format'];
  const text = {
    ...props,
    minLength: numberOf(property, 'minLength'),
    maxLength: numberOf(property, 'maxLength'),
    pattern: typeof property['pattern'] === 'string' ? property['pattern'] : undefined,
  };
  if (format === 'textarea' || (numberOf(property, 'maxLength') ?? 0) > 200) {
    return { ...base, type: PF_TEXTAREA, props: text };
  }
  if (format === PHONE_FORMAT) {
    return {
      ...base,
      type: PF_INPUT,
      props: { ...text, type: 'tel', phone: true },
      validators: { phone: { expression: (c: { value: unknown }) => !c.value || isPhoneValue(c.value) } },
    };
  }
  const inputType =
    format === 'email' ? 'email' : format === 'date' ? 'date' : format === 'date-time' ? 'datetime-local' : format === 'uri' ? 'url' : 'text';
  return { ...base, type: PF_INPUT, props: { ...text, type: inputType } };
}

/** Тіло надсилання: поля за схемою (порожні відкинуто, числа — числами, дата й час — ISO 8601) і файли окремо. */
export interface FormPayload {
  fields: Record<string, unknown>;
  files: Record<string, File>;
}

/** Модель форми → тіло надсилання. Поля, приховані умовами «Показувати, якщо» (F11-06), не надсилаються. */
export function toPayload(schema: JsonSchema | null | undefined, model: Record<string, unknown>): FormPayload {
  const fields: Record<string, unknown> = {};
  const files: Record<string, File> = {};
  const hidden = hiddenFields(schema, model);
  for (const [key, property] of propertiesOf(schema)) {
    if (hidden.has(key)) continue;
    const value = model[key];
    if (value === undefined || value === null || value === '') continue;
    if (isFileProperty(property)) {
      if (typeof File !== 'undefined' && value instanceof File) files[key] = value;
      continue;
    }
    const type = typeOf(property);
    if ((type === 'number' || type === 'integer') && typeof value === 'string') {
      const n = Number(value);
      fields[key] = Number.isFinite(n) ? n : value;
    } else if (property['format'] === PHONE_FORMAT && typeof value === 'string') {
      fields[key] = normalizePhone(value);
    } else if (property['format'] === 'date-time' && typeof value === 'string') {
      const date = new Date(value);
      fields[key] = Number.isNaN(date.getTime()) ? value : date.toISOString();
    } else if (Array.isArray(value) && value.length === 0) {
      continue;
    } else {
      fields[key] = value;
    }
  }
  return { fields, files };
}

function isPhoneValue(value: unknown): boolean {
  return typeof value === 'string' && isPhone(value);
}
