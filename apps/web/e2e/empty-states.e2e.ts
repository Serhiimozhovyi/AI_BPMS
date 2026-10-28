import { expect, test } from '@playwright/test';

/** F3-82: порожні стани з дією і сповіщення про результат дій. Api підмінено (`page.route`). */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

test('порожні «Конектори» → «Відкрити каталог» відкриває каталог', async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/connectors/catalog') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
  });

  await page.goto('/connectors');
  await expect(page.getByTestId('empty-title')).toContainText('Ще немає жодного MCP-сервера');
  await page.getByTestId('empty-action').click();
  await expect(page.getByTestId('tab-catalog')).toHaveClass(/mat-button-toggle-checked/);
  await expect(page).toHaveURL(/tab=catalog/);
});

test('створення змінної показує сповіщення', async ({ page }) => {
  const stored: { key: string; value: string; description: string | null }[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/variables' && request.method() === 'GET') return route.fulfill({ json: stored });
    if (path.startsWith('/api/v1/variables/') && request.method() === 'PUT') {
      const body = request.postDataJSON() as { value: string; description: string | null };
      const saved = { key: decodeURIComponent(path.split('/').pop()!), ...body };
      stored.push(saved);
      return route.fulfill({ json: saved });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/settings?section=variables');
  await page.getByTestId('var-key').fill('REGION');
  await page.getByTestId('var-value').fill('UA');
  await page.getByTestId('var-save').click();
  await expect(page.locator('.mat-mdc-snack-bar-container')).toContainText('Змінну «REGION» збережено');
});
