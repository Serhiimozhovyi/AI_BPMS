import { Page, expect, test } from '@playwright/test';

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
  await page.getByTestId('b-sample').selectOption('calendar');
  await expect(page.getByTestId('b-kind')).toHaveValue('calendar');
  await expect(page.getByTestId('bc-start')).toHaveValue('starts_at');
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
  await page.getByTestId('b-sample').selectOption('dashboard');
  await expect(page.getByTestId('b-kind')).toHaveValue('dashboard');
  await page.getByTestId('b-name').fill('Сьогодні');
  await page.getByTestId('b-add-block').click();
  const block = page.locator('[data-block="2"]');
  await block.locator('select').first().selectOption('actions');
  await page.getByTestId('bd-add-action-2').click();
  const action = page.locator('[data-block-action="0"]');
  await action.locator('input').first().fill('Новий слот');
  await action.locator('select').nth(1).selectOption('calendar');
  await action.locator('select').nth(2).selectOption('drawer');
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
  await page.getByTestId('as-home').selectOption('home');
  await page.getByTestId('as-add-group').click();
  await page.locator('[data-nav-group="0"] input').first().fill('Робота');
  await page.locator('[data-nav-page="home"]').check();
  await page.locator('[data-nav-page="calendar"]').check();
  await page.getByTestId('as-save').click();
  await expect(page.getByTestId('appset-saved')).toBeVisible();
  expect(appBodies).toEqual([{ title: 'Кабінет психолога', icon: null, home: 'home', timezone: null, nav: [{ label: 'Робота', pages: ['calendar', 'home'] }] }]);
});
