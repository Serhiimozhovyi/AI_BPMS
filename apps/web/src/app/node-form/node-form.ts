import { FormlyFieldConfig } from '@ngx-formly/core';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { isPlainObject } from './node-value';
import { NodeFieldProps, SECTION_WRAPPER, VALUE_FIELD, schemaFields } from './schema-fields';

/**
 * Форма вузла (F2-18): які поля показати і як зміни форми потрапляють у вузол сценарію.
 * Функції не змінюють вхідні об'єкти; невідомі параметри й поля вузла зберігаються як є.
 */

/** Типи, чиї `arguments` описує `inputSchema` tool'а з `tools_cache` (docs/connectors.md). */
export const MCP_TOOL_TYPES: readonly string[] = ['mcp.tool', 'mcp.poll'];

/** Поля параметрів: зі схеми дескриптора; для `mcp.tool` `arguments` — з `inputSchema` tool'а (невідомий tool — JSON). */
export function parameterFields(type: string, descriptor: NodeTypeInfo | undefined, tool: McpToolInfo | null | undefined): FormlyFieldConfig[] {
  if (!descriptor) return [];
  if (!MCP_TOOL_TYPES.includes(type)) return schemaFields(descriptor.parametersSchema);
  const argumentsSchema = isPlainObject(tool?.inputSchema)
    ? { ...tool.inputSchema, title: $localize`:@@nodeform.arguments:Аргументи` }
    : undefined;
  return schemaFields(descriptor.parametersSchema, {
    omit: ['serverId', 'toolName'],
    overrides: argumentsSchema ? { arguments: argumentsSchema } : undefined,
  });
}

/** Модель форми параметрів — глибока копія: formly змінює модель на місці. */
export function parametersModel(node: WorkflowNode): Record<string, unknown> {
  return isPlainObject(node.parameters) ? structuredClone(node.parameters) : {};
}

/** Вузол з параметрами з форми; незадані (`undefined`) поля прибираються. */
export function applyParameters(node: WorkflowNode, model: Record<string, unknown>): WorkflowNode {
  return { ...node, parameters: clean(model) as Record<string, unknown> };
}

/** Загальні поля вузла (docs/workflow-format.md, «Обов'язкові поля»): без виразів, типи — як у схемі формату. */
export interface CommonModel {
  timeoutSec?: unknown;
  retry?: { maxTries?: unknown; waitMs?: unknown };
  continueOnFail?: boolean;
  disabled?: boolean;
  notes?: unknown;
}

export function commonModel(node: WorkflowNode): CommonModel {
  return {
    timeoutSec: node.timeoutSec,
    retry: { maxTries: node.retry?.maxTries, waitMs: node.retry?.waitMs },
    continueOnFail: node.continueOnFail === true,
    disabled: node.disabled === true,
    notes: node.notes,
  };
}

export function commonFields(): FormlyFieldConfig[] {
  const field = (key: string, props: NodeFieldProps): FormlyFieldConfig => ({ key, type: VALUE_FIELD, props });
  return [
    field('timeoutSec', {
      label: $localize`:@@nodeform.timeoutSec:Тайм-аут, с`,
      rules: { kind: 'integer', minimum: 1, allowExpression: false },
    }),
    {
      key: 'retry',
      wrappers: [SECTION_WRAPPER],
      props: { label: $localize`:@@nodeform.retry:Повтори при помилці` },
      fieldGroup: [
        field('maxTries', {
          label: $localize`:@@nodeform.retry.maxTries:Спроб усього`,
          rules: { kind: 'integer', minimum: 1, allowExpression: false },
        }),
        field('waitMs', {
          label: $localize`:@@nodeform.retry.waitMs:Пауза, мс`,
          rules: { kind: 'integer', minimum: 0, allowExpression: false },
        }),
      ],
    },
    field('continueOnFail', {
      label: $localize`:@@nodeform.continueOnFail:Продовжувати при помилці (вихід error)`,
      rules: { kind: 'boolean', allowExpression: false },
    }),
    field('disabled', {
      label: $localize`:@@nodeform.disabled:Вимкнено`,
      rules: { kind: 'boolean', allowExpression: false },
    }),
    field('notes', {
      label: $localize`:@@nodeform.notes:Нотатки`,
      multiline: true,
      rules: { kind: 'string', allowExpression: false },
    }),
  ];
}

/**
 * Вузол із загальними полями з форми. Незадане поле прибирається; `false` для `continueOnFail`/`disabled`
 * записується лише якщо поле вже було у вузлі; `retry` — коли задано хоч одне з `maxTries`/`waitMs`.
 */
export function applyCommon(node: WorkflowNode, model: CommonModel): WorkflowNode {
  const next: WorkflowNode = { ...node };
  setOrDelete(next, 'timeoutSec', model.timeoutSec);
  setOrDelete(next, 'notes', model.notes === '' ? undefined : model.notes);
  for (const key of ['continueOnFail', 'disabled'] as const) {
    const value = model[key];
    setOrDelete(next, key, value === true || (value === false && key in node) ? value : undefined);
  }
  const retry = clean(model.retry ?? {}) as Record<string, unknown>;
  if (Object.keys(retry).length > 0) next.retry = { ...node.retry, ...retry } as WorkflowNode['retry'];
  else delete next.retry;
  return next;
}

/** Замінює вузол з тим самим `id`; решта визначення — ті самі об'єкти. Немає такого вузла — те саме визначення. */
export function updateNode(definition: WorkflowDefinition, node: WorkflowNode): WorkflowDefinition {
  const index = definition.nodes.findIndex((n) => n.id === node.id);
  if (index < 0 || definition.nodes[index] === node) return definition;
  const nodes = [...definition.nodes];
  nodes[index] = node;
  return { ...definition, nodes };
}

function setOrDelete(target: Record<string, unknown>, key: string, value: unknown): void {
  if (value === undefined) delete target[key];
  else target[key] = value;
}

/** Копія без `undefined`: у об'єктах ключ прибирається, у масивах стає `null` (як у JSON). */
function clean(value: unknown): unknown {
  if (Array.isArray(value)) return value.map((v) => (v === undefined ? null : clean(v)));
  if (isPlainObject(value)) {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) if (v !== undefined) out[k] = clean(v);
    return out;
  }
  return value;
}
