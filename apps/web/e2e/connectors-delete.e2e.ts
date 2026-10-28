import { Page, expect, test } from '@playwright/test';

/** F3-98: підключення можна видалити з інтерфейсу; активні сценарії блокують видалення зрозумілим текстом. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

function summary(id: string, name: string) {
  return { id, name, transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: new Date().toISOString() };
}

async function install(page: Page, options: { blocked?: boolean } = {}) {
  let items = [summary('k1', 'Тестовий Telegram'), summary('k2', 'Інший')];
  const deleted: string[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/connectors' && request.method() === 'GET') return route.fulfill({ json: { items, page: 1, pageSize: 200, total: items.length } });
    const match = /^\/api\/v1\/connectors\/(k\d)$/.exec(path);
    if (match && request.method() === 'GET') {
      return route.fulfill({
        json: { summary: items.find((c) => c.id === match[1]), config: { url: 'https://x.example/mcp' }, credentialId: null, tools: [{ name: 'send_message', inputSchema: { type: 'object' } }] },
      });
    }
    if (match && request.method() === 'DELETE') {
      if (options.blocked) {
        return route.fulfill({ status: 409, json: { code: 'CONNECTOR_IN_USE', detail: 'Конектор «Тестовий Telegram» використовують активні сценарії: «Погодження».' } });
      }
      deleted.push(match[1]);
      items = items.filter((c) => c.id !== match[1]);
      return route.fulfill({ status: 204, body: '' });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  return deleted;
}

test('видалити випробуване підключення: «Видалити» → підтвердження → зникло зі списку', async ({ page }) => {
  const deleted = await install(page);
  await page.goto('/connectors');
  await expect(page.locator('[data-connector]')).toHaveCount(2);

  await page.locator('[data-connector="k1"]').getByTestId('row-delete').click();
  await expect(page.getByTestId('delete-confirm-box')).toContainText('Тестовий Telegram');
  await page.getByTestId('delete-confirm').click();

  await expect(page.locator('[data-connector="k1"]')).toHaveCount(0);
  await expect(page.locator('[data-connector="k2"]')).toHaveCount(1);
  expect(deleted).toEqual(['k1']);
});

test('підключення в активному сценарії не видаляється: видно, які сценарії його тримають', async ({ page }) => {
  await install(page, { blocked: true });
  await page.goto('/connectors');
  await page.locator('[data-connector="k1"]').getByTestId('row-delete').click();
  await page.getByTestId('delete-confirm').click();

  await expect(page.getByTestId('detail-error')).toContainText('«Погодження»');
  await expect(page.getByTestId('detail-error')).toContainText('Деактивуйте');
  await expect(page.locator('[data-connector="k1"]')).toHaveCount(1);
});
