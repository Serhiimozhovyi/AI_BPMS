import { expect, test } from '@playwright/test';

/**
 * F3-94: каталог → Кіт CRM → підписані поля («Адреса OData», «Адреса токена», «Client ID», «Client secret»)
 * замість пар «назва змінної → значення»; облікові дані створюються й підключення встановлюється одним потоком.
 */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const CATALOG = [
  {
    manifest: {
      id: 'kit-crm',
      name: 'Кіт CRM',
      description: 'Угоди й задачі.',
      icon: '🐈',
      transport: 'stdio',
      stdio: { command: 'dotnet', arguments: ['kit.dll'] },
      credential: {
        type: 'oauth2.clientCredentials',
        map: { tokenUrl: 'KIT_TOKEN_URL', clientId: 'KIT_CLIENT_ID', clientSecret: 'KIT_CLIENT_SECRET' },
        env: [{ name: 'KIT_CLIENT_SECRET', secret: true, required: true }],
      },
      settings: [{ name: 'baseUrl', title: 'Адреса OData', required: true, env: 'Connector__BaseUrl', placeholder: 'https://crm.example.ua/odata/' }],
      triggers: [],
    },
    source: 'builtin',
    installed: [],
  },
];
const TYPES = [
  {
    type: 'oauth2.clientCredentials',
    displayName: 'OAuth2: сервісний клієнт (client credentials)',
    description: 'Доступ сервісу до сервісу без входу користувача.',
    fields: [
      { name: 'tokenUrl', title: 'Адреса токена', kind: 'url', required: true },
      { name: 'clientId', title: 'Client ID', kind: 'text', required: true },
      { name: 'clientSecret', title: 'Client secret', kind: 'secret', required: true },
    ],
    schema: {},
  },
];

test('Кіт CRM з каталогу: поля облікових даних і налаштування, перевірка, підключення створено', async ({ page }) => {
  let credentialBody: Record<string, unknown> | null = null;
  let checkBody: Record<string, unknown> | null = null;
  let installBody: Record<string, unknown> | null = null;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
    if (path === '/api/v1/connectors/catalog') return route.fulfill({ json: CATALOG });
    if (path === '/api/v1/credentials/check' && request.method() === 'POST') {
      checkBody = request.postDataJSON() as Record<string, unknown>;
      return route.fulfill({ json: { ok: false, error: 'invalid_client' } });
    }
    if (path === '/api/v1/credentials' && request.method() === 'POST') {
      credentialBody = request.postDataJSON() as Record<string, unknown>;
      return route.fulfill({
        status: 201,
        json: { id: 'cred1', name: credentialBody['name'], type: credentialBody['type'], fields: ['tokenUrl', 'clientId', 'clientSecret'], createdBy: USER.id, usedBy: { connectors: [], workflows: [] }, error: null },
      });
    }
    if (path === '/api/v1/connectors/catalog/kit-crm/install' && request.method() === 'POST') {
      installBody = request.postDataJSON() as Record<string, unknown>;
      return route.fulfill({
        status: 201,
        json: {
          summary: { id: 'k1', name: installBody['name'], transport: 'stdio', status: 'ok', toolCount: 0, toolsCachedAt: null, catalogId: 'kit-crm' },
          config: { command: 'dotnet', catalog: 'kit-crm' },
          credentialId: 'cred1',
          tools: [],
        },
      });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/connectors?catalog=kit-crm');
  await page.locator('[data-catalog="kit-crm"]').getByTestId('catalog-install').click();

  const panel = page.getByTestId('catalog-connect');
  // Підписані поля; жодних імен змінних середовища.
  await expect(panel.getByLabel('Адреса OData')).toBeVisible();
  await expect(panel.getByLabel('Адреса токена')).toBeVisible();
  await expect(panel.getByLabel('Client ID')).toBeVisible();
  await expect(panel.getByLabel('Client secret')).toBeVisible();
  await expect(panel).not.toContainText('KIT_');
  await expect(panel).not.toContainText('Connector__');
  await expect(panel.getByRole('button', { name: 'Додати пару' })).toHaveCount(0);

  // Без адреси OData підключити не можна.
  await expect(panel.getByTestId('credential-save')).toBeDisabled();
  await panel.getByLabel('Адреса OData').fill('https://crm.example.ua/odata/');
  await panel.getByLabel('Адреса токена').fill('https://sso.example.ua/token');
  await panel.getByLabel('Client ID').fill('openbp');
  await panel.getByLabel('Client secret').fill('s3cret');

  await panel.getByTestId('credential-check').click();
  await expect(panel.getByTestId('check-result')).toContainText('Провайдер не прийняв Client ID або Client secret');
  expect(checkBody).toEqual({
    type: 'oauth2.clientCredentials',
    data: { tokenUrl: 'https://sso.example.ua/token', clientId: 'openbp', clientSecret: 's3cret' },
  });

  await panel.getByTestId('credential-save').click();
  await expect(page.getByTestId('catalog-connect')).toHaveCount(0);
  expect(credentialBody).toEqual({
    name: 'Кіт CRM',
    type: 'oauth2.clientCredentials',
    data: { tokenUrl: 'https://sso.example.ua/token', clientId: 'openbp', clientSecret: 's3cret' },
  });
  expect(installBody).toEqual({ name: 'Кіт CRM', transport: 'stdio', credentialId: 'cred1', settings: { baseUrl: 'https://crm.example.ua/odata/' } });
});
