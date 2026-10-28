import { McpToolInfo } from '../connectors/connectors-api.service';
import { isPlainObject } from '../node-form/node-value';
import { WorkflowDefinition, WorkflowNode } from '../workflows/workflows-api.service';

/**
 * Конектор як один вузол із вибором дії (F3-55): дії (tools) групуються «Ресурс → Операція» за правилами імен
 * (`list_*`, `get_*`, `create_*`, `update_*`, `delete_*`) або за анотаціями `x-openbp-resource`/`x-openbp-operation`
 * в `inputSchema` tool'а (docs/connectors.md §5); зміна дії вузла скидає `arguments`, але лишає id, позицію і з'єднання.
 * Без Angular — чисті функції.
 */

/** Анотації групування в `inputSchema` tool'а. */
export const RESOURCE_ANNOTATION = 'x-openbp-resource';
export const OPERATION_ANNOTATION = 'x-openbp-operation';

/** Операції за префіксом імені tool'а, у порядку показу. */
const OPERATIONS: readonly { prefix: string; label: () => string }[] = [
  { prefix: 'list', label: () => $localize`:@@actions.op.list:Список` },
  { prefix: 'get', label: () => $localize`:@@actions.op.get:Отримати` },
  { prefix: 'create', label: () => $localize`:@@actions.op.create:Створити` },
  { prefix: 'update', label: () => $localize`:@@actions.op.update:Змінити` },
  { prefix: 'delete', label: () => $localize`:@@actions.op.delete:Видалити` },
];

/** Дія конектора: tool, назва операції в групі й порядок операції. */
export interface ConnectorAction {
  tool: McpToolInfo;
  /** Підпис у групі: операція (`Список`) або назва tool'а, якщо операції немає. */
  label: string;
  order: number;
}

/** Група дій за ресурсом; `resource = ''` — дії без ресурсу («Інші дії»). */
export interface ActionGroup {
  resource: string;
  label: string;
  actions: ConnectorAction[];
}

/** Назва дії для людини: `title` tool'а або його ім'я. */
export function actionLabel(tool: Pick<McpToolInfo, 'name' | 'title'>): string {
  return tool.title || tool.name;
}

/** Ресурс і операція дії: з анотацій, інакше з префікса імені; без них — ресурсу немає. */
export function classifyAction(tool: McpToolInfo): { resource: string; operation: string; order: number } {
  const schema = isPlainObject(tool.inputSchema) ? tool.inputSchema : {};
  const resource = schema[RESOURCE_ANNOTATION];
  const operation = schema[OPERATION_ANNOTATION];
  if (typeof resource === 'string' && resource.trim()) {
    const known = OPERATIONS.findIndex((o) => o.prefix === operation);
    const label = known >= 0 ? OPERATIONS[known].label() : typeof operation === 'string' && operation.trim() ? operation.trim() : actionLabel(tool);
    return { resource: resource.trim(), operation: label, order: known >= 0 ? known : OPERATIONS.length };
  }
  const match = /^([a-z]+)_(.+)$/.exec(tool.name);
  const known = match ? OPERATIONS.findIndex((o) => o.prefix === match[1]) : -1;
  if (match && known >= 0) return { resource: match[2], operation: OPERATIONS[known].label(), order: known };
  return { resource: '', operation: actionLabel(tool), order: OPERATIONS.length };
}

/**
 * Дії, згруповані за ресурсом: групи за назвою ресурсу, «Інші дії» — останньою; у групі — за порядком операцій
 * (список, отримати, створити, змінити, видалити), далі за назвою.
 */
export function groupActions(tools: readonly McpToolInfo[]): ActionGroup[] {
  const groups = new Map<string, ConnectorAction[]>();
  for (const tool of tools) {
    const { resource, operation, order } = classifyAction(tool);
    const list = groups.get(resource) ?? [];
    list.push({ tool, label: operation, order });
    groups.set(resource, list);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => (a === '' ? 1 : b === '' ? -1 : a.localeCompare(b, 'uk')))
    .map(([resource, actions]) => ({
      resource,
      label: resource || $localize`:@@actions.other:Інші дії`,
      actions: actions.sort((x, y) => x.order - y.order || x.label.localeCompare(y.label, 'uk')),
    }));
}

/** Дія за замовчуванням для нового вузла конектора — перша в згрупованому списку. */
export function defaultAction(tools: readonly McpToolInfo[]): McpToolInfo | null {
  return groupActions(tools)[0]?.actions[0]?.tool ?? null;
}

/** Пошук дії без регістру: кожне слово запиту є в назві, імені, ресурсі чи описі. */
export function filterActions(groups: readonly ActionGroup[], query: string): ActionGroup[] {
  const words = searchWords(query);
  if (words.length === 0) return [...groups];
  return groups
    .map((g) => ({
      ...g,
      actions: g.actions.filter((a) => matchesWords([a.label, a.tool.title, a.tool.name, a.tool.description, g.label], words)),
    }))
    .filter((g) => g.actions.length > 0);
}

/** Слова запиту в нижньому регістрі. */
export function searchWords(query: string): string[] {
  return query.trim().toLocaleLowerCase('uk').split(/\s+/).filter(Boolean);
}

/** Кожне слово є хоча б в одному з полів (порядок слів не важливий). */
export function matchesWords(fields: readonly (string | null | undefined)[], words: readonly string[]): boolean {
  const text = fields.filter(Boolean).join('\n').toLocaleLowerCase('uk');
  return words.every((w) => text.includes(w));
}

/** Автоматична назва: назва дії або вона з номером (`Send message 2`) — така назва змінюється разом з дією. */
export function isAutomaticName(name: string, tool: Pick<McpToolInfo, 'name' | 'title'> | null | undefined, toolName?: string): boolean {
  const labels = [tool ? actionLabel(tool) : null, toolName].filter((l): l is string => !!l);
  return labels.some((label) => name === label || (name.startsWith(`${label} `) && /^\d+$/.test(name.slice(label.length + 1))));
}

/** Унікальна назва вузла на основі `label` серед інших вузлів (`label`, `label 2`, …). */
export function uniqueNodeName(definition: WorkflowDefinition, label: string, exceptId?: string): string {
  const names = new Set(definition.nodes.filter((n) => n.id !== exceptId).map((n) => n.name));
  let name = label;
  for (let i = 2; names.has(name); i++) name = `${label} ${i}`;
  return name;
}

/**
 * Змінює дію вузла `nodeId` на `tool`: `toolName` — новий, `arguments` — порожні; назва змінюється, лише якщо була
 * автоматичною (`previous` — tool, з якого її утворено). Id, позиція, інші параметри й з'єднання — ті самі.
 */
export function changeNodeTool(definition: WorkflowDefinition, nodeId: string, tool: McpToolInfo, previous: McpToolInfo | null): WorkflowDefinition {
  const node = definition.nodes.find((n) => n.id === nodeId);
  if (!node || node.parameters?.['toolName'] === tool.name) return definition;
  const oldToolName = typeof node.parameters?.['toolName'] === 'string' ? node.parameters['toolName'] : undefined;
  const name = isAutomaticName(node.name, previous, oldToolName) ? uniqueNodeName(definition, actionLabel(tool), nodeId) : node.name;
  const changed: WorkflowNode = { ...node, name, parameters: { ...node.parameters, toolName: tool.name, arguments: {} } };
  return { ...definition, nodes: definition.nodes.map((n) => (n.id === nodeId ? changed : n)) };
}

/**
 * Інше підключення вузла `mcp.*` (F3-95): `serverId` змінюється, дія й `arguments` лишаються. Коли в новому підключенні
 * такої дії немає, `tool` — дія на заміну: вона ставиться з порожніми `arguments` (`changeNodeTool`).
 */
export function changeNodeConnection(
  definition: WorkflowDefinition,
  nodeId: string,
  serverId: string,
  tool: McpToolInfo | null,
  previous: McpToolInfo | null,
): WorkflowDefinition {
  const node = definition.nodes.find((n) => n.id === nodeId);
  if (!node || node.parameters?.['serverId'] === serverId) return definition;
  const moved: WorkflowDefinition = {
    ...definition,
    nodes: definition.nodes.map((n) => (n.id === nodeId ? { ...n, parameters: { ...n.parameters, serverId } } : n)),
  };
  return tool ? changeNodeTool(moved, nodeId, tool, previous) : moved;
}
