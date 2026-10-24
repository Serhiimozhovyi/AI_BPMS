import { Page, expect, test } from '@playwright/test';

/**
 * F3-66: бізнес-вигляд сценарію. Api підмінено (`page.route`): сценарій погодження з умовою, згорнутим `core.set`
 * і кроком конектора «Кіт CRM», плюс дія людини в Кіті з бізнес-атрибутів (docs/business-view.md).
 */

const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

function descriptor(type: string, displayName: string, group: string, inputs: string[], outputs: string[]) {
  return { type, version: 1, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

const DESCRIPTORS = [
  descriptor('core.webhook', 'Webhook', 'trigger', [], ['main']),
  descriptor('core.if', 'Умова', 'logic', ['main'], ['true', 'false']),
  descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
  descriptor('mcp.tool', 'MCP tool', 'integration', ['main'], ['main']),
];

const DEFINITION = {
  formatVersion: 1,
  name: 'Погодження задачі',
  settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
  nodes: [
    { id: 'n_hook', name: 'Нова задача', type: 'core.webhook', typeVersion: 1, position: [100, 200], parameters: {} },
    { id: 'n_if', name: 'Погоджено?', type: 'core.if', typeVersion: 1, position: [360, 200], parameters: {} },
    { id: 'n_set', name: 'Поля для Кіта', type: 'core.set', typeVersion: 1, position: [620, 120], parameters: {} },
    {
      id: 'n_task',
      name: 'Create task',
      type: 'mcp.tool',
      typeVersion: 1,
      position: [880, 120],
      parameters: { serverId: 'kit', toolName: 'create_task', arguments: {} },
      business: { title: 'Задача виконавцю', subtitle: 'kit-crm' },
    },
    { id: 'n_back', name: 'Повернути автору', type: 'mcp.tool', typeVersion: 1, position: [620, 300], parameters: { serverId: 'kit', toolName: 'comment', arguments: {} } },
  ],
  connections: [
    { from: { node: 'n_hook', output: 0 }, to: { node: 'n_if', input: 0 } },
    { from: { node: 'n_if', output: 0 }, to: { node: 'n_set', input: 0 } },
    { from: { node: 'n_set', output: 0 }, to: { node: 'n_task', input: 0 } },
    { from: { node: 'n_if', output: 1 }, to: { node: 'n_back', input: 0 } },
  ],
  pinData: {},
  business: {
    lanes: [{ id: 'kit2', name: 'Кіт 2', description: 'люди у веб-формах' }],
    steps: [{ id: 'h_approve', title: 'Погоджувач тисне «Погодити»', lane: 'kit2', links: [{ node: 'n_if', direction: 'in', label: 'читає статус' }] }],
  },
};

async function install(page: Page): Promise<void> {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') return route.fulfill({ json: DESCRIPTORS });
    if (path === '/api/v1/connectors') {
      const kit = { id: 'kit', name: 'Кіт CRM', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: null };
      return route.fulfill({ json: { items: [kit], page: 1, pageSize: 200, total: 1 } });
    }
    if (path === '/api/v1/connectors/kit') {
      const summary = { id: 'kit', name: 'Кіт CRM', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: null };
      return route.fulfill({ json: { summary, config: {}, credentialId: null, tools: [{ name: 'create_task', inputSchema: { type: 'object' } }] } });
    }
    if (path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
      return route.fulfill({
        json: { id: WORKFLOW_ID, name: 'Погодження задачі', active: false, version: 1, versionId: 'v1', definition: DEFINITION, updatedAt: '' },
      });
    }
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });
}

test.beforeEach(async ({ page }) => {
  await install(page);
});

test('«Бізнес»: доріжки й кроки без технічних деталей; клік на крок — технічний вигляд з панеллю вузла', async ({ page }) => {
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.getByTestId('view-technical')).toHaveAttribute('aria-checked', 'true');
  await page.getByTestId('view-business').click();
  await expect(page).toHaveURL(/\?view=business/);

  const view = page.getByTestId('business-view');
  await expect(view.getByTestId('business-lane')).toHaveCount(4);
  await expect(view.getByTestId('business-lane').first()).toContainText('Кіт 2');
  await expect(view.getByTestId('business-step')).toHaveCount(5);
  await expect(view).toContainText('Задача виконавцю');
  await expect(view).toContainText('читає статус');
  await expect(view).not.toContainText('core.');
  await expect(view).not.toContainText('create_task');
  await expect(view).not.toContainText('Поля для Кіта');
  await expect(page.locator('app-canvas')).toHaveCount(0);

  await view.locator('[data-step="n_task"]').click();
  await expect(page.getByTestId('view-technical')).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('editor-panel')).toBeVisible();
  await expect(page).not.toHaveURL(/view=business/);
});

test('?view=business після перезавантаження і експорт SVG', async ({ page }) => {
  await page.goto(`/workflows/${WORKFLOW_ID}?view=business`);
  await expect(page.getByTestId('business-view').getByTestId('business-step')).toHaveCount(5);
  const download = page.waitForEvent('download');
  await page.getByTestId('business-export-svg').click();
  const file = await download;
  // Ім'я файлу — назва сценарію; headless Chromium підміняє нелатинські імена на «download», тож тут не перевіряємо.
  const path = await file.path();
  const { readFileSync } = await import('node:fs');
  const svg = readFileSync(path, 'utf8');
  expect(svg).toContain('Погоджувач тисне');
  expect(svg).not.toContain('class=');
});

