import { expect, test } from '@playwright/test';

/** F3-84: оболонка на телефоні (390 px), сторінка «Не знайдено», банер «Сервер недоступний». Api підмінено `page.route`. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

async function mockApi(page: import('@playwright/test').Page) {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/auth/logout') return route.fulfill({ status: 204 });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
}

test.describe('телефон 390 px', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('бургер відкриває навігацію, «Вийти» доступне, перехід закриває drawer', async ({ page }) => {
    await mockApi(page);
    await page.goto('/');
    await expect(page.getByTestId('nav-burger')).toBeVisible();
    await expect(page.getByTestId('logout')).toHaveCount(0);

    await page.getByTestId('nav-burger').click();
    const drawer = page.getByTestId('nav-drawer');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByTestId('project-select')).toBeVisible();
    await expect(drawer.getByTestId('logout')).toBeVisible();

    await drawer.getByRole('link', { name: 'Сценарії' }).click();
    await expect(page).toHaveURL(/\/workflows$/);
    await expect(page.getByTestId('nav-drawer')).toHaveCount(0);

    await page.getByTestId('nav-burger').click();
    await page.getByTestId('logout').click();
    await expect(page).toHaveURL(/\/login/);
  });

  test('редактор сценарію — підказка й лише перегляд', async ({ page }) => {
    await mockApi(page);
    await page.goto('/workflows/abc');
    await expect(page.getByTestId('editor-desktop-hint')).toContainText('Редагування доступне на комп');
  });
});

test('невідома адреса → «Не знайдено» з посиланням на «Огляд»', async ({ page }) => {
  await mockApi(page);
  await page.goto('/no/such/page');
  await expect(page).toHaveURL(/\/no\/such\/page$/);
  await expect(page.getByTestId('not-found')).toContainText('Сторінку не знайдено');
  await page.getByTestId('not-found-home').click();
  await expect(page).toHaveURL(/\/$/);
});

test('Api недоступний → банер з «Повторити», після відповіді банер зникає', async ({ page }) => {
  let down = true;
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (down && path !== '/api/v1/auth/me') return route.abort('connectionrefused');
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.goto('/workflows');
  await expect(page.getByTestId('offline-banner')).toContainText('Немає зв’язку із сервером OpenBP');

  down = false;
  await page.getByTestId('offline-retry').click();
  await expect(page.getByTestId('offline-banner')).toHaveCount(0);
});
