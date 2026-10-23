import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowConnection, WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';

/**
 * Дані для тестів канвасу: дескриптори, як їх віддає `GET /nodes`, і генератор великого сценарію
 * з топологією спайку F1-11 (docs/adr/ADR-003-canvas.md) — для перевірки рендеру 200 вузлів.
 */

function descriptor(
  type: string,
  displayName: string,
  group: NodeTypeInfo['group'],
  inputs: string[],
  outputs: string[],
  version = 1,
): NodeTypeInfo {
  return { type, version, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

export const SAMPLE_DESCRIPTORS: NodeTypeInfo[] = [
  descriptor('core.manualTrigger', 'Ручний запуск', 'trigger', [], ['main']),
  descriptor('core.webhook', 'Webhook', 'trigger', [], ['main']),
  descriptor('core.schedule', 'Розклад', 'trigger', [], ['main']),
  descriptor('mcp.poll', 'Опитування MCP', 'trigger', [], ['main']),
  descriptor('core.if', 'Умова', 'logic', ['main'], ['true', 'false']),
  descriptor('core.switch', 'Перемикач', 'logic', ['main'], ['rule0', 'rule1', 'rule2', 'rule3', 'fallback']),
  descriptor('core.merge', "Об'єднати", 'logic', ['input1', 'input2'], ['main']),
  descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
  descriptor('core.code', 'Код (JavaScript)', 'transform', ['main'], ['main']),
  descriptor('mcp.tool', 'MCP tool', 'integration', ['main'], ['main']),
  descriptor('ai.llm', 'LLM', 'ai', ['main'], ['main']),
];

/**
 * Сценарій з `count` вузлів (за замовчуванням 200), як `largeGraph` спайку: кожен ряд — ланцюжок
 * тригер → IF/tool/LLM → …, вихід `false` кожного IF веде у наступний ряд.
 */
export function largeWorkflow(count = 200, columns = 10): WorkflowDefinition {
  const nodes: WorkflowNode[] = [];
  const connections: WorkflowConnection[] = [];
  const types = ['core.if', 'mcp.tool', 'ai.llm'];
  for (let i = 0; i < count; i++) {
    const col = i % columns;
    const row = Math.floor(i / columns);
    const first = col === 0;
    const type = first ? 'core.schedule' : types[i % types.length];
    nodes.push({
      id: `n${i}`,
      name: first ? `Тригер ${row + 1}` : `Вузол ${i}`,
      type,
      typeVersion: 1,
      position: [80 + col * 260, 80 + row * 150],
      parameters: type === 'mcp.tool' ? { serverId: 'srv', toolName: 'echo', arguments: {} } : {},
    });
    if (!first) connections.push({ from: { node: `n${i - 1}`, output: 0 }, to: { node: `n${i}`, input: 0 } });
    if (type === 'core.if' && i + columns < count) {
      connections.push({ from: { node: `n${i}`, output: 1 }, to: { node: `n${i + columns}`, input: 0 } });
    }
  }
  return { ...emptyWorkflowDefinition(`Великий сценарій (${count})`), nodes, connections };
}
