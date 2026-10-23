import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { JsonSchema } from './schema-fields';

/**
 * Дані для тестів форми вузла: схеми, як їх віддає `GET /nodes` (копії `src/OpenBP.Nodes.Core/Schemas/*.schema.json`,
 * `src/OpenBP.Mcp/Schemas/tool.schema.json`), і `inputSchema` tool'а `order` з FakeServer (`tests/OpenBP.Mcp.Tests/FakeServer`).
 */

export const SET_SCHEMA: JsonSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'core.set',
  type: 'object',
  required: ['fields'],
  additionalProperties: false,
  properties: {
    fields: {
      type: 'array',
      items: {
        type: 'object',
        required: ['name', 'value'],
        additionalProperties: false,
        properties: {
          name: { type: 'string', minLength: 1, pattern: '^[^.]+(\\.[^.]+)*$', description: 'Шлях поля через крапку, напр. order.customer' },
          value: { description: 'Будь-яке JSON-значення або {{ }}' },
        },
      },
    },
    keepOnlySet: { type: 'boolean', default: false, description: 'Лишити тільки задані поля' },
  },
};

export const IF_SCHEMA: JsonSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'core.if',
  $defs: {
    condition: {
      type: 'object',
      required: ['left', 'operator'],
      additionalProperties: false,
      properties: {
        left: { description: 'Ліве значення, зазвичай {{ $json.поле }}' },
        operator: {
          type: 'string',
          enum: ['equals', 'notEquals', 'gt', 'gte', 'lt', 'lte', 'contains', 'notContains', 'startsWith', 'endsWith', 'isEmpty', 'isNotEmpty', 'isTrue', 'isFalse'],
        },
        right: { description: 'Праве значення; не потрібне для isEmpty, isNotEmpty, isTrue, isFalse' },
      },
    },
  },
  type: 'object',
  required: ['conditions'],
  additionalProperties: false,
  properties: {
    conditions: { type: 'array', items: { $ref: '#/$defs/condition' } },
    combine: { type: 'string', enum: ['and', 'or'], default: 'and' },
  },
};

export const MCP_TOOL_SCHEMA: JsonSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'mcp.tool',
  type: 'object',
  required: ['serverId', 'toolName'],
  additionalProperties: false,
  properties: {
    serverId: { type: 'string', minLength: 1 },
    toolName: { type: 'string', minLength: 1 },
    arguments: { type: 'object' },
    itemsPath: { type: 'string', minLength: 1 },
  },
};

/** FakeServer: `order(int id, string? customer = null)` — як його описує MCP C# SDK. */
export const FAKE_ORDER_TOOL: McpToolInfo = {
  name: 'order',
  description: 'Повертає замовлення з id.',
  inputSchema: {
    type: 'object',
    properties: {
      id: { type: 'integer' },
      customer: { type: ['string', 'null'], default: null },
    },
    required: ['id'],
  },
};

function descriptor(type: string, group: NodeTypeInfo['group'], outputs: string[], parametersSchema: JsonSchema): NodeTypeInfo {
  return { type, version: 1, displayName: type, group, inputs: ['main'], outputs, parametersSchema, isTrigger: false };
}

export const SET_DESCRIPTOR = descriptor('core.set', 'transform', ['main'], SET_SCHEMA);
export const IF_DESCRIPTOR = descriptor('core.if', 'logic', ['true', 'false'], IF_SCHEMA);
export const MCP_TOOL_DESCRIPTOR = descriptor('mcp.tool', 'integration', ['main'], MCP_TOOL_SCHEMA);
