import { ConnectorCatalogEntry, McpToolInfo } from '../connectors/connectors-api.service';
import { NodeGroup, NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { DescriptorIndex, parsePosition } from './canvas-model';
import { actionLabel, groupActions, matchesWords, searchWords } from './connector-actions';

/**
 * Палітра канвасу: вузли `core.*`/`ai.*` з `GET /nodes`, згруповані за `NodeGroup`, і конектори (F3-96): записи каталогу
 * (підключені й ні), потім «Власні конектори» — підключення без запису каталогу. Конектор відкривається сторінкою
 * з подіями (пресети тригерів) і діями (з кешу tools підключення, а без нього — з `actions` маніфесту).
 * Окремо — до 5 часто вживаних дій з «Останніх»; пошук знаходить і дії та події конекторів, зокрема непідключених.
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
  /** Конектор: його дії й події — відкриваються сторінкою конектора й знаходяться пошуком. */
  actions?: PaletteItem[];
  events?: PaletteItem[];
  /** Конектор: дані його сторінки в палітрі (F3-96). */
  connector?: PaletteConnector;
  /**
   * Дія чи подія конектора з каталогу: id конектора (запис каталогу чи власне підключення). Підключення (`serverId`)
   * для неї обирає редактор у момент додавання вузла.
   */
  connectorId?: string;
  /** Пресет тригера конектора (F6-21): параметри нового вузла без `serverId` (його підставляє `createNode`). */
  presetParameters?: Record<string, unknown>;
}

/** Сторінка конектора в палітрі: групи дій «Ресурс → Операція» (F3-55) і події. */
export interface PaletteConnector {
  /** Запис каталогу або id власного підключення. */
  id: string;
  name: string;
  icon: string | null;
  description: string;
  /** Є хоча б одне підключення з відомими tools. */
  connected: boolean;
  events: PaletteItem[];
  actionGroups: { label: string; items: PaletteItem[] }[];
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
  /** Запис каталогу, з якого встановлено підключення; немає — власний конектор. */
  catalogId?: string | null;
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

/**
 * Порядок типів у групах палітри — від найуживаніших (F3-77). Типи поза списком йдуть після нього за назвою.
 * Тригери: Ручний запуск, Розклад, Webhook, Форма, Подія конектора, MCP: опитування, Помилка сценарію, Виклик з іншого сценарію.
 */
export const PALETTE_TYPE_ORDER: readonly string[] = [
  'core.manualTrigger',
  'core.schedule',
  'core.webhook',
  'core.formTrigger',
  'mcp.subscribe',
  'mcp.poll',
  'core.errorTrigger',
  'core.workflowTrigger',
  'core.if',
  'core.switch',
  'core.loop',
  'core.merge',
  'core.wait',
  'core.approval',
  'core.set',
  'core.code',
  'core.dataTable',
  'core.decisionTable',
  'ai.agent',
  'ai.llm',
];

function typeRank(type: string): number {
  const i = PALETTE_TYPE_ORDER.indexOf(type);
  return i < 0 ? PALETTE_TYPE_ORDER.length : i;
}

/** Id секцій конекторів і часто вживаних дій. */
export const CONNECTORS_SECTION = 'connectors';
export const CUSTOM_CONNECTORS_SECTION = 'custom-connectors';
export const FREQUENT_SECTION = 'frequent';

/** Скільки часто вживаних дій показує палітра. */
export const FREQUENT_LIMIT = 5;

/** Ключ елемента-конектора. */
export function connectorKey(serverId: string): string {
  return `connector:${serverId}`;
}

/** Ключ дії конектора (його пам'ятають «Останні»): `connectorId` — запис каталогу, а для власного конектора — id підключення. */
export function actionKey(connectorId: string, toolName: string): string {
  return `${connectorId}/${toolName}`;
}

/** Id конектора підключення: запис каталогу, якщо він є в каталозі, інакше сам id підключення. */
export function connectorIdOf(server: Pick<PaletteServer, 'id' | 'catalogId'>, catalogIds: ReadonlySet<string>): string {
  return server.catalogId && catalogIds.has(server.catalogId) ? server.catalogId : server.id;
}

/**
 * Будує секції палітри: спершу групи `core.*`/`ai.*` (лише найновіша версія типу), потім «Конектори» (за назвою)
 * і «Часті дії» — дії з `recent` (новіші першими), не більше {@link FREQUENT_LIMIT}.
 */
export function buildPalette(
  descriptors: readonly NodeTypeInfo[],
  servers: readonly PaletteServer[],
  recent: readonly string[] = [],
  catalog: readonly ConnectorCatalogEntry[] = [],
): PaletteSection[] {
  const index = new DescriptorIndex(descriptors);
  const latest = [...new Set(descriptors.map((d) => d.type))]
    .map((type) => index.latest(type)!)
    .filter((d) => d.type !== MCP_TOOL);

  const sections: PaletteSection[] = [];
  for (const group of GROUP_ORDER) {
    const items = latest
      .filter((d) => d.group === group)
      .sort((a, b) => typeRank(a.type) - typeRank(b.type) || a.displayName.localeCompare(b.displayName, 'uk'))
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
  const catalogIds = new Set(catalog.map((e) => e.manifest.id));
  const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, 'uk');
  const actions = new Map<string, PaletteItem>();

  /** Елементи конектора: дії з tools (а без підключення — з `actions` маніфесту) і події з пресетів. */
  const connectorItem = (
    id: string,
    name: string,
    icon: string | null,
    description: string,
    tools: readonly McpToolInfo[],
    presets: ConnectorCatalogEntry['manifest']['triggers'],
    single: PaletteServer | null,
    connected: boolean,
  ): PaletteItem => {
    const items = new Map<string, PaletteItem>();
    const actionItems = tools.map((t): PaletteItem => {
      const item: PaletteItem = {
        key: actionKey(id, t.name),
        label: `${name} → ${actionLabel(t)}`,
        nodeName: actionLabel(t),
        description: t.description ?? null,
        type: MCP_TOOL,
        typeVersion: toolVersion,
        group: 'integration',
        toolName: t.name,
        connectorId: id,
        ...(single ? { serverId: single.id } : {}),
      };
      items.set(t.name, item);
      actions.set(item.key, item);
      return item;
    });
    const events = presets.flatMap((preset): PaletteItem[] => {
      const descriptor = index.latest(preset.node.type);
      if (!descriptor) return [];
      return [
        {
          key: `trigger:${id}/${preset.id}`,
          label: `${name} → ${preset.title}`,
          nodeName: preset.title,
          description: preset.description ?? null,
          type: preset.node.type,
          typeVersion: descriptor.version,
          group: 'trigger',
          connectorId: id,
          presetParameters: preset.node.parameters,
          ...(single ? { serverId: single.id } : {}),
        },
      ];
    });
    const actionGroups = groupActions(tools).map((g) => ({
      label: g.label,
      items: g.actions.map((a) => items.get(a.tool.name)!),
    }));
    return {
      key: connectorKey(id),
      label: name,
      description: null,
      type: MCP_TOOL,
      typeVersion: toolVersion,
      group: 'integration',
      actions: actionItems,
      events,
      connector: { id, name, icon, description, connected, events, actionGroups },
      connectorId: id,
    };
  };

  const catalogConnectors: PaletteItem[] = [];
  for (const entry of [...catalog].sort((a, b) => a.manifest.name.localeCompare(b.manifest.name, 'uk'))) {
    const id = entry.manifest.id;
    const installed = servers.filter((s) => s.catalogId === id).sort(byName);
    // Дії — з tools першого підключення; без підключення — з маніфесту (лише назви).
    const tools: McpToolInfo[] =
      installed[0]?.tools.length ? installed[0].tools : (entry.manifest.actions ?? []).map((a) => ({ name: a.name, title: a.title, description: a.description ?? null, inputSchema: {} }));
    const connected = installed.length > 0 && installed[0].tools.length > 0;
    catalogConnectors.push(
      connectorItem(id, entry.manifest.name, entry.manifest.icon ?? null, entry.manifest.description, tools, entry.manifest.triggers ?? [], installed.length === 1 ? installed[0] : null, connected),
    );
  }
  const customConnectors: PaletteItem[] = [];
  for (const server of [...servers].sort(byName)) {
    if (server.catalogId && catalogIds.has(server.catalogId)) continue;
    if (server.tools.length === 0) continue;
    customConnectors.push(connectorItem(server.id, server.name, null, '', server.tools, [], server, true));
  }
  if (catalogConnectors.length > 0) {
    sections.push({ id: CONNECTORS_SECTION, label: $localize`:@@canvas.palette.connectors:Конектори`, kind: 'core', items: catalogConnectors });
  }
  if (customConnectors.length > 0) {
    sections.push({
      id: catalogConnectors.length > 0 ? CUSTOM_CONNECTORS_SECTION : CONNECTORS_SECTION,
      label: catalogConnectors.length > 0 ? $localize`:@@canvas.palette.customConnectors:Власні конектори` : $localize`:@@canvas.palette.connectors:Конектори`,
      kind: 'core',
      items: customConnectors,
    });
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
      if (item.connector) {
        // Збіг із назвою конектора — сам конектор (його сторінка); інакше — дії й події, що збігаються.
        if (matchesWords([item.label], words)) items.push(item);
        else items.push(...[...(item.events ?? []), ...(item.actions ?? [])].filter((a) => matchesWords([a.label, a.toolName, a.description], words)));
      } else if (matches(item)) {
        items.push(item);
      }
    }
    const unique = items.filter((i, n) => items.findIndex((x) => x.key === i.key) === n);
    if (unique.length > 0) result.push({ ...section, items: unique });
  }
  return result;
}

/** Підказка праворуч від назви елемента: стан конектора й кількість дій або ім'я tool'а. Технічний тип не показується (F3-77): він лише в `title` і в пошуку. */
export function itemHint(item: PaletteItem): string {
  const connector = item.connector;
  if (connector) {
    const count = (item.actions?.length ?? 0) + (item.events?.length ?? 0);
    const state = connector.connected ? $localize`:@@canvas.palette.connected:підключено` : $localize`:@@canvas.palette.notConnected:не підключено`;
    return count > 0 ? $localize`:@@canvas.palette.connectorHint:${state}:state: · ${count}:count:` : state;
  }
  return item.toolName ?? '';
}

/** Сторінка конектора: «Події» й групи дій («Ресурс → Операція», як у полі «Дія»); порожні групи прибираються. */
export function connectorPageSections(item: PaletteItem): PaletteSection[] {
  const connector = item.connector;
  if (!connector) return [];
  const sections: PaletteSection[] = [];
  if (connector.events.length > 0) sections.push({ id: 'events', label: $localize`:@@canvas.palette.events:Події`, kind: 'core', items: connector.events });
  const groups = connector.actionGroups.filter((g) => g.items.length > 0);
  for (const g of groups) {
    sections.push({ id: `actions:${g.label}`, label: groups.length > 1 ? g.label : $localize`:@@canvas.palette.actions:Дії`, kind: 'core', items: g.items });
  }
  return sections;
}

/** Підказка при наведенні: опис, а за його відсутності — технічний тип. */
export function itemTitle(item: PaletteItem): string {
  const description = item.description?.trim();
  return description ? `${description}\n(${item.type})` : item.type;
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

  const parameters: Record<string, unknown> = item.presetParameters
    ? { serverId: item.serverId, ...structuredClone(item.presetParameters) }
    : item.type === MCP_TOOL
      ? { serverId: item.serverId, toolName: item.toolName, arguments: {} }
      : {};
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
