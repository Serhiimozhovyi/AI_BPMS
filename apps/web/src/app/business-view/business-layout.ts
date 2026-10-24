import { BusinessEdge, BusinessEdgeKind, BusinessLane, BusinessStep, BusinessView } from './business-model';

/**
 * Розкладка бізнес-вигляду: колонка — ранг кроку в DAG переходів, рядок — доріжка (у доріжці кілька
 * під-рядків, якщо в колонці кілька її кроків). Детермінована: залежить лише від порядку кроків і ребер.
 */

export const LANE_LABEL_WIDTH = 190;
export const STEP_WIDTH = 150;
export const STEP_HEIGHT = 64;
export const COLUMN_GAP = 40;
export const ROW_HEIGHT = 96;
export const LANE_GAP = 12;
const LANE_PADDING = (ROW_HEIGHT - STEP_HEIGHT) / 2;
/** На скільки вершини ромба рішення виступають за прямокутник кроку. */
export const DIAMOND_OVERHANG = 6;

export interface PlacedLane extends BusinessLane {
  y: number;
  height: number;
}

export interface PlacedStep extends BusinessStep {
  column: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PlacedEdge extends BusinessEdge {
  /** SVG-шлях `d` з ортогональних відрізків. */
  path: string;
  labelX: number;
  labelY: number;
}

export interface BusinessLayout {
  width: number;
  height: number;
  lanes: PlacedLane[];
  steps: PlacedStep[];
  edges: PlacedEdge[];
}

export function layoutBusinessView(view: BusinessView): BusinessLayout {
  const columns = assignColumns(view);

  // Під-рядок кроку в доріжці: перший вільний у його колонці.
  const subRow = new Map<string, number>();
  const taken = new Map<string, number>();
  for (const step of view.steps) {
    const key = `${step.lane}|${columns.get(step.id)}`;
    const row = taken.get(key) ?? 0;
    taken.set(key, row + 1);
    subRow.set(step.id, row);
  }

  const lanes: PlacedLane[] = [];
  let y = 0;
  for (const lane of view.lanes) {
    const rows = Math.max(1, ...view.steps.filter((s) => s.lane === lane.id).map((s) => subRow.get(s.id)! + 1));
    lanes.push({ ...lane, y, height: rows * ROW_HEIGHT });
    y += rows * ROW_HEIGHT + LANE_GAP;
  }
  const laneY = new Map(lanes.map((l) => [l.id, l.y]));

  const steps: PlacedStep[] = view.steps.map((step) => {
    const column = columns.get(step.id)!;
    return {
      ...step,
      column,
      x: LANE_LABEL_WIDTH + column * (STEP_WIDTH + COLUMN_GAP),
      y: (laneY.get(step.lane) ?? 0) + subRow.get(step.id)! * ROW_HEIGHT + LANE_PADDING,
      width: STEP_WIDTH,
      height: STEP_HEIGHT,
    };
  });
  const byId = new Map(steps.map((s) => [s.id, s]));

  // Гілки рішення: найближча за висотою виходить праворуч, решта — з нижньої/верхньої вершини ромба.
  const sideBranch = new Map<string, string>();
  for (const step of steps) {
    if (step.kind !== 'decision') continue;
    const forward = view.edges.filter((e) => e.from === step.id && (byId.get(e.to)?.column ?? -1) > step.column);
    const closest = [...forward].sort((a, b) => Math.abs(byId.get(a.to)!.y - step.y) - Math.abs(byId.get(b.to)!.y - step.y))[0];
    if (closest) sideBranch.set(step.id, closest.id);
  }

  const edges = view.edges.flatMap((edge): PlacedEdge[] => {
    const from = byId.get(edge.from);
    const to = byId.get(edge.to);
    if (!from || !to) return [];
    const vertical = from.kind === 'decision' && to.column > from.column && sideBranch.get(from.id) !== edge.id && to.y !== from.y;
    return [{ ...edge, ...(vertical ? decisionBranch(from, to) : route(from, to)) }];
  });

  const maxColumn = Math.max(0, ...steps.map((s) => s.column));
  return {
    width: LANE_LABEL_WIDTH + (maxColumn + 1) * (STEP_WIDTH + COLUMN_GAP),
    height: Math.max(0, y - LANE_GAP),
    lanes,
    steps,
    edges,
  };
}

const FLOW_KINDS: ReadonlySet<BusinessEdgeKind> = new Set(['flow', 'alt']);

/**
 * Колонка кроку вузла — найдовший шлях від джерел за переходами. Зовнішній крок стає в колонку першого
 * зв'язаного вузла: так дія людини в Кіті стоїть над кроком OpenBP, який її читає чи записує.
 */
function assignColumns(view: BusinessView): Map<string, number> {
  const columns = new Map<string, number>();
  const flow = view.edges.filter((e) => FLOW_KINDS.has(e.kind));
  const nodeSteps = view.steps.filter((s) => s.kind !== 'external');
  const indegree = new Map(nodeSteps.map((s) => [s.id, 0]));
  for (const e of flow) if (indegree.has(e.to) && indegree.has(e.from)) indegree.set(e.to, indegree.get(e.to)! + 1);

  const queue = nodeSteps.filter((s) => indegree.get(s.id) === 0).map((s) => s.id);
  for (const id of queue) columns.set(id, 0);
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const e of flow) {
      if (e.from !== id || !indegree.has(e.to)) continue;
      columns.set(e.to, Math.max(columns.get(e.to) ?? 0, columns.get(id)! + 1));
      const left = indegree.get(e.to)! - 1;
      indegree.set(e.to, left);
      if (left === 0) queue.push(e.to);
    }
  }
  // Цикл (некоректний граф) не повинен ламати схему: решта — у колонку 0.
  for (const s of nodeSteps) if (!columns.has(s.id)) columns.set(s.id, 0);

  for (const s of view.steps) {
    if (s.kind !== 'external') continue;
    const link = view.edges.find((e) => (e.from === s.id && columns.has(e.to)) || (e.to === s.id && columns.has(e.from)));
    const other = link ? (link.from === s.id ? link.to : link.from) : undefined;
    columns.set(s.id, other !== undefined ? columns.get(other)! : 0);
  }
  return columns;
}

/**
 * Ортогональний шлях. Ціль правіше — з правого боку джерела в лівий бік цілі зламом посередині
 * проміжку між колонками; інакше — вертикально з верху/низу джерела, по горизонталі над/під кроками, у верх/низ цілі.
 */
function route(from: PlacedStep, to: PlacedStep): { path: string; labelX: number; labelY: number } {
  if (to.column > from.column) {
    const sx = from.x + from.width;
    const sy = from.y + from.height / 2;
    const tx = to.x;
    const ty = to.y + to.height / 2;
    const midX = tx - COLUMN_GAP / 2;
    const path = sy === ty ? `M${sx} ${sy}H${tx}` : `M${sx} ${sy}H${midX}V${ty}H${tx}`;
    return { path, labelX: sy === ty ? sx + 4 : midX + 4, labelY: sy === ty ? sy - 6 : (sy + ty) / 2 };
  }
  const cx = from.x + from.width / 2;
  const tx = to.x + to.width / 2;
  const down = to.y > from.y;
  const sy = down ? from.y + from.height : from.y;
  const ty = down ? to.y : to.y + to.height;
  if (cx === tx) return { path: `M${cx} ${sy}V${ty}`, labelX: cx + 6, labelY: (sy + ty) / 2 };
  const midY = down ? sy + LANE_PADDING : sy - LANE_PADDING;
  return { path: `M${cx} ${sy}V${midY}H${tx}V${ty}`, labelX: Math.min(cx, tx) + 6, labelY: midY - 4 };
}

/** Гілка рішення з нижньої чи верхньої вершини ромба; підпис — біля повороту до цілі, щоб гілки не зливались. */
function decisionBranch(from: PlacedStep, to: PlacedStep): { path: string; labelX: number; labelY: number } {
  const cx = from.x + from.width / 2;
  const down = to.y > from.y;
  const sy = down ? from.y + from.height + DIAMOND_OVERHANG : from.y - DIAMOND_OVERHANG;
  const ty = to.y + to.height / 2;
  return { path: `M${cx} ${sy}V${ty}H${to.x}`, labelX: cx + 6, labelY: ty - 6 };
}
