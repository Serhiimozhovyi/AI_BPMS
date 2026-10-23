import { WorkflowConnection, WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { parsePosition } from './canvas-model';

/**
 * Редагування визначення сценарію з канвасу (F2-17): чисті функції `визначення → нове визначення`.
 * Не змінюють вхідні об'єкти; вузли й ребра, яких дія не торкається, — ті самі об'єкти (round-trip невідомих полів).
 */

/** Нове ребро: вихід `sourceOutput` вузла `source` → вхід `targetInput` вузла `target`. */
export interface NewConnection {
  source: string;
  sourceOutput: number;
  target: string;
  targetInput: number;
}

/** Що виділено на канвасі: id вузлів і індекси ребер у `connections`. */
export interface CanvasSelection {
  nodes: readonly string[];
  edges: readonly number[];
}

export const EMPTY_SELECTION: CanvasSelection = { nodes: [], edges: [] };

/** Скопійовані вузли і ребра між ними (ребра назовні копії не потрапляють). */
export interface CanvasClipboard {
  nodes: WorkflowNode[];
  connections: WorkflowConnection[];
}

/** Зсув вставленої копії відносно оригіналу (на кожну наступну вставку — ще раз). */
export const PASTE_OFFSET = 40;

/**
 * Додає ребро. Не додає (повертає те саме визначення), якщо такого вузла немає, ребро вже є
 * або це петля на той самий вузол — цикли формат забороняє (docs/workflow-format.md, «Правила»).
 */
export function connectNodes(definition: WorkflowDefinition, c: NewConnection): WorkflowDefinition {
  if (c.source === c.target) return definition;
  const ids = new Set(definition.nodes.map((n) => n.id));
  if (!ids.has(c.source) || !ids.has(c.target)) return definition;
  if (!Number.isInteger(c.sourceOutput) || c.sourceOutput < 0 || !Number.isInteger(c.targetInput) || c.targetInput < 0) {
    return definition;
  }
  const exists = definition.connections.some(
    (e) => e?.from?.node === c.source && e?.from?.output === c.sourceOutput && e?.to?.node === c.target && e?.to?.input === c.targetInput,
  );
  if (exists) return definition;
  const connection: WorkflowConnection = {
    from: { node: c.source, output: c.sourceOutput },
    to: { node: c.target, input: c.targetInput },
  };
  return { ...definition, connections: [...definition.connections, connection] };
}

/**
 * Видаляє виділене: вузли разом з усіма їхніми ребрами і `pinData`, плюс виділені ребра.
 * Нічого не виділено — те саме визначення.
 */
export function deleteSelection(definition: WorkflowDefinition, selection: CanvasSelection): WorkflowDefinition {
  const nodeIds = new Set(selection.nodes);
  const edgeIndexes = new Set(selection.edges);
  const keptNodes = definition.nodes.filter((n) => !nodeIds.has(n.id));
  const keptConnections = definition.connections.filter(
    (e, i) => !edgeIndexes.has(i) && !nodeIds.has(e?.from?.node) && !nodeIds.has(e?.to?.node),
  );
  const nodesChanged = keptNodes.length !== definition.nodes.length;
  const connectionsChanged = keptConnections.length !== definition.connections.length;
  if (!nodesChanged && !connectionsChanged) return definition;
  const nodes = nodesChanged ? keptNodes : definition.nodes;
  const connections = connectionsChanged ? keptConnections : definition.connections;

  let pinData = definition.pinData;
  if (pinData && typeof pinData === 'object' && Object.keys(pinData).some((k) => nodeIds.has(k))) {
    pinData = Object.fromEntries(Object.entries(pinData).filter(([k]) => !nodeIds.has(k)));
  }
  return { ...definition, nodes, connections, pinData };
}

/** Копіює виділені вузли і ребра між ними. Нічого не виділено — `null`. */
export function copySelection(definition: WorkflowDefinition, selection: CanvasSelection): CanvasClipboard | null {
  const ids = new Set(selection.nodes);
  const nodes = definition.nodes.filter((n) => ids.has(n.id));
  if (nodes.length === 0) return null;
  const connections = definition.connections.filter((e) => ids.has(e?.from?.node) && ids.has(e?.to?.node));
  // Глибока копія: буфер не повинен змінюватись разом зі сценарієм.
  return structuredClone({ nodes, connections });
}

/** Результат вставки: нове визначення і id вставлених вузлів (щоб виділити їх). */
export interface PasteResult {
  definition: WorkflowDefinition;
  nodeIds: string[];
}

/**
 * Вставляє копію з буфера: нові унікальні `id`, унікальні `name` («Назва 2»), позиції зі зсувом
 * `PASTE_OFFSET × times`, ребра між вставленими вузлами перемаплені на нові `id`. Решта полів вузла — як у копії.
 */
export function pasteClipboard(
  definition: WorkflowDefinition,
  clipboard: CanvasClipboard,
  newId: () => string,
  times = 1,
): PasteResult {
  const ids = new Set(definition.nodes.map((n) => n.id));
  const names = new Set(definition.nodes.map((n) => n.name));
  const idMap = new Map<string, string>();
  const offset = PASTE_OFFSET * times;

  const pasted = structuredClone(clipboard.nodes).map((node): WorkflowNode => {
    let id = newId();
    while (ids.has(id)) id = newId();
    ids.add(id);
    idMap.set(node.id, id);
    const name = uniqueName(node.name, names);
    names.add(name);
    const { x, y } = parsePosition(node.position);
    return { ...node, id, name, position: [x + offset, y + offset] };
  });

  const connections = structuredClone(clipboard.connections)
    .filter((e) => idMap.has(e?.from?.node) && idMap.has(e?.to?.node))
    .map(
      (e): WorkflowConnection => ({
        ...e,
        from: { ...e.from, node: idMap.get(e.from.node)! },
        to: { ...e.to, node: idMap.get(e.to.node)! },
      }),
    );

  return {
    definition: { ...definition, nodes: [...definition.nodes, ...pasted], connections: [...definition.connections, ...connections] },
    nodeIds: pasted.map((n) => n.id),
  };
}

/** `name`, якщо вільна, інакше `name 2`, `name 3`… (суфікс-число копії замінюється, а не накопичується). */
function uniqueName(name: string, taken: ReadonlySet<string>): string {
  if (!taken.has(name)) return name;
  const base = name.replace(/ \d+$/, '');
  for (let i = 2; ; i++) {
    const candidate = `${base} ${i}`;
    if (!taken.has(candidate)) return candidate;
  }
}
