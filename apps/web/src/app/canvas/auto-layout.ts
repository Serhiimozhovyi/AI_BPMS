import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { parsePosition } from './canvas-model';

/** Проміжок між колонками (від правого краю картки до лівого краю наступної). */
export const LAYOUT_GAP_X = 100;
/** Проміжок між картками в колонці. */
export const LAYOUT_GAP_Y = 40;
/** Крок сітки канвасу — позиції після вирівнювання на ній. */
const GRID = 10;

/**
 * «Вирівняти» (F3-58): шарове розміщення зліва направо за ребрами. Колонка вузла — найдовший шлях від джерел
 * (вузлів без вхідних ребер); у колонці порядок — за середнім рядком попередників (менше перетинів), далі за поточним
 * `y`, `x` і `id` — тож результат детермінований. Колонки центруються по вертикалі відносно найвищої; лівий верхній кут
 * графа лишається там, де був. Змінюються лише `position` вузлів — ребра, параметри й решта полів ті самі.
 *
 * @param width ширина картки на канвасі.
 * @param heightOf висота картки вузла (залежить від кількості портів).
 */
export function layoutLeftToRight(
  definition: WorkflowDefinition,
  width: number,
  heightOf: (node: WorkflowNode) => number,
): WorkflowDefinition {
  const nodes = definition.nodes;
  if (nodes.length === 0) return definition;
  const ids = new Set(nodes.map((n) => n.id));
  const edges = definition.connections.filter((e) => ids.has(e?.from?.node) && ids.has(e?.to?.node) && e.from.node !== e.to.node);
  const position = new Map(nodes.map((n) => [n.id, parsePosition(n.position)]));
  const byPosition = (a: WorkflowNode, b: WorkflowNode) => {
    const pa = position.get(a.id)!;
    const pb = position.get(b.id)!;
    return pa.y - pb.y || pa.x - pb.x || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
  };

  // Колонки: Kahn за ребрами, колонка = найдовший шлях; вузли в циклі (некоректний граф) — у колонку 0.
  const column = new Map<string, number>();
  const indegree = new Map(nodes.map((n) => [n.id, 0]));
  for (const e of edges) indegree.set(e.to.node, indegree.get(e.to.node)! + 1);
  const queue = nodes.filter((n) => indegree.get(n.id) === 0).sort(byPosition).map((n) => n.id);
  for (const id of queue) column.set(id, 0);
  // for…of бачить і елементи, додані в чергу під час обходу.
  for (const id of queue) {
    for (const e of edges) {
      if (e.from.node !== id) continue;
      const to = e.to.node;
      column.set(to, Math.max(column.get(to) ?? 0, column.get(id)! + 1));
      const left = indegree.get(to)! - 1;
      indegree.set(to, left);
      if (left === 0) queue.push(to);
    }
  }
  for (const n of nodes) if (!column.has(n.id)) column.set(n.id, 0);

  // Порядок у колонках: колонка 0 — за поточною позицією; далі — за середнім рядком попередників.
  const columns: WorkflowNode[][] = [];
  for (const n of nodes) (columns[column.get(n.id)!] ??= []).push(n);
  const row = new Map<string, number>();
  columns.forEach((list, c) => {
    const key = (n: WorkflowNode) => {
      if (c === 0) return 0;
      const preds = edges.filter((e) => e.to.node === n.id && row.has(e.from.node)).map((e) => row.get(e.from.node)! + e.from.output * 0.01);
      return preds.length === 0 ? Number.POSITIVE_INFINITY : preds.reduce((a, b) => a + b, 0) / preds.length;
    };
    const keys = new Map(list.map((n) => [n.id, key(n)]));
    list.sort((a, b) => keys.get(a.id)! - keys.get(b.id)! || byPosition(a, b));
    list.forEach((n, i) => row.set(n.id, i));
  });

  const heights = new Map(nodes.map((n) => [n.id, heightOf(n)]));
  const columnHeight = (list: WorkflowNode[] | undefined) =>
    (list ?? []).reduce((sum, n) => sum + heights.get(n.id)!, 0) + Math.max(0, (list?.length ?? 0) - 1) * LAYOUT_GAP_Y;
  const tallest = Math.max(...columns.map(columnHeight));
  const originX = Math.min(...nodes.map((n) => position.get(n.id)!.x));
  const originY = Math.min(...nodes.map((n) => position.get(n.id)!.y));

  const placed = new Map<string, [number, number]>();
  columns.forEach((list, c) => {
    if (!list) return;
    let y = originY + (tallest - columnHeight(list)) / 2;
    for (const n of list) {
      placed.set(n.id, [snap(originX + c * (width + LAYOUT_GAP_X)), snap(y)]);
      y += heights.get(n.id)! + LAYOUT_GAP_Y;
    }
  });

  let changed = false;
  const next = nodes.map((n) => {
    const [x, y] = placed.get(n.id)!;
    const p = position.get(n.id)!;
    if (p.x === x && p.y === y && Array.isArray(n.position)) return n;
    changed = true;
    return { ...n, position: [x, y] as [number, number] };
  });
  return changed ? { ...definition, nodes: next } : definition;
}

function snap(value: number): number {
  return Math.round(value / GRID) * GRID;
}
