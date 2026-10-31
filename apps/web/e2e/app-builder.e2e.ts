import { Page, expect, test } from '@playwright/test';
import { chooseOption } from './mat-select';

/** F8-19: конструктор зручних сторінок — календар і головна збираються формою (без JSON) з живим переглядом. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

const TABLES = [
  {
    id: 't1',
    name: 'slots',
    projectId: 'p1',
    rowCount: 2,
    updatedAt: '2026-10-29T00:00:00Z',
    columns: [
      { name: 'note', type: 'string' },
      { name: 'status', type: 'enum', values: ['open', 'booked'] },
      { name: 'minutes', type: 'number' },
      { name: 'starts_at', type: 'datetime' },
    ],
  },
];

interface Saved {
  key: string;
  body: { name: string; kind: string; definition: Record<string, unknown> };
}

async function mockApi(page: Page, saved: Saved[], previews: Saved[], appBodies: unknown[]) {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/workflow-groups') {
      return route.fulfill({ json: [{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role: 'admin', appRoles: [{ key: 'psychologist', name: 'Психолог' }] }] });
    }
    if (path === '/api/v1/workflow-groups/p1/app' && request.method() === 'PUT') {
      appBodies.push(request.postDataJSON());
      return route.fulfill({ json: {} });
    }
    if (path === '/api/v1/tables') return route.fulfill({ json: TABLES });
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 200 } });
    if (path === '/api/v1/projects/p1/pages') {
      return route.fulfill({
        json: saved.map((s) => ({ id: s.key, key: s.key, name: s.body.name, kind: s.body.kind, definition: s.body.definition, roles: [], sortOrder: 0, updatedAt: '' })),
      });
    }
    const match = /^\/api\/v1\/projects\/p1\/pages\/([^/]+)(\/preview)?$/.exec(path);
    if (match && request.method() === 'POST' && match[2]) {
      const body = request.postDataJSON();
      previews.push({ key: match[1], body });
      const view = {
        page: { id: 'x', key: match[1], name: body.name, kind: body.kind, definition: body.definition, roles: [], sortOrder: 0, updatedAt: '2026-10-29T00:00:00Z' },
        tableId: 't1',
        columns: [],
        schema: null,
        canEdit: true,
        timezone: 'Europe/Kyiv',
      };
      if (body.kind === 'calendar') return route.fulfill({ json: { view, events: { from: '', to: '', truncated: false, events: [] } } });
      if (body.kind === 'dashboard') return route.fulfill({ json: { view, dashboard: { blocks: [{ index: 0, kind: 'text', width: 12, text: 'Вітаємо, admin@firma.ua!' }] } } });
      return route.fulfill({ json: { view } });
    }
    if (match && request.method() === 'PUT') {
      const body = request.postDataJSON();
      saved.push({ key: match[1], body });
      return route.fulfill({ json: {} });
    }
    return route.fulfill({ json: [] });
  });
}

test('календар і головна збираються в конструкторі без JSON, перегляд оновлюється, вигляд застосунку зберігається', async ({ page }) => {
  const saved: Saved[] = [];
  const previews: Saved[] = [];
  const appBodies: unknown[] = [];
  await mockApi(page, saved, previews, appBodies);

  // Календар зі зразка.
  await page.goto('/projects/p1/app/_builder');
  await chooseOption(page.getByTestId('b-sample'), 'Календар');
  await expect(page.getByTestId('b-kind')).toContainText('Календар');
  await expect(page.getByTestId('bc-start')).toContainText('starts_at');
  await page.getByTestId('b-name').fill('Мій розклад');
  await page.locator('[data-view="month"]').uncheck();
  await expect(page.getByTestId('builder-preview').locator('app-page-calendar')).toBeVisible();
  await expect.poll(() => previews.at(-1)?.body.kind).toBe('calendar');
  await page.getByTestId('b-save').click();
  await expect(page).toHaveURL(/\/app\/calendar$/);
  expect(saved[0].key).toBe('calendar');
  expect(saved[0].body.definition).toMatchObject({
    table: 'slots',
    calendar: { start: 'starts_at', duration: 'minutes', title: { column: 'note' }, views: ['week', 'day'], default: 'week' },
    filters: [{ column: 'status', kind: 'chips' }],
  });

  // Головна зі зразка, текст блоку і кнопка «Новий слот» бічною панеллю.
  await page.goto('/projects/p1/app/_builder');
  await chooseOption(page.getByTestId('b-sample'), 'Головна');
  await expect(page.getByTestId('b-kind')).toContainText('Головна');
  await page.getByTestId('b-name').fill('Сьогодні');
  await page.getByTestId('b-add-block').click();
  const block = page.locator('[data-block="2"]');
  await chooseOption(block.locator('mat-select').first(), 'Кнопки');
  await page.getByTestId('bd-add-action-2').click();
  const action = page.locator('[data-block-action="0"]');
  await action.locator('input').first().fill('Новий слот');
  await chooseOption(action.locator('mat-select').nth(1), 'calendar');
  await chooseOption(action.locator('mat-select').nth(2), 'бічною панеллю');
  await expect(page.getByTestId('builder-preview').getByText('Вітаємо, admin@firma.ua!')).toBeVisible();
  await page.getByTestId('b-save').click();
  await expect(page).toHaveURL(/\/app\/home$/);
  const home = saved.find((s) => s.key === 'home')!;
  expect(home.body.kind).toBe('dashboard');
  const blocks = home.body.definition['blocks'] as { kind: string; actions?: unknown[] }[];
  expect(blocks.map((b) => b.kind)).toEqual(['text', 'list', 'actions']);
  expect(blocks[2].actions).toEqual([{ label: 'Новий слот', page: 'calendar', mode: 'drawer' }]);

  // Вигляд застосунку: назва, головна, меню.
  await page.goto('/projects/p1/app/_app');
  await page.getByTestId('as-title').fill('Кабінет психолога');
  await chooseOption(page.getByTestId('as-home'), 'Сьогодні');
  await page.getByTestId('as-add-group').click();
  await page.locator('[data-nav-group="0"] input').first().fill('Робота');
  await page.locator('[data-nav-page="home"]').check();
  await page.locator('[data-nav-page="calendar"]').check();
  await page.getByTestId('as-save').click();
  await expect(page.getByTestId('appset-saved')).toBeVisible();
  expect(appBodies).toEqual([{ title: 'Кабінет психолога', icon: null, home: 'home', timezone: null, nav: [{ label: 'Робота', pages: ['calendar', 'home'] }] }]);
});

test('форма: колонки таблиці перетягуються на канвас, два поля — в один рядок, приховане поле ставить сервер (F11-05)', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1600, height: 1100 });
  const saved: Saved[] = [];
  const previews: Saved[] = [];
  await mockApi(page, saved, previews, []);

  await page.goto('/projects/p1/app/_builder');
  await page.getByTestId('b-key').fill('book');
  await page.getByTestId('b-name').fill('Запис');
  await chooseOption(page.getByTestId('b-kind'), 'Форма');
  await chooseOption(page.getByTestId('b-table'), 'slots');

  const designer = page.getByTestId('b-fields');
  await expect(designer.locator('[data-column]')).toHaveCount(4);
  await designer.getByTestId('fd-col-note').click();
  await expect(designer.getByTestId('fd-card-note')).toBeVisible();
  await designer.getByTestId('fd-prop-title').fill('Примітка');

  // Колонку «minutes» тягнемо з палітри в рядок поля «Примітка».
  const source = (await designer.getByTestId('fd-col-minutes').boundingBox())!;
  const row = designer.locator('[data-row="0:0"]');
  const target = (await row.boundingBox())!;
  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
  await page.mouse.down();
  for (let i = 1; i <= 14; i++) {
    await page.mouse.move(
      source.x + source.width / 2 + ((target.x + target.width * 0.8 - source.x - source.width / 2) * i) / 14,
      source.y + source.height / 2 + ((target.y + target.height / 2 - source.y - source.height / 2) * i) / 14,
    );
  }
  await page.mouse.up();
  await expect(designer.getByTestId('fd-card-minutes')).toBeVisible();
  const [a, b] = [(await designer.getByTestId('fd-card-note').boundingBox())!, (await designer.getByTestId('fd-card-minutes').boundingBox())!];
  expect(Math.abs(a.y - b.y)).toBeLessThan(2);
  await expect(designer.getByTestId('fd-col-minutes')).toHaveCount(0);

  await page.getByTestId('b-hidden-add').click();
  await chooseOption(page.getByTestId('b-hidden-column-0'), 'status');
  await page.getByTestId('b-hidden-text-0').fill('open');
  await expect(designer.getByTestId('fd-col-status')).toHaveCount(0);
  await testInfo.attach('page-form-designer', { body: await page.screenshot(), contentType: 'image/png' });

  await page.getByTestId('b-save').click();
  await expect(page).toHaveURL(/\/app\/book$/);
  expect(saved.find((s) => s.key === 'book')!.body.definition).toMatchObject({
    table: 'slots',
    fields: [{ column: 'note', label: 'Примітка' }, { column: 'minutes' }, { column: 'status', value: 'open' }],
    formLayout: { sections: [{ rows: [[{ field: 'note' }, { field: 'minutes' }]] }] },
  });
});
