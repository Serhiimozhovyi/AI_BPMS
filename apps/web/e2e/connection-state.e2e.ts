import { expect, test } from '@playwright/test';

/** F3-100: на картці підключення немає «tools»; стан з'єднання й «Перевірити з'єднання»; застарілий кеш перевіряється сам. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const TOOLS = [{ name: 'send_message', inputSchema: { type: 'object' } }];

test("картка підключення: стан з'єднання, «Перевірити з'єднання», автоперевірка старого кешу", async ({ page }) => {
  let summary = { id: 'k1', name: 'Бот продажів', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: new Date(Date.now() - 3 * 3_600_000).toISOString() };
  let checks = 0;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/connectors' && request.method() === 'GET') return route.fulfill({ json: { items: [summary], page: 1, pageSize: 200, total: 1 } });
    if (path === '/api/v1/connectors/k1/refresh-tools' && request.method() === 'POST') {
      checks += 1;
      summary = { ...summary, toolsCachedAt: new Date().toISOString() };
      return route.fulfill({ json: { summary, config: { url: 'https://x.example/mcp' }, credentialId: null, tools: TOOLS } });
    }
    if (path === '/api/v1/connectors/k1') return route.fulfill({ json: { summary, config: { url: 'https://x.example/mcp' }, credentialId: null, tools: TOOLS } });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/connectors');
  await page.locator('[data-connector="k1"] button').first().click();

  const detail = page.getByTestId('connector-detail');
  await expect(page.getByTestId('connection-state')).toContainText('перевірено щойно');
  expect(checks).toBe(1);
  await expect(detail.getByTestId('check-connection')).toHaveText("Перевірити з'єднання");
  expect((await detail.innerText()).toLowerCase()).not.toContain('tools');

  await detail.getByTestId('check-connection').click();
  await expect.poll(() => checks).toBe(2);
});
