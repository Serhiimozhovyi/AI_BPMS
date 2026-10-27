import { expect, test } from '@playwright/test';

/** F3-81: журнал виконань — адреса `/executions/:id` як джерело виділення; «Відкрити в редакторі» виділяє вузол, що впав. Api підмінено. */
const MANUAL = { type: 'core.manualTrigger', version: 1, displayName: 'Ручний запуск', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: { type: 'object' }, isTrigger: true };
const SET = { type: 'core.set', version: 1, displayName: 'Задати поля', group: 'data', inputs: ['main'], outputs: ['main'], parametersSchema: { type: 'object' }, isTrigger: false };

const definition = {
  formatVersion: 1,
  name: 'Заявки',
  settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
  nodes: [
    { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
    { id: 'n_fail', name: 'Упав тут', type: 'core.set', typeVersion: 1, position: [300, 0], parameters: {} },
  ],
  connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_fail', input: 0 } }],
  pinData: {},
};
const workflow = { id: 'w1', name: 'Заявки', active: false, version: 1, versionId: 'v1', definition, updatedAt: '2026-10-27T09:00:00Z' };
const summary = (id: string, state: string) => ({
  id,
  workflowId: 'w1',
  versionId: 'v1',
  mode: 'manual',
  state,
  startedAt: '2026-10-27T10:00:00Z',
  finishedAt: state === 'running' ? null : '2026-10-27T10:00:02Z',
  error: state === 'error' ? 'Крок упав' : null,
});
const step = (id: string, nodeId: string, status: string) => ({
  id,
  nodeId,
  runIndex: 0,
  status,
  input: [[{ json: {} }]],
  output: status === 'success' ? [[{ json: {} }]] : [],
  error: status === 'error' ? { message: 'Крок упав' } : null,
  startedAt: '2026-10-27T10:00:00Z',
  finishedAt: '2026-10-27T10:00:01Z',
});

test('виконання відкривається за адресою, перезавантаження зберігає його, редактор виділяє вузол, що впав', async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/nodes') return route.fulfill({ json: [MANUAL, SET] });
    if (path === '/api/v1/executions') return route.fulfill({ json: { items: [summary('e1', 'error'), summary('e2', 'success')], page: 1, pageSize: 50, total: 2 } });
    if (path === '/api/v1/executions/e1') return route.fulfill({ json: { summary: summary('e1', 'error'), steps: [step('s1', 'n_start', 'success'), step('s2', 'n_fail', 'error')] } });
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [workflow], page: 1, pageSize: 200, total: 1 } });
    if (path === '/api/v1/workflows/w1') return route.fulfill({ json: workflow });
    if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
    if (path.endsWith('/versions') || path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });

  await page.goto('/executions');
  await page.locator('[data-execution="e1"]').getByTestId('open-execution').click();
  await expect(page).toHaveURL(/\/executions\/e1$/);
  await expect(page.getByTestId('execution-detail')).toBeVisible();

  await page.reload();
  await expect(page).toHaveURL(/\/executions\/e1$/);
  await expect(page.getByTestId('execution-detail')).toBeVisible();

  await page.getByTestId('open-in-editor').click();
  await expect(page).toHaveURL(/\/workflows\/w1\?.*node=n_fail/);
  await expect(page.getByTestId('node-panel-name')).toHaveText('Упав тут');
});
