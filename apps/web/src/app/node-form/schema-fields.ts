import { FormlyFieldConfig } from '@ngx-formly/core';
import { ValueRules, isPlainObject } from './node-value';

/**
 * JSON Schema параметрів → поля ngx-formly (F2-18). Підтримується підмножина, яку дають дескриптори `core.*`
 * і `inputSchema` MCP tools: `type` (зокрема `[T, "null"]`), `enum`/`const`, `properties`/`required`, `items`,
 * локальні `$ref` (`#/$defs/…`, `#/definitions/…`), `title`, `description`, `default`, `minimum`.
 * Решта (`oneOf`, кілька типів, об'єкти без `properties`) редагується як JSON-значення.
 * Значення за замовчуванням у модель не підставляються — лише показуються підказкою: відкриття панелі не змінює вузол.
 */

export type JsonSchema = Record<string, unknown>;

/** Типи полів, які реєструє `provideNodeForm()`. */
export const VALUE_FIELD = 'app-value';
export const ARRAY_FIELD = 'app-array';
export const SECTION_WRAPPER = 'app-section';

/** Глибина вкладеності, після якої об'єкти й масиви редагуються як JSON (захист від рекурсивних `$ref`). */
const MAX_DEPTH = 6;

export interface SchemaFieldsOptions {
  /** Властивості верхнього рівня, яких немає у формі (напр. `serverId`/`toolName` для `mcp.tool`). */
  omit?: readonly string[];
  /** Інша схема для властивості верхнього рівня (напр. `arguments` ← `inputSchema` tool'а); `$ref` — відносно неї. */
  overrides?: Readonly<Record<string, JsonSchema>>;
}

/** Властивості props, які читають типи полів форми вузла. */
export interface NodeFieldProps {
  label?: string;
  description?: string;
  required?: boolean;
  placeholder?: string;
  rules?: ValueRules;
  multiline?: boolean;
}

/** Поля форми для об'єктної схеми (параметри вузла). Не об'єкт — порожній список. */
export function schemaFields(schema: JsonSchema | null | undefined, options: SchemaFieldsOptions = {}): FormlyFieldConfig[] {
  if (!isPlainObject(schema)) return [];
  const root = schema;
  const resolved = resolve(schema, root, 0);
  const properties = isPlainObject(resolved['properties']) ? resolved['properties'] : {};
  const required = requiredOf(resolved);
  const omit = new Set(options.omit ?? []);
  return Object.entries(properties)
    .filter(([key]) => !omit.has(key))
    .map(([key, prop]) => {
      const override = options.overrides?.[key];
      return override
        ? fieldFor(key, override, required.has(key), override, 1)
        : fieldFor(key, asSchema(prop), required.has(key), root, 1);
    });
}

function fieldFor(key: string | undefined, raw: JsonSchema, required: boolean, root: JsonSchema, depth: number): FormlyFieldConfig {
  const schema = resolve(raw, root, 0);
  const props: NodeFieldProps = {
    label: typeof schema['title'] === 'string' && schema['title'] ? schema['title'] : key,
    description: typeof schema['description'] === 'string' ? schema['description'] : undefined,
    required,
  };
  if (schema['default'] !== undefined && schema['default'] !== null) props.placeholder = defaultHint(schema['default']);

  const enumValues = Array.isArray(schema['enum']) ? schema['enum'] : 'const' in schema ? [schema['const']] : null;
  if (enumValues) {
    return valueField(key, props, { kind: 'enum', options: enumValues });
  }

  const types = typesOf(schema);
  const type = types.length === 1 ? types[0] : undefined;

  if (type === 'object' && depth < MAX_DEPTH && isPlainObject(schema['properties'])) {
    const nested = requiredOf(schema);
    return {
      key,
      wrappers: [SECTION_WRAPPER],
      props,
      fieldGroup: Object.entries(schema['properties']).map(([k, p]) => fieldFor(k, asSchema(p), nested.has(k), root, depth + 1)),
    };
  }

  if (type === 'array' && depth < MAX_DEPTH && isPlainObject(schema['items'])) {
    return {
      key,
      type: ARRAY_FIELD,
      props,
      fieldArray: fieldFor(undefined, schema['items'], false, root, depth + 1),
    };
  }

  switch (type) {
    case 'string':
      return valueField(key, props, { kind: 'string' });
    case 'number':
    case 'integer':
    case 'boolean':
      return valueField(key, props, { kind: type, minimum: typeof schema['minimum'] === 'number' ? schema['minimum'] : undefined });
    case 'object':
    case 'array':
      return valueField(key, { ...props, multiline: true }, { kind: 'json', jsonType: type });
    default:
      return valueField(key, { ...props, multiline: true }, { kind: 'json' });
  }
}

function valueField(key: string | undefined, props: NodeFieldProps, rules: ValueRules): FormlyFieldConfig {
  return { key, type: VALUE_FIELD, props: { ...props, rules } };
}

/** Типи схеми без `null` (nullable-поле редагується як його основний тип). */
function typesOf(schema: JsonSchema): string[] {
  const t = schema['type'];
  const list = Array.isArray(t) ? t : typeof t === 'string' ? [t] : [];
  return list.filter((x): x is string => typeof x === 'string' && x !== 'null');
}

function requiredOf(schema: JsonSchema): Set<string> {
  const r = schema['required'];
  return new Set(Array.isArray(r) ? r.filter((x): x is string => typeof x === 'string') : []);
}

/** Розкриває локальний `$ref`; невідомий або зациклений — порожня схема (довільне значення). */
function resolve(schema: JsonSchema, root: JsonSchema, hops: number): JsonSchema {
  const ref = schema['$ref'];
  if (typeof ref !== 'string') return schema;
  if (hops > 10 || !ref.startsWith('#')) return {};
  let target: unknown = root;
  for (const part of ref.slice(1).split('/').filter(Boolean)) {
    const name = decodeURIComponent(part).replace(/~1/g, '/').replace(/~0/g, '~');
    target = isPlainObject(target) ? target[name] : undefined;
  }
  if (!isPlainObject(target)) return {};
  // Поля поруч із `$ref` (title, description) перекривають ціль.
  const siblings = { ...schema };
  delete siblings['$ref'];
  return { ...resolve(target, root, hops + 1), ...siblings };
}

function asSchema(value: unknown): JsonSchema {
  return isPlainObject(value) ? value : {};
}

function defaultHint(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value);
}
