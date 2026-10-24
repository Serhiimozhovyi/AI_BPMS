import { BusinessLane, BusinessStep as FormatStep, WorkflowBusiness, WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { BusinessEdge, BusinessView, FOLDED_TYPES } from './business-model';

/**
 * Правки бізнес-атрибутів з бізнес-вигляду (F3-68). Змінюють лише `business` сценарію, вузла чи ребра —
 * вузли, ребра й параметри лишаються ті самі. Кожна функція повертає нове визначення або те саме, якщо змін немає.
 * Валідатор (F3-67) вимагає, щоб доріжка кроку була в `business.lanes`, тому доріжку за замовчуванням, у яку
 * переносять крок або яку перейменовують, спершу оголошуємо.
 */

/** Нова назва кроку: для вузла — `business.title` (порожньо чи як `name` — прибрати), для дії людини — `title`. */
export function setStepTitle(definition: WorkflowDefinition, stepId: string, title: string): WorkflowDefinition {
  const value = title.trim();
  const node = findNode(definition, stepId);
  if (node) return updateNodeBusiness(definition, stepId, { title: value && value !== node.name ? value : undefined });
  if (!value) return definition;
  return updateFormatStep(definition, stepId, (s) => ({ ...s, title: value }));
}

/** Підрядок кроку; порожньо — прибрати. */
export function setStepSubtitle(definition: WorkflowDefinition, stepId: string, subtitle: string): WorkflowDefinition {
  const value = subtitle.trim() || undefined;
  if (findNode(definition, stepId)) return updateNodeBusiness(definition, stepId, { subtitle: value });
  return updateFormatStep(definition, stepId, (s) => withOptional(s, 'subtitle', value));
}

/**
 * Перенести крок у доріжку. Доріжку за замовчуванням спершу оголошуємо в `business.lanes` з усіма видимими.
 * Порожній `laneId` для вузла — повернути доріжку за типом вузла.
 */
export function moveStepToLane(definition: WorkflowDefinition, view: BusinessView, stepId: string, laneId: string): WorkflowDefinition {
  if (!laneId) return findNode(definition, stepId) ? updateNodeBusiness(definition, stepId, { lane: undefined }) : definition;
  const declared = declareLanes(definition, view, [laneId]);
  if (findNode(declared, stepId)) return updateNodeBusiness(declared, stepId, { lane: laneId });
  return updateFormatStep(declared, stepId, (s) => ({ ...s, lane: laneId }));
}

/**
 * Сховати вузол з бізнес-вигляду чи показати його. `hidden` пишемо лише тоді, коли він відрізняється від правила
 * за замовчуванням (згорнуті типи й вимкнені вузли сховані й так).
 */
export function setStepHidden(definition: WorkflowDefinition, nodeId: string, hidden: boolean): WorkflowDefinition {
  const node = findNode(definition, nodeId);
  if (!node) return definition;
  const hiddenByDefault = node.disabled === true || FOLDED_TYPES.has(node.type);
  return updateNodeBusiness(definition, nodeId, { hidden: hidden === hiddenByDefault ? undefined : hidden });
}

/**
 * Нова дія людини в доріжці `laneId`, пов'язана з кроком вузла `nodeId`: `in` — сценарій читає її результат,
 * `out` — сценарій ставить її людині. Повертає визначення й id нового кроку.
 */
export function addExternalStep(
  definition: WorkflowDefinition,
  view: BusinessView,
  laneId: string,
  nodeId: string,
  title: string,
  direction: 'in' | 'out' = 'in',
): { definition: WorkflowDefinition; stepId: string } {
  const declared = declareLanes(definition, view, [laneId]);
  const business = declared.business ?? {};
  const steps = business.steps ?? [];
  const stepId = uniqueId('s', new Set(steps.map((s) => s.id)));
  const step: FormatStep = { id: stepId, title, lane: laneId, links: [{ node: nodeId, direction }] };
  return { definition: { ...declared, business: { ...business, steps: [...steps, step] } }, stepId };
}

/** Видалити дію людини разом з її зв'язками. */
export function removeExternalStep(definition: WorkflowDefinition, stepId: string): WorkflowDefinition {
  const steps = definition.business?.steps;
  if (!steps?.some((s) => s.id === stepId)) return definition;
  return { ...definition, business: { ...definition.business, steps: steps.filter((s) => s.id !== stepId) } };
}

/** Перемкнути напрям зв'язку дії людини: «читає» ↔ «ставить задачу». */
export function toggleLinkDirection(definition: WorkflowDefinition, stepId: string, link: number): WorkflowDefinition {
  return updateFormatStep(definition, stepId, (s) => ({
    ...s,
    links: s.links.map((l, i) => (i === link ? { ...l, direction: l.direction === 'in' ? 'out' : 'in' } : l)),
  }));
}

/** Додати зв'язок дії людини з ще одним вузлом (повторний з тим самим вузлом і напрямом — без змін). */
export function linkExternalStep(definition: WorkflowDefinition, stepId: string, nodeId: string, direction: 'in' | 'out'): WorkflowDefinition {
  const step = definition.business?.steps?.find((s) => s.id === stepId);
  if (!step || step.links.some((l) => l.node === nodeId && l.direction === direction) || !findNode(definition, nodeId)) return definition;
  return updateFormatStep(definition, stepId, (s) => ({ ...s, links: [...s.links, { node: nodeId, direction }] }));
}

/** Підпис стрілки; порожньо — прибрати (повертається назва виходу). */
export function setEdgeLabel(definition: WorkflowDefinition, edge: BusinessEdge, label: string): WorkflowDefinition {
  const value = label.trim() || undefined;
  const origin = edge.origin;
  if ('connection' in origin) {
    const connection = definition.connections[origin.connection];
    if (!connection) return definition;
    const business = withOptional(connection.business ?? {}, 'label', value);
    const next = Object.keys(business).length > 0 ? { ...connection, business } : withoutKey(connection, 'business');
    return { ...definition, connections: definition.connections.map((c, i) => (i === origin.connection ? next : c)) };
  }
  return updateFormatStep(definition, origin.step, (s) => ({
    ...s,
    links: s.links.map((l, i) => (i === origin.link ? withOptional(l, 'label', value) : l)),
  }));
}

/** Перейменувати доріжку (оголосивши її, якщо вона за замовчуванням). */
export function renameLane(definition: WorkflowDefinition, view: BusinessView, laneId: string, name: string): WorkflowDefinition {
  const value = name.trim();
  if (!value) return definition;
  return updateLane(declareLanes(definition, view, [laneId]), laneId, (l) => ({ ...l, name: value }));
}

/** Підпис доріжки під назвою; порожньо — прибрати. */
export function setLaneDescription(definition: WorkflowDefinition, view: BusinessView, laneId: string, description: string): WorkflowDefinition {
  const value = description.trim() || undefined;
  return updateLane(declareLanes(definition, view, [laneId]), laneId, (l) => withOptional(l, 'description', value));
}

/** Посунути доріжку вгору (`-1`) чи вниз (`1`). Оголошуються всі видимі доріжки, щоб порядок був явний. */
export function moveLane(definition: WorkflowDefinition, view: BusinessView, laneId: string, delta: -1 | 1): WorkflowDefinition {
  const declared = declareLanes(definition, view, view.lanes.map((l) => l.id));
  const lanes = [...(declared.business?.lanes ?? [])];
  const from = lanes.findIndex((l) => l.id === laneId);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= lanes.length) return definition;
  [lanes[from], lanes[to]] = [lanes[to], lanes[from]];
  return { ...declared, business: { ...declared.business, lanes } };
}

/** Нова порожня доріжка в кінці. Повертає визначення й id доріжки. */
export function addLane(definition: WorkflowDefinition, view: BusinessView, name: string): { definition: WorkflowDefinition; laneId: string } {
  const declared = declareLanes(definition, view, view.lanes.map((l) => l.id));
  const lanes = declared.business?.lanes ?? [];
  const laneId = uniqueId('lane', new Set([...lanes.map((l) => l.id), ...view.lanes.map((l) => l.id)]));
  return { definition: { ...declared, business: { ...declared.business, lanes: [...lanes, { id: laneId, name: name.trim() || laneId }] } }, laneId };
}

// --- допоміжне

/**
 * Оголошує в `business.lanes` доріжки `ids`, яких там ще немає, з назвами з поточного вигляду. Якщо оголошених
 * доріжок ще не було, оголошує всі видимі в поточному порядку — інакше оголошена стрибнула б нагору.
 */
export function declareLanes(definition: WorkflowDefinition, view: BusinessView, ids: readonly string[]): WorkflowDefinition {
  const current = definition.business?.lanes ?? [];
  const known = new Set(current.map((l) => l.id));
  const wanted = current.length === 0 ? [...view.lanes.map((l) => l.id), ...ids] : ids;
  const added: BusinessLane[] = [];
  for (const id of wanted) {
    if (known.has(id)) continue;
    const lane = view.lanes.find((l) => l.id === id);
    if (!lane) continue;
    known.add(id);
    added.push({ id: lane.id, name: lane.name, ...(lane.description ? { description: lane.description } : {}) });
  }
  if (added.length === 0) return definition;
  return { ...definition, business: { ...definition.business, lanes: [...current, ...added] } };
}

function findNode(definition: WorkflowDefinition, id: string): WorkflowNode | undefined {
  return definition.nodes.find((n) => n.id === id);
}

/** Задає поля `node.business`; `undefined` прибирає поле, порожній `business` прибирається цілком. */
function updateNodeBusiness(definition: WorkflowDefinition, nodeId: string, patch: Record<string, string | boolean | undefined>): WorkflowDefinition {
  let changed = false;
  const nodes = definition.nodes.map((n) => {
    if (n.id !== nodeId) return n;
    let business: Record<string, unknown> = { ...(n.business ?? {}) };
    for (const [key, value] of Object.entries(patch)) business = withOptional(business, key, value);
    const same = JSON.stringify(business) === JSON.stringify(n.business ?? {});
    if (same) return n;
    changed = true;
    return Object.keys(business).length > 0 ? { ...n, business } : withoutKey(n, 'business');
  });
  return changed ? { ...definition, nodes } : definition;
}

function updateFormatStep(definition: WorkflowDefinition, stepId: string, update: (s: FormatStep) => FormatStep): WorkflowDefinition {
  const steps = definition.business?.steps;
  if (!steps?.some((s) => s.id === stepId)) return definition;
  return { ...definition, business: { ...definition.business, steps: steps.map((s) => (s.id === stepId ? update(s) : s)) } as WorkflowBusiness };
}

function updateLane(definition: WorkflowDefinition, laneId: string, update: (l: BusinessLane) => BusinessLane): WorkflowDefinition {
  const lanes = definition.business?.lanes;
  if (!lanes?.some((l) => l.id === laneId)) return definition;
  return { ...definition, business: { ...definition.business, lanes: lanes.map((l) => (l.id === laneId ? update(l) : l)) } };
}

function withOptional<T extends object>(target: T, key: string, value: unknown): T {
  return value === undefined ? withoutKey(target, key) : ({ ...target, [key]: value } as T);
}

function withoutKey<T extends object>(target: T, key: string): T {
  if (!(key in target)) return target;
  const copy = { ...target } as Record<string, unknown>;
  delete copy[key];
  return copy as T;
}

function uniqueId(prefix: string, taken: ReadonlySet<string>): string {
  let i = taken.size + 1;
  while (taken.has(`${prefix}_${i}`)) i++;
  return `${prefix}_${i}`;
}
