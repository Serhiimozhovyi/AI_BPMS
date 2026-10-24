import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeGroup, NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { DescriptorIndex, parsePosition } from './canvas-model';
import { actionLabel, defaultAction, matchesWords, searchWords } from './connector-actions';

/**
 * Палітра канвасу: вузли `core.*`/`ai.*` з `GET /nodes`, згруповані за `NodeGroup`, і конектори (MCP-сервери з
 * `tools_cache`, `GET /connectors/{id}`) — кожен одним елементом; дія (tool) обирається в панелі вузла (F3-55).
 * Окремо — до 5 часто вживаних дій з «Останніх»; пошук знаходить і дії конекторів (за назвою, ім'ям, описом).
 * Узагальнений `mcp.tool` у палітрі не показується.
 */

export interface PaletteItem {
  /** Унікальний у межах палітри ключ. */
  key: string;
  label: string;
  description: string | null;
  type: string;
  typeVersion: number;
  group: NodeGroup;
  /** Для tools: id конектора (`mcp_servers.id`) і назва tool'а. */
  serverId?: string;
  toolName?: string;
  /** Назва нового вузла, якщо інша за `label` (дія конектора: назва tool'а без назви конектора). */
  nodeName?: string;
  /** Конектор: його дії — не показуються, але знаходяться пошуком. */
  actions?: PaletteItem[];
}

export interface PaletteSection {
  id: string;
  label: string;
  kind: 'core' | 'server';
  items: PaletteItem[];
}

/** Конектор із закешованими tools для палітри. */
export interface PaletteServer {
  id: string;
  name: string;
  tools: McpToolInfo[];
}

export const MCP_TOOL = 'mcp.tool';

/** Порядок і назви груп палітри. */
export const GROUP_ORDER: readonly NodeGroup[] = ['trigger', 'logic', 'transform', 'ai', 'integration'];

export function groupLabel(group: NodeGroup | 'unknown'): string {
  switch (group) {
    case 'trigger':
      return $localize`:@@canvas.group.trigger:Тригери`;
    case 'logic':
      return $localize`:@@canvas.group.logic:Логіка`;
    case 'transform':
      return $localize`:@@canvas.group.transform:Дані`;
    case 'ai':
      return $localize`:@@canvas.group.ai:ШІ`;
    case 'integration':
      return $localize`:@@canvas.group.integration:MCP`;
    default:
      return $localize`:@@canvas.group.unknown:Невідомий тип`;
  }
}

/** Id секцій конекторів і часто вживаних дій. */
export const CONNECTORS_SECTION = 'connectors';
export const FREQUENT_SECTION = 'frequent';

/** Скільки часто вживаних дій показує палітра. */
export const FREQUENT_LIMIT = 5;

/** Ключ елемента-конектора. */
export function connectorKey(serverId: string): string {
  return `connector:${serverId}`;
}

/** Ключ дії конектора (його пам'ятають «Останні»). */
export function actionKey(serverId: string, toolName: string): string {
  return `${serverId}/${toolName}`;
}

/**
 * Будує секції палітри: спершу групи `core.*`/`ai.*` (лише найновіша версія типу), потім «Конектори» (за назвою)
 * і «Часті дії» — дії з `recent` (новіші першими), не більше {@link FREQUENT_LIMIT}.
 */
export function buildPalette(descriptors: readonly NodeTypeInfo[], servers: readonly PaletteServer[], recent: readonly string[] = []): PaletteSection[] {
  const index = new DescriptorIndex(descriptors);
  const latest = [...new Set(descriptors.map((d) => d.type))]
    .map((type) => index.latest(type)!)
    .filter((d) => d.type !== MCP_TOOL);

  const sections: PaletteSection[] = [];
  for (const group of GROUP_ORDER) {
    const items = latest
      .filter((d) => d.group === group)
      .sort((a, b) => a.displayName.localeCompare(b.displayName, 'uk'))
      .map(
        (d): PaletteItem => ({
          key: d.type,
          label: d.displayName,
          description: d.description ?? null,
          type: d.type,
          typeVersion: d.version,
          group: d.group,
        }),
      );
    if (items.length > 0) sections.push({ id: `group:${group}`, label: groupLabel(group), kind: 'core', items });
  }

  const toolVersion = index.latest(MCP_TOOL)?.version ?? 1;
  const connectors: PaletteItem[] = [];
  const actions = new Map<string, PaletteItem>();
  for (const server of [...servers].sort((a, b) => a.name.localeCompare(b.name, 'uk'))) {
    const first = defaultAction(server.tools);
    if (!first) continue;
    const serverActions = server.tools.map(
      (t): PaletteItem => ({
        key: actionKey(server.id, t.name),
        label: `${server.name} → ${actionLabel(t)}`,
        nodeName: actionLabel(t),
        description: t.description ?? null,
        type: MCP_TOOL,
        typeVersion: toolVersion,
        group: 'integration',
        serverId: server.id,
        toolName: t.name,
      }),
    );
    for (const a of serverActions) actions.set(a.key, a);
    connectors.push({
      key: connectorKey(server.id),
      label: server.name,
      nodeName: actionLabel(first),
      description: $localize`:@@canvas.palette.connectorActions:Дій: ${server.tools.length}:count:. Дія обирається в панелі вузла.`,
      type: MCP_TOOL,
      typeVersion: toolVersion,
      group: 'integration',
      serverId: server.id,
      toolName: first.name,
      actions: serverActions,
    });
  }
  if (connectors.length > 0) {
    sections.push({ id: CONNECTORS_SECTION, label: $localize`:@@canvas.palette.connectors:Конектори`, kind: 'core', items: connectors });
  }
  const frequent = recent
    .map((k) => actions.get(k))
    .filter((a): a is PaletteItem => !!a)
    .slice(0, FREQUENT_LIMIT);
  if (frequent.length > 0) {
    sections.push({ id: FREQUENT_SECTION, label: $localize`:@@canvas.palette.frequent:Часті дії`, kind: 'core', items: frequent });
  }
  return sections;
}

/**
 * Пошук у палітрі без урахування регістру: кожне слово запиту — у назві, типі, назві tool'а чи описі; збіг із назвою
 * секції лишає всю секцію. Дії конекторів, що збігаються, показуються на місці свого конектора (F3-55): «надіслати
 * повідомлення» → «Telegram → Надіслати повідомлення»; сам конектор — лише якщо збігається його назва. Порожні секції
 * прибираються.
 */
export function filterPalette(sections: readonly PaletteSection[], query: string): PaletteSection[] {
  const words = searchWords(query);
  if (words.length === 0) return [...sections];
  const matches = (i: PaletteItem) => matchesWords([i.label, i.type, i.toolName, i.description], words);
  const result: PaletteSection[] = [];
  for (const section of sections) {
    if (matchesWords([section.label], words)) {
      result.push(section);
      continue;
    }
    const items: PaletteItem[] = [];
    for (const item of section.items) {
      if (item.actions) {
        // Збіг із назвою конектора — сам конектор (його дії — в панелі вузла); інакше — дії, що збігаються.
        if (matchesWords([item.label], words)) items.push(item);
        else items.push(...item.actions.filter((a) => matchesWords([a.label, a.toolName, a.description], words)));
      } else if (matches(item)) {
        items.push(item);
      }
    }
    const unique = items.filter((i, n) => items.findIndex((x) => x.key === i.key) === n);
    if (unique.length > 0) result.push({ ...section, items: unique });
  }
  return result;
}

/** Підказка праворуч від назви елемента: кількість дій конектора, ім'я tool'а або тип вузла. */
export function itemHint(item: PaletteItem): string {
  if (item.actions) return $localize`:@@canvas.palette.actionsCount:${item.actions.length}:count: дій`;
  return item.toolName ?? item.type;
}

/** Відступ нового вузла праворуч від крайнього (ширина картки + проміжок). */
export const NEW_NODE_STEP_X = 260;

/** Позиція нового вузла: праворуч від крайнього правого, або `[100, 200]` у порожньому сценарії. */
export function nextNodePosition(definition: WorkflowDefinition): [number, number] {
  if (definition.nodes.length === 0) return [100, 200];
  let right = parsePosition(definition.nodes[0].position);
  for (const node of definition.nodes) {
    const p = parsePosition(node.position);
    if (p.x > right.x) right = p;
  }
  return [right.x + NEW_NODE_STEP_X, right.y];
}

/** Новий вузол з елемента палітри: стабільний унікальний `id`, унікальна `name`, параметри за замовчуванням. */
export function createNode(
  item: PaletteItem,
  definition: WorkflowDefinition,
  position: [number, number] = nextNodePosition(definition),
  newId: () => string = randomNodeId,
): WorkflowNode {
  const ids = new Set(definition.nodes.map((n) => n.id));
  let id = newId();
  while (ids.has(id)) id = newId();

  const names = new Set(definition.nodes.map((n) => n.name));
  const label = item.nodeName ?? item.label;
  let name = label;
  for (let i = 2; names.has(name); i++) name = `${label} ${i}`;

  const parameters: Record<string, unknown> =
    item.type === MCP_TOOL ? { serverId: item.serverId, toolName: item.toolName, arguments: {} } : {};
  return { id, name, type: item.type, typeVersion: item.typeVersion, position, parameters };
}

/** Додає вузол з палітри в кінець `nodes`; решта визначення не змінюється. */
export function addNode(definition: WorkflowDefinition, item: PaletteItem, position?: [number, number]): WorkflowDefinition {
  return { ...definition, nodes: [...definition.nodes, createNode(item, definition, position)] };
}

/** `n_` + 12 hex; `getRandomValues`, а не `randomUUID`, — працює і поза secure context (http у локальній мережі). */
export function randomNodeId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return `n_${Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')}`;
}
