import { Page, expect, test } from '@playwright/test';

/**
 * Перемикання проєктів на вкладці «Учасники»: шапка, кнопка учасників і посилання на інший проєкт мають показувати
 * учасників саме нового проєкту, а зміни йти в нього. Раніше Angular повторно використовував сторінку проєкту, і на
 * `/projects/B/members` лишалися заголовок і учасники проєкту A.
 */
const GROUPS = [
  { id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 3, role: 'admin', appRoles: [{ key: 'psychologist', name: 'Психолог' }] },
  { id: 'p2', name: 'Продажі', sortOrder: 1, workflowCount: 1, role: 'admin', appRoles: [] },
];

const MEMBERS: Record<string, { userId: string; email: string; role: string; appRole: string | null }[]> = {
  p1: [{ userId: 'u1', email: 'psy@firma.ua', role: 'editor', appRole: 'psychologist' }],
  p2: [{ userId: 'u2', email: 'sales@firma.ua', role: 'viewer', appRole: null }],
};

async function mockApi(page: Page, writes: string[]) {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u0', email: 'admin@openbp.local', role: 'admin' } });
    if (path === '/api/v1/workflow-groups') return route.fulfill({ json: GROUPS });
    const members = /^\/api\/v1\/workflow-groups\/(p\d)\/members(\/add)?$/.exec(path);
    if (members && request.method() === 'GET') return route.fulfill({ json: MEMBERS[members[1]] });
    if (members && request.method() === 'POST') {
      writes.push(members[1]);
      const { email } = request.postDataJSON() as { email: string };
      return route.fulfill({ json: { member: { userId: 'u9', email, role: 'editor', appRole: null }, created: false, temporaryPassword: null } });
    }
    return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
  });
}

async function pickProject(page: Page, name: string) {
  await page.getByTestId('project-select').click();
  await page.getByRole('option', { name }).click();
}

test('вибір іншого проєкту в шапці на «Учасниках» показує учасників нового проєкту', async ({ page }) => {
  const writes: string[] = [];
  await mockApi(page, writes);
  await page.goto('/projects/p1/members');
  await expect(page.getByTestId('project-name')).toHaveText('Психологічна підтримка');
  await expect(page.locator('[data-member]')).toHaveCount(1);
  await expect(page.locator('[data-member="psy@firma.ua"]')).toBeVisible();
  await expect(page.locator('[data-app-role="psychologist"]')).toBeVisible();
  // «Прибрати» — danger: червоний текст, а не зелений primary від mat-button.
  await expect(page.locator('[data-member="psy@firma.ua"] [data-testid="member-remove"]')).toHaveCSS('color', 'rgb(179, 38, 30)');

  await pickProject(page, 'Продажі');
  await expect(page).toHaveURL(/\/projects\/p2\/members$/);
  await expect(page.getByTestId('project-name')).toHaveText('Продажі');
  await expect(page.locator('[data-member="sales@firma.ua"]')).toBeVisible();
  await expect(page.locator('[data-member="psy@firma.ua"]')).toHaveCount(0);
  await expect(page.getByTestId('app-roles-empty')).toBeVisible();

  // Новий учасник додається в проєкт, який видно на екрані.
  await page.getByTestId('member-email').fill('new@firma.ua');
  await page.getByTestId('member-add').click();
  await expect(page.locator('[data-member="new@firma.ua"]')).toBeVisible();
  expect(writes).toEqual(['p2']);

  // «Усі проєкти» в шапці — назад до списку проєктів.
  await pickProject(page, 'Усі проєкти');
  await expect(page).toHaveURL(/\/projects$/);
});

test('перехід за посиланням на учасників іншого проєкту перечитує сторінку', async ({ page }) => {
  await mockApi(page, []);
  await page.goto('/projects/p1/members');
  await expect(page.locator('[data-member="psy@firma.ua"]')).toBeVisible();

  // Перехід у межах SPA (як кнопка «Учасники» в шапці чи меню групи, «Назад» у браузері) — без перезавантаження сторінки.
  await page.evaluate(() => {
    history.pushState({}, '', '/projects/p2/members');
    dispatchEvent(new PopStateEvent('popstate', { state: {} }));
  });
  await expect(page).toHaveURL(/\/projects\/p2\/members$/);
  await expect(page.getByTestId('project-name')).toHaveText('Продажі');
  await expect(page.locator('[data-member="sales@firma.ua"]')).toBeVisible();
  await expect(page.locator('[data-member="psy@firma.ua"]')).toHaveCount(0);
});
