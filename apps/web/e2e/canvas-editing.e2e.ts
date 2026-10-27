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

/** Нотатка на канвасі (F3-62). */
interface Note {
  id: string;
  text: string;
  position: [number, number];
  size?: [number, number];
  color?: string;
}

interface Definition {
  formatVersion: 1;
  name: string;
  settings: Record<string, unknown>;
  nodes: Node[];
  connections: Connection[];
  pinData: Record<string, unknown>;
  notes?: Note[];
}

const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';
const ERROR_WORKFLOW_ID = '66666666-7777-8888-9999-000000000000';

function descriptor(type: string, displayName: string, group: string, inputs: string[], outputs: string[]) {
  return { type, version: 1, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

const DESCRIPTORS = [
  descriptor('core.manualTrigger', 'Ручний запуск', 'trigger', [], ['main']),
  descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
  descriptor('core.code', 'Код (JavaScript)', 'transform', ['main'], ['main']),
];

/** Шаблон «Почати з прикладу» (F3-59), як його віддає `GET /workflows/templates`. */
const TEMPLATES = [
  {
    id: 'hello-manual',
    name: 'Привіт, OpenBP',
    description: 'Ручний запуск, Set і Code.',
    definition: {
      formatVersion: 1,
      name: 'Привіт, OpenBP',
      settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 60, errorWorkflowId: null, saveSuccessfulData: true },
      nodes: [
        { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
        { id: 'n_greeting', name: 'Привітання', type: 'core.set', typeVersion: 1, position: [300, 0], parameters: { fields: [] } },
      ],
      connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_greeting', input: 0 } }],
      pinData: {},
    },
  },
];

/** Відповідь фейкового помічника (F3-60): кроки інструментів, нове визначення, текст. */
function assistantStream(definition: unknown): string {
  const events = [
    { type: 'step', tool: 'get_workflow', ok: true, text: 'визначення сценарію' },
    { type: 'step', tool: 'describe_nodes', ok: true, text: 'типів вузлів: 3' },
    { type: 'step', tool: 'update_workflow', ok: true, text: "сценарій оновлено: вузлів 2, з'єднань 1" },
    { type: 'definition', definition },
    { type: 'message', text: 'Додав ручний запуск і «Привітання».' },
    { type: 'done' },
  ];
  return events.map((e) => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');
}

/** Фейковий Api: сценарій з версіями; кожен `PUT` — нова версія. */
class FakeApi {
  version = 1;
  /** Визначення кожної збереженої версії (індекс = номер − 1) — для «Історії» (F3-61). */
  saved: Definition[] = [];
  assistantRequests: unknown[] = [];
  /** Останнє виконання сценарію (F3-76); `null` — запусків не було. */
  lastExecution: { summary: Record<string, unknown>; steps: Record<string, unknown>[] } | null = null;
  definition: Definition = {
    formatVersion: 1,
    name: 'E2E',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [],
    connections: [],
    pinData: {},
  };

  /** Усі версії: перша — початкове визначення. */
  history(): Definition[] {
    return this.saved.length ? this.saved : [this.definition];
  }

  detail() {
    return {
      id: WORKFLOW_ID,
      name: this.definition.name,
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
      if (path === '/api/v1/workflows/templates') return route.fulfill({ json: TEMPLATES });
      if (path === `/api/v1/workflows/${WORKFLOW_ID}/versions`) {
        const all = this.history();
        const list = all.map((_, i) => ({ id: `v${i + 1}`, version: i + 1, createdAt: '2026-10-24T10:00:00Z', createdBy: 'u1', createdByEmail: 'e2e@openbp.test' }));
        return route.fulfill({ json: list.reverse() });
      }
      const versionMatch = new RegExp(`^/api/v1/workflows/${WORKFLOW_ID}/versions/(\\d+)$`).exec(path);
      if (versionMatch) return route.fulfill({ json: this.history()[Number(versionMatch[1]) - 1] });
      if (path === `/api/v1/workflows/${WORKFLOW_ID}/assistant` && request.method() === 'POST') {
        this.assistantRequests.push(request.postDataJSON());
        return route.fulfill({ status: 200, contentType: 'text/event-stream', body: assistantStream(TEMPLATES[0].definition) });
      }
      if (path === '/api/v1/executions' && request.method() === 'GET') {
        const items = this.lastExecution ? [this.lastExecution.summary] : [];
        return route.fulfill({ json: { items, page: 1, pageSize: 1, total: items.length } });
      }
      if (path === '/api/v1/executions/e1' && this.lastExecution) return route.fulfill({ json: this.lastExecution });
      if (path === '/api/v1/workflows') {
        const items = [this.detail(), { id: ERROR_WORKFLOW_ID, name: 'Сповіщення про помилки', active: true, version: 2, updatedAt: '' }];
        return route.fulfill({ json: { items, page: 1, pageSize: 200, total: items.length } });
      }
      if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
        if (request.method() === 'GET') return route.fulfill({ json: this.detail() });
        if (request.method() === 'PUT') {
          this.saved = this.history();
          this.definition = request.postDataJSON() as Definition;
          this.version++;
          this.saved.push(this.definition);
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

test('новий інстанс без конекторів: у палітрі немає помилки, лише підказка (F3-77)', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-item="core.manualTrigger"]')).toBeVisible();
  await expect(page.getByTestId('tools-error')).toHaveCount(0);
  await expect(page.getByTestId('no-connectors')).toContainText('Конекторів ще немає');
  await expect(page.locator('[data-item="core.manualTrigger"]')).not.toContainText('core.manualTrigger');
});

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

test('налаштування сценарію: зміни йдуть у settings збереженої версії, себе немає серед сценаріїв помилок', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.getByTestId('counts')).toContainText('вузлів: 0');

  await page.getByTestId('toolbar-more').click();
  await page.getByTestId('settings').click();
  const dialog = page.getByTestId('workflow-settings');
  await expect(dialog.getByTestId('settings-timeout')).toHaveValue('300');
  await dialog.getByTestId('settings-timeout').fill('1200');
  await dialog.getByTestId('settings-error-workflow').click();
  await expect(page.getByRole('option')).toHaveText(['Немає', 'Сповіщення про помилки']);
  await page.getByRole('option', { name: 'Сповіщення про помилки' }).click();
  await dialog.getByTestId('settings-save-successful').getByRole('switch').click();
  await page.getByTestId('settings-apply').click();
  await expect(dialog).toHaveCount(0);

  await waitSaved(page);
  expect(api.definition.settings).toEqual({ timezone: 'Europe/Kyiv', executionTimeoutSec: 1200, errorWorkflowId: ERROR_WORKFLOW_ID, saveSuccessfulData: false });

  await page.getByTestId('undo').click();
  // Одразу після undo індикатор ще «збережено» (автозбереження з затримкою) — чекаємо саму збережену версію.
  await expect.poll(() => api.definition.settings['executionTimeoutSec']).toBe(300);
  await waitSaved(page);
});

test('F3-50: Tab → тригер, «+» біля порту → пошук → Enter — два з’єднані вузли', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.getByTestId('counts')).toContainText('вузлів: 0');

  // Порожній сценарій: спливаюча палітра лише з тригерами.
  await page.keyboard.press('Tab');
  const popup = page.getByTestId('quick-add');
  await expect(popup).toBeVisible();
  await expect(popup.locator('[data-item]')).toHaveCount(1);
  await page.keyboard.press('Enter');
  await expect(popup).toHaveCount(0);
  await expect(card(page, 'Ручний запуск')).toBeVisible();

  // Новий вузол виділено — біля його виходу «+».
  await page.getByTestId('port-add').click();
  await expect(popup).toContainText('Наступний крок');
  await expect(popup.locator('[data-item="core.manualTrigger"]')).toHaveCount(0);
  await page.getByTestId('quick-add-search').fill('задати');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await waitSaved(page);
  expect(graph(api.definition).edges).toEqual(['Ручний запуск#0->Задати поля#0']);

  // Esc закриває без змін; «Останні» — першими.
  await page.getByTestId('port-add').click();
  await expect(popup.locator('.quick__heading').first()).toHaveText('Останні');
  await page.keyboard.press('Escape');
  await expect(popup).toHaveCount(0);
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
});

test('F3-50: з’єднання, відпущене в порожньому місці, створює з’єднаний вузол', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.manualTrigger"]').click();
  await expect(card(page, 'Ручний запуск')).toBeVisible();

  const a = (await card(page, 'Ручний запуск').boundingBox())!;
  await page.mouse.move(a.x + a.width, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width + 150, a.y + a.height / 2 + 40, { steps: 5 });
  await page.mouse.move(a.x + a.width + 300, a.y + a.height / 2 + 80, { steps: 5 });
  await page.mouse.up();

  const popup = page.getByTestId('quick-add');
  await expect(popup).toBeVisible();
  await popup.locator('[data-item="core.code"]').click();

  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await waitSaved(page);
  expect(graph(api.definition).edges).toEqual(['Ручний запуск#0->Код (JavaScript)#0']);
  // Вузол там, де відпустили, а не на типовому місці (панель нового вузла звужує полотно, тож сценарій може зсунутись —
  // міряємо відстань між картками, а не абсолютні координати).
  const a2 = (await card(page, 'Ручний запуск').boundingBox())!;
  const b = (await card(page, 'Код (JavaScript)').boundingBox())!;
  expect(b.x).toBeGreaterThan(a2.x + a2.width + 200);
});

test('F3-50: «+» на виділеному ребрі вставляє крок між вузлами', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.manualTrigger"]').click();
  await page.locator('[data-item="core.set"]').click();
  await connect(page, 'Ручний запуск', 'Задати поля');
  await expect(page.locator('path.edge')).toHaveCount(1);

  const a = (await card(page, 'Ручний запуск').boundingBox())!;
  const b = (await card(page, 'Задати поля').boundingBox())!;
  await page.mouse.click((a.x + a.width + b.x) / 2, a.y + a.height / 2);
  await page.getByTestId('edge-add').click();
  await expect(page.getByTestId('quick-add')).toContainText('Вставити крок');
  await page.getByTestId('quick-add-search').fill('код');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('counts')).toContainText('вузлів: 3 · ребер: 2');
  await waitSaved(page);
  expect(graph(api.definition).edges).toEqual(['Ручний запуск#0->Код (JavaScript)#0', 'Код (JavaScript)#0->Задати поля#0'].sort());
});

test('F3-58: ПКМ → Дублювати, D вимикає вузол, перейменування з тулбару', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.set"]').click();
  await expect(page.locator('[data-node-id]')).toHaveCount(1);

  // ПКМ на вузлі — контекстне меню; «Дублювати» додає копію поруч.
  await card(page, 'Задати поля').click({ button: 'right' });
  await expect(page.getByTestId('context-menu')).toHaveAttribute('data-kind', 'node');
  await page.getByTestId('menu-duplicate').click();
  await expect(page.getByTestId('context-menu')).toHaveCount(0);
  await expect(card(page, 'Задати поля 2')).toBeVisible();

  // D на виділеному вузлі — вимикає (копія зсунута на 40 px і перекриває центр оригіналу — клік у кут).
  await card(page, 'Задати поля').click({ position: { x: 12, y: 12 } });
  await expect(card(page, 'Задати поля')).toHaveClass(/selected/);
  // Виділення доходить до редактора через подію vflow — чекаємо панель саме цього вузла, інакше D влучить у копію.
  await expect(page.getByTestId('node-panel-name')).toHaveText('Задати поля');
  await page.keyboard.press('d');
  await expect(card(page, 'Задати поля')).toHaveClass(/disabled/);
  await waitSaved(page);
  expect(api.definition.nodes.find((n) => n.name === 'Задати поля')).toMatchObject({ disabled: true });

  // Тулбар над карткою: «Перейменувати» — inline-поле, Enter зберігає.
  await card(page, 'Задати поля 2').hover();
  await card(page, 'Задати поля 2').getByTestId('node-tool-rename').click();
  const input = page.getByTestId('node-rename');
  await expect(input).toBeFocused();
  await input.fill('Підготувати дані');
  await input.press('Enter');
  await expect(card(page, 'Підготувати дані')).toBeVisible();
  await waitSaved(page);
  expect(graph(api.definition).nodes).toEqual(['Задати поля:core.set', 'Підготувати дані:core.set']);
});

test('F3-58: «Вирівняти» розставляє вузли зліва направо і не змінює ребра; є мінікарта і масштаб', async ({ page }) => {
  const api = new FakeApi();
  const node = (id: string, name: string, type: string, x: number, y: number): Node => ({ id, name, type, typeVersion: 1, position: [x, y], parameters: {} });
  api.definition = {
    ...api.definition,
    nodes: [node('t', 'Ручний запуск', 'core.manualTrigger', 600, 300), node('s', 'Задати поля', 'core.set', 0, 0), node('c', 'Код (JavaScript)', 'core.code', 300, 500)],
    connections: [
      { from: { node: 't', output: 0 }, to: { node: 's', input: 0 } },
      { from: { node: 's', output: 0 }, to: { node: 'c', input: 0 } },
    ],
  };
  const edgesBefore = structuredClone(api.definition.connections);
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id]')).toHaveCount(3);
  await expect(page.locator('svg rect[stroke="#b9b4cc"]')).toHaveCount(0);
  await page.getByTestId('canvas-minimap').click();
  await expect(page.locator('svg rect[stroke="#b9b4cc"]')).toHaveCount(1);

  await page.getByTestId('canvas-arrange').click();
  await waitSaved(page);
  const x = Object.fromEntries(api.definition.nodes.map((n) => [n.id, n.position[0]]));
  expect(x['t']).toBeLessThan(x['s']);
  expect(x['s']).toBeLessThan(x['c']);
  expect(api.definition.connections).toEqual(edgesBefore);
  await expect(page.locator('path.edge')).toHaveCount(2);

  // «+» збільшує картки, fit повертає весь сценарій у видиму область.
  const before = (await card(page, 'Задати поля').boundingBox())!.width;
  await page.getByTestId('canvas-zoom-in').click();
  await expect.poll(async () => (await card(page, 'Задати поля').boundingBox())!.width).toBeGreaterThan(before);
  await page.getByTestId('canvas-fit').click();
  for (const name of ['Ручний запуск', 'Задати поля', 'Код (JavaScript)']) await expect(card(page, name)).toBeInViewport();
});

test('F3-59: порожній сценарій — «Почати з прикладу» ставить вузли шаблону; «Додати тригер» і підказка на порту', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  const start = page.getByTestId('empty-start');
  await expect(start).toBeVisible();

  await page.getByTestId('start-template').click();
  await page.locator('[data-template="hello-manual"]').click();
  await expect(start).toHaveCount(0);
  await expect(card(page, 'Старт')).toBeVisible();
  await expect(card(page, 'Привітання')).toBeVisible();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await waitSaved(page);
  expect(graph(api.definition)).toEqual({ nodes: ['Привітання:core.set', 'Старт:core.manualTrigger'], edges: ['Старт#0->Привітання#0'] });
  expect(api.definition.name).toBe('E2E');
  await expect(page.getByTestId('first-step-hint')).toHaveCount(0);

  // Undo повертає порожній сценарій — картка знову на місці; «Додати тригер» → палітра лише з тригерами.
  await page.keyboard.press('Control+z');
  await expect(start).toBeVisible();
  await page.getByTestId('start-trigger').click();
  const popup = page.getByTestId('quick-add');
  await expect(popup).toContainText('Додати тригер');
  await expect(popup.locator('[data-item]')).toHaveCount(1);
  await page.keyboard.press('Enter');
  await expect(card(page, 'Ручний запуск')).toBeVisible();

  // До першого з'єднання біля порту тригера — «Додайте наступний крок».
  await expect(page.getByTestId('first-step-hint')).toHaveText('Додайте наступний крок');
  await page.getByTestId('port-add').click();
  await page.getByTestId('quick-add-search').fill('задати');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await expect(page.getByTestId('first-step-hint')).toHaveCount(0);
});

test('F3-60: «Описати процес» → помічник будує сценарій з тригером і Set; «Скасувати» прибирає відповідь', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);

  await page.getByTestId('start-describe').click();
  await expect(page.getByTestId('assistant-panel')).toBeVisible();
  await expect(page.getByTestId('empty-start')).toHaveCount(0);
  await page.getByTestId('assistant-input').fill('зроби сценарій з тригером і Set');
  await page.getByTestId('assistant-send').click();

  await expect(page.getByTestId('assistant-log')).toContainText('Додав ручний запуск і «Привітання».');
  await expect(page.getByTestId('assistant-log')).toContainText('Змінив сценарій');
  await expect(card(page, 'Старт')).toBeVisible();
  await expect(card(page, 'Привітання')).toBeVisible();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await waitSaved(page);
  expect(graph(api.definition).nodes).toEqual(['Привітання:core.set', 'Старт:core.manualTrigger']);
  expect(api.assistantRequests).toEqual([expect.objectContaining({ message: 'зроби сценарій з тригером і Set', history: [] })]);

  // Відповідь помічника — одна правка в історії: «Скасувати» повертає порожній сценарій.
  await page.getByTestId('assistant-close').click();
  await page.getByTestId('undo').click();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 0');
});

test('F3-61: змінити, відкрити історію, відновити попередню версію — канвас як до зміни; перегляд версії лише для читання', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);

  // Версія 2 — шаблон з двома вузлами, версія 3 — без «Привітання».
  await page.getByTestId('start-template').click();
  await page.locator('[data-template="hello-manual"]').click();
  await waitSaved(page);
  await card(page, 'Привітання').click({ position: { x: 12, y: 12 } });
  await expect(page.getByTestId('node-panel-name')).toHaveText('Привітання');
  await page.getByTestId('delete').click();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 1 · ребер: 0');
  await waitSaved(page);
  expect(api.version).toBe(3);

  await page.getByTestId('toolbar-more').click();
  await page.getByTestId('history').click();
  const versions = page.getByTestId('version');
  await expect(versions).toHaveCount(3);
  await expect(versions.nth(0)).toContainText('поточна');
  await expect(versions.nth(0).getByTestId('version-diff')).toContainText('видалено: Привітання');
  await expect(versions.nth(1).getByTestId('version-diff')).toContainText('додано: Старт, Привітання');
  await expect(versions.nth(0).getByTestId('version-restore')).toHaveCount(0);
  await versions.nth(1).getByTestId('version-restore').click();

  await expect(page.getByTestId('editor-notice')).toContainText('Відновлено версію 2');
  await expect(card(page, 'Привітання')).toBeVisible();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await waitSaved(page);
  expect(api.version).toBe(4);
  expect(graph(api.definition)).toEqual(graph(api.saved[1]));

  // «Переглянути» версію 3: канвас лише для читання, без палітри; «До поточної версії» повертає редагування.
  await page.getByTestId('toolbar-more').click();
  await page.getByTestId('history').click();
  await page.locator('[data-version="3"]').getByTestId('version-view').click();
  await expect(page).toHaveURL(/version=3/);
  await expect(page.getByTestId('version-banner')).toHaveText('Версія 3');
  await expect(page.getByTestId('counts')).toContainText('вузлів: 1');
  await expect(page.getByTestId('undo')).toHaveCount(0);
  await page.getByTestId('version-current').click();
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');
  await expect(page.getByTestId('undo')).toBeVisible();
});

test('F3-62: ПКМ → «Додати нотатку», Markdown-текст, колір, Shift+S і Escape; після перезавантаження нотатка на місці', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await page.locator('[data-item="core.set"]').click();
  await expect(page.locator('[data-node-id]')).toHaveCount(1);

  // ПКМ на порожньому місці канвасу → «Додати нотатку»: нова нотатка там, де клацнули, одразу з полем тексту.
  const canvas = page.locator('vflow');
  const box = (await canvas.boundingBox())!;
  await page.mouse.click(box.x + box.width - 320, box.y + 80, { button: 'right' });
  await expect(page.getByTestId('context-menu')).toHaveAttribute('data-kind', 'canvas');
  await page.getByTestId('menu-add-note').click();
  const editor = page.getByTestId('note-editor');
  await expect(editor).toBeFocused();
  await editor.fill('## Ліміт 1С\n\nПеревірити **до запуску**\n- [ ] узгодити з бухгалтерією');
  await editor.press('Control+Enter');
  await expect(editor).toHaveCount(0);
  const note = page.locator('[data-note-id]');
  await expect(note).toHaveCount(1);
  await expect(note.locator('h2')).toHaveText('Ліміт 1С');
  await expect(note.locator('strong')).toHaveText('до запуску');
  await expect(note.locator('li')).toContainText('☐ узгодити');
  await waitSaved(page);
  expect(api.definition.notes).toHaveLength(1);
  expect(api.definition.notes![0]).toMatchObject({ text: '## Ліміт 1С\n\nПеревірити **до запуску**\n- [ ] узгодити з бухгалтерією', color: 'yellow', size: [240, 120] });
  const noteId = api.definition.notes![0].id;

  // Палітра над виділеною нотаткою: колір зберігається у визначенні.
  await note.locator('h2').click();
  await expect(note).toHaveClass(/selected/);
  await page.getByTestId('note-color-green').click();
  await expect(note).toHaveAttribute('data-note-color', 'green');
  await waitSaved(page);
  expect(api.definition.notes![0].color).toBe('green');

  // Shift+S: друга нотатка в центрі; Escape на порожній прибирає її, вузол і перша нотатка лишаються.
  await page.mouse.click(box.x + 60, box.y + box.height - 60);
  await page.keyboard.press('Shift+S');
  await expect(page.getByTestId('note-editor')).toBeFocused();
  await expect(page.locator('[data-note-id]')).toHaveCount(2);
  await page.keyboard.press('Escape');
  await expect(page.locator('[data-note-id]')).toHaveCount(1);
  await expect(page.locator('[data-node-id]')).toHaveCount(1);
  await waitSaved(page);
  expect(api.definition.notes!.map((n) => n.id)).toEqual([noteId]);
  expect(graph(api.definition).nodes).toEqual(['Задати поля:core.set']);

  // Перезавантаження: нотатка на місці з тим самим текстом і кольором; вузол теж.
  await page.reload();
  await expect(page.locator('[data-node-id]')).toHaveCount(1);
  const reloaded = page.locator(`[data-note-id="${noteId}"]`);
  await expect(reloaded).toBeVisible();
  await expect(reloaded.locator('h2')).toHaveText('Ліміт 1С');
  await expect(reloaded).toHaveAttribute('data-note-color', 'green');

  // Подвійний клік — редагування; клік поза нотаткою зберігає новий текст.
  await reloaded.locator('h2').dblclick();
  const again = page.getByTestId('note-editor');
  await expect(again).toBeFocused();
  await again.fill('Готово');
  await page.mouse.click(box.x + 60, box.y + box.height - 60);
  await expect(reloaded.locator('p')).toHaveText('Готово');
  await waitSaved(page);
  expect(api.definition.notes![0].text).toBe('Готово');
});

/** F3-75: шапка в один рядок, «Виконати» й «Опублікувати» видимі та клікабельні на 1024, 1280 і 1920 px. */
for (const width of [1024, 1280, 1920]) {
  test(`шапка на ${width} px: один рядок, «Виконати» й «Опублікувати» у видимій частині`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    const api = new FakeApi();
    await api.install(page);
    await page.goto(`/workflows/${WORKFLOW_ID}`);
    await page.locator('[data-item="core.manualTrigger"]').click();
    await expect(page.locator('[data-node-id]')).toHaveCount(1);

    const bar = page.locator('app-editor-toolbar header');
    const box = await bar.boundingBox();
    expect(box!.height).toBeLessThanOrEqual(56);
    for (const id of ['run', 'publish']) {
      const button = page.getByTestId(id);
      await expect(button).toBeVisible();
      const rect = await button.boundingBox();
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(width);
      expect(await bar.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
    }
    await expect(page.getByTestId('run')).toBeEnabled();
    await expect(page.getByTestId('publish')).toBeEnabled();
  });
}

test('назва сценарію перейменовується в шапці: Enter зберігає, Esc скасовує', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.getByTestId('workflow-name')).toContainText('E2E');

  await page.getByTestId('workflow-name-edit').click();
  await page.getByTestId('workflow-name-input').fill('Інша назва');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('workflow-name')).toContainText('E2E');

  await page.getByTestId('workflow-name-edit').click();
  await page.getByTestId('workflow-name-input').fill('Заявки клієнтів');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('workflow-name')).toContainText('Заявки клієнтів');
  await expect.poll(() => api.definition.name).toBe('Заявки клієнтів');
});

/** Ряд із `count` вузлів «Задати поля» завдовжки більше за екран, з'єднаних ланцюжком. */
function longChain(count: number): Pick<Definition, 'nodes' | 'connections'> {
  const nodes: Node[] = Array.from({ length: count }, (_, i) => ({
    id: i === 0 ? 'n0' : `n${i}`,
    name: i === 0 ? 'Старт' : `Крок ${i}`,
    type: i === 0 ? 'core.manualTrigger' : 'core.set',
    typeVersion: 1,
    position: [i * 280, 120],
    parameters: {},
  }));
  const connections: Connection[] = nodes.slice(1).map((n, i) => ({ from: { node: nodes[i].id, output: 0 }, to: { node: n.id, input: 0 } }));
  return { nodes, connections };
}

test('F3-76: довгий сценарій при відкритті видно повністю з відступом, а не притиснутим до краю', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const api = new FakeApi();
  Object.assign(api.definition, longChain(8));
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id]')).toHaveCount(8);

  const canvas = (await page.locator('.editor__canvas').boundingBox())!;
  await expect.poll(async () => {
    const first = (await page.locator('[data-node-id="n0"]').boundingBox())!;
    const last = (await page.locator('[data-node-id="n7"]').boundingBox())!;
    return first.x >= canvas.x + 40 && last.x + last.width <= canvas.x + canvas.width - 40;
  }).toBe(true);
});

test('F3-76: мінікарта згорнута за замовчуванням і розгортається кнопкою', async ({ page }) => {
  const api = new FakeApi();
  Object.assign(api.definition, longChain(3));
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id]')).toHaveCount(3);
  const minimap = page.locator('svg.root-svg > svg');
  await expect(minimap).toHaveCount(0);

  await page.getByTestId('canvas-minimap').click();
  await expect(minimap).toHaveCount(1);
  // Рамка мінікарти (її розмір задає ngx-vflow: 20 % полотна, далі стискається до 180×110).
  const box = (await page.locator('svg rect[stroke="#b9b4cc"]').boundingBox())!;
  expect(box.width).toBeLessThanOrEqual(181);
  expect(box.height).toBeLessThanOrEqual(111);
});

test('F3-76: масштаб і положення сценарію запам\'ятовуються й відновлюються при наступному відкритті', async ({ page }) => {
  const api = new FakeApi();
  Object.assign(api.definition, longChain(3));
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id="n0"]')).toBeVisible();
  await page.getByTestId('canvas-zoom-out').click();
  await page.waitForTimeout(900);
  const before = (await page.locator('[data-node-id="n0"]').boundingBox())!;

  await page.reload();
  await expect(page.locator('[data-node-id="n0"]')).toBeVisible();
  await page.waitForTimeout(500);
  const after = (await page.locator('[data-node-id="n0"]').boundingBox())!;
  expect(Math.abs(after.x - before.x)).toBeLessThan(3);
  expect(Math.abs(after.width - before.width)).toBeLessThan(2);
});

test('F3-76: останній запуск, що впав, видно на вузлі; після правки й вимкнення значки зникають', async ({ page }) => {
  const api = new FakeApi();
  Object.assign(api.definition, longChain(3));
  const step = (nodeId: string, status: string, error?: string) => ({
    id: `s-${nodeId}`,
    nodeId,
    runIndex: 0,
    status,
    input: [],
    output: [],
    error: error ? { message: error } : null,
    startedAt: '2026-10-27T18:10:40',
    finishedAt: '2026-10-27T18:10:41',
  });
  const summary = { id: 'e1', workflowId: WORKFLOW_ID, versionId: 'v1', mode: 'manual', state: 'error', startedAt: '2026-10-27T18:10:40', finishedAt: '2026-10-27T18:10:42', error: 'boom' };
  api.lastExecution = { summary, steps: [step('n0', 'success'), step('n1', 'error', 'Крок 1: ділення на нуль'), step('n2', 'skipped')] };
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);

  await expect(page.locator('[data-node-id="n1"]')).toHaveAttribute('data-run-status', 'error');
  await expect(page.locator('[data-node-id="n0"]')).toHaveAttribute('data-run-status', 'success');
  await expect(page.locator('[data-node-id="n0"] [data-testid="node-run-time"]')).toHaveText('18:10:41');
  await expect(page.getByTestId('last-run-menu')).toHaveCount(0);

  // Перемикач у керуванні канвасом.
  await page.getByTestId('canvas-last-run').click();
  await expect(page.locator('[data-node-id="n1"]')).not.toHaveAttribute('data-run-status', 'error');
  await page.getByTestId('canvas-last-run').click();
  await expect(page.locator('[data-node-id="n1"]')).toHaveAttribute('data-run-status', 'error');

  // Будь-яка правка робить дані неактуальними.
  await card(page, 'Крок 2').click({ position: { x: 12, y: 12 } });
  await page.getByTestId('delete').click();
  await expect(page.locator('[data-run-status]')).toHaveCount(0);
  await page.getByTestId('toolbar-more').click();
  await expect(page.getByTestId('last-run-menu')).toContainText('18:10, помилка');
});

test('F3-76: вузол, доданий з палітри за межі екрана, одразу видно', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const api = new FakeApi();
  Object.assign(api.definition, longChain(5));
  await api.install(page);
  await page.goto(`/workflows/${WORKFLOW_ID}`);
  await expect(page.locator('[data-node-id="n0"]')).toBeVisible();
  for (let i = 0; i < 3; i++) await page.getByTestId('canvas-zoom-in').click();

  await page.locator('[data-item="core.code"]').click();
  const added = card(page, 'Код (JavaScript)');
  await expect(added).toHaveCount(1);
  await expect(added).toBeInViewport({ ratio: 0.9 });
});
