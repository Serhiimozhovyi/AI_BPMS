import { Page, expect, test } from '@playwright/test';

/**
 * F3-96: палітра → Telegram → «Надіслати фото» → на канвасі вузол «Надіслати фото» з підключенням; конектор без
 * підключення відкриває діалог «Нове підключення», а «Скасувати» вузол не додає.
 */
const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

const SEND_PHOTO = { name: 'send_photo', title: 'Надіслати фото', description: 'Фото в чат.', inputSchema: { type: 'object', properties: { chat_id: { type: 'string' } } } };
const SEND_MESSAGE = {
  name: 'send_message',
  description: 'Надсилає повідомлення.',
  inputSchema: { type: 'object', properties: { chat_id: { type: 'string' }, text: { type: 'string' } }, required: ['chat_id', 'text'] },
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
    properties: { serverId: { type: 'string' }, toolName: { type: 'string' }, arguments: { type: 'object' } },
  },
  isTrigger: false,
};
const MANUAL = { type: 'core.manualTrigger', version: 1, displayName: 'Ручний запуск', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: { type: 'object' }, isTrigger: true };

const CATALOG = [
  {
    manifest: {
      id: 'telegram',
      name: 'Telegram',
      description: 'Повідомлення й кнопки.',
      transport: 'stdio',
      stdio: { command: 'dotnet', arguments: ['tg.dll'] },
      credential: { type: 'telegram.bot', map: { token: 'TELEGRAM_BOT_TOKEN' } },
      actions: [{ name: 'send_message', title: 'Надіслати повідомлення' }, { name: 'send_photo', title: 'Надіслати фото' }],
      triggers: [],
    },
    source: 'builtin',
    installed: [],
  },
];
const TYPES = [{ type: 'telegram.bot', displayName: 'Telegram: бот', description: '', fields: [{ name: 'token', title: 'Токен бота', kind: 'secret', required: true }], schema: {} }];

async function installApi(page: Page, connected = true): Promise<{ nodes: () => Record<string, unknown>[] }> {
  let definition = {
    formatVersion: 1,
    name: 'Telegram',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [
      { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
      { id: 'n_msg', name: 'Повідомлення', type: 'mcp.tool', typeVersion: 1, position: [300, 0], parameters: { serverId: 'bot1', toolName: 'send_message', arguments: { chat_id: '@agro', text: 'Привіт' } } },
    ] as Record<string, unknown>[],
    connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_msg', input: 0 } }],
    pinData: {},
  };
  const summaries: Record<string, unknown>[] = connected ? [{ id: 'bot1', name: 'Бот продажів', transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: '2026-10-26T10:00:00Z', catalogId: 'telegram' }] : [];
  const detail = () => ({ id: WORKFLOW_ID, name: 'Telegram', active: false, version: 1, versionId: 'v1', definition, updatedAt: new Date().toISOString() });

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') return route.fulfill({ json: [MANUAL, MCP_TOOL] });
    if (path === '/api/v1/connectors') return route.fulfill({ json: { items: summaries, page: 1, pageSize: 200, total: summaries.length } });
    if (path === '/api/v1/connectors/catalog') return route.fulfill({ json: CATALOG });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
    if (path === '/api/v1/credentials' && request.method() === 'POST') {
      const body = request.postDataJSON() as { name: string; type: string };
      return route.fulfill({ status: 201, json: { id: 'cred2', name: body.name, type: body.type, fields: ['token'], usedBy: { connectors: [], workflows: [] }, error: null } });
    }
    if (path === '/api/v1/connectors/catalog/telegram/install' && request.method() === 'POST') {
      const body = request.postDataJSON() as { name: string };
      const summary = { id: 'bot2', name: body.name, transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: null, catalogId: 'telegram' };
      summaries.push(summary);
      return route.fulfill({ status: 201, json: { summary, config: {}, credentialId: 'cred2', tools: [SEND_MESSAGE, SEND_PHOTO] } });
    }
    const connector = /^\/api\/v1\/connectors\/(bot\d)$/.exec(path);
    if (connector) return route.fulfill({ json: { summary: summaries.find((s) => s['id'] === connector[1]), config: {}, credentialId: null, tools: [SEND_MESSAGE, SEND_PHOTO] } });
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [detail()], page: 1, pageSize: 200, total: 1 } });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
      if (request.method() === 'GET') return route.fulfill({ json: detail() });
      if (request.method() === 'PUT') {
        definition = request.postDataJSON() as typeof definition;
        return route.fulfill({ json: detail() });
      }
    }
    if (path === '/api/v1/workflows/templates' || path.endsWith('/versions')) return route.fulfill({ json: [] });
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });
  return { nodes: () => definition.nodes };
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('openbp.editor.palette', '1'));
});

test('палітра → Telegram → «Надіслати фото» → вузол із підключенням на канвасі', async ({ page }) => {
  const api = await installApi(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);

  await page.getByTestId('palette-search').waitFor();
  await page.locator('[data-item="connector:telegram"]').click();
  const pageEl = page.getByTestId('palette-page');
  await expect(pageEl).toContainText('Telegram');
  await expect(pageEl).toContainText('підключено');

  await page.getByTestId('palette-search').fill('фото');
  await page.locator('[data-item="telegram/send_photo"]').click();

  await expect(page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: /^Надіслати фото$/ }) })).toHaveCount(1);
  await expect
    .poll(() => api.nodes().find((n) => n['name'] === 'Надіслати фото')?.['parameters'])
    .toEqual({ serverId: 'bot1', toolName: 'send_photo', arguments: {} });
});

test('конектор без підключення: діалог «Нове підключення»; «Закрити» вузол не додає, токен — додає з новим підключенням', async ({ page }) => {
  const api = await installApi(page, false);
  await page.goto(`/workflows/${WORKFLOW_ID}`);

  await page.locator('[data-item="connector:telegram"]').click();
  await expect(page.getByTestId('palette-page')).toContainText('не підключено');
  await page.locator('[data-item="telegram/send_photo"]').click();

  const dialog = page.getByTestId('new-connection');
  await expect(dialog.getByTestId('catalog-connect')).toBeVisible();
  await page.getByTestId('new-connection-close').click();
  await expect(dialog).toHaveCount(0);
  expect(api.nodes()).toHaveLength(2);

  await page.locator('[data-item="telegram/send_photo"]').click();
  await page.getByTestId('new-connection').getByLabel('Токен бота').fill('123:abc');
  await page.getByTestId('new-connection').getByTestId('credential-save').click();
  // Спершу вузол на канвасі (створення облікових даних → підключення → перезавантаження списку), потім автозбереження
  // (пауза 800 мс + PUT): на завантаженому CI обидва кроки разом перевищували 5 с очікування за замовчуванням.
  await expect(page.getByTestId('new-connection')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: /^Надіслати фото$/ }) })).toHaveCount(1, { timeout: 15_000 });
  await expect
    .poll(() => api.nodes().find((n) => n['name'] === 'Надіслати фото')?.['parameters'], { timeout: 15_000 })
    .toEqual({ serverId: 'bot2', toolName: 'send_photo', arguments: {} });
});
