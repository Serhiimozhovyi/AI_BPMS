import { Page, expect, test } from '@playwright/test';
import { chooseOption } from './mat-select';

/**
 * F12-02: розробник проєкту перемикається «як користувач» з роллю застосунку, бачить оболонку застосунку цієї ролі
 * (сервер звужує права за заголовком `X-OpenBP-View-As`, F12-01), смугу режиму, і виходить назад до своїх прав.
 */
const HEADER = 'x-openbp-view-as';
const page_ = (key: string, name: string) => ({ id: key, key, name, kind: 'list', definition: { table: 'bookings', columns: [{ column: 'name' }] }, roles: ['psychologist'], sortOrder: 0, updatedAt: '2026-10-31T00:00:00Z' });
const BOOKINGS = page_('bookings', 'Мої записи');

async function mockApi(page: Page, seen: (string | null)[]) {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const viewAs = (await request.allHeaders())[HEADER] ?? null;
    seen.push(viewAs);
    if (path === '/api/v1/auth/me') {
      return route.fulfill({
        json: viewAs
          ? { id: 'u2', email: 'dev@firma.ua', role: 'viewer', viewAs: { projectId: 'p1', projectRole: 'viewer', appRole: 'psychologist', memberId: null, actorEmail: 'dev@firma.ua' } }
          : { id: 'u2', email: 'dev@firma.ua', role: 'editor' },
      });
    }
    if (path === '/api/v1/workflow-groups') {
      const appRoles = [
        { key: 'coordinator', name: 'Координатор' },
        { key: 'psychologist', name: 'Психолог' },
      ];
      return route.fulfill({
        json: viewAs
          ? [{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role: 'viewer', appRole: 'psychologist', appRoles, app: { title: 'Кабінет психолога', home: 'bookings', nav: [{ label: '', pages: ['bookings'] }] } }]
          : [
              { id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 3, role: 'editor', appRoles },
              { id: 'p2', name: 'Продажі', sortOrder: 1, workflowCount: 1, role: 'editor', appRoles: [] },
            ],
      });
    }
    if (path === '/api/v1/workflow-groups/p1/members') return route.fulfill({ json: [] });
    if (path === '/api/v1/projects/p1/pages') return route.fulfill({ json: viewAs ? [BOOKINGS] : [BOOKINGS, page_('all', 'Усі записи')] });
    if (path === '/api/v1/projects/p1/pages/bookings') {
      return route.fulfill({ json: { page: BOOKINGS, tableId: 't1', columns: [{ column: 'name', label: 'Клієнт', type: 'string' }], schema: null, canEdit: false, timezone: 'Europe/Kyiv' } });
    }
    if (path === '/api/v1/projects/p1/pages/bookings/rows') {
      return route.fulfill({ json: { items: [{ key: 'b1', data: { name: 'Олена' }, display: {}, actions: [] }], page: 1, pageSize: 25, total: 1 } });
    }
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });
}

test('editor переглядає проєкт як «Психолог» і виходить із перегляду', async ({ page }) => {
  const seen: (string | null)[] = [];
  await mockApi(page, seen);
  await page.goto('/projects/p1/members');
  await expect(page.getByTestId('project-select')).toBeVisible();
  await expect(page.getByTestId('view-as-banner')).toHaveCount(0);

  await page.getByTestId('user-menu').click();
  await page.getByTestId('view-as-open').click();
  await expect(page.getByRole('dialog')).toContainText('Психологічна підтримка');
  await chooseOption(page.getByTestId('view-as-role'), 'Психолог');
  await page.screenshot({ path: 'test-results/view-as-dialog.png' });
  await page.getByTestId('view-as-apply').click();

  // Оболонка застосунку ролі: меню «Мої записи», без вибору проєкту; смуга режиму.
  await expect(page.getByTestId('view-as-banner')).toBeVisible();
  await expect(page.getByTestId('view-as-label')).toHaveText('Психолог');
  await expect(page.getByTestId('view-as-project')).toHaveText('Психологічна підтримка');
  await expect(page.getByTestId('shell-title')).toHaveText('Кабінет психолога');
  await expect(page.locator('[data-testid="shell-nav"] a')).toHaveText(['Мої записи']);
  await expect(page.getByTestId('project-select')).toHaveCount(0);
  await expect(page.getByText('Олена')).toBeVisible();
  expect(seen.at(-1)).toBe('project=p1&role=viewer&appRole=psychologist');
  await page.screenshot({ path: 'test-results/view-as-banner.png' });

  // Вихід: заголовок більше не надсилається, знову меню AI_BPMS і вибір проєкту.
  const before = seen.length;
  await page.getByTestId('view-as-exit').click();
  await expect(page.getByTestId('view-as-banner')).toHaveCount(0);
  await expect(page).toHaveURL(/\/projects\/p1\/members$/);
  await expect(page.getByTestId('project-select')).toBeVisible();
  expect(seen.slice(before).every((h) => h === null)).toBe(true);
});
