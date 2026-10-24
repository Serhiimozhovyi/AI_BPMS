import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { missingFields, missingFieldsByNode } from './missing-fields';

const llm: NodeTypeInfo = {
  type: 'ai.llm',
  version: 1,
  displayName: 'ШІ',
  group: 'ai',
  inputs: ['main'],
  outputs: ['main'],
  parametersSchema: { type: 'object', required: ['model', 'prompt'], properties: {} },
  credentials: [
    { name: 'llm', type: 'llm', parameter: 'credential', required: true },
    { name: 'extra', type: 'x', parameter: 'extra', required: false },
  ],
  isTrigger: false,
};

const mcp: NodeTypeInfo = { ...llm, type: 'mcp.tool', parametersSchema: { type: 'object', required: ['serverId', 'toolName'] }, credentials: null };

const send: McpToolInfo = { name: 'send', inputSchema: { type: 'object', required: ['chat_id', 'text'] } };

function node(parameters: Record<string, unknown>, extra: Partial<WorkflowNode> = {}): WorkflowNode {
  return { id: 'n1', name: 'Вузол', type: 'ai.llm', typeVersion: 1, position: [0, 0], parameters, ...extra };
}

describe('missingFields', () => {
  it('рахує незаповнені required схеми й обов’язкові credentials', () => {
    expect(missingFields(node({ model: 'gpt', prompt: '   ' }), llm)).toEqual(['prompt', 'credential']);
    expect(missingFields(node({ model: null }), llm)).toEqual(['model', 'prompt', 'credential']);
  });

  it('заповнені поля і вирази не рахуються', () => {
    expect(missingFields(node({ model: 'gpt', prompt: '{{ $json.text }}', credential: 'c1' }), llm)).toEqual([]);
    expect(missingFields(node({ model: 0, prompt: false, credential: 'c1' }), llm)).toEqual([]);
  });

  it('для mcp.tool — ще й required з inputSchema tool’а', () => {
    const n = node({ serverId: 's1', toolName: 'send', arguments: { chat_id: '{{ $json.chat }}', text: '' } }, { type: 'mcp.tool' });
    expect(missingFields(n, mcp, send)).toEqual(['arguments.text']);
    expect(missingFields(node({ serverId: 's1', toolName: 'send' }, { type: 'mcp.tool' }), mcp, send)).toEqual(['arguments.chat_id', 'arguments.text']);
  });

  it('без дескриптора — нічого не рахується', () => {
    expect(missingFields(node({}), undefined)).toEqual([]);
  });

  it('по вузлах: вимкнені й повністю заповнені вузли пропускаються', () => {
    const nodes = [
      node({}, { id: 'a' }),
      node({}, { id: 'b', disabled: true }),
      node({ model: 'm', prompt: 'p', credential: 'c' }, { id: 'c' }),
    ];
    const result = missingFieldsByNode(nodes, () => llm);
    expect([...result.keys()]).toEqual(['a']);
    expect(result.get('a')?.length).toBe(3);
  });
});
