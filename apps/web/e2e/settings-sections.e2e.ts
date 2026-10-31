import { expect, test } from '@playwright/test';

/** F3-83: «Налаштування» — розділи з навігацією, однакові поля без нативних `select`. Api підмінено (`page.route`). */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

test.beforeEach(async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/variables') return route.fulfill({ json: [] });
    if (path === '/api/v1/api-keys' || path === '/api/v1/workflow-groups') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
});

test('/settings?section=variables показує розділ змінних, без нативних select на сторінці', async ({ page }) => {
  await page.goto('/settings?section=variables');
  await expect(page.getByTestId('vars-form')).toBeVisible();
  await expect(page.getByTestId('create-button')).toHaveCount(0);
  await expect(page.locator('select')).toHaveCount(0);
  await expect(page.locator('[data-section="variables"]')).toHaveAttribute('aria-current', 'page');
});

test('навігація перемикає розділ і веде у query; вкладки «Підключити ШІ-агента»', async ({ page }) => {
  await page.goto('/settings');
  await expect(page.getByTestId('key-name')).toBeVisible();
  await expect(page.locator('select')).toHaveCount(0);

  await page.locator('[data-section="agent"]').click();
  await expect(page).toHaveURL(/section=agent/);
  await expect(page.getByTestId('mcp-config-desktop')).toContainText('mcp-remote');
  await page.getByRole('tab', { name: 'Claude Code' }).click();
  await expect(page.getByTestId('mcp-config-code')).toContainText('claude mcp add');
  await page.getByRole('tab', { name: 'Copilot (VS Code)' }).click();
  await expect(page.getByTestId('mcp-config-vscode')).toContainText('${input:ai-bpms-key}');
  await page.getByRole('tab', { name: 'Copilot Studio' }).click();
  await expect(page.getByTestId('mcp-config-copilot-studio')).toContainText('Header name: Authorization');
});
