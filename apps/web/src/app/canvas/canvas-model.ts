import { NodeGroup, NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowConnection, WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';

/**
 * Модель канвасу — проєкція визначення сценарію (docs/workflow-format.md) для відображення.
 * Джерело правди — `WorkflowDefinition`: модель тримає посилання на вихідні вузли й ребра, тож
 * {@link fromCanvasModel} повертає визначення без втрат, з усіма невідомими полями на будь-якому рівні.
 */

/** Група картки: група дескриптора або `unknown` — тип не зареєстровано в Api. */
export type CanvasNodeGroup = NodeGroup | 'unknown';

export interface CanvasNode {
  id: string;
  name: string;
  type: string;
  /** Другий рядок картки: тип вузла або назва tool'а для `mcp.tool`. */
  subtitle: string;
  group: CanvasNodeGroup;
  x: number;
  y: number;
  /** Назви вхідних портів за індексами; порожньо — тригер. */
  inputs: string[];
  /** Назви вихідних портів за індексами; `continueOnFail` додає останній `error`. */
  outputs: string[];
  disabled: boolean;
  /** Вихідний вузол визначення — незмінний. */
  source: WorkflowNode;
}

export interface CanvasEdge {
  id: string;
  source: string;
  sourceOutput: number;
  target: string;
  targetInput: number;
  /** Вихідне ребро визначення — незмінне. */
  origin: WorkflowConnection;
}

export interface CanvasModel {
  nodes: CanvasNode[];
  edges: CanvasEdge[];
}

/** Нова позиція вузла (після перетягування). */
export interface NodePosition {
  id: string;
  x: number;
  y: number;
}

/** Порт `error`, який движок додає вузлам з `continueOnFail: true` (docs/workflow-format.md, «Правила»). */
export const ERROR_OUTPUT = 'error';

/** Індекс дескрипторів: точна версія типу або, якщо її немає, найновіша. */
export class DescriptorIndex {
  private readonly exact = new Map<string, NodeTypeInfo>();
  private readonly latestByType = new Map<string, NodeTypeInfo>();

  constructor(descriptors: readonly NodeTypeInfo[]) {
    for (const d of descriptors) {
      this.exact.set(`${d.type}@${d.version}`, d);
      const current = this.latestByType.get(d.type);
      if (!current || current.version < d.version) this.latestByType.set(d.type, d);
    }
  }

  find(type: string, version?: number): NodeTypeInfo | undefined {
    return (version !== undefined ? this.exact.get(`${type}@${version}`) : undefined) ?? this.latestByType.get(type);
  }

  latest(type: string): NodeTypeInfo | undefined {
    return this.latestByType.get(type);
  }
}

/** `[x, y]` з поля `position`; некоректне значення — `(0, 0)`. */
export function parsePosition(position: unknown): { x: number; y: number } {
  if (Array.isArray(position) && typeof position[0] === 'number' && typeof position[1] === 'number') {
    return { x: position[0], y: position[1] };
  }
  return { x: 0, y: 0 };
}

/** Визначення сценарію → модель канвасу. Порти — з дескрипторів; невідомий тип отримує порти за ребрами. */
export function toCanvasModel(
  definition: WorkflowDefinition,
  descriptors: readonly NodeTypeInfo[] | DescriptorIndex,
): CanvasModel {
  const index = descriptors instanceof DescriptorIndex ? descriptors : new DescriptorIndex(descriptors);
  const connections = Array.isArray(definition.connections) ? definition.connections : [];
  const nodes = Array.isArray(definition.nodes) ? definition.nodes : [];

  // Найбільші індекси портів, на які посилаються ребра: щоб жодне ребро не зникло з канвасу,
  // навіть якщо дескриптор невідомий або має менше портів (стара версія, помилка в JSON).
  const maxOutput = new Map<string, number>();
  const maxInput = new Map<string, number>();
  for (const c of connections) {
    const from = c?.from;
    const to = c?.to;
    if (from && typeof from.output === 'number') {
      maxOutput.set(from.node, Math.max(maxOutput.get(from.node) ?? -1, from.output));
    }
    if (to && typeof to.input === 'number') {
      maxInput.set(to.node, Math.max(maxInput.get(to.node) ?? -1, to.input));
    }
  }

  const canvasNodes = nodes.map((node): CanvasNode => {
    const descriptor = index.find(node.type, node.typeVersion);
    const inputs = descriptor ? visibleInputs(node, descriptor, maxInput.get(node.id) ?? -1) : [];
    const outputs = descriptor ? visibleOutputs(node, descriptor, maxOutput.get(node.id) ?? -1) : [];
    if (node.continueOnFail === true) outputs.push(ERROR_OUTPUT);
    padPorts(inputs, maxInput.get(node.id) ?? -1, descriptor ? undefined : 'main');
    padPorts(outputs, maxOutput.get(node.id) ?? -1, descriptor ? undefined : 'main');
    const { x, y } = parsePosition(node.position);
    return {
      id: node.id,
      name: node.name,
      type: node.type,
      subtitle: subtitleOf(node),
      group: descriptor?.group ?? 'unknown',
      x,
      y,
      inputs,
      outputs,
      disabled: node.disabled === true,
      source: node,
    };
  });

  const canvasEdges = connections.map(
    (c, i): CanvasEdge => ({
      id: `e${i}:${c?.from?.node}:${c?.from?.output}->${c?.to?.node}:${c?.to?.input}`,
      source: c?.from?.node,
      sourceOutput: c?.from?.output,
      target: c?.to?.node,
      targetInput: c?.to?.input,
      origin: c,
    }),
  );

  return { nodes: canvasNodes, edges: canvasEdges };
}

/**
 * Модель канвасу → визначення сценарію. Поля, яких канвас не торкався, переносяться з `base` і вихідних
 * вузлів/ребер без змін (зокрема невідомі); `position` і кінці ребер оновлюються лише якщо змінились.
 */
export function fromCanvasModel(model: CanvasModel, base: WorkflowDefinition): WorkflowDefinition {
  return {
    ...base,
    nodes: model.nodes.map((n) => {
      const { x, y } = parsePosition(n.source.position);
      return x !== n.x || y !== n.y ? { ...n.source, position: [n.x, n.y] as [number, number] } : n.source;
    }),
    connections: model.edges.map((e) => {
      const o = e.origin;
      const same =
        o?.from?.node === e.source && o?.from?.output === e.sourceOutput && o?.to?.node === e.target && o?.to?.input === e.targetInput;
      return same
        ? o
        : {
            ...o,
            from: { ...o?.from, node: e.source, output: e.sourceOutput },
            to: { ...o?.to, node: e.target, input: e.targetInput },
          };
    }),
  };
}

/** Переносить нові позиції вузлів у визначення; вузли без змін і решта полів — ті самі об'єкти. */
export function moveNodes(
  definition: WorkflowDefinition,
  positions: readonly NodePosition[],
  descriptors: readonly NodeTypeInfo[] | DescriptorIndex,
): WorkflowDefinition {
  if (positions.length === 0) return definition;
  const byId = new Map(positions.map((p) => [p.id, p]));
  const model = toCanvasModel(definition, descriptors);
  const nodes = model.nodes.map((n) => {
    const p = byId.get(n.id);
    return p ? { ...n, x: p.x, y: p.y } : n;
  });
  return fromCanvasModel({ ...model, nodes }, definition);
}

/**
 * `core.switch` версії 2 має виходи `fallback, rule0…rule15`: показуємо `fallback` і по порту на кожне задане правило.
 * Порт, з якого вже йде ребро, показуємо завжди, щоб ребро не зникло. З `continueOnFail` порт `error`
 * стоїть одразу після всіх виходів дескриптора, тож тоді показуємо всі.
 */
function visibleOutputs(node: WorkflowNode, descriptor: NodeTypeInfo, maxEdgeOutput: number): string[] {
  const outputs = [...descriptor.outputs];
  if (node.type !== 'core.switch' || descriptor.version < 2 || node.continueOnFail === true) return outputs;
  const rules = node.parameters?.['rules'];
  const count = Array.isArray(rules) ? rules.length : 0;
  return outputs.slice(0, Math.max(1 + Math.max(1, count), maxEdgeOutput + 1));
}

/**
 * `core.merge` версії 2 має входи `input1…input8` (F7-06): показуємо `parameters.branches` (типово 2) і кожен
 * вхід, до якого вже йде ребро.
 */
function visibleInputs(node: WorkflowNode, descriptor: NodeTypeInfo, maxEdgeInput: number): string[] {
  const inputs = [...descriptor.inputs];
  if (node.type !== 'core.merge' || descriptor.version < 2) return inputs;
  const branches = node.parameters?.['branches'];
  const count = typeof branches === 'number' && Number.isInteger(branches) ? branches : 2;
  return inputs.slice(0, Math.max(Math.min(Math.max(2, count), inputs.length), maxEdgeInput + 1));
}

function padPorts(ports: string[], maxIndex: number, prefix: string | undefined): void {
  while (ports.length <= maxIndex) {
    ports.push(prefix && ports.length === 0 ? prefix : String(ports.length));
  }
}

function subtitleOf(node: WorkflowNode): string {
  if (node.type === 'mcp.tool' || node.type === 'mcp.poll' || node.type === 'mcp.waitFor') {
    const toolName = node.parameters?.['toolName'];
    if (typeof toolName === 'string' && toolName) return `${node.type} · ${toolName}`;
  }
  return node.type;
}
