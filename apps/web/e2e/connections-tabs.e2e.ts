import { expect, test } from '@playwright/test';

/**
 * F3-99: «Підключення» — каталог → Telegram → токен → підключення у «Підключено», а облікові дані — у «Облікових
 * даних» з кількістю підключень. Старі адреси ведуть на вкладки.
 */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const CATALOG = [
  {
    manifest: {
      id: 'telegram',
      name: 'Telegram',
      description: 'Повідомлення й кнопки.',
      icon: '✈️',
      transport: 'stdio',
      stdio: { command: 'dotnet', arguments: ['tg.dll'] },
      credential: { type: 'telegram.bot', map: { token: 'TELEGRAM_BOT_TOKEN' } },
      triggers: [],
    },
    source: 'builtin',
    installed: [],
  },
];
const TYPES = [
  {
    type: 'telegram.bot',
    displayName: 'Telegram: бот',
    description: 'Токен бота від @BotFather.',
    fields: [{ name: 'token', title: 'Токен бота', kind: 'secret', required: true }],
    schema: {},
  },
];

test('каталог → Telegram → токен → «Підключено» і «Облікові дані» з використанням', async ({ page }) => {
  const connectors: Record<string, unknown>[] = [];
  const credentials: Record<string, unknown>[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
    if (path === '/api/v1/connectors/catalog') return route.fulfill({ json: CATALOG });
    if (path === '/api/v1/connectors' && request.method() === 'GET') {
      return route.fulfill({ json: { items: connectors, page: 1, pageSize: 200, total: connectors.length } });
    }
    if (path === '/api/v1/credentials' && request.method() === 'GET') return route.fulfill({ json: credentials });
    if (path === '/api/v1/credentials' && request.method() === 'POST') {
      const body = request.postDataJSON() as { name: string; type: string };
      credentials.push({
        id: 'cred1',
        name: body.name,
        type: body.type,
        fields: ['token'],
        createdBy: USER.id,
        usedBy: { connectors: [{ id: 'k1', name: 'Telegram' }], workflows: [] },
        error: null,
      });
      return route.fulfill({ status: 201, json: credentials[0] });
    }
    if (path === '/api/v1/connectors/catalog/telegram/install' && request.method() === 'POST') {
      const summary = { id: 'k1', name: 'Telegram', transport: 'stdio', status: 'ok', toolCount: 0, toolsCachedAt: null, catalogId: 'telegram' };
      connectors.push(summary);
      return route.fulfill({ status: 201, json: { summary, config: { command: 'dotnet' }, credentialId: 'cred1', tools: [] } });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  // Одне місце в меню; старе посилання відкриває вкладку «Каталог» з підсвіченим записом.
  await page.goto('/connectors?catalog=telegram');
  await expect(page).toHaveURL(/\/connections\?tab=catalog&catalog=telegram/);
  await expect(page.locator('nav').getByText('Підключення')).toBeVisible();
  await expect(page.locator('nav').getByText('Облікові дані')).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);

  await page.locator('[data-catalog="telegram"]').getByTestId('catalog-install').click();
  const panel = page.getByTestId('catalog-connect');
  await panel.getByLabel('Токен бота').fill('123:abc');
  await panel.getByTestId('credential-save').click();

  // Після встановлення — вкладка «Підключено».
  await expect(page).toHaveURL(/tab=connected/);
  await expect(page.locator('[data-connector="k1"]')).toContainText('Telegram');

  await page.getByTestId('tab-credentials').click();
  await expect(page).toHaveURL(/tab=credentials/);
  const row = page.locator('[data-credential="cred1"]');
  await expect(row).toContainText('Telegram');
  await expect(row.getByTestId('connection-count')).toHaveText('1');
});

test('/credentials відкриває вкладку «Облікові дані»', async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.goto('/credentials');
  await expect(page).toHaveURL(/\/connections\?tab=credentials/);
  await expect(page.getByTestId('empty')).toBeVisible();
});
