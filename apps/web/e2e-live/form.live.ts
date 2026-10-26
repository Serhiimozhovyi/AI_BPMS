import { APIRequestContext, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F6-03: публічні форми проти справжніх Api + Worker. Тригер `core.formTrigger`: людина заповнює `/form/{path}` →
 * виконання `success` з полями форми. Крок `core.wait` (режим form): виконання чекає, сторінка за `$execution.resumeUrl`
 * показує форму, надсилання продовжує виконання.
 */

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

interface Detail {
  summary: { id: string; state: string; error: string | null };
  steps: { nodeId: string; status: string; output: { json: Record<string, unknown> }[][] }[];
}

const settings = { timezone: 'Europe/Kyiv', executionTimeoutSec: 0, errorWorkflowId: null, saveSuccessfulData: true };

async function detail(api: APIRequestContext, id: string): Promise<Detail> {
  return json<Detail>(await api.get(`/api/v1/executions/${id}`));
}

test('F6-03: заповнити форму → виконання success з даними форми', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const path = `leave-${Date.now()}`;
  const created = await json<{ id: string }>(
    await api.post('/api/v1/workflows', {
      data: {
        formatVersion: 1,
        name: `Заява на відпустку ${Date.now()}`,
        settings,
        nodes: [
          {
            id: 'n_form',
            name: 'Заява',
            type: 'core.formTrigger',
            typeVersion: 1,
            position: [0, 0],
            parameters: {
              path,
              title: 'Заява на відпустку',
              description: 'Заповніть за тиждень до відпустки.',
              schema: {
                type: 'object',
                required: ['name', 'days'],
                properties: { name: { type: 'string', title: 'ПІБ' }, days: { type: 'integer', title: 'Днів', minimum: 1 } },
              },
              onSubmit: { message: 'Дякуємо, заяву прийнято' },
            },
          },
          {
            id: 'n_set',
            name: 'Підсумок',
            type: 'core.set',
            typeVersion: 1,
            position: [300, 0],
            parameters: { fields: [{ name: 'summary', value: '{{ $json.fields.name }}: {{ $json.fields.days }} дн.' }], keepOnlySet: true },
          },
        ],
        connections: [{ from: { node: 'n_form', output: 0 }, to: { node: 'n_set', input: 0 } }],
        pinData: {},
      },
    }),
  );
  await json(await api.post(`/api/v1/workflows/${created.id}/activate`));

  await page.goto(`/form/${path}`);
  await expect(page.getByTestId('form-title')).toHaveText('Заява на відпустку');
  // Без меню застосунку.
  await expect(page.locator('mat-toolbar')).toHaveCount(0);
  await page.getByTestId('form-submit').click();
  await expect(page.getByText('Заповніть це поле.').first()).toBeVisible();

  await page.getByTestId('form-field-name').fill('Олена Коваль');
  await page.getByTestId('form-field-days').fill('5');
  const submitted = page.waitForResponse((r) => r.url().endsWith(`/api/v1/forms/${path}`) && r.request().method() === 'POST');
  await page.getByTestId('form-submit').click();
  const { executionId } = (await (await submitted).json()) as { executionId: string };
  await expect(page.getByTestId('form-done')).toHaveText('Дякуємо, заяву прийнято');

  await expect.poll(async () => (await detail(api, executionId)).summary.state, { timeout: 30_000 }).toBe('success');
  const set = (await detail(api, executionId)).steps.find((s) => s.nodeId === 'n_set')!;
  expect(set.output[0][0].json['summary']).toBe('Олена Коваль: 5 дн.');

  await json(await api.post(`/api/v1/workflows/${created.id}/deactivate`));
});

test('F6-03: форма кроку очікування продовжує виконання', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const created = await json<{ id: string }>(
    await api.post('/api/v1/workflows', {
      data: {
        formatVersion: 1,
        name: `Погодження ${Date.now()}`,
        settings,
        nodes: [
          { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
          {
            id: 'n_link',
            name: 'Посилання',
            type: 'core.set',
            typeVersion: 1,
            position: [200, 0],
            parameters: { fields: [{ name: 'link', value: '{{ $execution.resumeUrl }}' }] },
          },
          {
            id: 'n_wait',
            name: 'Погодження',
            type: 'core.wait',
            typeVersion: 3,
            position: [400, 0],
            parameters: {
              mode: 'form',
              maxWaitSec: 3600,
              form: {
                title: 'Погодьте заявку',
                submitLabel: 'Погодити',
                message: 'Рішення записано',
                schema: { type: 'object', required: ['comment'], properties: { comment: { type: 'string', title: 'Коментар' } } },
              },
            },
          },
          {
            id: 'n_done',
            name: 'Готово',
            type: 'core.set',
            typeVersion: 1,
            position: [600, 0],
            parameters: { fields: [{ name: 'comment', value: '{{ $json.fields.comment }}' }], keepOnlySet: true },
          },
        ],
        connections: [
          { from: { node: 'n_start', output: 0 }, to: { node: 'n_link', input: 0 } },
          { from: { node: 'n_link', output: 0 }, to: { node: 'n_wait', input: 0 } },
          { from: { node: 'n_wait', output: 0 }, to: { node: 'n_done', input: 0 } },
        ],
        pinData: {},
      },
    }),
  );
  const started = await json<{ id: string }>(await api.post(`/api/v1/workflows/${created.id}/executions`, { data: {} }));
  await expect.poll(async () => (await detail(api, started.id)).summary.state, { timeout: 30_000 }).toBe('waiting');
  const link = String((await detail(api, started.id)).steps.find((s) => s.nodeId === 'n_link')!.output[0][0].json['link']);
  expect(link).toMatch(/\/form\/resume\/[A-Za-z0-9_-]+$/);

  await page.goto(link.slice(link.indexOf('/form/')));
  await expect(page.getByTestId('form-title')).toHaveText('Погодьте заявку');
  await page.getByTestId('form-field-comment').fill('Погоджено');
  await page.getByTestId('form-submit').click();
  await expect(page.getByTestId('form-done')).toHaveText('Рішення записано');

  await expect.poll(async () => (await detail(api, started.id)).summary.state, { timeout: 30_000 }).toBe('success');
  const done = (await detail(api, started.id)).steps.find((s) => s.nodeId === 'n_done')!;
  expect(done.output[0][0].json['comment']).toBe('Погоджено');
});
