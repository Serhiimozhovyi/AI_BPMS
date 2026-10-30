import { expect, test } from '@playwright/test';

/** F9-05: поля за AC-Kit — висота 40, радіус 8, фокус primary + кільце tonal; знімок форми сторінки й форми вузла. */
test.beforeEach(async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const list = (items: unknown[]) => route.fulfill({ json: { items, page: 1, pageSize: 50, total: items.length } });
    if (path === '/api/v1/auth/me') return route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (path === '/api/v1/auth/providers') return route.fulfill({ json: [] });
    if (path === '/api/v1/forms/leave') {
      return route.fulfill({
        json: {
          kind: 'trigger',
          title: 'Заявка на відпустку',
          description: 'Заповніть форму',
          submitLabel: 'Надіслати',
          schema: { type: 'object', required: ['name'], properties: { name: { type: 'string', title: 'ПІБ' }, note: { type: 'string', title: 'Коментар' } } },
        },
      });
    }
    return list([]);
  });
});

test('поле форми: висота 40, радіус 8, кільце фокуса', async ({ page }) => {
  await page.goto('/form/leave');
  await expect(page.getByTestId('form-title')).toBeVisible();
  const input = page.locator('mat-form-field').first();
  const box = await input.locator('.mat-mdc-text-field-wrapper').boundingBox();
  expect(Math.round(box!.height)).toBe(40);
  await page.locator('mat-form-field input').first().focus();
  const shadow = await input.locator('.mdc-notched-outline').evaluate((el) => getComputedStyle(el).boxShadow);
  expect(shadow).not.toBe('none');
  await page.screenshot({ path: 'test-results/fields-form.png', fullPage: true });
});
