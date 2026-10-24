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

export const SWITCH_SCHEMA: JsonSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'core.switch',
  type: 'object',
  required: ['rules'],
  additionalProperties: false,
  $defs: {
    condition: (IF_SCHEMA['$defs'] as Record<string, unknown>)['condition'],
    rule: {
      type: 'object',
      required: ['conditions'],
      additionalProperties: false,
      properties: {
        conditions: { type: 'array', items: { $ref: '#/$defs/condition' } },
        combine: { type: 'string', enum: ['and', 'or'], default: 'and' },
      },
    },
  },
  properties: {
    rules: {
      type: 'array',
      minItems: 1,
      maxItems: 4,
      items: { $ref: '#/$defs/rule' },
      description: 'Правило i спрямовує item на вихід rule{i}; жодне не виконалось — вихід fallback',
    },
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
export const CODE_SCHEMA: JsonSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  title: 'core.code',
  type: 'object',
  required: ['code'],
  additionalProperties: false,
  properties: {
    code: { type: 'string', minLength: 1, description: "Тіло JS-функції; повертає об'єкт або масив об'єктів" },
    mode: { type: 'string', enum: ['eachItem', 'allItems'], default: 'eachItem' },
  },
};

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

/** `core.decisionTable` (F6-19) — копія `decisionTable.schema.json`. */
export const DECISION_TABLE_SCHEMA: JsonSchema = {
  "title": "core.decisionTable",
  "type": "object",
  "required": [
    "inputs",
    "outputs",
    "rules"
  ],
  "additionalProperties": false,
  "$defs": {
    "cell": {
      "description": "Умова на вхід з тим самим номером: \"-\" — будь-яке значення, інакше оператор core.if і праве значення",
      "oneOf": [
        {
          "const": "-"
        },
        {
          "type": "object",
          "required": [
            "operator"
          ],
          "additionalProperties": false,
          "properties": {
            "operator": {
              "type": "string",
              "enum": [
                "equals",
                "notEquals",
                "gt",
                "gte",
                "lt",
                "lte",
                "contains",
                "notContains",
                "startsWith",
                "endsWith",
                "isEmpty",
                "isNotEmpty",
                "isTrue",
                "isFalse"
              ]
            },
            "right": {
              "description": "Праве значення; не потрібне для isEmpty, isNotEmpty, isTrue, isFalse"
            }
          }
        }
      ]
    },
    "rule": {
      "type": "object",
      "required": [
        "when",
        "then"
      ],
      "additionalProperties": false,
      "properties": {
        "when": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/cell"
          },
          "description": "По умові на кожен вхід (у порядку inputs)"
        },
        "then": {
          "type": "array",
          "items": {},
          "description": "Значення виходів (у порядку outputs); можна вирази {{ }}"
        }
      }
    }
  },
  "properties": {
    "inputs": {
      "title": "Входи",
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "required": [
          "name",
          "expression"
        ],
        "additionalProperties": false,
        "properties": {
          "name": {
            "title": "Назва",
            "type": "string"
          },
          "expression": {
            "title": "Значення",
            "description": "Зазвичай {{ $json.поле }}"
          }
        }
      }
    },
    "outputs": {
      "title": "Виходи",
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "required": [
          "name"
        ],
        "additionalProperties": false,
        "properties": {
          "name": {
            "title": "Поле",
            "type": "string",
            "description": "Поле item'а, куди записується значення"
          }
        }
      }
    },
    "rules": {
      "title": "Правила",
      "type": "array",
      "items": {
        "$ref": "#/$defs/rule"
      }
    },
    "hitPolicy": {
      "title": "Які правила застосувати",
      "type": "string",
      "enum": [
        "first",
        "all",
        "collect"
      ],
      "default": "first",
      "description": "first — перше правило, що збіглося; all — item на кожне правило, що збіглося; collect — один item, у полях виходів масиви значень усіх збігів"
    }
  }
};

function descriptor(type: string, group: NodeTypeInfo['group'], outputs: string[], parametersSchema: JsonSchema): NodeTypeInfo {
  return { type, version: 1, displayName: type, group, inputs: ['main'], outputs, parametersSchema, isTrigger: false };
}

export const SET_DESCRIPTOR = descriptor('core.set', 'transform', ['main'], SET_SCHEMA);
export const IF_DESCRIPTOR = descriptor('core.if', 'logic', ['true', 'false'], IF_SCHEMA);
export const SWITCH_DESCRIPTOR = descriptor('core.switch', 'logic', ['rule0', 'rule1', 'rule2', 'rule3', 'fallback'], SWITCH_SCHEMA);
export const DECISION_TABLE_DESCRIPTOR = descriptor('core.decisionTable', 'logic', ['main', 'noMatch'], DECISION_TABLE_SCHEMA);
export const CODE_DESCRIPTOR = descriptor('core.code', 'transform', ['main'], CODE_SCHEMA);
export const MCP_TOOL_DESCRIPTOR = descriptor('mcp.tool', 'integration', ['main'], MCP_TOOL_SCHEMA);
