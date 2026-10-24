import { WorkflowConnection, WorkflowDefinition } from '../workflows/workflows-api.service';
import { connectNodes } from './canvas-edit';
import { parsePosition } from './canvas-model';
import { NEW_NODE_STEP_X, PaletteItem, PaletteSection, createNode, randomNodeId } from './palette';

/**
 * Додавання вузла з контексту канвасу (F3-50): з вихідного порту, на ребрі, перетягуванням з'єднання в порожнє місце.
 * Чисті функції `визначення → нове визначення`, як у `canvas-edit.ts`; вузли й ребра, яких дія не торкається, — ті самі
 * об'єкти.
 */

/** Звідки додається вузол: вихід `output` вузла `node`. */
export interface PortRef {
  node: string;
  output: number;
}

/** Результат додавання: нове визначення і id нового вузла (щоб виділити його). */
export interface QuickAddResult {
  definition: WorkflowDefinition;
  nodeId: string;
}

/** Що показує спливаюча палітра: лише тригери (порожній сценарій) чи кроки після тригера. */
export type QuickAddMode = 'trigger' | 'step';

/** Ключ `localStorage` зі списком ключів останніх обраних елементів палітри (новіші — першими). */
export const RECENT_KEY = 'openbp.palette.recent';

/** Скільки елементів пам'ятає секція «Останні». */
export const RECENT_LIMIT = 8;

/** Id секції «Останні» у спливаючій палітрі. */
export const RECENT_SECTION_ID = 'recent';

/** Розмір картки вузла на канвасі з запасом на проміжок — для пошуку вільного місця. */
const NODE_BOX_WIDTH = 240;
const NODE_BOX_HEIGHT = 100;

/** Вертикальний крок, на який зсувається новий вузол, поки місце зайняте. */
export const FREE_STEP_Y = 120;

/** Вертикальний зсув нового вузла для кожного наступного виходу джерела (гілки IF/Switch не накладаються). */
const OUTPUT_STEP_Y = 120;

/** Елемент палітри створює тригер: у нього немає входів, тож з'єднати його з портом не можна. */
export function isTriggerItem(item: PaletteItem): boolean {
  return item.group === 'trigger';
}

/**
 * Секції для спливаючої палітри: у режимі `trigger` — лише тригери, у режимі `step` — усе, крім тригерів.
 * Першою йде секція «Останні» з елементів, що є серед дозволених (у порядку `recent`). Порожні секції прибираються.
 */
export function quickAddSections(sections: readonly PaletteSection[], mode: QuickAddMode, recent: readonly string[] = []): PaletteSection[] {
  const allowed = (item: PaletteItem) => (mode === 'trigger') === isTriggerItem(item);
  const filtered = sections
    .map((s) => ({ ...s, items: s.items.filter(allowed) }))
    .filter((s) => s.items.length > 0);

  const byKey = new Map(filtered.flatMap((s) => s.items).map((i) => [i.key, i]));
  const recentItems = recent.map((k) => byKey.get(k)).filter((i): i is PaletteItem => !!i);
  if (recentItems.length === 0) return filtered;
  const label = $localize`:@@canvas.quickAdd.recent:Останні`;
  return [{ id: RECENT_SECTION_ID, label, kind: 'core', items: recentItems }, ...filtered];
}

/** Ключ на початок списку «Останні» без повторів, не більше {@link RECENT_LIMIT}. */
export function rememberRecent(recent: readonly string[], key: string): string[] {
  return [key, ...recent.filter((k) => k !== key)].slice(0, RECENT_LIMIT);
}

export function readRecent(): string[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
    return Array.isArray(value) ? value.filter((k): k is string => typeof k === 'string').slice(0, RECENT_LIMIT) : [];
  } catch {
    return [];
  }
}

export function writeRecent(recent: readonly string[]): void {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(recent));
  } catch {
    // Сховище недоступне (приватний режим) — «Останні» живуть до перезавантаження.
  }
}

/** Найближча вниз позиція, де картка нового вузла не накладається на наявні вузли. */
export function freePosition(definition: WorkflowDefinition, position: readonly [number, number]): [number, number] {
  const occupied = definition.nodes.map((n) => parsePosition(n.position));
  const [x] = position;
  let [, y] = position;
  // Кількість кроків обмежена кількістю вузлів: кожен вузол може заблокувати щонайбільше кілька позицій.
  for (let i = 0; i <= occupied.length * 2; i++) {
    if (!occupied.some((p) => Math.abs(p.x - x) < NODE_BOX_WIDTH && Math.abs(p.y - y) < NODE_BOX_HEIGHT)) break;
    y += FREE_STEP_Y;
  }
  return [x, y];
}

/** Позиція нового вузла праворуч від порту `from`: на рівні джерела, наступні виходи — нижче; вільне місце. */
export function positionAfter(definition: WorkflowDefinition, from: PortRef): [number, number] {
  const source = definition.nodes.find((n) => n.id === from.node);
  if (!source) return freePosition(definition, [100, 200]);
  const { x, y } = parsePosition(source.position);
  return freePosition(definition, [x + NEW_NODE_STEP_X, y + Math.max(0, from.output) * OUTPUT_STEP_Y]);
}

/**
 * Додає вузол з елемента палітри і, якщо задано `from` і вузол не тригер, з'єднує `from` → вхід 0 нового вузла.
 * Позиція — `position` (місце, куди відпустили з'єднання) або праворуч від порту; зайняте місце зсувається вниз.
 */
export function addConnectedNode(
  definition: WorkflowDefinition,
  item: PaletteItem,
  from: PortRef | null,
  position?: readonly [number, number],
  newId: () => string = randomNodeId,
): QuickAddResult {
  const at = position ? freePosition(definition, position) : from ? positionAfter(definition, from) : freePosition(definition, [100, 200]);
  const node = createNode(item, definition, at, newId);
  const added: WorkflowDefinition = { ...definition, nodes: [...definition.nodes, node] };
  const connected =
    from && !isTriggerItem(item) ? connectNodes(added, { source: from.node, sourceOutput: from.output, target: node.id, targetInput: 0 }) : added;
  return { definition: connected, nodeId: node.id };
}

/**
 * Вставляє вузол на ребро `edgeIndex`: `A → B` стає `A → новий (вхід 0)` і `новий (вихід 0) → B` на тому самому місці
 * в `connections`. Новий вузол стає праворуч від A на рядку B; якщо між A і B мало місця, B і всі вузли після нього
 * зсуваються праворуч, щоб нічого не накладалось. Тригер на ребро не вставляється, як і вузол на неіснуюче ребро чи
 * ребро на неіснуючі вузли: тоді `null`.
 */
export function insertOnEdge(
  definition: WorkflowDefinition,
  item: PaletteItem,
  edgeIndex: number,
  newId: () => string = randomNodeId,
): QuickAddResult | null {
  if (isTriggerItem(item)) return null;
  const edge = definition.connections[edgeIndex];
  const source = definition.nodes.find((n) => n.id === edge?.from?.node);
  const target = definition.nodes.find((n) => n.id === edge?.to?.node);
  if (!edge || !source || !target) return null;

  const a = parsePosition(source.position);
  const b = parsePosition(target.position);
  const x = a.x + NEW_NODE_STEP_X;
  const shift = Math.max(0, x + NEW_NODE_STEP_X - b.x);
  const moved = shift > 0 ? descendants(definition, target.id) : new Set<string>();
  moved.delete(source.id);
  const nodes = definition.nodes.map((n) => {
    if (!moved.has(n.id)) return n;
    const p = parsePosition(n.position);
    return { ...n, position: [p.x + shift, p.y] as [number, number] };
  });
  const shifted = shift > 0 ? { ...definition, nodes } : definition;

  const node = createNode(item, shifted, freePosition(shifted, [x, b.y]), newId);
  const incoming: WorkflowConnection = { ...edge, to: { node: node.id, input: 0 } };
  const outgoing: WorkflowConnection = { from: { node: node.id, output: 0 }, to: edge.to };
  const connections = [...definition.connections.slice(0, edgeIndex), incoming, outgoing, ...definition.connections.slice(edgeIndex + 1)];
  return { definition: { ...shifted, nodes: [...shifted.nodes, node], connections }, nodeId: node.id };
}

/** Вузол `id` і всі, до кого від нього ведуть ребра. */
function descendants(definition: WorkflowDefinition, id: string): Set<string> {
  const result = new Set<string>([id]);
  const stack = [id];
  while (stack.length > 0) {
    const current = stack.pop()!;
    for (const c of definition.connections) {
      const next = c?.to?.node;
      if (c?.from?.node === current && typeof next === 'string' && !result.has(next)) {
        result.add(next);
        stack.push(next);
      }
    }
  }
  return result;
}
