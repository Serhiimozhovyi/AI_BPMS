import { CanvasNodeGroup } from './canvas-model';

/** Значки типів вузлів (Material Symbols Outlined, шрифт уже підключено в `index.html`). */
const TYPE_ICONS: Readonly<Record<string, string>> = {
  'core.manualTrigger': 'play_circle',
  'core.schedule': 'schedule',
  'core.webhook': 'webhook',
  'core.formTrigger': 'dynamic_form',
  'core.errorTrigger': 'error',
  'core.workflowTrigger': 'input',
  'core.if': 'call_split',
  'core.switch': 'alt_route',
  'core.loop': 'repeat',
  'core.merge': 'merge',
  'core.wait': 'hourglass_empty',
  'core.approval': 'how_to_reg',
  'core.decisionTable': 'table_chart',
  'core.respondToWebhook': 'reply',
  'core.executeWorkflow': 'account_tree',
  'core.set': 'edit_note',
  'core.code': 'code',
  'core.dataTable': 'table_rows',
  'mcp.tool': 'extension',
  'mcp.poll': 'sync',
  'mcp.subscribe': 'notifications_active',
  'mcp.waitFor': 'pending_actions',
  'ai.llm': 'psychology',
  'ai.agent': 'smart_toy',
};

/** Запасний значок за групою — для типів без власного (нові вузли, невідомий тип). */
const GROUP_ICONS: Readonly<Record<CanvasNodeGroup, string>> = {
  trigger: 'bolt',
  logic: 'alt_route',
  transform: 'tune',
  integration: 'extension',
  ai: 'smart_toy',
  unknown: 'help',
};

/** Значок картки вузла: за типом, інакше за групою. */
export function nodeIcon(type: string, group: CanvasNodeGroup): string {
  return TYPE_ICONS[type] ?? GROUP_ICONS[group];
}

/** Типи з власним значком — для тесту повноти каталогу. */
export const ICON_TYPES: readonly string[] = Object.keys(TYPE_ICONS);
