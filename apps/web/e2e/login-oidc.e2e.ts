import { expect, test } from '@playwright/test';

/**
 * F4-14: вхід через OIDC-провайдера. Api і провайдер підмінено (`page.route`): кнопка веде браузер на
 * `/api/v1/auth/oidc/<key>/login`, «провайдер» одразу повертає з сесією на `returnUrl` — як справжній Api після
 * callback'а `/signin-oidc/<key>`; відмова повертає на `/login?oidcError=<код>`.
 */
const USER = { id: 'u1', email: 'olena@astarta.ua', role: 'editor' };

test('кнопка «Увійти через …» → провайдер → повернення на сторінку; відмова — повідомлення', async ({
  page,
}) => {
  let signedIn = false;
  const passwordLogin = false;
  const loginRequests: string[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === '/api/v1/auth/providers') {
      return route.fulfill({
        json: {
          passwordLogin,
          providers: [
            { key: 'kit', displayName: 'Кіт 2', loginUrl: '/api/v1/auth/oidc/kit/login' },
          ],
        },
      });
    }
    if (url.pathname === '/api/v1/auth/oidc/kit/login') {
      loginRequests.push(url.search);
      const returnUrl = url.searchParams.get('returnUrl') ?? '/';
      if (returnUrl.startsWith('/executions')) {
        return route.fulfill({
          status: 302,
          headers: { location: '/login?oidcError=OIDC_EMAIL_NOT_ALLOWED' },
        });
      }
      signedIn = true;
      return route.fulfill({ status: 302, headers: { location: returnUrl } });
    }
    if (url.pathname === '/api/v1/auth/me') {
      return signedIn
        ? route.fulfill({ json: USER })
        : route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    }
    if (url.pathname === '/api/v1/auth/logout' && request.method() === 'POST') {
      signedIn = false;
      return route.fulfill({ status: 204 });
    }
    if (!signedIn) return route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (url.pathname === '/api/v1/api-keys') return route.fulfill({ json: [] });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  // Вхід паролем вимкнено: лише кнопка провайдера.
  await page.goto('/settings');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fsettings$/);
  await expect(page.getByTestId('login-provider-kit')).toHaveText('Увійти через Кіт 2');
  await expect(page.getByTestId('login-form')).toHaveCount(0);

  await page.getByTestId('login-provider-kit').click();
  await expect(page).toHaveURL(/\/settings$/);
  await expect(page.getByTestId('current-user')).toHaveText(USER.email);
  expect(loginRequests).toEqual(['?returnUrl=%2Fsettings']);

  // Вихід; провайдер відмовляє (домен пошти) — повідомлення на сторінці входу. Спершу чекаємо, доки лінива сторінка
  // налаштувань стартує (її запит ключів іде в ngOnInit): запит після виходу дістає 401 і веде на `/login?returnUrl=…`.
  await expect(page.getByTestId('key-name')).toBeVisible();
  await page.getByTestId('logout').click();
  await expect(page).toHaveURL(/\/login$/);
  await page.goto('/login?returnUrl=%2Fexecutions');
  await page.getByTestId('login-provider-kit').click();
  await expect(page).toHaveURL(/\/login\?oidcError=OIDC_EMAIL_NOT_ALLOWED$/);
  await expect(page.getByTestId('login-oidc-error')).toContainText('не має доступу до OpenBP');

  // Аварійний вхід паролем: ?password показує форму поруч із кнопкою.
  await page.goto('/login?password');
  await expect(page.getByTestId('login-form')).toBeVisible();
  await expect(page.getByTestId('login-provider-kit')).toBeVisible();
});
