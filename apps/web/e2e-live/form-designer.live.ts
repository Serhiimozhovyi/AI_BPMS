import { APIRequestContext, Locator, Page, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F11-08: конструктор форм наскрізно проти справжніх Api + Worker. У редакторі з вузла `core.formTrigger` відкрити
 * конструктор, зібрати «Заяву на відпустку» (дві секції, рядок «З — По», «Вид» кнопками, «Причина» за умовою),
 * зберегти, активувати сценарій, заповнити форму на телефоні (390 px) і перевірити item виконання.
 */

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

interface Detail {
  summary: { id: string; state: string };
  steps: { nodeId: string; status: string; output: { json: Record<string, unknown> }[][] }[];
}

/** Вибір пункту `mat-select` за підписом — як користувач. */
async function choose(select: Locator, label: string): Promise<void> {
  await select.click();
  const option = select.page().getByRole('option', { name: label, exact: true });
  await option.click();
  await expect(option).toHaveCount(0);
}

/** Нове поле з палітри: підпис і ключ. */
async function addField(dialog: Locator, kind: string, title: string, key: string): Promise<void> {
  await dialog.getByTestId(`fd-kind-${kind}`).click();
  await dialog.getByTestId('fd-prop-title').fill(title);
  await dialog.getByTestId('fd-prop-key').fill(key);
  await dialog.getByTestId('fd-prop-key').press('Enter');
  await expect(dialog.getByTestId(`fd-card-${key}`)).toBeVisible();
}

async function required(dialog: Locator): Promise<void> {
  await dialog.getByTestId('fd-prop-required').locator('input').check();
}

async function openNode(page: Page, nodeId: string): Promise<void> {
  // Канвас після завантаження ще підганяє масштаб — повторюємо, доки панель вузла не відкриється.
  await expect(async () => {
    await page.locator(`[data-node-id="${nodeId}"] .node__name`).dblclick();
    await expect(page.getByTestId('node-panel')).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
}

test('F11-08: зібрати заяву на відпустку в конструкторі, заповнити на телефоні, item виконання', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const path = `leave-designer-${Date.now()}`;
  const created = await json<{ id: string }>(
    await api.post('/api/v1/workflows', {
      data: {
        formatVersion: 1,
        name: `Заява з конструктора ${Date.now()}`,
        settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 0, errorWorkflowId: null, saveSuccessfulData: true },
        nodes: [
          {
            id: 'n_form',
            name: 'Заява',
            type: 'core.formTrigger',
            typeVersion: 1,
            position: [0, 0],
            parameters: { path, title: 'Заява на відпустку', submitLabel: 'Надіслати заяву', schema: { type: 'object', properties: {} }, onSubmit: { message: 'Дякуємо, заяву прийнято' } },
          },
        ],
        connections: [],
        pinData: {},
      },
    }),
  );

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/workflows/${created.id}`);
  await openNode(page, 'n_form');
  await page.getByTestId('fs-open').click();
  const dialog = page.getByTestId('form-designer-dialog');
  await expect(dialog).toBeVisible();

  // Секція «Хто»: ПІБ.
  await dialog.getByTestId('fd-section-title').first().click();
  await dialog.getByTestId('fd-section-prop-title').fill('Хто');
  await addField(dialog, 'text', 'ПІБ', 'name');
  await required(dialog);

  // Секція «Відпустка»: «З — По» в одному рядку, «Вид» кнопками, «Причина» — лише за свій рахунок.
  await dialog.getByTestId('fd-add-section').click();
  await dialog.getByTestId('fd-section-prop-title').fill('Відпустка');
  await addField(dialog, 'date', 'З', 'from');
  await required(dialog);
  await addField(dialog, 'date', 'По', 'to');
  await required(dialog);
  await dialog.getByTestId('fd-join').click();
  await addField(dialog, 'select', 'Вид', 'kind');
  await required(dialog);
  await dialog.getByTestId('fd-option-label-0').fill('Щорічна');
  await dialog.getByTestId('fd-option-value-0').fill('annual');
  await dialog.getByTestId('fd-option-label-1').fill('За свій рахунок');
  await dialog.getByTestId('fd-option-value-1').fill('unpaid');
  await expect(dialog.getByTestId('fd-prop-widget')).toContainText('Кнопки');
  await addField(dialog, 'textarea', 'Причина', 'reason');
  await required(dialog);
  await choose(dialog.getByTestId('fd-if-field'), 'Вид');
  await choose(dialog.getByTestId('fd-if-value'), 'За свій рахунок');

  // Перегляд діє наживо: «Причина» з'являється після вибору варіанта.
  await dialog.getByTestId('fd-mode-preview').click();
  await expect(dialog.getByTestId('form-field-reason')).toHaveCount(0);
  await dialog.getByRole('radio', { name: 'За свій рахунок' }).check();
  await expect(dialog.getByTestId('form-field-reason')).toBeVisible();
  await page.screenshot({ path: 'test-results/f11-08-designer-preview.png' });
  await dialog.getByTestId('fd-mode-design').click();
  await page.screenshot({ path: 'test-results/f11-08-designer.png' });

  await page.getByTestId('fdd-save').click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId('fs-open')).toContainText('(5)');

  // Автозбереження записало схему у вузол. Порядок ключів `properties` jsonb не тримає — порядок задає розмітка.
  await expect
    .poll(async () => {
      const wf = await json<{ definition: { nodes: { parameters: { schema: { properties: object } } }[] } }>(await api.get(`/api/v1/workflows/${created.id}`));
      return Object.keys(wf.definition.nodes[0].parameters.schema.properties).sort();
    }, { timeout: 15_000 })
    .toEqual(['from', 'kind', 'name', 'reason', 'to']);
  const wf = await json<{ definition: { nodes: { parameters: { schema: Record<string, unknown> } }[] } }>(await api.get(`/api/v1/workflows/${created.id}`));
  const schema = wf.definition.nodes[0].parameters.schema;
  expect(schema['required']).toEqual(['name', 'from', 'to', 'kind', 'reason']);
  expect(schema['x-openbp-layout']).toEqual({
    sections: [
      { title: 'Хто', rows: [[{ field: 'name' }]] },
      {
        title: 'Відпустка',
        rows: [
          [{ field: 'from' }, { field: 'to' }],
          [{ field: 'kind' }],
          [{ field: 'reason', visibleIf: { field: 'kind', operator: 'eq', value: 'unpaid' } }],
        ],
      },
    ],
  });
  await json(await api.post(`/api/v1/workflows/${created.id}/activate`));

  // Людина заповнює форму з телефона.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/form/${path}`);
  await expect(page.getByTestId('form-title')).toHaveText('Заява на відпустку');
  await expect(page.getByRole('heading', { name: 'Відпустка' })).toBeVisible();
  await page.getByTestId('form-field-name').fill('Олена Коваль');
  await page.getByTestId('form-field-from').fill('2026-11-10');
  await page.getByTestId('form-field-to').fill('2026-11-14');
  await expect(page.getByTestId('form-field-reason')).toHaveCount(0);
  await page.getByRole('radio', { name: 'За свій рахунок' }).check();
  await page.getByTestId('form-field-reason').fill('Сімейні обставини');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await page.screenshot({ path: 'test-results/f11-08-form-390.png', fullPage: true });

  const submitted = page.waitForResponse((r) => r.url().endsWith(`/api/v1/forms/${path}`) && r.request().method() === 'POST');
  await page.getByTestId('form-submit').click();
  const { executionId } = (await (await submitted).json()) as { executionId: string };
  await expect(page.getByTestId('form-done')).toHaveText('Дякуємо, заяву прийнято');

  const detail = async () => json<Detail>(await api.get(`/api/v1/executions/${executionId}`));
  await expect.poll(async () => (await detail()).summary.state, { timeout: 30_000 }).toBe('success');
  const trigger = (await detail()).steps.find((s) => s.nodeId === 'n_form')!;
  expect(trigger.output[0][0].json['fields']).toEqual({ name: 'Олена Коваль', from: '2026-11-10', to: '2026-11-14', kind: 'unpaid', reason: 'Сімейні обставини' });

  await json(await api.post(`/api/v1/workflows/${created.id}/deactivate`));
});
