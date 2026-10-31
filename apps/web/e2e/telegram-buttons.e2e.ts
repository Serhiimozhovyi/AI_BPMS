import { Page, expect, test } from '@playwright/test';
import { chooseOption } from './mat-select';

/**
 * F6-30: крок Telegram `send_message` з редактором кнопок: інлайн-кнопки й клавіатура, збереження, перевідкриття.
 * Api підмінено in-memory сховищем, яке переживає перезавантаження сторінки.
 */
const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

const SEND_MESSAGE = {
  name: 'send_message',
  description: 'Надсилає повідомлення.',
  inputSchema: {
    type: 'object',
    properties: {
      chat_id: { type: ['string', 'integer'], 'x-openbp-widget': 'chat' },
      text: { type: 'string', maxLength: 4096 },
      parse_mode: { type: ['string', 'null'], enum: ['HTML', 'MarkdownV2', 'Markdown'] },
      reply_markup: { description: 'Кнопки під повідомленням чи клавіатура.', 'x-openbp-widget': 'keyboard' },
    },
    required: ['chat_id', 'text'],
  },
};

const MCP_TOOL = {
  type: 'mcp.tool',
  version: 1,
  displayName: 'Виклик MCP tool',
  group: 'integration',
  inputs: ['main'],
  outputs: ['main'],
  parametersSchema: {
    type: 'object',
    required: ['serverId', 'toolName'],
    properties: { serverId: { type: 'string' }, toolName: { type: 'string' }, arguments: { type: 'object' }, itemsPath: { type: 'string' } },
  },
  isTrigger: false,
};

const MANUAL = { type: 'core.manualTrigger', version: 1, displayName: 'Ручний запуск', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: { type: 'object' }, isTrigger: true };

interface Definition {
  formatVersion: 1;
  name: string;
  settings: Record<string, unknown>;
  nodes: Record<string, unknown>[];
  connections: unknown[];
  pinData: Record<string, unknown>;
}

async function installApi(page: Page): Promise<{ definition: () => Definition }> {
  let version = 1;
  let definition: Definition = {
    formatVersion: 1,
    name: 'Telegram кнопки',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [
      { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
      { id: 'n_msg', name: 'Повідомлення', type: 'mcp.tool', typeVersion: 1, position: [300, 0], parameters: { serverId: 'srv_tg', toolName: 'send_message', arguments: { chat_id: '@agro', text: 'Привіт, {{ $json.name }}' } } },
    ],
    connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_msg', input: 0 } }],
    pinData: {},
  };
  const detail = () => ({ id: WORKFLOW_ID, name: 'Telegram кнопки', active: false, version, versionId: `v${version}`, definition, updatedAt: new Date().toISOString() });
  const summary = { id: 'srv_tg', name: 'Telegram', transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: '2026-10-26T10:00:00Z' };

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') return route.fulfill({ json: [MANUAL, MCP_TOOL] });
    if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [summary], page: 1, pageSize: 200, total: 1 } });
    if (path === '/api/v1/connectors/srv_tg') return route.fulfill({ json: { summary, config: { transport: 'stdio' }, credentialId: null, tools: [SEND_MESSAGE] } });
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [detail()], page: 1, pageSize: 200, total: 1 } });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
      if (request.method() === 'GET') return route.fulfill({ json: detail() });
      if (request.method() === 'PUT') {
        definition = { ...(request.postDataJSON() as Definition), name: 'Telegram кнопки' };
        version++;
        return route.fulfill({ json: detail() });
      }
    }
    if (path === '/api/v1/workflows/templates' || path.endsWith('/versions')) return route.fulfill({ json: [] });
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });
  return { definition: () => definition };
}

async function openMessageNode(page: Page): Promise<void> {
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  const card = page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: /^Повідомлення$/ }) });
  await card.click({ position: { x: 12, y: 12 } });
  await expect(page.getByTestId('node-panel-name')).toHaveText('Повідомлення');
}

test('крок Telegram: інлайн-кнопки, клавіатура з контактом — зберегти, перевідкрити', async ({ page }) => {
  const api = await installApi(page);
  await openMessageNode(page);

  // Лічильник довжини тексту з лімітом Bot API.
  await expect(page.getByTestId('counter-text')).toContainText('/ 4096');
  await expect(page.getByTestId('kb-mode-none')).toHaveAttribute('aria-checked', 'true');

  // Інлайн-кнопки: «Записатися» (callback) і «Сайт» (url) в одному рядку.
  await page.getByTestId('kb-mode-inline').click();
  await page.getByTestId('kb-add-row').click();
  await page.getByTestId('kb-text-0-0').fill('Записатися');
  await page.getByTestId('kb-text-0-0').blur();
  await page.getByTestId('kb-value-0-0').fill('book:{{ $json.id }}');
  await page.getByTestId('kb-value-0-0').blur();
  await page.getByTestId('kb-add-button-0').click();
  await page.getByTestId('kb-text-0-1').fill('Сайт');
  await page.getByTestId('kb-text-0-1').blur();
  await chooseOption(page.getByTestId('kb-action-0-1'), 'Відкрити посилання (url)');
  await page.getByTestId('kb-value-0-1').fill('https://example.com');
  await page.getByTestId('kb-value-0-1').blur();
  await expect(page.getByTestId('kb-preview')).toContainText('Записатися');
  await expect(page.getByTestId('kb-issues')).toHaveCount(0);

  await expect(page.getByTestId('saved')).toBeVisible();
  await expect(page.getByTestId('dirty')).toHaveCount(0);
  const inline = { inline_keyboard: [[{ text: 'Записатися', callback_data: 'book:{{ $json.id }}' }, { text: 'Сайт', url: 'https://example.com' }]] };
  expect((api.definition().nodes[1]['parameters'] as { arguments: Record<string, unknown> }).arguments['reply_markup']).toEqual(inline);

  // Перевідкриття: той самий режим і кнопки.
  await openMessageNode(page);
  await expect(page.getByTestId('kb-mode-inline')).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('kb-text-0-1')).toHaveValue('Сайт');
  await expect(page.getByTestId('kb-action-0-1')).toContainText('Відкрити посилання (url)');

  // Клавіатура: контакт; інлайн-кнопки лишаються в пам'яті редактора, але у вузол іде лише активний режим.
  await page.getByTestId('kb-mode-reply').click();
  await page.getByTestId('kb-add-row').click();
  await page.getByTestId('kb-text-0-0').fill('Поділитися номером');
  await page.getByTestId('kb-text-0-0').blur();
  await chooseOption(page.getByTestId('kb-request-0-0'), 'Запросити контакт');
  await page.getByTestId('kb-onetime').check();
  await expect(page.getByTestId('saved')).toBeVisible();
  await expect(page.getByTestId('dirty')).toHaveCount(0);
  expect((api.definition().nodes[1]['parameters'] as { arguments: Record<string, unknown> }).arguments['reply_markup']).toEqual({
    keyboard: [[{ text: 'Поділитися номером', request_contact: true }]],
    resize_keyboard: true,
    one_time_keyboard: true,
  });

  await openMessageNode(page);
  await expect(page.getByTestId('kb-mode-reply')).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('kb-request-0-0')).toContainText('Запросити контакт');
  await expect(page.getByTestId('kb-onetime')).toBeChecked();
});
