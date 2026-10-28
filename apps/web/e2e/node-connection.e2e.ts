import { Page, expect, test } from '@playwright/test';

/**
 * F3-95: у панелі вузла Telegram — поле «Підключення»; «+ Нове підключення…» → токен бота → вузол отримує нове
 * підключення без переходу на іншу сторінку, дія й аргументи лишаються.
 */
const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

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
      triggers: [],
    },
    source: 'builtin',
    installed: [],
  },
];
const TYPES = [{ type: 'telegram.bot', displayName: 'Telegram: бот', description: '', fields: [{ name: 'token', title: 'Токен бота', kind: 'secret', required: true }], schema: {} }];

async function installApi(page: Page): Promise<{ node: () => Record<string, unknown> }> {
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
  const summaries: Record<string, unknown>[] = [{ id: 'bot1', name: 'Бот продажів', transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: '2026-10-26T10:00:00Z', catalogId: 'telegram' }];
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
      return route.fulfill({ status: 201, json: { summary, config: {}, credentialId: 'cred2', tools: [SEND_MESSAGE] } });
    }
    const connector = /^\/api\/v1\/connectors\/(bot\d)$/.exec(path);
    if (connector) return route.fulfill({ json: { summary: summaries.find((s) => s['id'] === connector[1]), config: {}, credentialId: null, tools: [SEND_MESSAGE] } });
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
  return { node: () => definition.nodes.find((n) => n['id'] === 'n_msg')! };
}

test('вузол Telegram: «+ Нове підключення…» → токен бота → вузол отримує нове підключення', async ({ page }) => {
  const api = await installApi(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: /^Повідомлення$/ }) }).click({ position: { x: 12, y: 12 } });
  await expect(page.getByTestId('node-panel-name')).toHaveText('Повідомлення');

  const select = page.getByTestId('connection-select');
  await expect(select).toContainText('Бот продажів');
  // Сирого serverId · toolName у тілі панелі немає.
  await expect(page.getByTestId('node-panel')).not.toContainText('bot1 · send_message');

  await select.click();
  await page.getByTestId('connection-new').click();

  const dialog = page.getByTestId('new-connection');
  await expect(dialog.getByTestId('catalog-connect')).toBeVisible();
  // Форма може перемонтуватися, поки діалог дочитує каталог і типи облікових даних, і тоді вписаний токен зникає:
  // токен вписується, доки не лишиться в полі, а кнопка не стане активною (інакше «Підключити» мовчки нічого не надсилає).
  const save = dialog.getByTestId('credential-save');
  await expect(async () => {
    await dialog.getByLabel('Токен бота').fill('123:abc');
    await expect(dialog.getByLabel('Токен бота')).toHaveValue('123:abc');
    await expect(save).toBeEnabled();
  }).toPass({ timeout: 10_000 });
  const installed = page.waitForResponse((r) => r.url().endsWith('/connectors/catalog/telegram/install') && r.request().method() === 'POST');
  await save.click();
  await installed;

  await expect(dialog).toHaveCount(0, { timeout: 15_000 });
  await expect(page.getByTestId('connection-select')).toContainText('Telegram');
  await expect.poll(() => api.node()['parameters']).toEqual({ serverId: 'bot2', toolName: 'send_message', arguments: { chat_id: '@agro', text: 'Привіт' } });
});
