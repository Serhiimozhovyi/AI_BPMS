import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';

/**
 * F11-01: розмітка форми `x-openbp-layout` — секції, два поля в рядку на широкому екрані й у стовпчик на телефоні,
 * згортання секції. Схема — з `examples/leave-request-form.json`.
 */
const example = JSON.parse(readFileSync(resolve(__dirname, '../../../examples/leave-request-form.json'), 'utf-8'));
const params = example.nodes[0].parameters;

test.beforeEach(async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (path === '/api/v1/auth/providers') return route.fulfill({ json: [] });
    if (path === `/api/v1/forms/${params.path}`) {
      return route.fulfill({
        json: { kind: 'trigger', title: params.title, description: params.description, submitLabel: params.submitLabel, schema: params.schema },
      });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
});

async function boxes(page: import('@playwright/test').Page, ...keys: string[]) {
  return Promise.all(keys.map(async (k) => (await page.getByTestId(`form-field-${k}`).locator('xpath=ancestor::formly-field[1]').boundingBox())!));
}

test('1024 px: секції, «З — По» в одному рядку, згорнута секція розгортається', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 900 });
  await page.goto(`/form/${params.path}`);
  await expect(page.getByTestId('form-title')).toHaveText(params.title);

  await expect(page.getByTestId('form-section')).toHaveCount(3);
  await expect(page.getByRole('heading', { name: 'Відпустка' })).toBeVisible();
  const [from, to] = await boxes(page, 'from', 'to');
  expect(Math.abs(from.y - to.y)).toBeLessThan(2);
  expect(to.x).toBeGreaterThan(from.x + from.width - 1);
  await expect(page.getByTestId('form-field-name')).toHaveAttribute('placeholder', "Прізвище та ім'я");
  await expect(page.getByTestId('form-field-reason')).toHaveAttribute('rows', '2');

  const toggle = page.getByTestId('form-section-toggle');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByText('Обрати файл')).toBeHidden();
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByText('Обрати файл')).toBeVisible();
  await page.screenshot({ path: 'test-results/form-layout-1024.png', fullPage: true });
});

test('390 px: рядок розпадається у стовпчик без горизонтальної прокрутки', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/form/${params.path}`);
  await expect(page.getByTestId('form-title')).toBeVisible();

  const [from, to] = await boxes(page, 'from', 'to');
  expect(to.y).toBeGreaterThan(from.y + from.height - 1);
  expect(Math.abs(from.x - to.x)).toBeLessThan(2);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await page.screenshot({ path: 'test-results/form-layout-390.png', fullPage: true });
});
