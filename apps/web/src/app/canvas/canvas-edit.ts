import {
  BusinessStep,
  WorkflowBusiness,
  WorkflowConnection,
  WorkflowDefinition,
  WorkflowNode,
} from '../workflows/workflows-api.service';
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
 * Прибирає з `business.steps` зв'язки на вузли `nodeIds` (F3-67). Крок, що лишився без зв'язків, видаляється:
 * на схемі він нічого не означає, а валідатор такий крок відхиляє. Нічого не змінилось — той самий об'єкт.
 */
export function detachBusinessSteps(business: WorkflowBusiness | undefined, nodeIds: Set<string>): WorkflowBusiness | undefined {
  if (!business || !Array.isArray(business.steps)) return business;
  let changed = false;
  const steps: BusinessStep[] = [];
  for (const step of business.steps) {
    const links = Array.isArray(step?.links) ? step.links : [];
    const kept = links.filter((l) => !nodeIds.has(l?.node));
    if (kept.length === links.length) {
      steps.push(step);
      continue;
    }
    changed = true;
    if (kept.length > 0) steps.push({ ...step, links: kept });
  }
  return changed ? { ...business, steps } : business;
}

/**
 * Видаляє виділене: вузли разом з усіма їхніми ребрами, `pinData` і зв'язками зовнішніх кроків `business.steps`,
 * плюс виділені ребра. Нічого не виділено — те саме визначення.
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
  const business = detachBusinessSteps(definition.business, nodeIds);
  const next: WorkflowDefinition = { ...definition, nodes, connections, pinData };
  if (business !== definition.business) next.business = business;
  return next;
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

/** Перемикає `disabled` вузлів (F3-58): якщо хоч один увімкнений — усі вимикаються, інакше всі вмикаються. */
export function toggleDisabled(definition: WorkflowDefinition, ids: readonly string[]): WorkflowDefinition {
  const set = new Set(ids);
  const targets = definition.nodes.filter((n) => set.has(n.id));
  if (targets.length === 0) return definition;
  const disable = targets.some((n) => !n.disabled);
  return {
    ...definition,
    nodes: definition.nodes.map((n) => {
      if (!set.has(n.id)) return n;
      if (disable) return { ...n, disabled: true };
      const rest = { ...n };
      delete rest.disabled;
      return rest;
    }),
  };
}

/** Результат перейменування: нове визначення або причина, чому назва не підходить. */
export type RenameResult = { definition: WorkflowDefinition; error: null } | { definition: null; error: 'empty' | 'taken' };

/**
 * Перейменовує вузол (F3-58): назва без пробілів по краях, не порожня й унікальна в сценарії. Посилання
 * `$node["Стара"]` / `$node['Стара']` у рядках параметрів усіх вузлів (вирази `{{ }}`, код `core.code`) переписуються на нову
 * назву — інакше вирази зламаються.
 */
export function renameNode(definition: WorkflowDefinition, nodeId: string, name: string): RenameResult {
  const node = definition.nodes.find((n) => n.id === nodeId);
  const next = name.trim();
  if (!node || next === node.name) return { definition, error: null };
  if (!next) return { definition: null, error: 'empty' };
  if (definition.nodes.some((n) => n.id !== nodeId && n.name === next)) return { definition: null, error: 'taken' };
  const old = node.name;
  return {
    definition: {
      ...definition,
      nodes: definition.nodes.map((n) => {
        const parameters = renameReferences(n.parameters, old, next);
        if (n.id === nodeId) return { ...n, name: next, parameters };
        return parameters === n.parameters ? n : { ...n, parameters };
      }),
    },
    error: null,
  };
}

/** Рядки всередині значення: `$node["old"]` → `$node["new"]`; без змін — той самий об'єкт. */
function renameReferences<T>(value: T, oldName: string, newName: string): T {
  if (typeof value === 'string') {
    if (!value.includes('$node')) return value;
    const replaced = value.replace(/\$node\[\s*(["'])((?:\\.|(?!\1).)*)\1\s*\]/g, (match, quote: string, raw: string) => {
      const name = quote === '"' ? safeJson(`"${raw}"`) : raw.replace(/\\(.)/g, '$1');
      return name === oldName ? `$node[${JSON.stringify(newName)}]` : match;
    });
    return (replaced === value ? value : replaced) as T;
  }
  if (Array.isArray(value)) {
    let changed = false;
    const items = value.map((v) => {
      const r = renameReferences(v, oldName, newName);
      if (r !== v) changed = true;
      return r;
    });
    return (changed ? items : value) as T;
  }
  if (value && typeof value === 'object') {
    let changed = false;
    const result: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) {
      const r = renameReferences(v, oldName, newName);
      if (r !== v) changed = true;
      result[k] = r;
    }
    return (changed ? result : value) as T;
  }
  return value;
}

function safeJson(text: string): string | null {
  try {
    return JSON.parse(text) as string;
  } catch {
    return null;
  }
}

/** Напрямок переходу стрілками між вузлами (F3-58). */
export type NeighborDirection = 'left' | 'right' | 'up' | 'down';

/**
 * Сусідній вузол для стрілок: праворуч — перший наступник за ребрами, ліворуч — перший попередник; вгору/вниз —
 * найближчий вузол вище/нижче (відстань по x важить удвічі більше, щоб лишатися в тій самій колонці). Немає — `null`.
 */
export function neighborNode(definition: WorkflowDefinition, nodeId: string, direction: NeighborDirection): string | null {
  const ids = new Set(definition.nodes.map((n) => n.id));
  if (!ids.has(nodeId)) return null;
  if (direction === 'right' || direction === 'left') {
    const linked = definition.connections
      .filter((e) => (direction === 'right' ? e?.from?.node === nodeId : e?.to?.node === nodeId))
      .map((e) => (direction === 'right' ? e.to.node : e.from.node))
      .filter((id) => ids.has(id) && id !== nodeId);
    if (linked.length === 0) return null;
    // Кілька — верхній за позицією, щоб перехід був передбачуваним.
    const pos = new Map(definition.nodes.map((n) => [n.id, parsePosition(n.position)]));
    return [...new Set(linked)].sort((a, b) => pos.get(a)!.y - pos.get(b)!.y || pos.get(a)!.x - pos.get(b)!.x)[0];
  }
  const from = parsePosition(definition.nodes.find((n) => n.id === nodeId)!.position);
  const sign = direction === 'down' ? 1 : -1;
  let best: { id: string; score: number } | null = null;
  for (const n of definition.nodes) {
    if (n.id === nodeId) continue;
    const p = parsePosition(n.position);
    const dy = (p.y - from.y) * sign;
    if (dy <= 0) continue;
    const score = dy + Math.abs(p.x - from.x) * 2;
    if (!best || score < best.score) best = { id: n.id, score };
  }
  return best?.id ?? null;
}
