import { Locator, Page, expect, test } from '@playwright/test';

/**
 * F4-13: `<openbp-canvas>` на демо-сторінці (element-demo/) з «агресивними» стилями хоста. Api підмінено
 * in-memory сценарієм (`page.route`); `api-base` — абсолютна адреса, тож перевіряється і префікс запитів.
 */

const WORKFLOW_ID = '11111111-2222-3333-4444-555555555555';
const TOKEN = 'obp_test_token';

interface Definition {
  formatVersion: 1;
  name: string;
  settings: Record<string, unknown>;
  nodes: { id: string; name: string; type: string; typeVersion: number; position: [number, number]; parameters: Record<string, unknown> }[];
  connections: { from: { node: string; output: number }; to: { node: string; input: number } }[];
  pinData: Record<string, unknown>;
}

function descriptor(type: string, displayName: string, group: string, inputs: string[], outputs: string[]) {
  return { type, version: 1, displayName, group, inputs, outputs, parametersSchema: { type: 'object' }, isTrigger: inputs.length === 0 };
}

const DESCRIPTORS = [
  descriptor('core.manualTrigger', 'Ручний запуск', 'trigger', [], ['main']),
  descriptor('core.set', 'Задати поля', 'transform', ['main'], ['main']),
];

class FakeApi {
  version = 1;
  puts = 0;
  /** Значення `Authorization` усіх запитів до Api. */
  readonly authorization: (string | undefined)[] = [];
  definition: Definition = {
    formatVersion: 1,
    name: 'Вбудований',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
    nodes: [
      { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
      { id: 'n_set', name: 'Поля', type: 'core.set', typeVersion: 1, position: [320, 0], parameters: {} },
    ],
    connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_set', input: 0 } }],
    pinData: {},
  };

  detail() {
    return { id: WORKFLOW_ID, name: 'Вбудований', active: false, version: this.version, versionId: `v${this.version}`, definition: this.definition, updatedAt: new Date().toISOString() };
  }

  async install(page: Page): Promise<void> {
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      this.authorization.push(await request.headerValue('authorization') ?? undefined);
      const path = new URL(request.url()).pathname;
      if (path === '/api/v1/nodes') return route.fulfill({ json: DESCRIPTORS });
      if (path === '/api/v1/connectors') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
      if (path === '/api/v1/credentials/options') return route.fulfill({ json: [] });
      if (path === `/api/v1/workflows/${WORKFLOW_ID}`) {
        if (request.method() === 'GET') return route.fulfill({ json: this.detail() });
        if (request.method() === 'PUT') {
          this.puts++;
          this.definition = request.postDataJSON() as Definition;
          this.version++;
          return route.fulfill({ json: this.detail() });
        }
      }
      return route.fulfill({ status: 404, contentType: 'application/problem+json', json: { title: 'Not Found', status: 404, code: 'NOT_FOUND', detail: 'Сценарій не знайдено.' } });
    });
  }
}

function demo(params: Record<string, string>): string {
  return `/element-demo/?${new URLSearchParams({ 'api-base': 'http://localhost:4310', ...params })}`;
}

function card(page: Page, name: string): Locator {
  return page.locator('[data-node-id]').filter({ has: page.locator('.node__name', { hasText: name }) });
}

async function dragBy(page: Page, target: Locator, dx: number, dy: number): Promise<void> {
  const box = (await target.boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx / 2, y + dy / 2, { steps: 5 });
  await page.mouse.move(x + dx, y + dy, { steps: 5 });
  await page.mouse.up();
}

function hostEvents(page: Page): Promise<{ type: string; detail: Record<string, unknown> }[]> {
  return page.evaluate(() => (window as unknown as { openbpEvents: { type: string; detail: Record<string, unknown> }[] }).openbpEvents);
}

test('завантажити сценарій, змінити, зберегти → подія openbp-saved; токен іде в Authorization', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(demo({ 'workflow-id': WORKFLOW_ID, token: TOKEN }));

  await expect(page.getByTestId('workflow-name')).toHaveText('Вбудований');
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  // Вбудований режим: без посилань на сторінки SPA.
  await expect(page.getByTestId('executions-link')).toHaveCount(0);

  await dragBy(page, card(page, 'Поля'), 120, 80);
  await expect(page.getByTestId('saved')).toBeVisible();
  await expect.poll(() => hostEvents(page)).toContainEqual({ type: 'openbp-saved', detail: { workflowId: WORKFLOW_ID, version: 2 } });
  expect(api.puts).toBe(1);
  const moved = api.definition.nodes.find((n) => n.id === 'n_set')!;
  expect(moved.position).not.toEqual([320, 0]);

  // Палітра працює: у сценарії з вузлами вона — смуга (F3-89), розгортаємо; новий вузол → ще одна версія.
  await page.getByTestId('palette-expand').click();
  await page.locator('[data-item="core.set"]').click();
  await expect(page.locator('[data-node-id]')).toHaveCount(3);
  await expect.poll(() => api.version).toBe(3);
  await expect.poll(() => hostEvents(page)).toContainEqual({ type: 'openbp-saved', detail: { workflowId: WORKFLOW_ID, version: 3 } });

  // Backspace у полі панелі вузла (усередині Shadow DOM) редагує текст, а не видаляє вузол.
  await card(page, 'Старт').click();
  // «Додатково» (F3-78) згорнуте за замовчуванням.
  await page.getByTestId('node-extra-toggle').click();
  const name = page.getByTestId('node-common').locator('input').first();
  await name.click();
  await name.press('End');
  await name.press('Backspace');
  await expect(page.locator('[data-node-id]')).toHaveCount(3);

  expect(api.authorization.length).toBeGreaterThan(0);
  expect(new Set(api.authorization)).toEqual(new Set([`Bearer ${TOKEN}`]));
});

test('Виконати → подія openbp-run і живий статус через SSE з токеном; діалог налаштувань — у Shadow DOM', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  const executionId = 'eeeeeeee-0000-0000-0000-000000000001';
  let sseAuthorization: string | undefined;
  await page.route(`**/api/v1/workflows/${WORKFLOW_ID}/executions`, (route) =>
    route.fulfill({ status: 202, json: { id: executionId, workflowId: WORKFLOW_ID, mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null } }),
  );
  await page.route(`**/api/v1/executions/${executionId}/events`, async (route) => {
    sseAuthorization = (await route.request().headerValue('authorization')) ?? undefined;
    const step = (nodeId: string, i: number) => ({ id: `s${i}`, nodeId, runIndex: 0, status: 'success', input: [], output: [[{ json: { ok: true } }]], error: null, startedAt: '', finishedAt: '' });
    const body = [
      'retry: 2000\n\n',
      `event: step\nid: s1\ndata: ${JSON.stringify(step('n_start', 1))}\n\n`,
      `event: step\nid: s2\ndata: ${JSON.stringify(step('n_set', 2))}\n\n`,
      `event: status\ndata: ${JSON.stringify({ executionId, state: 'success', error: null })}\n\n`,
    ].join('');
    return route.fulfill({ status: 200, headers: { 'content-type': 'text/event-stream' }, body });
  });
  await page.goto(demo({ 'workflow-id': WORKFLOW_ID, token: TOKEN }));
  await expect(page.locator('[data-node-id]')).toHaveCount(2);

  await page.getByTestId('run').click();
  await expect(page.getByTestId('run-status')).toHaveAttribute('data-state', 'success');
  await expect(card(page, 'Поля')).toHaveAttribute('data-run-status', 'success');
  await expect.poll(() => hostEvents(page)).toContainEqual({ type: 'openbp-run', detail: { executionId } });
  expect(sseAuthorization).toBe(`Bearer ${TOKEN}`);

  await page.getByTestId('toolbar-more').click();
  await page.getByTestId('settings').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  const placement = await dialog.evaluate((el) => ({
    inShadow: (el.getRootNode() as ShadowRoot).host?.tagName.toLowerCase() ?? null,
    surface: getComputedStyle(el.closest('.mat-mdc-dialog-surface') ?? el.querySelector('.mat-mdc-dialog-surface') ?? el).backgroundColor,
  }));
  expect(placement).toEqual({ inShadow: 'openbp-canvas', surface: 'rgb(255, 255, 255)' });
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
});

test('readonly: без палітри й кнопок, вузли не перетягуються, Delete нічого не видаляє, форма недоступна', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(demo({ 'workflow-id': WORKFLOW_ID, readonly: '' }));

  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  await expect(page.getByTestId('readonly')).toBeVisible();
  await expect(page.locator('[data-item]')).toHaveCount(0);
  for (const id of ['undo', 'delete', 'run', 'settings']) await expect(page.getByTestId(id)).toHaveCount(0);

  // Перетягування вузла лише зсуває полотно (pan): відстань між вузлами та сама.
  const offset = async () => {
    const a = (await card(page, 'Старт').boundingBox())!;
    const b = (await card(page, 'Поля').boundingBox())!;
    return { dx: Math.round(b.x - a.x), dy: Math.round(b.y - a.y) };
  };
  // Вузли стоять на одній висоті: поки dy ≠ 0, канвас ще розкладає картки (інакше `before` знято до кінця рендеру).
  await expect.poll(async () => (await offset()).dy).toBe(0);
  const before = await offset();
  await dragBy(page, card(page, 'Поля'), 150, 100);
  expect(await offset()).toEqual(before);

  // Виділення працює (панель вузла видно), але форма — inert, а Delete не видаляє.
  await card(page, 'Поля').click();
  await expect(page.getByTestId('node-panel')).toBeVisible();
  await expect(page.getByTestId('node-common')).toHaveAttribute('inert', '');
  await expect(page.getByTestId('node-actions')).toHaveCount(0);
  await page.keyboard.press('Delete');
  await expect(page.locator('[data-node-id]')).toHaveCount(2);

  await page.waitForTimeout(1500);
  expect(api.puts).toBe(0);
  expect((await hostEvents(page)).filter((e) => e.type === 'openbp-saved')).toEqual([]);
});

test('стилі хоста не ламають канвас, а стилі канвасу не потрапляють на сторінку', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  // Стилі сторінки до підключення елемента — для порівняння.
  await page.route('**/dist/element/openbp-canvas.js', (route) => route.fulfill({ contentType: 'text/javascript', body: '' }));
  await page.goto(demo({}));
  const hostStyle = () =>
    page.evaluate(() => {
      const css = (el: Element) => getComputedStyle(el);
      const h1 = document.getElementById('title')!;
      return {
        bodyBackground: css(document.body).backgroundColor,
        bodyFont: css(document.body).fontFamily,
        h1Color: css(h1).color,
        h1Font: css(h1).fontFamily,
        h1Size: css(h1).fontSize,
        rootVars: getComputedStyle(document.documentElement).getPropertyValue('--bp-primary'),
      };
    });
  const before = await hostStyle();
  await page.unroute('**/dist/element/openbp-canvas.js');

  await page.goto(demo({ 'workflow-id': WORKFLOW_ID }));
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  expect(await hostStyle()).toEqual(before);
  expect(before.rootVars).toBe('');

  // `.node { display: none }` і червоні кнопки хоста не діють усередині Shadow DOM.
  await expect(card(page, 'Поля')).toBeVisible();
  const undo = page.getByTestId('undo');
  await expect(undo).toBeVisible();
  const button = await undo.evaluate((el) => ({ bg: getComputedStyle(el).backgroundColor, font: getComputedStyle(el).fontFamily }));
  expect(button.bg).not.toBe('rgb(255, 0, 0)');
  expect(button.font).toContain('Ubuntu');
  // Тема AI_BPMS — на :host елемента.
  const kit = await page.locator('openbp-canvas').evaluate((el) => getComputedStyle(el).getPropertyValue('--bp-primary').trim());
  expect(kit).toBe('#0d736d');
});

test('невідомий сценарій → подія openbp-error з кодом Api', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto(demo({ 'workflow-id': '99999999-0000-0000-0000-000000000000' }));
  await expect(page.getByTestId('page-error')).toContainText('Сценарій не знайдено.');
  await expect.poll(() => hostEvents(page)).toContainEqual({ type: 'openbp-error', detail: { code: 'NOT_FOUND', message: 'Сценарій не знайдено.' } });
});
