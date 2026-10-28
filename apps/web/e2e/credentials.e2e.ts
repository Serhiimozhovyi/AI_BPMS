import { expect, test } from '@playwright/test';

/**
 * F4-02: облікові дані. Api підмінено (`page.route`) як справжній: значення зберігаються «на сервері»,
 * а у відповідях — лише назви заповнених полів.
 */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const SECRET = 'sk-ant-very-secret-123';
const TYPES = [
  {
    type: 'anthropic',
    displayName: 'LLM: Anthropic',
    description: 'Вузол ai.llm з провайдером anthropic.',
    fields: [{ name: 'apiKey', title: 'API-ключ', kind: 'secret', required: true }],
    schema: {},
  },
  {
    type: 'mcp.http',
    displayName: 'Конектор HTTP: заголовки',
    description: 'Секретні заголовки MCP-сервера.',
    fields: [{ name: 'headers', title: 'Заголовки', kind: 'secretMap', required: true }],
    schema: {},
  },
];

test('створити облікові дані → «заповнено», значення секрету немає в DOM', async ({ page }) => {
  const stored: { id: string; name: string; type: string; data: Record<string, unknown> }[] = [];
  const summary = (c: (typeof stored)[number]) => ({
    id: c.id,
    name: c.name,
    type: c.type,
    fields: Object.keys(c.data),
    createdBy: USER.id,
    usedBy: { connectors: [], workflows: [] },
    error: null,
  });
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/credentials/types') return route.fulfill({ json: TYPES });
    if (path === '/api/v1/credentials' && request.method() === 'GET') return route.fulfill({ json: stored.map(summary) });
    if (path === '/api/v1/credentials' && request.method() === 'POST') {
      const body = request.postDataJSON() as { name: string; type: string; data: Record<string, unknown> };
      const created = { id: `c${stored.length + 1}`, ...body };
      stored.push(created);
      return route.fulfill({ status: 201, json: summary(created) });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/credentials');
  await expect(page.getByTestId('empty')).toBeVisible();

  await page.getByTestId('add-button').click();
  await page.getByTestId('credential-name').fill('Anthropic прод');
  await page.getByTestId('credential-type').click();
  await page.locator('mat-option[data-type="anthropic"]').click();
  await page.getByTestId('field-apiKey').fill(SECRET);
  await page.getByTestId('credential-save').click();

  const row = page.locator('[data-credential="c1"]');
  await expect(row).toContainText('Anthropic прод');
  await expect(row).toContainText('API-ключ');
  expect(stored[0].data).toEqual({ apiKey: SECRET });

  // Редагування: поле позначене «заповнено», значення в DOM немає.
  await row.getByTestId('edit').click();
  await expect(page.getByTestId('field-apiKey')).toHaveValue('');
  await expect(page.getByText('заповнено', { exact: true })).toBeVisible();
  expect(await page.content()).not.toContain(SECRET);
  await page.screenshot({ path: 'test-results/credentials.png', fullPage: true });
});
