import { Page, expect, test } from '@playwright/test';

/** F8-18: користувач лише з роллю застосунку входить одразу в оболонку — меню розділів і головна з блоків, без меню OpenBP. */
const USER = { id: 'u9', email: 'olena@firma.ua', role: 'viewer' };

const page = (key: string, name: string, kind: string, definition: object) => ({ id: key, key, name, kind, definition, roles: [], sortOrder: 0, updatedAt: '2026-11-04T00:00:00Z' });

const TODAY = page('today', 'Сьогодні', 'dashboard', {
  table: 'slots',
  blocks: [
    { kind: 'text', text: 'Вітаємо, olena@firma.ua!', width: 12 },
    { kind: 'list', label: 'Найближчі записи', page: 'bookings', limit: 5, width: 6 },
    { kind: 'actions', actions: [{ label: 'Додати слот', page: 'schedule' }] },
  ],
});
const BOOKINGS = page('bookings', 'Записи', 'list', { table: 'bookings', columns: [{ column: 'name' }] });
const SCHEDULE = page('schedule', 'Мій розклад', 'calendar', { table: 'slots', calendar: { start: 'starts_at', duration: 'duration_min', title: { column: 'note' } } });

async function mockApi(page: Page) {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/workflow-groups') {
      return route.fulfill({
        json: [
          {
            id: 'p1',
            name: 'Психологічна підтримка',
            sortOrder: 0,
            workflowCount: 5,
            role: 'viewer',
            appRole: 'psychologist',
            app: {
              title: 'Кабінет психолога',
              icon: 'psychology',
              home: 'today',
              nav: [
                { label: '', pages: ['today', 'schedule'] },
                { label: 'Записи', pages: ['bookings'] },
              ],
            },
          },
        ],
      });
    }
    if (path === '/api/v1/projects/p1/pages') return route.fulfill({ json: [BOOKINGS, TODAY, SCHEDULE] });
    if (path === '/api/v1/projects/p1/pages/today') {
      return route.fulfill({ json: { page: TODAY, tableId: 't1', columns: [], schema: null, canEdit: false, timezone: 'Europe/Kyiv' } });
    }
    if (path === '/api/v1/projects/p1/pages/today/dashboard') {
      return route.fulfill({
        json: {
          blocks: [
            { index: 0, kind: 'text', width: 12, text: 'Вітаємо, olena@firma.ua!' },
            {
              index: 1,
              kind: 'list',
              label: 'Найближчі записи',
              width: 6,
              source: 'bookings',
              view: { page: BOOKINGS, tableId: 't2', columns: [{ column: 'name', label: 'Ім’я', type: 'string' }], schema: null, canEdit: false, timezone: 'Europe/Kyiv' },
              rows: { items: [{ key: 'b1', data: { name: 'Марія' }, display: {}, actions: [] }], page: 1, pageSize: 5, total: 1 },
            },
            { index: 2, kind: 'actions', width: 12, actions: [0] },
          ],
        },
      });
    }
    if (path === '/api/v1/projects/p1/pages/schedule') {
      return route.fulfill({ json: { page: SCHEDULE, tableId: 't1', columns: [], schema: null, canEdit: false, timezone: 'Europe/Kyiv' } });
    }
    if (path === '/api/v1/projects/p1/pages/schedule/calendar') {
      return route.fulfill({ json: { from: '', to: '', events: [], truncated: false } });
    }
    return route.fulfill({ json: [] });
  });
}

test('користувач із роллю застосунку потрапляє в оболонку з головною та меню розділів', async ({ page }) => {
  await mockApi(page);
  await page.goto('/');

  await expect(page).toHaveURL(/\/projects\/p1\/app\/today$/);
  await expect(page.getByTestId('shell-title')).toHaveText('Кабінет психолога');
  await expect(page.getByTestId('block-text')).toHaveText('Вітаємо, olena@firma.ua!');
  await expect(page.locator('[data-block="1"] [data-row="b1"]')).toContainText('Марія');

  // Ні меню OpenBP, ні вибору проєкту, ні вкладок проєкту.
  await expect(page.getByTestId('project-select')).toHaveCount(0);
  await expect(page.getByTestId('project-tabs')).toHaveCount(0);
  await expect(page.getByTestId('nav-app')).toHaveCount(0);

  // Меню оболонки: групи й розділи; перехід на календар.
  await expect(page.getByTestId('shell-group')).toHaveText('Записи');
  await expect(page.locator('[data-testid="shell-nav"] a')).toHaveText(['Сьогодні', 'Мій розклад', 'Записи']);
  await page.locator('[data-page="schedule"]').click();
  await expect(page).toHaveURL(/\/app\/schedule$/);
  await expect(page.getByTestId('cal-grid')).toBeVisible();

  // Кнопка головної веде на календар.
  await page.locator('[data-page="today"]').click();
  await page.locator('[data-block-action="0"]').click();
  await expect(page).toHaveURL(/\/app\/schedule$/);
});
