/**
 * Модель графа для спайку F1-11 (docs/adr/ADR-003-canvas.md). Обидва прототипи — ngx-vflow і Rete.js —
 * рендерили один і той самий набір вузлів, щоб порівняння було чесним; після рішення лишився ngx-vflow.
 * До F2-F слугує демо-даними редактора і джерелом графів для перевірки продуктивності.
 */

export type SpikeNodeGroup = 'trigger' | 'ai' | 'integration' | 'logic';

export interface SpikeNode {
  id: string;
  name: string;
  /** Тип вузла з формату workflow, напр. `core.webhook`, `mcp.tool`. */
  type: string;
  group: SpikeNodeGroup;
  x: number;
  y: number;
  /** Назви вхідних портів; порожній масив — тригер. */
  inputs: string[];
  /** Назви вихідних портів у порядку індексів. */
  outputs: string[];
}

export interface SpikeEdge {
  id: string;
  source: string;
  sourceOutput: number;
  target: string;
  targetInput: number;
}

export interface SpikeGraph {
  nodes: SpikeNode[];
  edges: SpikeEdge[];
}

/** Три вузли з прикладу docs/workflow-format.md: webhook → ai.llm → mcp.tool. */
export function demoGraph(): SpikeGraph {
  const nodes: SpikeNode[] = [
    { id: 'n_webhook', name: 'Нова заявка', type: 'core.webhook', group: 'trigger', x: 80, y: 160, inputs: [], outputs: ['main'] },
    { id: 'n_llm', name: 'Розібрати заявку', type: 'ai.llm', group: 'ai', x: 380, y: 160, inputs: ['main'], outputs: ['main'] },
    { id: 'n_1c', name: '1С: створити замовлення', type: 'mcp.tool', group: 'integration', x: 680, y: 160, inputs: ['main'], outputs: ['main', 'error'] },
  ];
  const edges: SpikeEdge[] = [
    { id: 'e1', source: 'n_webhook', sourceOutput: 0, target: 'n_llm', targetInput: 0 },
    { id: 'e2', source: 'n_llm', sourceOutput: 0, target: 'n_1c', targetInput: 0 },
  ];
  return { nodes, edges };
}

/**
 * Сітка з `count` вузлів (за замовчуванням 200): кожен ряд — ланцюжок тригер → IF → …,
 * вихід `false` кожного IF веде у наступний ряд. Дає реалістичну щільність ребер.
 */
export function largeGraph(count = 200, columns = 10): SpikeGraph {
  const nodes: SpikeNode[] = [];
  const edges: SpikeEdge[] = [];
  const groups: SpikeNodeGroup[] = ['logic', 'integration', 'ai'];
  for (let i = 0; i < count; i++) {
    const col = i % columns;
    const row = Math.floor(i / columns);
    const first = col === 0;
    const group = first ? 'trigger' : groups[i % groups.length];
    nodes.push({
      id: `n${i}`,
      name: first ? `Тригер ${row + 1}` : `Вузол ${i}`,
      type: first ? 'core.schedule' : group === 'logic' ? 'core.if' : group === 'ai' ? 'ai.llm' : 'mcp.tool',
      group,
      x: 80 + col * 260,
      y: 80 + row * 150,
      inputs: first ? [] : ['main'],
      outputs: group === 'logic' ? ['true', 'false'] : ['main'],
    });
    if (!first) {
      edges.push({ id: `e${i}`, source: `n${i - 1}`, sourceOutput: 0, target: `n${i}`, targetInput: 0 });
    }
    if (group === 'logic' && i + columns < count) {
      edges.push({ id: `e${i}-down`, source: `n${i}`, sourceOutput: 1, target: `n${i + columns}`, targetInput: 0 });
    }
  }
  return { nodes, edges };
}

/** Колір групи для картки вузла — однаковий в обох прототипах. */
export const GROUP_COLORS: Record<SpikeNodeGroup, string> = {
  trigger: '#2e7d32',
  ai: '#6a1b9a',
  integration: '#1565c0',
  logic: '#ef6c00',
};

export const GROUP_LABELS: Record<SpikeNodeGroup, string> = {
  trigger: 'Тригер',
  ai: 'ШІ',
  integration: 'MCP',
  logic: 'Логіка',
};
