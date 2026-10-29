import { Page, expect, test } from '@playwright/test';

/** F8-16: список застосунку проєкту — панель фільтрів, мітки, підлеглий список під рядком, форма в боковій панелі. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

const LIST_PAGE = {
  id: 'pg1',
  key: 'events',
  name: 'Події',
  kind: 'list',
  definition: {
    table: 'events',
    columns: [
      { column: 'title', label: 'Назва' },
      { column: 'status', format: 'badge', labels: { planned: 'Заплановано', cancelled: 'Скасовано' }, colors: { planned: 'green', cancelled: 'gray' } },
    ],
    filters: [{ column: 'status', kind: 'chips', label: 'Стан' }],
    expand: [{ table: 'regs', by: 'event_id', label: 'Учасники', columns: [{ column: 'name', label: 'Ім’я' }], empty: 'Ще ніхто не зареєструвався' }],
    create: { page: 'event-form', mode: 'drawer' },
  },
  roles: [],
  sortOrder: 0,
  updatedAt: '2026-10-29T00:00:00Z',
};

const FORM_PAGE = { ...LIST_PAGE, id: 'pg2', key: 'event-form', name: 'Подія', kind: 'form', definition: { table: 'events', onSubmit: { operation: 'insert' } }, sortOrder: 1 };

async function mockApi(page: Page, requests: string[]) {
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/workflow-groups') {
      return route.fulfill({ json: [{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role: 'admin' }] });
    }
    if (path === '/api/v1/projects/p1/pages') return route.fulfill({ json: [LIST_PAGE, FORM_PAGE] });
    if (path === '/api/v1/projects/p1/pages/events') {
      return route.fulfill({
        json: {
          page: LIST_PAGE,
          tableId: 't1',
          columns: [
            { column: 'title', label: 'Назва', type: 'string' },
            { column: 'status', label: 'Стан', type: 'enum' },
          ],
          schema: null,
          canEdit: true,
          timezone: 'Europe/Kyiv',
          filterOptions: { status: [{ value: 'planned', label: 'Заплановано' }, { value: 'cancelled', label: 'Скасовано' }] },
        },
      });
    }
    if (path === '/api/v1/projects/p1/pages/events/rows') {
      requests.push(url.search);
      return route.fulfill({
        json: { items: [{ key: 'e1', data: { title: 'Консультація', status: 'planned' }, display: {}, actions: [] }], page: 1, pageSize: 50, total: 1 },
      });
    }
    if (path === '/api/v1/projects/p1/pages/events/rows/e1/expand/0') {
      return route.fulfill({ json: { label: 'Учасники', columns: [{ column: 'name', label: 'Ім’я', type: 'string' }], rows: [{ key: 'r1', data: { name: 'Марія' }, display: {}, actions: [] }] } });
    }
    if (path === '/api/v1/projects/p1/pages/event-form') {
      return route.fulfill({
        json: {
          page: FORM_PAGE,
          tableId: 't1',
          columns: [],
          schema: { type: 'object', properties: { title: { type: 'string', title: 'Назва' } } },
          canEdit: true,
        },
      });
    }
    return route.fulfill({ json: [] });
  });
}

test('список: фільтр-мітка, підлеглий список і форма в боковій панелі', async ({ page }) => {
  const requests: string[] = [];
  await mockApi(page, requests);
  await page.goto('/projects/p1/app/events');

  await expect(page.getByTestId('app-page-title')).toHaveText('Події');
  await expect(page.locator('[data-row]')).toHaveCount(1);
  await expect(page.locator('[data-badge]').first()).toHaveText('Заплановано');

  await page.locator('[data-chip]').first().click();
  await expect.poll(() => requests.some((s) => s.includes('filters'))).toBe(true);

  await page.locator('[data-expand-toggle]').first().click();
  await expect(page.locator('[data-sub-row]')).toContainText('Марія');

  await page.getByTestId('app-create').click();
  await expect(page.getByTestId('app-drawer')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('app-drawer')).toHaveCount(0);
});
