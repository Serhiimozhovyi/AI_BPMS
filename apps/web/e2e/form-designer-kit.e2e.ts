import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

/** F11-02: конструктор форм на сторінці-зразку `/dev/kit` — три панелі, додавання поля, знімок, axe. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

test('конструктор форм: секції й рядки на канвасі, додавання поля, axe без serious/critical', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });

  await page.goto('/dev/kit');
  const designer = page.getByTestId('kit-form-designer');
  await expect(designer.getByTestId('fd-section')).toHaveCount(2);

  const from = designer.getByTestId('fd-card-from');
  const to = designer.getByTestId('fd-card-to');
  const a = await from.boundingBox();
  const b = await to.boundingBox();
  expect(Math.abs((a?.y ?? 0) - (b?.y ?? 1))).toBeLessThan(2);

  await designer.getByTestId('fd-card-name').click();
  await designer.getByTestId('fd-kind-phone').click();
  await expect(designer.getByTestId('fd-card-telefon')).toHaveAttribute('aria-pressed', 'true');
  await expect(designer.getByTestId('fd-prop-title')).toHaveValue('Телефон');
  await designer.getByTestId('fd-card-telefon').press('Delete');
  await expect(designer.getByTestId('fd-card-telefon')).toHaveCount(0);
  await designer.getByTestId('fd-card-name').click();

  await testInfo.attach('form-designer', { body: await designer.screenshot(), contentType: 'image/png' });

  const results = await new AxeBuilder({ page }).include('[data-testid="kit-form-designer"]').analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(blocking.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
});
