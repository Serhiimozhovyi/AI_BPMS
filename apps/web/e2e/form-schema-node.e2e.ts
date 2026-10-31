import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

/**
 * F11-04: поля форми вузла `core.formTrigger` — кнопка «Налаштувати поля» у панелі, повноекранний конструктор,
 * перегляд 1024/390, збереження схеми у параметр вузла. Схема вузла — справжній `formTrigger.schema.json`
 * (з анотацією `x-openbp-widget: form-schema`); Api підмінено.
 */
const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';
const descriptorSchema = JSON.parse(readFileSync(resolve(__dirname, '../../../src/OpenBP.Nodes.Core/Schemas/formTrigger.schema.json'), 'utf-8'));

const definition = {
  formatVersion: 1,
  name: 'Форма',
  settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
  nodes: [
    {
      id: 'n_form',
      name: 'Заява',
      type: 'core.formTrigger',
      typeVersion: 1,
      position: [0, 0],
      parameters: { path: 'hr/leave', title: 'Відпустка', submitLabel: 'Подати', schema: { type: 'object', properties: {} } },
    },
  ],
  connections: [],
  pinData: {},
};

test('конструктор у панелі вузла: додати два поля, переглянути на телефоні, зберегти в параметр вузла', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  let saved: typeof definition | null = null;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') {
      return route.fulfill({
        json: [{ type: 'core.formTrigger', version: 1, displayName: 'Форма', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: descriptorSchema, isTrigger: true }],
      });
    }
    if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
      if (request.method() === 'PUT') saved = request.postDataJSON();
      return route.fulfill({
        json: { id: WORKFLOW_ID, name: 'Форма', active: false, version: saved ? 2 : 1, versionId: saved ? 'v2' : 'v1', definition: saved ?? definition, updatedAt: new Date().toISOString() },
      });
    }
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
    if (path === '/api/v1/executions') return route.fulfill({ json: { items: [], page: 1, pageSize: 1, total: 0 } });
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });

  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-node-id] .node__name').dblclick();
  await expect(page.getByTestId('node-panel')).toBeVisible();
  await expect(page.getByTestId('fs-empty')).toBeVisible();
  await expect(page.getByTestId('fs-open')).toContainText('(0)');

  await page.getByTestId('fs-open').click();
  const dialog = page.getByTestId('form-designer-dialog');
  await expect(dialog).toBeVisible();
  const box = (await page.locator('.bp-dialog-fullscreen').boundingBox())!;
  expect(box.width).toBeGreaterThanOrEqual(1430);
  await expect(page.getByTestId('fdd-save')).toBeDisabled();

  await dialog.getByTestId('fd-kind-text').click();
  await dialog.getByTestId('fd-prop-title').fill('ПІБ');
  await dialog.getByTestId('fd-kind-email').click();
  await dialog.getByTestId('fd-prop-title').fill('Пошта для відповіді');
  await expect(dialog.getByTestId('fd-card-tekst')).toBeVisible();

  await dialog.getByTestId('fd-mode-preview').click();
  await expect(dialog.getByTestId('fp-title')).toHaveText('Відпустка');
  await expect(dialog.getByTestId('fp-submit')).toHaveText('Подати');
  await expect(dialog.getByTestId('fp-page')).toHaveAttribute('data-width', '1024');
  await expect(dialog.getByTestId('form-field-tekst')).toBeVisible();
  await dialog.getByTestId('fp-width-390').click();
  expect((await dialog.getByTestId('fp-page').boundingBox())!.width).toBeLessThanOrEqual(390);
  await testInfo.attach('form-schema-dialog-preview', { body: await page.screenshot(), contentType: 'image/png' });

  const results = await new AxeBuilder({ page }).include('[data-testid="form-designer-dialog"]').analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(blocking.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);

  await page.getByTestId('fdd-save').click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId('fs-open')).toContainText('(2)');
  await expect(page.getByTestId('fs-list')).toContainText('ПІБ');
  await expect.poll(() => saved !== null).toBe(true);
  const schema = saved!.nodes[0].parameters['schema'] as { properties: Record<string, { title: string; format?: string }>; };
  expect(Object.entries(schema.properties).map(([key, p]) => `${key}:${p.title}`)).toEqual(['tekst:ПІБ', 'poshta:Пошта для відповіді']);
  expect(schema.properties['poshta'].format).toBe('email');
});

test('скасування діалогу зі змінами питає підтвердження і не пише в вузол', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  let puts = 0;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') {
      return route.fulfill({
        json: [{ type: 'core.formTrigger', version: 1, displayName: 'Форма', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: descriptorSchema, isTrigger: true }],
      });
    }
    if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
      if (request.method() === 'PUT') puts++;
      return route.fulfill({ json: { id: WORKFLOW_ID, name: 'Форма', active: false, version: 1, versionId: 'v1', definition, updatedAt: new Date().toISOString() } });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-node-id] .node__name').dblclick();
  await page.getByTestId('fs-open').click();
  await page.getByTestId('fd-kind-text').click();
  let asked = '';
  page.once('dialog', (d) => {
    asked = d.message();
    void d.dismiss();
  });
  await page.getByTestId('fdd-cancel').click();
  await expect.poll(() => asked).toContain('Закрити без збереження');
  await expect(page.getByTestId('form-designer-dialog')).toBeVisible();
  page.once('dialog', (d) => void d.accept());
  await page.getByTestId('fdd-cancel').click();
  await expect(page.getByTestId('form-designer-dialog')).toBeHidden();
  await expect(page.getByTestId('fs-open')).toContainText('(0)');
  expect(puts).toBe(0);
});
