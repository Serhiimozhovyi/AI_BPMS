import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

/**
 * F11-07: віджети форми — варіанти кнопками (`radio`), кілька варіантів чипами (`chips`), телефон з маскою. Форма
 * заповнюється лише з клавіатури; axe без порушень serious/critical; у тілі надсилання телефон — цифрами.
 */
const schema = {
  type: 'object',
  required: ['kind', 'phone'],
  properties: {
    kind: {
      type: 'string',
      title: 'Вид відпустки',
      oneOf: [
        { const: 'annual', title: 'Щорічна' },
        { const: 'unpaid', title: 'За свій рахунок' },
        { const: 'study', title: 'Навчальна' },
      ],
      'x-openbp-ui': { widget: 'radio' },
    },
    days: {
      type: 'array',
      title: 'Зручні дні',
      items: { type: 'string', enum: ['Пн', 'Вт', 'Ср'] },
      'x-openbp-ui': { widget: 'chips' },
    },
    phone: { type: 'string', format: 'phone', title: 'Телефон', 'x-openbp-ui': { placeholder: '+380 67 123 45 67' } },
    address: { type: 'string', format: 'textarea', title: 'Адреса', 'x-openbp-ui': { rows: 2 } },
  },
};

test('radio, chips і телефон заповнюються з клавіатури; axe зелений', async ({ page }) => {
  let sent: unknown = null;
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ status: 401, json: { code: 'UNAUTHENTICATED' } });
    if (path === '/api/v1/auth/providers') return route.fulfill({ json: [] });
    if (path === '/api/v1/forms/hr/widgets') {
      if (route.request().method() === 'POST') {
        sent = route.request().postDataJSON();
        return route.fulfill({ json: { message: 'Надіслано' } });
      }
      return route.fulfill({ json: { kind: 'trigger', title: 'Віджети форми', submitLabel: 'Надіслати', schema } });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.setViewportSize({ width: 1024, height: 900 });
  await page.goto('/form/hr/widgets');
  await expect(page.getByTestId('form-title')).toBeVisible();

  // Радіо: фокус на варіанті, пробіл вибирає, стрілка вниз — наступний варіант.
  await page.getByRole('radio', { name: 'Щорічна' }).focus();
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('radio', { name: 'За свій рахунок' })).toBeChecked();

  // Чипи: Tab до списку, пробіл вибирає, стрілка вправо — наступний.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('option', { name: 'Пн' })).toBeFocused();
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Space');
  await expect(page.getByRole('option', { name: 'Пн' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('option', { name: 'Ср' })).toHaveAttribute('aria-selected', 'true');

  // Телефон: маска під час введення.
  await page.keyboard.press('Tab');
  await expect(page.getByTestId('form-field-phone')).toBeFocused();
  await page.keyboard.type('0671234567');
  await expect(page.getByTestId('form-field-phone')).toHaveValue('+380 67 123 45 67');
  await page.keyboard.press('Tab');
  await expect(page.getByTestId('form-field-address')).toHaveAttribute('rows', '2');
  await page.keyboard.type('Київ, вул. Хрещатик, 1');

  const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const blocking = violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`);
  expect(blocking).toEqual([]);
  await page.screenshot({ path: 'test-results/form-widgets.png', fullPage: true });

  await page.getByTestId('form-submit').click();
  await expect
    .poll(() => sent)
    .toEqual({ fields: { kind: 'unpaid', days: ['Пн', 'Ср'], phone: '380671234567', address: 'Київ, вул. Хрещатик, 1' } });
});
