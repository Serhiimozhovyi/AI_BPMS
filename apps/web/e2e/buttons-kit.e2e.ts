import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

/** F9-02: кнопки AC-Kit на сторінці-зразку `/dev/kit` — розміри 40/32 px, знімок і axe. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

test('кнопки: висота 40 і 32 px, знімок, axe без serious/critical', async ({ page }, testInfo) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/dev/kit');
  const section = page.getByTestId('kit-buttons');
  await expect(section).toBeVisible();

  const md = section.locator('[data-size="md"] button.mat-mdc-unelevated-button').first();
  const sm = section.locator('[data-size="sm"] button.mat-mdc-unelevated-button').first();
  expect((await md.boundingBox())?.height).toBe(40);
  expect((await sm.boundingBox())?.height).toBe(32);
  await expect(section.locator('.bp-btn-danger').first()).toBeVisible();

  await testInfo.attach('buttons', { body: await section.screenshot(), contentType: 'image/png' });

  const results = await new AxeBuilder({ page }).include('[data-testid="kit-buttons"]').analyze();
  const blocking = results.violations.filter(
    (v) => v.impact === 'serious' || v.impact === 'critical',
  );
  expect(blocking.map((v) => v.id)).toEqual([]);
});
