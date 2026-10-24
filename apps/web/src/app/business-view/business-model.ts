import { CanvasModel, CanvasNode, ERROR_OUTPUT } from '../canvas/canvas-model';
import { WorkflowConnection, WorkflowDefinition } from '../workflows/workflows-api.service';

/**
 * Бізнес-вигляд сценарію (docs/business-view.md): доріжки учасників, кроки людською мовою, рішення й
 * очікування. Обчислюється з того самого визначення, окремої моделі немає. Бізнес-атрибути формату
 * (`business` на рівні сценарію, вузла й ребра — F3-67) необов'язкові: без них діють правила за замовчуванням.
 */

export type BusinessStepKind = 'trigger' | 'task' | 'decision' | 'wait' | 'loop' | 'external';

/** `flow` — перехід; `alt` — гілка таймауту чи збою; `read`/`write` — зв'язок із дією людини в зовнішній системі. */
export type BusinessEdgeKind = 'flow' | 'alt' | 'read' | 'write';

export interface BusinessLane {
  id: string;
  name: string;
  description?: string;
}

export interface BusinessStep {
  id: string;
  /** Вузол сценарію; немає — зовнішній крок (дія людини в іншій системі). */
  nodeId?: string;
  kind: BusinessStepKind;
  title: string;
  subtitle?: string;
  /** Коротка позначка: тривалість очікування. */
  badge?: string;
  lane: string;
}

export interface BusinessEdge {
  id: string;
  from: string;
  to: string;
  kind: BusinessEdgeKind;
  label?: string;
  /** Гілка «так»/«ні» рішення — підпис зелений чи червоний, як True/False на схемі AC-Kit. */
  branch?: 'yes' | 'no';
  /** Звідки стрілка у визначенні — щоб змінити її підпис: перше ребро ланцюжка або зв'язок зовнішнього кроку. */
  origin: { connection: number } | { step: string; link: number };
}

export interface BusinessView {
  lanes: BusinessLane[];
  steps: BusinessStep[];
  edges: BusinessEdge[];
}

/** Конектор для назви доріжки: `parameters.serverId` вузла `mcp.*` → назва. */
export interface BusinessConnector {
  id: string;
  name: string;
}

/** Технічні вузли, що в бізнес-вигляді згортаються (ребра через них з'єднуються напряму). */
export const FOLDED_TYPES: ReadonlySet<string> = new Set(['core.set', 'core.code', 'core.merge']);

const DECISION_TYPES = new Set(['core.if', 'core.switch']);
const WAIT_TYPES = new Set(['core.wait', 'core.waitFor']);
const AI_TYPES = new Set(['ai.llm', 'ai.agent']);
const ALT_OUTPUTS = new Set([ERROR_OUTPUT, 'timeout']);

export const LANE_OPENBP = 'openbp';
const LANE_USER = 'user';
const LANE_EXTERNAL = 'external';
const LANE_AI = 'ai';
const LANE_OTHER = 'other';

export function buildBusinessView(
  definition: WorkflowDefinition,
  model: CanvasModel,
  connectors: readonly BusinessConnector[] = [],
): BusinessView {
  const meta = readWorkflowBusiness(definition);
  const nodes = new Map(model.nodes.map((n) => [n.id, n]));
  const visible = new Set(model.nodes.filter(isVisible).map((n) => n.id));
  const connectorNames = new Map(connectors.map((c) => [c.id, c.name]));

  const lanes = new Map<string, BusinessLane>();
  for (const lane of meta.lanes) lanes.set(lane.id, lane);
  const ensureLane = (lane: BusinessLane): string => {
    if (!lanes.has(lane.id)) lanes.set(lane.id, lane);
    return lane.id;
  };

  const steps: BusinessStep[] = [];
  for (const node of model.nodes) {
    if (!visible.has(node.id)) continue;
    const business = readNodeBusiness(node);
    const lane = business.lane && meta.laneIds.has(business.lane) ? business.lane : ensureLane(defaultLane(node, connectorNames));
    const kind = stepKind(node);
    steps.push({
      id: node.id,
      nodeId: node.id,
      kind,
      title: business.title || node.name,
      ...(business.subtitle ? { subtitle: business.subtitle } : {}),
      ...(kind === 'wait' ? optionalBadge(waitBadge(node)) : {}),
      lane,
    });
  }

  const edges = flowEdges(model, nodes, visible);

  for (const step of meta.steps) {
    const links = step.links.filter((l) => visible.has(l.node));
    if (links.length === 0) continue;
    const lane = meta.laneIds.has(step.lane) ? step.lane : ensureLane(otherLane());
    steps.push({
      id: step.id,
      kind: 'external',
      title: step.title,
      ...(step.subtitle ? { subtitle: step.subtitle } : {}),
      lane,
    });
    links.forEach((l, i) => {
      const read = l.direction === 'in';
      edges.push({
        id: `x:${step.id}:${i}`,
        from: read ? step.id : l.node,
        to: read ? l.node : step.id,
        kind: read ? 'read' : 'write',
        ...(l.label ? { label: l.label } : {}),
        origin: { step: step.id, link: l.index },
      });
    });
  }

  return { lanes: orderLanes(lanes, meta.laneIds, steps), steps, edges };
}

function isVisible(node: CanvasNode): boolean {
  const hidden = readNodeBusiness(node).hidden;
  if (hidden !== undefined) return !hidden;
  return !node.disabled && !FOLDED_TYPES.has(node.type);
}

function stepKind(node: CanvasNode): BusinessStepKind {
  if (DECISION_TYPES.has(node.type)) return 'decision';
  if (WAIT_TYPES.has(node.type)) return 'wait';
  if (node.type === 'core.loop') return 'loop';
  if (node.inputs.length === 0 || node.group === 'trigger') return 'trigger';
  return 'task';
}

function defaultLane(node: CanvasNode, connectorNames: ReadonlyMap<string, string>): BusinessLane {
  if (node.type === 'mcp.tool' || node.type === 'mcp.poll') {
    const serverId = node.source.parameters?.['serverId'];
    const name = typeof serverId === 'string' ? connectorNames.get(serverId) : undefined;
    return name ? { id: `connector:${serverId as string}`, name } : otherLane();
  }
  if (node.type === 'core.manualTrigger') return { id: LANE_USER, name: $localize`:@@business.lane.user:Користувач` };
  if (node.type === 'core.webhook') return { id: LANE_EXTERNAL, name: $localize`:@@business.lane.external:Зовнішня система` };
  if (AI_TYPES.has(node.type)) return { id: LANE_AI, name: $localize`:@@business.lane.ai:ШІ` };
  return { id: LANE_OPENBP, name: 'OpenBP' };
}

function otherLane(): BusinessLane {
  return { id: LANE_OTHER, name: $localize`:@@business.lane.other:Інша система` };
}

/** Власні доріжки — у заданому порядку; решта — за першою появою кроку. */
function orderLanes(lanes: ReadonlyMap<string, BusinessLane>, own: ReadonlySet<string>, steps: readonly BusinessStep[]): BusinessLane[] {
  const used = new Set(steps.map((s) => s.lane));
  const result = [...lanes.values()].filter((l) => own.has(l.id));
  for (const step of steps) {
    if (!own.has(step.lane) && !result.some((l) => l.id === step.lane)) result.push(lanes.get(step.lane)!);
  }
  return result.filter((l) => own.has(l.id) || used.has(l.id));
}

/**
 * Ребра між видимими кроками: ребро в згорнутий вузол продовжується його виходами, поки не дійде до видимих.
 * Підпис і вид — з першого ребра ланцюжка (виходу видимого кроку).
 */
function flowEdges(model: CanvasModel, nodes: ReadonlyMap<string, CanvasNode>, visible: ReadonlySet<string>): BusinessEdge[] {
  // Ребра моделі канвасу йдуть у тому ж порядку, що й `connections` визначення: індекс — позиція ребра.
  const outgoing = new Map<string, { c: WorkflowConnection; index: number }[]>();
  model.edges.forEach((edge, index) => {
    const list = outgoing.get(edge.source) ?? [];
    list.push({ c: edge.origin, index });
    outgoing.set(edge.source, list);
  });

  const reachVisible = (start: string): string[] => {
    const found: string[] = [];
    const seen = new Set<string>();
    const stack = [start];
    while (stack.length > 0) {
      const id = stack.pop()!;
      if (seen.has(id) || !nodes.has(id)) continue;
      seen.add(id);
      if (visible.has(id)) {
        found.push(id);
        continue;
      }
      for (const { c } of outgoing.get(id) ?? []) stack.push(c?.to?.node);
    }
    return found;
  };

  const result: BusinessEdge[] = [];
  const keys = new Set<string>();
  for (const node of model.nodes) {
    if (!visible.has(node.id)) continue;
    for (const { c, index } of outgoing.get(node.id) ?? []) {
      const output = node.outputs[c?.from?.output] ?? '';
      const kind: BusinessEdgeKind = ALT_OUTPUTS.has(output) ? 'alt' : 'flow';
      const label = connectionLabel(c) ?? outputLabel(node, output);
      for (const target of reachVisible(c?.to?.node)) {
        const key = `${node.id}->${target}:${label ?? ''}`;
        if (keys.has(key)) continue;
        keys.add(key);
        const branch = output === 'true' ? 'yes' : output === 'false' ? 'no' : undefined;
        result.push({ id: `f:${key}`, from: node.id, to: target, kind, ...(label ? { label } : {}), ...(branch ? { branch } : {}), origin: { connection: index } });
      }
    }
  }
  return result;
}

/** Підпис гілки: лише для рішень, вузлів з кількома виходами і гілок збою/таймауту. */
function outputLabel(node: CanvasNode, output: string): string | undefined {
  if (output === 'true') return $localize`:@@business.branch.yes:так`;
  if (output === 'false') return $localize`:@@business.branch.no:ні`;
  if (output === ERROR_OUTPUT) return $localize`:@@business.branch.error:збій`;
  if (output === 'timeout') return $localize`:@@business.branch.timeout:таймаут`;
  if (output === 'fallback') return $localize`:@@business.branch.fallback:інакше`;
  const rule = /^rule(\d+)$/.exec(output);
  if (rule) return $localize`:@@business.branch.rule:варіант ${Number(rule[1]) + 1}:n:`;
  const unnamed = !output || output === 'main' || output === 'item' || /^\d+$/.test(output);
  return node.outputs.length > 1 && !unnamed ? output : undefined;
}

const UNIT_LABELS: Record<string, () => string> = {
  milliseconds: () => $localize`:@@business.unit.ms:мс`,
  seconds: () => $localize`:@@business.unit.s:с`,
  minutes: () => $localize`:@@business.unit.min:хв`,
  hours: () => $localize`:@@business.unit.h:год`,
  days: () => $localize`:@@business.unit.d:дн`,
};

function waitBadge(node: CanvasNode): string | undefined {
  const amount = node.source.parameters?.['amount'];
  if (typeof amount !== 'number') return undefined;
  const unit = node.source.parameters?.['unit'];
  const label = UNIT_LABELS[typeof unit === 'string' ? unit : 'seconds'];
  return label ? `${amount} ${label()}` : String(amount);
}

function optionalBadge(badge: string | undefined): { badge?: string } {
  return badge ? { badge } : {};
}

// --- Бізнес-атрибути формату (F3-67). Читаються обережно: будь-яке поле може бути відсутнє чи некоректне.

interface NodeBusiness {
  title?: string;
  subtitle?: string;
  lane?: string;
  hidden?: boolean;
}

interface ExternalStepLink {
  /** Індекс у `links` визначення. */
  index: number;
  node: string;
  direction: 'in' | 'out';
  label?: string;
}

interface ExternalStep {
  id: string;
  title: string;
  subtitle?: string;
  lane: string;
  links: ExternalStepLink[];
}

interface WorkflowBusiness {
  lanes: BusinessLane[];
  laneIds: Set<string>;
  steps: ExternalStep[];
}

function readNodeBusiness(node: CanvasNode): NodeBusiness {
  const raw = asRecord(node.source['business']);
  return {
    title: str(raw['title']),
    subtitle: str(raw['subtitle']),
    lane: str(raw['lane']),
    hidden: typeof raw['hidden'] === 'boolean' ? raw['hidden'] : undefined,
  };
}

function connectionLabel(c: WorkflowConnection | undefined): string | undefined {
  return str(asRecord(c?.['business'])['label']);
}

export function readWorkflowBusiness(definition: WorkflowDefinition): WorkflowBusiness {
  const raw = asRecord(definition['business']);
  const lanes: BusinessLane[] = [];
  const laneIds = new Set<string>();
  for (const item of Array.isArray(raw['lanes']) ? raw['lanes'] : []) {
    const lane = asRecord(item);
    const id = str(lane['id']);
    if (!id || laneIds.has(id)) continue;
    laneIds.add(id);
    const description = str(lane['description']);
    lanes.push({ id, name: str(lane['name']) || id, ...(description ? { description } : {}) });
  }
  const steps: ExternalStep[] = [];
  for (const item of Array.isArray(raw['steps']) ? raw['steps'] : []) {
    const step = asRecord(item);
    const id = str(step['id']);
    const title = str(step['title']);
    if (!id || !title) continue;
    const links: ExternalStepLink[] = [];
    const rawLinks: unknown[] = Array.isArray(step['links']) ? step['links'] : [];
    for (const [index, l] of rawLinks.entries()) {
      const link = asRecord(l);
      const node = str(link['node']);
      if (!node) continue;
      const label = str(link['label']);
      links.push({ index, node, direction: link['direction'] === 'out' ? 'out' : 'in', ...(label ? { label } : {}) });
    }
    const subtitle = str(step['subtitle']);
    steps.push({ id, title, lane: str(step['lane']) ?? '', links, ...(subtitle ? { subtitle } : {}) });
  }
  return { lanes, laneIds, steps };
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined;
}
