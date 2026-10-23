import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeGroup, NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';
import { DescriptorIndex, parsePosition } from './canvas-model';

/**
 * Палітра канвасу: вузли `core.*`/`ai.*` з `GET /nodes`, згруповані за `NodeGroup`, і tools MCP-серверів
 * із `tools_cache` (`GET /connectors/{id}`), згруповані за сервером. Узагальнений `mcp.tool` у палітрі
 * не показується — його замінюють конкретні tools.
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

/** Будує секції палітри: спершу групи `core.*`/`ai.*` (лише найновіша версія типу), потім сервери за назвою. */
export function buildPalette(descriptors: readonly NodeTypeInfo[], servers: readonly PaletteServer[]): PaletteSection[] {
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
  for (const server of [...servers].sort((a, b) => a.name.localeCompare(b.name, 'uk'))) {
    if (server.tools.length === 0) continue;
    sections.push({
      id: `server:${server.id}`,
      label: server.name,
      kind: 'server',
      items: server.tools.map(
        (t): PaletteItem => ({
          key: `${server.id}/${t.name}`,
          label: t.title || t.name,
          description: t.description ?? null,
          type: MCP_TOOL,
          typeVersion: toolVersion,
          group: 'integration',
          serverId: server.id,
          toolName: t.name,
        }),
      ),
    });
  }
  return sections;
}

/**
 * Пошук у палітрі без урахування регістру: за назвою, типом, назвою tool'а, описом; збіг із назвою секції
 * (наприклад, сервера) лишає всю секцію. Порожні секції прибираються.
 */
export function filterPalette(sections: readonly PaletteSection[], query: string): PaletteSection[] {
  const q = query.trim().toLocaleLowerCase('uk');
  if (!q) return [...sections];
  const result: PaletteSection[] = [];
  for (const section of sections) {
    if (section.label.toLocaleLowerCase('uk').includes(q)) {
      result.push(section);
      continue;
    }
    const items = section.items.filter((i) =>
      [i.label, i.type, i.toolName, i.description].some((v) => v?.toLocaleLowerCase('uk').includes(q)),
    );
    if (items.length > 0) result.push({ ...section, items });
  }
  return result;
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
  let name = item.label;
  for (let i = 2; names.has(name); i++) name = `${item.label} ${i}`;

  const parameters: Record<string, unknown> =
    item.type === MCP_TOOL ? { serverId: item.serverId, toolName: item.toolName, arguments: {} } : {};
  return { id, name, type: item.type, typeVersion: item.typeVersion, position, parameters };
}

/** Додає вузол з палітри в кінець `nodes`; решта визначення не змінюється. */
export function addNode(definition: WorkflowDefinition, item: PaletteItem, position?: [number, number]): WorkflowDefinition {
  return { ...definition, nodes: [...definition.nodes, createNode(item, definition, position)] };
}

/** `n_` + 12 hex; `getRandomValues`, а не `randomUUID`, — працює і поза secure context (http у локальній мережі). */
function randomNodeId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return `n_${Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')}`;
}
