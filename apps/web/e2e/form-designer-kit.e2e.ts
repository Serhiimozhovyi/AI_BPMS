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

test('конструктор форм: рядок «З — По» перетягуванням, ширина межею, axe (F11-03)', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.goto('/dev/kit');
  const designer = page.getByTestId('kit-form-designer');
  const from = designer.getByTestId('fd-card-from');
  const to = designer.getByTestId('fd-card-to');

  // Розбити наявний рядок, щоб зібрати його знову мишею.
  await to.click();
  await designer.getByTestId('fd-split').click();
  // Після «Окремим рядком» картки перемальовуються: на мить елемента немає — `boundingBox()` дає null, poll повторює.
  const top = async (l: typeof from) => (await l.boundingBox())?.y ?? Number.NaN;
  await expect.poll(async () => (await top(to)) - (await top(from))).toBeGreaterThan(20);

  // Тягнемо «По» за ручку на праву половину «З».
  const handle = to.locator('.fd__handle');
  const h = (await handle.boundingBox())!;
  const f = (await from.boundingBox())!;
  await page.mouse.move(h.x + h.width / 2, h.y + h.height / 2);
  await page.mouse.down();
  for (let i = 1; i <= 12; i++) {
    await page.mouse.move(h.x + ((f.x + f.width * 0.85 - h.x) * i) / 12, h.y + ((f.y + f.height / 2 - h.y) * i) / 12);
  }
  await page.mouse.move(f.x + f.width * 0.9, f.y + f.height / 2);
  await page.mouse.up();
  await expect.poll(async () => Math.abs((await top(to)) - (await top(from)))).toBeLessThan(2);
  await expect(from).toHaveAttribute('data-width', '6');
  // Анімація CDK після скидання (200 мс) закінчилась: координати межі вже остаточні.
  await expect(designer.locator('.cdk-drag-animating, .cdk-drag-preview')).toHaveCount(0);

  // Межа між «З» і «По»: вправо на ~2 колонки.
  const resize = from.getByTestId('fd-resize');
  const r = (await resize.boundingBox())!;
  const row = (await designer.locator('[data-row="0:1"]').boundingBox())!;
  const unit = row.width / 12;
  await page.mouse.move(r.x + r.width / 2, r.y + r.height / 2);
  await page.mouse.down();
  await page.mouse.move(r.x + r.width / 2 + unit, r.y + r.height / 2, { steps: 4 });
  await page.mouse.move(r.x + r.width / 2 + unit * 2, r.y + r.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect(from).toHaveAttribute('data-width', '8');
  await expect(to).toHaveAttribute('data-width', '4');

  // Схема в режимі JSON — рядок з ширинами 8/4.
  await designer.getByTestId('fd-mode-json').click();
  const schema = JSON.parse(await designer.getByTestId('fd-json').inputValue());
  expect(schema['x-openbp-layout'].sections[0].rows[1]).toEqual([
    { field: 'from', width: 8 },
    { field: 'to', width: 4 },
  ]);
  await designer.getByTestId('fd-mode-design').click();
  await designer.getByTestId('fd-card-from').click();
  await testInfo.attach('form-designer-row', { body: await designer.screenshot(), contentType: 'image/png' });

  const results = await new AxeBuilder({ page }).include('[data-testid="kit-form-designer"]').analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(blocking.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
});

test('конструктор форм: набір одразу після вибору поля з палітри йде в нове поле, не в попереднє', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
  await page.goto('/dev/kit');
  const designer = page.getByTestId('kit-form-designer');
  await designer.getByTestId('fd-card-name').click();
  await expect(designer.getByTestId('fd-prop-title')).toHaveValue('ПІБ');
  // Клік у палітрі й набір в одній задачі браузера: між ними Angular ще не встиг перемалювати панель.
  for (let i = 0; i < 3; i++) {
    await page.evaluate((text) => {
      const root = document.querySelector('[data-testid="kit-form-designer"]')!;
      (root.querySelector('[data-testid="fd-kind-text"]') as HTMLButtonElement).click();
      const input = root.querySelector('[data-testid="fd-prop-title"]') as HTMLInputElement;
      input.value = text;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }, `Поле ${i}`);
  }
  await expect(designer.getByTestId('fd-card-name')).toContainText('ПІБ');
  for (let i = 0; i < 3; i++) await expect(designer.locator('[data-testid^="fd-card-"]', { hasText: `Поле ${i}` })).toHaveCount(1);
});
