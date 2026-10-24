import { expect, test } from '@playwright/test';

/** F4-03: облікові дані створюються з форми конектора і одразу обираються; конектор зберігається з їхнім id. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const TYPES = [
  {
    type: 'mcp.http',
    displayName: 'Конектор HTTP: заголовки',
    description: 'Секретні заголовки MCP-сервера.',
    fields: [{ name: 'headers', title: 'Заголовки', kind: 'secretMap', required: true }],
    schema: {},
  },
  {
    type: 'mcp.stdio',
    displayName: 'Конектор stdio: змінні середовища',
    description: 'Секретні змінні середовища MCP-сервера.',
    fields: [{ name: 'env', title: 'Змінні середовища', kind: 'secretMap', required: true }],
    schema: {},
  },
  {
    type: 'oauth2',
    displayName: 'OAuth2 (authorization code)',
    description: 'Вхід через провайдера.',
    fields: [{ name: 'clientId', title: 'Client ID', kind: 'text', required: true }],
    schema: {},
  },
];

test('конектор: створити облікові дані в діалозі → обрано → POST /connectors з credentialId', async ({ page }) => {
  let connectorBody: Record<string, unknown> | null = null;
  let credentialBody: Record<string, unknown> | null = null;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials/options') return route.fulfill({ json: [{ id: 'old', name: 'Старий stdio', type: 'mcp.stdio' }] });
    if (path === '/api/v1/credentials' && request.method() === 'POST') {
      credentialBody = request.postDataJSON() as Record<string, unknown>;
      return route.fulfill({
        status: 201,
        json: {
          id: '0b9a1c64-8d1e-4f7a-9c3b-2d5e6f708192',
          name: credentialBody['name'],
          type: credentialBody['type'],
          fields: ['headers.Authorization'],
          createdBy: USER.id,
          usedBy: { connectors: [], workflows: [] },
          error: null,
        },
      });
    }
    if (path === '/api/v1/connectors' && request.method() === 'POST') {
      connectorBody = request.postDataJSON() as Record<string, unknown>;
      return route.fulfill({
        status: 201,
        json: {
          summary: { id: 'k1', name: connectorBody['name'], transport: 'http', status: 'ok', toolCount: 0, toolsCachedAt: null },
          config: connectorBody['config'],
          credentialId: connectorBody['credentialId'],
          tools: [],
        },
      });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/connectors');
  await page.getByTestId('add-button').click();
  const form = page.getByTestId('connector-form');
  await form.locator('input[name="name"]').fill('CRM');
  await form.getByRole('radio', { name: 'HTTP' }).click();
  await form.locator('input[name="url"]').fill('https://crm.example/mcp');

  await page.getByTestId('create-credential').click();
  const dialog = page.getByTestId('credential-dialog');
  // Тип за транспортом уже обрано (для HTTP — заголовки; OAuth2 — на вибір).
  await expect(dialog.getByTestId('credential-type')).toContainText('Конектор HTTP: заголовки');
  await dialog.getByTestId('credential-name').fill('CRM токен');
  await dialog.getByTestId('pair-key').fill('Authorization');
  await dialog.getByTestId('pair-value').fill('Bearer secret-token');
  await dialog.getByTestId('credential-save').click();
  await expect(dialog).toHaveCount(0);
  expect(credentialBody).toEqual({ name: 'CRM токен', type: 'mcp.http', data: { headers: { Authorization: 'Bearer secret-token' } } });
  await expect(page.getByTestId('connector-credential')).toContainText('CRM токен');

  await form.getByRole('button', { name: 'Додати' }).click();
  await expect.poll(() => connectorBody?.['credentialId']).toBe('0b9a1c64-8d1e-4f7a-9c3b-2d5e6f708192');
});
