import { WorkflowConnection, WorkflowDefinition, WorkflowNode } from './workflows-api.service';

/**
 * Короткий diff двох визначень для історії версій (F3-61): вузли за `id` (додано, видалено, змінено; переміщення без
 * інших змін — окремо), ребра за кінцями й портами (додано, видалено). Чиста функція; назви — для показу людині.
 */
export interface VersionDiff {
  addedNodes: string[];
  removedNodes: string[];
  changedNodes: string[];
  movedNodes: string[];
  addedEdges: string[];
  removedEdges: string[];
  /** Змінилось щось поза вузлами й ребрами: налаштування, `pinData`, бізнес-розмітка. */
  otherChanged: boolean;
}

export function diffDefinitions(before: WorkflowDefinition, after: WorkflowDefinition): VersionDiff {
  const old = new Map(before.nodes.map((n) => [n.id, n]));
  const next = new Map(after.nodes.map((n) => [n.id, n]));
  const diff: VersionDiff = { addedNodes: [], removedNodes: [], changedNodes: [], movedNodes: [], addedEdges: [], removedEdges: [], otherChanged: false };

  for (const node of after.nodes) {
    const was = old.get(node.id);
    if (!was) diff.addedNodes.push(node.name);
    else if (!same(withoutPosition(was), withoutPosition(node))) diff.changedNodes.push(node.name);
    else if (!same(was.position, node.position)) diff.movedNodes.push(node.name);
  }
  for (const node of before.nodes) if (!next.has(node.id)) diff.removedNodes.push(node.name);

  const edgeKey = (names: Map<string, WorkflowNode>) => (c: WorkflowConnection) =>
    `${names.get(c.from.node)?.name ?? c.from.node}#${c.from.output} → ${names.get(c.to.node)?.name ?? c.to.node}#${c.to.input}`;
  const beforeEdges = new Set(before.connections.map(edgeKey(old)));
  const afterEdges = new Set(after.connections.map(edgeKey(next)));
  diff.addedEdges = [...afterEdges].filter((e) => !beforeEdges.has(e));
  diff.removedEdges = [...beforeEdges].filter((e) => !afterEdges.has(e));

  const rest = (def: WorkflowDefinition): Record<string, unknown> => {
    const other: Record<string, unknown> = { ...def };
    delete other['nodes'];
    delete other['connections'];
    delete other['name'];
    return other;
  };
  diff.otherChanged = !same(rest(before), rest(after));
  return diff;
}

/** Diff порожній — версії однакові (крім назви сценарію). */
export function isEmptyDiff(diff: VersionDiff): boolean {
  return (
    !diff.otherChanged &&
    [diff.addedNodes, diff.removedNodes, diff.changedNodes, diff.movedNodes, diff.addedEdges, diff.removedEdges].every((l) => l.length === 0)
  );
}

function withoutPosition(node: WorkflowNode): Record<string, unknown> {
  const copy: Record<string, unknown> = { ...node };
  delete copy['position'];
  return copy;
}

/** Порівняння JSON без урахування порядку ключів. */
function same(a: unknown, b: unknown): boolean {
  return stable(a) === stable(b);
}

function stable(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stable).join(',')}]`;
  if (value && typeof value === 'object') {
    const obj = value as Record<string, unknown>;
    return `{${Object.keys(obj)
      .filter((k) => obj[k] !== undefined)
      .sort()
      .map((k) => `${JSON.stringify(k)}:${stable(obj[k])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

/** Короткий опис змін для людини (F3-61, F6-10); `null` — перша версія. */
export function describeDiff(diff: VersionDiff | null): string {
  if (!diff) return $localize`:@@versions.diff.first:Перша версія.`;
  if (isEmptyDiff(diff)) return $localize`:@@versions.diff.none:Без змін у вузлах і з'єднаннях.`;
  const parts: string[] = [];
  if (diff.addedNodes.length) parts.push($localize`:@@versions.diff.added:додано: ${diff.addedNodes.join(', ')}:names:`);
  if (diff.removedNodes.length) parts.push($localize`:@@versions.diff.removed:видалено: ${diff.removedNodes.join(', ')}:names:`);
  if (diff.changedNodes.length) parts.push($localize`:@@versions.diff.changed:змінено: ${diff.changedNodes.join(', ')}:names:`);
  if (diff.movedNodes.length) parts.push($localize`:@@versions.diff.moved:переміщено: ${diff.movedNodes.length}:count:`);
  const edges = diff.addedEdges.length + diff.removedEdges.length;
  if (edges) parts.push($localize`:@@versions.diff.edges:з'єднань +${diff.addedEdges.length}:added: / −${diff.removedEdges.length}:removed:`);
  if (diff.otherChanged) parts.push($localize`:@@versions.diff.other:налаштування чи дані`);
  return parts.join('; ');
}
