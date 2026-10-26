import { FormlyFieldConfig } from '@ngx-formly/core';

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

/** Поля форми для об'єктної схеми. Не об'єкт — порожній список. */
export function formFields(schema: JsonSchema | null | undefined): FormlyFieldConfig[] {
  const required = requiredOf(schema);
  return propertiesOf(schema).map(([key, property]) => fieldFor(key, property, required.has(key)));
}

function fieldFor(key: string, property: JsonSchema, required: boolean): FormlyFieldConfig {
  const props: PublicFieldProps = {
    label: typeof property['title'] === 'string' ? property['title'] : key,
    description: typeof property['description'] === 'string' ? property['description'] : undefined,
    required,
  };
  const type = typeOf(property);
  const options = optionsOf(property);
  const items = isObject(property['items']) ? property['items'] : null;
  const defaultValue = property['default'];
  const base = { key, defaultValue, props } satisfies FormlyFieldConfig;

  if (isFileProperty(property)) {
    return { ...base, type: PF_FILE };
  }
  if (options) {
    return { ...base, type: PF_SELECT, props: { ...props, options } };
  }
  if (type === 'array' && items && optionsOf(items)) {
    return { ...base, type: PF_SELECT, props: { ...props, options: optionsOf(items)!, multiple: true } };
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
  const inputType =
    format === 'email' ? 'email' : format === 'date' ? 'date' : format === 'date-time' ? 'datetime-local' : format === 'uri' ? 'url' : 'text';
  return { ...base, type: PF_INPUT, props: { ...text, type: inputType } };
}

/** Тіло надсилання: поля за схемою (порожні відкинуто, числа — числами, дата й час — ISO 8601) і файли окремо. */
export interface FormPayload {
  fields: Record<string, unknown>;
  files: Record<string, File>;
}

/** Модель форми → тіло надсилання. */
export function toPayload(schema: JsonSchema | null | undefined, model: Record<string, unknown>): FormPayload {
  const fields: Record<string, unknown> = {};
  const files: Record<string, File> = {};
  for (const [key, property] of propertiesOf(schema)) {
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
