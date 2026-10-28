import { FormlyFieldConfig } from '@ngx-formly/core';
import { WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { applyCommon, applyParameters, commonModel, credentialTypesFor, parameterFields, updateNode } from './node-form';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { formatValue, parseValue, valueError } from './node-value';
import { ARRAY_FIELD, ASSIGNMENTS_FIELD, COMBINE_FIELD, CONDITION_FIELD, SECTION_WRAPPER, VALUE_FIELD, schemaFields } from './schema-fields';
import { FAKE_ORDER_TOOL, IF_DESCRIPTOR, MCP_TOOL_DESCRIPTOR, SET_SCHEMA, SWITCH_SCHEMA } from './test-schemas';

function node(extra: Partial<WorkflowNode> = {}): WorkflowNode {
  return { id: 'n1', name: 'Вузол', type: 'core.set', typeVersion: 1, position: [0, 0], parameters: {}, ...extra };
}

function summary(fields: FormlyFieldConfig[]): unknown[] {
  return fields.map((f) => ({
    key: f.key,
    type: f.type ?? null,
    wrappers: f.wrappers ?? null,
    kind: (f.props?.['rules'] as { kind?: string } | undefined)?.kind ?? null,
    required: f.props?.required ?? false,
    ...(f.fieldGroup ? { group: summary(f.fieldGroup) } : {}),
    ...(f.fieldArray ? { item: summary([f.fieldArray as FormlyFieldConfig])[0] } : {}),
  }));
}

describe('node-value', () => {
  it('вираз {{ }} лишається рядком у полі будь-якого типу', () => {
    for (const kind of ['number', 'integer', 'boolean', 'enum', 'json'] as const) {
      expect(parseValue('{{ $json.total }}', { kind })).toBe('{{ $json.total }}');
      expect(valueError('{{ $json.total }}', { kind })).toBeNull();
    }
  });

  it('текст перетворюється на тип схеми; невідповідний — помилка', () => {
    expect(parseValue('42', { kind: 'integer' })).toBe(42);
    expect(parseValue('', { kind: 'integer' })).toBeUndefined();
    expect(parseValue('true', { kind: 'boolean' })).toBe(true);
    expect(parseValue('{"a":1}', { kind: 'json' })).toEqual({ a: 1 });
    expect(parseValue('просто текст', { kind: 'json' })).toBe('просто текст');
    expect(parseValue('42', { kind: 'string' })).toBe('42');
    expect(valueError(parseValue('abc', { kind: 'number' }), { kind: 'number' })).toBe('number');
    expect(valueError(1.5, { kind: 'integer' })).toBe('integer');
    expect(valueError(0, { kind: 'integer', minimum: 1 })).toBe('minimum');
    expect(valueError('x', { kind: 'enum', options: ['and', 'or'] })).toBe('enum');
    expect(valueError([1], { kind: 'json', jsonType: 'object' })).toBe('object');
  });

  it('загальні поля не приймають виразів', () => {
    expect(parseValue('{{ 5 }}', { kind: 'integer', allowExpression: false })).toBe('{{ 5 }}');
    expect(valueError('{{ 5 }}', { kind: 'integer', allowExpression: false })).toBe('expression');
  });

  it('formatValue: рядок як є, решта — JSON', () => {
    expect(formatValue('{{ $json.a }}', 'number')).toBe('{{ $json.a }}');
    expect(formatValue(5, 'integer')).toBe('5');
    expect(formatValue({ a: 1 }, 'json')).toBe('{\n  "a": 1\n}');
    expect(formatValue(null, 'string')).toBe('');
  });
});

describe('schemaFields', () => {
  it('core.set: таблиця присвоєнь (F3-53) — name з автодоповненням, довільне value — JSON в один рядок; boolean', () => {
    const fields = schemaFields(SET_SCHEMA);
    const parts = (fields[0].fieldArray as FormlyFieldConfig).fieldGroup ?? [];
    expect(parts[0].props).toMatchObject({ suggestInputFields: true });
    expect(parts[1].props).toMatchObject({ multiline: false });
    expect(summary(fields)).toEqual([
      {
        key: 'fields',
        type: ASSIGNMENTS_FIELD,
        wrappers: null,
        kind: null,
        required: true,
        item: {
          key: undefined,
          type: null,
          wrappers: null,
          kind: null,
          required: false,
          group: [
            { key: 'name', type: VALUE_FIELD, wrappers: null, kind: 'string', required: true },
            { key: 'value', type: VALUE_FIELD, wrappers: null, kind: 'json', required: true },
          ],
        },
      },
      { key: 'keepOnlySet', type: VALUE_FIELD, wrappers: null, kind: 'boolean', required: false },
    ]);
  });

  it('core.if: $ref на $defs розкривається; умова — рядок з операторами українською, combine — чіпи; default — лише підказка', () => {
    const fields = parameterFields('core.if', IF_DESCRIPTOR, null);
    const conditions = fields[0];
    expect(conditions.type).toBe(ARRAY_FIELD);
    expect(conditions.props).toMatchObject({ addLabel: 'Додати умову' });
    const item = conditions.fieldArray as FormlyFieldConfig;
    expect(item.type).toBe(CONDITION_FIELD);
    expect(item.fieldGroup?.map((f) => f.key)).toEqual(['left', 'operator', 'right']);
    expect(item.fieldGroup?.[0].props).toMatchObject({ multiline: false, label: 'Значення' });
    expect(item.fieldGroup?.[1].props?.['rules']).toMatchObject({ kind: 'enum' });
    const rules = item.fieldGroup?.[1].props?.['rules'] as { options: string[]; labels: string[] };
    expect(rules.labels[rules.options.indexOf('gte')]).toBe('більше або дорівнює');
    expect(rules.labels[rules.options.indexOf('isEmpty')]).toBe('порожнє');
    expect(fields[1].type).toBe(COMBINE_FIELD);
    expect(fields[1].props).toMatchObject({ rules: { kind: 'enum', options: ['and', 'or'] }, placeholder: 'and' });
    expect(fields[1].defaultValue).toBeUndefined();
  });

  it('core.switch: правила з портами, «Додати правило», межі minItems/maxItems; усередині — умови й combine', () => {
    const fields = schemaFields(SWITCH_SCHEMA);
    expect(fields[0]).toMatchObject({ key: 'rules', type: ARRAY_FIELD, props: { addLabel: 'Додати правило', rulePorts: true, minItems: 1, maxItems: 4 } });
    const rule = fields[0].fieldArray as FormlyFieldConfig;
    expect(rule.fieldGroup?.map((f) => [f.key, f.type])).toEqual([
      ['conditions', ARRAY_FIELD],
      ['combine', COMBINE_FIELD],
    ]);
    expect((rule.fieldGroup?.[0].fieldArray as FormlyFieldConfig).type).toBe(CONDITION_FIELD);
  });

  it('mcp.tool: arguments — з inputSchema tool’а, serverId/toolName не редагуються', () => {
    const fields = parameterFields('mcp.tool', MCP_TOOL_DESCRIPTOR, FAKE_ORDER_TOOL);
    expect(summary(fields)).toEqual([
      {
        key: 'arguments',
        type: null,
        wrappers: [SECTION_WRAPPER],
        kind: null,
        required: false,
        group: [
          { key: 'id', type: VALUE_FIELD, wrappers: null, kind: 'integer', required: true },
          { key: 'customer', type: VALUE_FIELD, wrappers: null, kind: 'string', required: false },
        ],
      },
      { key: 'itemsPath', type: VALUE_FIELD, wrappers: null, kind: 'string', required: false },
    ]);
  });

  it('mcp.tool без схеми tool’а: arguments — JSON-об’єкт', () => {
    const fields = parameterFields('mcp.tool', MCP_TOOL_DESCRIPTOR, null);
    expect(fields[0].props?.['rules']).toEqual({ kind: 'json', jsonType: 'object' });
  });

  it('невідомий тип — без полів; рекурсивний $ref не зациклюється', () => {
    expect(parameterFields('x.y', undefined, null)).toEqual([]);
    const recursive = { type: 'object', $defs: { n: { type: 'object', properties: { child: { $ref: '#/$defs/n' } } } }, properties: { root: { $ref: '#/$defs/n' } } };
    expect(() => schemaFields(recursive)).not.toThrow();
  });
});

describe('applyParameters / applyCommon / updateNode', () => {
  it('параметри з форми: невідомі поля зберігаються, незадані прибираються', () => {
    const n = node({ parameters: { fields: [], custom: { a: 1 } } });
    const next = applyParameters(n, { fields: [{ name: 'total', value: '{{ $json.sum }}' }], custom: { a: 1 }, keepOnlySet: undefined });
    expect(next.parameters).toEqual({ fields: [{ name: 'total', value: '{{ $json.sum }}' }], custom: { a: 1 } });
    expect(n.parameters).toEqual({ fields: [], custom: { a: 1 } });
    expect(next).not.toBe(n);
  });

  it('загальні поля: задані записуються, порожні прибираються, невідомі поля вузла лишаються', () => {
    const n = node({ disabled: false, extra: 'x' });
    const next = applyCommon(n, { ...commonModel(n), timeoutSec: 30, retry: { maxTries: 3, waitMs: 1000 }, continueOnFail: true, notes: '' });
    expect(next).toMatchObject({ timeoutSec: 30, retry: { maxTries: 3, waitMs: 1000 }, continueOnFail: true, disabled: false, extra: 'x' });
    expect('notes' in next).toBe(false);

    const cleared = applyCommon(next, { ...commonModel(next), timeoutSec: undefined, retry: { maxTries: undefined, waitMs: undefined }, continueOnFail: false });
    expect('timeoutSec' in cleared || 'retry' in cleared).toBe(false);
    expect(cleared.continueOnFail).toBe(false);
    expect(applyCommon(node(), commonModel(node()))).toEqual(node());
  });

  it('updateNode замінює вузол за id, решта — ті самі об’єкти', () => {
    const a = node({ id: 'a' });
    const b = node({ id: 'b' });
    const def: WorkflowDefinition = { ...emptyWorkflowDefinition('w'), nodes: [a, b] };
    const changed = { ...b, name: 'Нове' };
    const next = updateNode(def, changed);
    expect(next.nodes[0]).toBe(a);
    expect(next.nodes[1]).toBe(changed);
    expect(next.settings).toBe(def.settings);
    expect(updateNode(def, node({ id: 'zzz' }))).toBe(def);
  });
});

describe('вибір облікових даних (F4-03)', () => {
  const llm: NodeTypeInfo = {
    type: 'ai.llm',
    version: 1,
    displayName: 'LLM',
    group: 'ai',
    inputs: ['main'],
    outputs: ['main'],
    parametersSchema: {
      type: 'object',
      required: ['provider', 'credentialId'],
      properties: { provider: { type: 'string', enum: ['anthropic', 'openai', 'openai-compatible', 'ollama'] }, credentialId: { type: 'string' } },
    },
    credentials: [{ name: 'llm', type: 'openai-compatible', parameter: 'credentialId', required: true }],
    isTrigger: false,
  };
  const options = [
    { id: 'a1', name: 'Anthropic прод', type: 'anthropic' },
    { id: 'o1', name: 'OpenAI', type: 'openai' },
    { id: 'h1', name: 'Headers', type: 'mcp.http' },
  ];

  function credentialField(parameters: Record<string, unknown>) {
    const fields = parameterFields('ai.llm', llm, null, options, parameters);
    return fields.find((f) => f.key === 'credentialId')!;
  }

  it('список лише потрібного типу (для ai.llm — за провайдером), назви як підписи', () => {
    const rules = credentialField({ provider: 'anthropic' }).props!['rules'] as { kind: string; options: string[]; labels: string[] };
    expect(rules.kind).toBe('enum');
    expect(rules.options).toEqual(['a1']);
    expect(rules.labels).toEqual(['Anthropic прод']);
    expect(credentialField({}).props!['label']).toBe('Облікові дані');
    expect(credentialTypesFor('ai.llm', llm.credentials![0], {})).toEqual(['anthropic', 'openai', 'gemini', 'openai-compatible', 'ollama']);
    expect(credentialTypesFor('ai.agent', llm.credentials![0], { provider: 'openai' })).toEqual(['openai']);
  });

  it('обране значення пишеться в параметр вузла', () => {
    const n = node({ type: 'ai.llm', parameters: { provider: 'openai' } });
    const field = credentialField({ provider: 'openai' });
    const rules = field.props!['rules'] as ValueRulesLike;
    expect(parseValue('o1', rules)).toBe('o1');
    expect(applyParameters(n, { provider: 'openai', credentialId: 'o1' }).parameters).toEqual({ provider: 'openai', credentialId: 'o1' });
    expect(valueError('o1', rules)).toBeNull();
  });

  it('видалений id — у списку з позначкою і помилкою поля', () => {
    const rules = credentialField({ provider: 'anthropic', credentialId: 'gone' }).props!['rules'] as ValueRulesLike & { labels: string[] };
    expect(rules.options).toContain('gone');
    expect(rules.labels.at(-1)).toContain('не знайдено');
    expect(valueError('gone', rules)).toBe('missing');
  });

  it('без списку (немає прав) — звичайне текстове поле', () => {
    const fields = parameterFields('ai.llm', llm, null, null, {});
    const rules = fields.find((f) => f.key === 'credentialId')!.props!['rules'] as { kind: string };
    expect(rules.kind).toBe('string');
  });
});

type ValueRulesLike = Parameters<typeof valueError>[1];

