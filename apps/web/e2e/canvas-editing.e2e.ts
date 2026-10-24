import { Locator, Page, expect, test } from '@playwright/test';

/**
 * F2-17: редагування канвасу з автозбереженням. Api підмінено in-memory сховищем (`page.route`), яке переживає
 * перезавантаження сторінки, — тож «перезавантажити — граф той самий» перевіряє саме збережену версію.
 */

interface Node {
  id: string;
  name: string;
  type: string;
  typeVersion: number;
  position: [number, number];
  parameters: Record<string, unknown>;
}

interface Connection {
  from: { node: string; output: number };
  to: { node: string; input: number };
}

interface Definition {
  formatVersion: 1;
  name: string;
  settings: Record<string, unknown>;
  nodes: Node[];
  connections: Connection[];
  pinData: Record<string, unknown>;
}

const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';

function descriptor(type: string, displayName: string, group: string, inputs: string[], outputs: string[]) {
  return { type, version: 1, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

const DESCRIPTORS = [
  descriptor('core.manualTrigger', 'Ручний запуск', 'trigger', [], ['main']),
  descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
  descriptor('core.code', 'Код (JavaScript)', 'transform', ['main'], ['main']),
];

/** Фейковий Api: сценарій з версіями; кожен `PUT` — нова версія. */
class FakeApi {
  version = 1;
  definition: Definition = {
    formatVersion: 1,
    name: 'E2E',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [],
    connections: [],
    pinData: {},
  };

  detail() {
    return {
      id: WORKFLOW_ID,
      name: 'E2E',
      active: false,
      version: this.version,
      versionId: `v${this.version}`,
      definition: this.definition,
      updatedAt: new Date().toISOString(),
    };
  }

  async install(page: Page): Promise<void> {
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
      if (path === '/api/v1/nodes') return route.fulfill({ json: DESCRIPTORS });
      if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
      if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
        if (request.method() === 'GET') return route.fulfill({ json: this.detail() });
        if (request.method() === 'PUT') {
          this.definition = { ...(request.postDataJSON() as Definition), name: 'E2E' };
          this.version++;
          return route.fulfill({ json: this.detail() });
        }
      }
      return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
    });
  }
}

/** Граф для порівняння: вузли за назвою і ребра за назвами кінців. */
function graph(definition: Definition) {
  const names = new Map(definition.nodes.map((n) => [n.id, n.name]));
  return {
    nodes: definition.nodes.map((n) => `${n.name}:${n.type}`).sort(),
    edges: definition.connections.map((c) => `${names.get(c.from.node)}#${c.from.output}->${names.get(c.to.node)}#${c.to.input}`).sort(),
  };
}

function card(page: Page, name: string): Locator {
  return page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`) }) });
}

/** Тягне мишею від вихідного порту `from` до вхідного порту `to` (порти — на середині правого/лівого краю картки). */
async function connect(page: Page, from: string, to: string): Promise<void> {
  await expect(card(page, from)).toBeVisible();
  await expect(card(page, to)).toBeVisible();
  const a = (await card(page, from).boundingBox())!;
  const b = (await card(page, to).boundingBox())!;
  await page.mouse.move(a.x + a.width, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move((a.x + a.width + b.x) / 2, b.y + b.height / 2, { steps: 5 });
  await page.mouse.move(b.x, b.y + b.height / 2, { steps: 5 });
  await page.mouse.up();
}

async function waitSaved(page: Page): Promise<void> {
  await expect(page.getByTestId('saved')).toBeVisible();
  await expect(page.getByTestId('dirty')).toHaveCount(0);
}

test('додати 3 вузли, з’єднати, undo, redo, перезавантажити — граф той самий', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.getByTestId('counts')).toContainText('вузлів: 0');

  await page.locator('[data-item="core.manualTrigger"]').click();
  await page.locator('[data-item="core.set"]').click();
  await page.locator('[data-item="core.code"]').click();
  await expect(page.locator('[data-node-id]')).toHaveCount(3);

  await connect(page, 'Ручний запуск', 'Задати поля');
  await expect(page.getByTestId('counts')).toContainText('ребер: 1');
  await connect(page, 'Задати поля', 'Код (JavaScript)');
  await expect(page.getByTestId('counts')).toContainText('ребер: 2');
  await expect(page.locator('path.edge')).toHaveCount(2);

  await page.getByTestId('undo').click();
  await expect(page.getByTestId('counts')).toContainText('ребер: 1');
  await page.getByTestId('redo').click();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 3 · ребер: 2');

  await waitSaved(page);
  const expected = {
    nodes: ['Задати поля:core.set', 'Код (JavaScript):core.code', 'Ручний запуск:core.manualTrigger'],
    edges: ['Ручний запуск#0->Задати поля#0', 'Задати поля#0->Код (JavaScript)#0'].sort(),
  };
  expect(graph(api.definition)).toEqual(expected);
  const saved = structuredClone(api.definition);

  await page.reload();
  await expect(page.locator('[data-node-id]')).toHaveCount(3);
  await expect(page.getByTestId('counts')).toContainText('вузлів: 3 · ребер: 2');
  await expect(page.locator('path.edge')).toHaveCount(2);
  for (const name of ['Ручний запуск', 'Задати поля', 'Код (JavaScript)']) await expect(card(page, name)).toBeVisible();
  // Перезавантаження нічого не змінило і не зберегло.
  expect(api.definition).toEqual(saved);
});

test('клавіші: Ctrl+C / Ctrl+V копіює вузол, Delete видаляє, Ctrl+Z повертає', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.set"]').click();
  await expect(page.locator('[data-node-id]')).toHaveCount(1);

  await card(page, 'Задати поля').click();
  await expect(page.getByTestId('delete')).toBeEnabled();
  await page.keyboard.press('ControlOrMeta+c');
  await page.keyboard.press('ControlOrMeta+v');
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  await expect(card(page, 'Задати поля 2')).toBeVisible();

  // Вставлена копія виділена — Delete видаляє саме її.
  await page.keyboard.press('Delete');
  await expect(page.locator('[data-node-id]')).toHaveCount(1);
  await expect(card(page, 'Задати поля 2')).toHaveCount(0);

  await page.keyboard.press('ControlOrMeta+z');
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  await waitSaved(page);
  expect(graph(api.definition).nodes).toEqual(['Задати поля 2:core.set', 'Задати поля:core.set']);
});

test('помилки валідації з Api (422) показуються на вузлі', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.route(`**/api/v1/workflows/${WORKFLOW_ID}`, async (route) => {
    if (route.request().method() !== 'PUT') return route.fallback();
    const body = route.request().postDataJSON() as Definition;
    return route.fulfill({
      status: 422,
      contentType: 'application/problem+json',
      json: {
        title: 'Сценарій не пройшов валідацію',
        status: 422,
        code: 'WORKFLOW_VALIDATION',
        detail: 'Сценарій не пройшов валідацію.',
        errors: [{ code: 'SCHEMA_VIOLATION', message: 'Поле fields обов’язкове', nodeId: body.nodes[0].id, path: '/nodes/0/parameters' }],
      },
    });
  });
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.set"]').click();

  await expect(page.getByTestId('save-error')).toContainText('Сценарій не пройшов валідацію');
  await expect(card(page, 'Задати поля')).toHaveAttribute('data-invalid', 'true');
  await expect(card(page, 'Задати поля').getByTestId('node-errors')).toHaveAttribute('title', 'Поле fields обов’язкове');
  expect(api.version).toBe(1);
});

test('виділене ребро видаляється клавішею Delete, вузли лишаються', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.manualTrigger"]').click();
  await page.locator('[data-item="core.set"]').click();
  await connect(page, 'Ручний запуск', 'Задати поля');
  await expect(page.locator('path.edge')).toHaveCount(1);

  // Клік по середині ребра (між портами двох карток).
  const a = (await card(page, 'Ручний запуск').boundingBox())!;
  const b = (await card(page, 'Задати поля').boundingBox())!;
  await page.mouse.click((a.x + a.width + b.x) / 2, a.y + a.height / 2);
  await expect(page.locator('path.edge_selected')).toHaveCount(1);
  await page.keyboard.press('Delete');

  await expect(page.locator('path.edge')).toHaveCount(0);
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  await waitSaved(page);
  expect(api.definition.connections).toEqual([]);
});
