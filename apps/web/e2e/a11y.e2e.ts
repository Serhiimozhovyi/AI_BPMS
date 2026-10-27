import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

/**
 * F3-86: доступність — axe на основних сторінках; порушення рівня `serious`/`critical` ламають збірку.
 * Api підмінено (`page.route`), тож перевіряється лише розмітка SPA.
 */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

const DEFINITION = {
  formatVersion: 1,
  name: 'Заявки з сайту',
  settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
  nodes: [
    { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
    { id: 'n_set', name: 'Задати поля', type: 'core.set', typeVersion: 1, position: [300, 0], parameters: {} },
  ],
  connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_set', input: 0 } }],
  pinData: {},
};

const WORKFLOW = {
  id: WORKFLOW_ID,
  name: 'Заявки з сайту',
  active: false,
  version: 2,
  versionId: 'v2',
  definition: DEFINITION,
  updatedAt: '2026-10-23T10:00:00Z',
  groupId: null,
  triggerType: 'core.manualTrigger',
  lastExecution: { id: 'e1', status: 'success', startedAt: '2026-10-26T09:00:00Z' },
};

function descriptor(type: string, displayName: string, group: string, inputs: string[], outputs: string[]) {
  return { type, version: 1, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

const TASK = {
  id: 't1',
  executionId: 'e1',
  workflowId: WORKFLOW_ID,
  workflowName: 'Знижки',
  nodeId: 'n_appr',
  title: 'Погодити знижку 15%',
  status: 'open',
  assignee: { email: 'admin@firma.ua' },
  escalated: false,
  canComplete: true,
  createdAt: '2026-10-26T09:00:00Z',
  dueAt: null,
};

async function mockApi(page: Page, signedIn = true) {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const page1 = (items: unknown[]) => route.fulfill({ json: { items, page: 1, pageSize: 50, total: items.length } });
    if (path === '/api/v1/auth/me') return signedIn ? route.fulfill({ json: USER }) : route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (path === '/api/v1/auth/providers') return route.fulfill({ json: [] });
    if (path === '/api/v1/nodes') {
      return route.fulfill({
        json: [
          descriptor('core.manualTrigger', 'Ручний запуск', 'trigger', [], ['main']),
          descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
        ],
      });
    }
    if (path === '/api/v1/workflows') return page1([WORKFLOW]);
    if (path === `/api/v1/workflows/${WORKFLOW_ID}`) return route.fulfill({ json: WORKFLOW });
    if (path === `/api/v1/workflows/${WORKFLOW_ID}/versions`) return route.fulfill({ json: [{ id: 'v2', version: 2, createdAt: '2026-10-24T10:00:00Z', createdBy: 'u1', createdByEmail: 'admin@firma.ua' }] });
    if (path === '/api/v1/workflow-groups') return route.fulfill({ json: [] });
    if (path === '/api/v1/workflows/templates') return route.fulfill({ json: [] });
    if (path === '/api/v1/executions') {
      return page1([
        { id: 'e1', workflowId: WORKFLOW_ID, versionId: 'v2', mode: 'trigger', state: 'success', startedAt: '2026-10-26T09:00:00Z', finishedAt: '2026-10-26T09:00:05Z', error: null, correlationKey: null },
      ]);
    }
    if (path === '/api/v1/tasks') return page1([TASK]);
    if (path === '/api/v1/forms/leave') {
      return route.fulfill({
        json: { kind: 'trigger', title: 'Заявка на відпустку', description: 'Заповніть форму', submitLabel: 'Надіслати', schema: { type: 'object', required: ['name'], properties: { name: { type: 'string', title: 'ПІБ' } } } },
      });
    }
    if (path === '/api/v1/connectors' || path === '/api/v1/credentials' || path === '/api/v1/tables') return page1([]);
    if (path === '/api/v1/api-keys') return route.fulfill({ json: [] });
    return page1([]);
  });
}

const PAGES: { name: string; url: string; ready: (page: Page) => Promise<void>; signedIn?: boolean }[] = [
  { name: 'Вхід', url: '/login', signedIn: false, ready: (p) => expect(p.getByTestId('login-form')).toBeVisible() },
  { name: 'Огляд', url: '/', ready: (p) => expect(p.getByRole('heading', { level: 1 })).toBeVisible() },
  { name: 'Сценарії', url: '/workflows', ready: (p) => expect(p.getByTestId('open-link').first()).toBeVisible() },
  { name: 'Редактор', url: `/workflows/${WORKFLOW_ID}`, ready: (p) => expect(p.locator('[data-node-id]').first()).toBeVisible() },
  { name: 'Виконання', url: '/executions', ready: (p) => expect(p.getByTestId('state').first()).toBeVisible() },
  { name: 'Задачі', url: '/tasks', ready: (p) => expect(p.getByTestId('task-link')).toBeVisible() },
  { name: 'Таблиці', url: '/tables', ready: (p) => expect(p.getByRole('heading', { level: 1 })).toBeVisible() },
  { name: 'Конектори', url: '/connectors', ready: (p) => expect(p.getByRole('heading', { level: 1 })).toBeVisible() },
  { name: 'Облікові дані', url: '/credentials', ready: (p) => expect(p.getByRole('heading', { level: 1 })).toBeVisible() },
  { name: 'Налаштування', url: '/settings', ready: (p) => expect(p.getByRole('heading', { level: 1 })).toBeVisible() },
  { name: 'Форма', url: '/form/leave', signedIn: false, ready: (p) => expect(p.getByTestId('form-title')).toBeVisible() },
];

for (const { name, url, ready, signedIn } of PAGES) {
  test(`axe: ${name} — без порушень serious/critical`, async ({ page }) => {
    await mockApi(page, signedIn ?? true);
    await page.goto(url);
    await ready(page);
    await page.waitForTimeout(500);
    const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const blocking = violations
      .filter((v) => v.impact === 'serious' || v.impact === 'critical')
      .map((v) => ({
        id: v.id,
        impact: v.impact,
        help: v.help,
        nodes: v.nodes.slice(0, 12).map((n) => `${n.target.join(' ')} ${(n.any[0]?.message ?? '').slice(0, 140)}`),
      }));
    expect(blocking, JSON.stringify(blocking, null, 2)).toEqual([]);
  });
}

test('клавіатура в редакторі: Tab доходить до «Виконати», Enter запускає виконання', async ({ page }) => {
  await mockApi(page);
  let runRequested = false;
  await page.route(`**/api/v1/workflows/${WORKFLOW_ID}/executions`, async (route) => {
    runRequested = true;
    return route.fulfill({ status: 201, json: { id: 'e9', workflowId: WORKFLOW_ID, versionId: 'v2', mode: 'manual', state: 'success', startedAt: '2026-10-27T09:00:00Z', finishedAt: '2026-10-27T09:00:01Z', error: null } });
  });
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id]').first()).toBeVisible();

  const run = page.getByTestId('run');
  for (let i = 0; i < 40 && !(await run.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab');
  await expect(run).toBeFocused();
  // Фокус видно: або власний контур, або індикатор Material на ::before.
  const ring = await run.evaluate((el) => {
    const own = getComputedStyle(el);
    const before = getComputedStyle(el, '::before');
    return [own.outlineStyle, own.boxShadow, before.outlineStyle, before.borderTopStyle].join('|');
  });
  expect(ring, ring).not.toMatch(/^none\|none\|none\|none$/);
  await page.keyboard.press('Enter');
  await expect.poll(() => runRequested).toBe(true);
});
