import { FormlyFieldConfig } from '@ngx-formly/core';
import { isAssignmentSchema, isCombineSchema, isConditionSchema, isRuleSchema, operatorLabel } from './conditions';
import { isDecisionRulesSchema } from './decision-table';
import { OPTIONS_ANNOTATION, OptionsSource, parseOptionsSource } from './dynamic-options';
import { ValueRules, isPlainObject } from './node-value';

/**
 * JSON Schema параметрів → поля ngx-formly (F2-18). Підтримується підмножина, яку дають дескриптори `core.*`
 * і `inputSchema` MCP tools: `type` (зокрема `[T, "null"]`), `enum`/`const`, `properties`/`required`, `items`,
 * локальні `$ref` (`#/$defs/…`, `#/definitions/…`), `title`, `description`, `default`, `minimum`.
 * Решта (`oneOf`, кілька типів, об'єкти без `properties`) редагується як JSON-значення. Умови, «AND/OR» і присвоєння
 * впізнаються за формою схеми й отримують власні типи полів (F3-53, `conditions.ts`). Властивість з анотацією
 * `x-openbp-options` — поле зі списком варіантів з конектора (F3-54, `dynamic-options.ts`).
 * Значення за замовчуванням у модель не підставляються — лише показуються підказкою: відкриття панелі не змінює вузол.
 */

export type JsonSchema = Record<string, unknown>;

/** Типи полів, які реєструє `provideNodeForm()`. */
export const VALUE_FIELD = 'app-value';
export const ARRAY_FIELD = 'app-array';
export const CODE_FIELD = 'app-code';
export const SECTION_WRAPPER = 'app-section';
/** Умова «ліве · оператор · праве» в один ряд (F3-53). */
export const CONDITION_FIELD = 'app-condition';
/** Перемикач «усі умови (AND) / будь-яка (OR)» чіпами (F3-53). */
export const COMBINE_FIELD = 'app-combine';
/** Таблиця присвоєнь «поле = значення» `core.set` (F3-53). */
export const ASSIGNMENTS_FIELD = 'app-assignments';
/** Правила таблиці рішень `core.decisionTable` таблицею «умови на входи → значення виходів» (F6-19). */
export const DECISION_RULES_FIELD = 'app-decision-rules';

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
  /** Масив: підпис кнопки додавання (типово «Додати»). */
  addLabel?: string;
  /** Масив правил `core.switch`: над кожним — вихідний порт `rule{i}`. */
  rulePorts?: boolean;
  /** Масив: межі кількості елементів зі схеми (`minItems`/`maxItems`). */
  minItems?: number;
  maxItems?: number;
  /** Масив: «Видалити» компактною кнопкою «✕» (умови, правила). */
  compact?: boolean;
  /** Рядок-назва присвоєння: автодоповнення іменами полів входу. */
  suggestInputFields?: boolean;
  /** Анотація `x-openbp-options`: значення обирається зі списку, який повертає tool конектора (F3-54). */
  optionsSource?: OptionsSource;
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
  const field = typedField(key, raw, required, root, depth);
  const source = field.type === VALUE_FIELD ? parseOptionsSource(resolve(raw, root, 0)[OPTIONS_ANNOTATION]) : null;
  return source ? { ...field, props: { ...field.props, multiline: false, optionsSource: source } } : field;
}

function typedField(key: string | undefined, raw: JsonSchema, required: boolean, root: JsonSchema, depth: number): FormlyFieldConfig {
  const schema = resolve(raw, root, 0);
  const props: NodeFieldProps = {
    label: typeof schema['title'] === 'string' && schema['title'] ? schema['title'] : key,
    description: typeof schema['description'] === 'string' ? schema['description'] : undefined,
    required,
  };
  if (schema['default'] !== undefined && schema['default'] !== null) props.placeholder = defaultHint(schema['default']);

  const enumValues = Array.isArray(schema['enum']) ? schema['enum'] : 'const' in schema ? [schema['const']] : null;
  if (enumValues && isCombineSchema(schema)) {
    const label = props.label === key ? $localize`:@@cond.combine:Як поєднати умови` : props.label;
    return { key, type: COMBINE_FIELD, props: { ...props, label, rules: { kind: 'enum', options: enumValues } } };
  }
  if (enumValues) {
    return valueField(key, props, { kind: 'enum', options: enumValues });
  }

  const types = typesOf(schema);
  const type = types.length === 1 ? types[0] : undefined;

  if (type === 'object' && depth < MAX_DEPTH && isPlainObject(schema['properties']) && isConditionSchema(schema)) {
    return { key, type: CONDITION_FIELD, props, fieldGroup: conditionParts(schema, root, depth) };
  }

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
    const items = resolve(schema['items'], root, 0);
    if (typeof schema['minItems'] === 'number') props.minItems = schema['minItems'];
    if (typeof schema['maxItems'] === 'number') props.maxItems = schema['maxItems'];
    // Назва без `title` у схемі — ключ; для впізнаних масивів — людська назва.
    const untitled = props.label === key;
    if (isDecisionRulesSchema(items)) {
      if (untitled) props.label = $localize`:@@dt.rules:Правила`;
      return { key, type: DECISION_RULES_FIELD, props };
    }
    if (isPlainObject(items['properties']) && isAssignmentSchema(items)) {
      if (untitled) props.label = $localize`:@@set.fields:Поля`;
      return { key, type: ASSIGNMENTS_FIELD, props, fieldArray: { fieldGroup: assignmentParts(items, root, depth) } };
    }
    if (isConditionSchema(items)) {
      if (untitled) props.label = $localize`:@@cond.conditions:Умови`;
      props.addLabel = $localize`:@@cond.add:Додати умову`;
      props.compact = true;
    } else if (isRuleSchema(items)) {
      if (untitled) props.label = $localize`:@@cond.rules:Правила`;
      props.addLabel = $localize`:@@cond.addRule:Додати правило`;
      props.rulePorts = true;
    }
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

/** Частини умови: ліве значення й праве — однорядкові, оператор — список з підписами українською. */
function conditionParts(schema: JsonSchema, root: JsonSchema, depth: number): FormlyFieldConfig[] {
  const required = requiredOf(schema);
  return Object.entries(properties(schema)).map(([k, p]) => {
    const field = fieldFor(k, asSchema(p), required.has(k), root, depth + 1);
    const props = field.props as NodeFieldProps;
    if (k === 'left') return { ...field, props: { ...props, multiline: false, label: $localize`:@@cond.left:Значення`, description: undefined, placeholder: '{{ $json.поле }}' } };
    if (k === 'right') return { ...field, props: { ...props, multiline: false, label: $localize`:@@cond.right:Порівняти з`, description: undefined } };
    if (k === 'operator' && props.rules?.kind === 'enum') {
      const options = props.rules.options ?? [];
      return { ...field, props: { ...props, label: $localize`:@@cond.operator:Оператор`, rules: { ...props.rules, labels: options.map((o) => operatorLabel(String(o))) } } };
    }
    return field;
  });
}

/** Частини присвоєння: назва з автодоповненням полями входу, значення — однорядкове. */
function assignmentParts(schema: JsonSchema, root: JsonSchema, depth: number): FormlyFieldConfig[] {
  const required = requiredOf(schema);
  return Object.entries(properties(schema)).map(([k, p]) => {
    const field = fieldFor(k, asSchema(p), required.has(k), root, depth + 1);
    const props = field.props as NodeFieldProps;
    if (k === 'name') return { ...field, props: { ...props, label: $localize`:@@set.name:Поле`, description: undefined, suggestInputFields: true } };
    return { ...field, props: { ...props, label: $localize`:@@set.value:Значення`, description: undefined, multiline: false } };
  });
}

function properties(schema: JsonSchema): Record<string, unknown> {
  return isPlainObject(schema['properties']) ? schema['properties'] : {};
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
