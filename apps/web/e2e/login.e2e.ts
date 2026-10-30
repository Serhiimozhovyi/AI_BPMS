import { expect, test } from '@playwright/test';

/**
 * F3-24: вхід. Api підмінено (`page.route`): сесія з'являється після `POST /auth/login` з правильним паролем і
 * зникає після `POST /auth/logout` — як cookie-сесія справжнього Api.
 */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };
const PASSWORD = 'Admin-pass-123';

test('сторінка без сесії → вхід → повернення на неї → вихід', async ({ page }) => {
  let signedIn = false;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/v1/auth/me') {
      return signedIn ? route.fulfill({ json: USER }) : route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    }
    if (path === '/api/v1/auth/login' && request.method() === 'POST') {
      const body = request.postDataJSON() as { email: string; password: string };
      signedIn = body.email === USER.email && body.password === PASSWORD;
      return signedIn ? route.fulfill({ status: 204 }) : route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    }
    if (path === '/api/v1/auth/logout' && request.method() === 'POST') {
      signedIn = false;
      return route.fulfill({ status: 204 });
    }
    if (!signedIn) return route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (path === '/api/v1/api-keys') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  // Без сесії — на вхід, навігації ще немає.
  await page.goto('/settings');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fsettings$/);
  await expect(page.getByRole('navigation')).toHaveCount(0);

  // Невірний пароль — повідомлення, лишаємось на вході.
  await page.getByTestId('login-email').fill(USER.email);
  await page.getByTestId('login-password').fill('wrong');
  await page.getByTestId('login-submit').click();
  await expect(page.getByTestId('login-error')).toContainText('Невірний email або пароль');
  await expect(page).toHaveURL(/\/login/);

  // Правильний пароль — назад на /settings, у меню аватара email і «Вийти» (F9-04).
  await page.getByTestId('login-password').fill(PASSWORD);
  await page.getByTestId('login-password').press('Enter');
  await expect(page).toHaveURL(/\/settings$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Налаштування');
  await expect(page.getByTestId('user-menu')).toHaveText('A');
  await page.getByTestId('user-menu').click();
  await expect(page.getByTestId('current-user')).toHaveText(USER.email);

  // Вихід — знову сторінка входу; повернутися на сторінку без входу не можна.
  await page.getByTestId('logout').click();
  await expect(page).toHaveURL(/\/login$/);
  await page.goto('/workflows');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fworkflows$/);
});
