import { APIRequestContext, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F6-02: погодження проти справжніх Api + Worker. Сценарій з `core.approval` зупиняється (`waiting`), задача з'являється
 * в «Задачах», користувач відкриває її за `$execution.taskUrl`, заповнює коментар і погоджує — виконання продовжується
 * виходом «approve» і завершується `success`.
 */

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

interface Detail {
  summary: { id: string; state: string; error: string | null };
  steps: { nodeId: string; status: string; output: { json: Record<string, unknown> }[][] }[];
}

async function detail(api: APIRequestContext, id: string): Promise<Detail> {
  return json<Detail>(await api.get(`/api/v1/executions/${id}`));
}

test('F6-02: сценарій чекає погодження, користувач погоджує в UI, сценарій завершується', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const title = `Погодити знижку ${Date.now()}`;
  const created = await json<{ id: string }>(
    await api.post('/api/v1/workflows', {
      data: {
        formatVersion: 1,
        name: `Знижки ${Date.now()}`,
        settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 0, errorWorkflowId: null, saveSuccessfulData: true },
        nodes: [
          { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
          {
            id: 'n_link',
            name: 'Лист',
            type: 'core.set',
            typeVersion: 1,
            position: [200, 0],
            parameters: { fields: [{ name: 'link', value: '{{ $execution.taskUrl }}' }] },
          },
          {
            id: 'n_appr',
            name: 'Погодження',
            type: 'core.approval',
            typeVersion: 1,
            position: [400, 0],
            parameters: {
              title,
              description: 'Клієнт ТОВ «Зерно», знижка 15%',
              assignee: { expression: E2E_ADMIN.email },
              maxWaitSec: 3600,
              fields: { type: 'object', required: ['comment'], properties: { comment: { type: 'string', title: 'Коментар' } } },
            },
          },
          {
            id: 'n_ok',
            name: 'Погоджено',
            type: 'core.set',
            typeVersion: 1,
            position: [600, -100],
            parameters: { fields: [{ name: 'result', value: '{{ $json.decision }}: {{ $json.data.comment }}' }], keepOnlySet: true },
          },
          {
            id: 'n_no',
            name: 'Відхилено',
            type: 'core.set',
            typeVersion: 1,
            position: [600, 100],
            parameters: { fields: [{ name: 'result', value: 'reject' }], keepOnlySet: true },
          },
        ],
        connections: [
          { from: { node: 'n_start', output: 0 }, to: { node: 'n_link', input: 0 } },
          { from: { node: 'n_link', output: 0 }, to: { node: 'n_appr', input: 0 } },
          { from: { node: 'n_appr', output: 1 }, to: { node: 'n_ok', input: 0 } },
          { from: { node: 'n_appr', output: 2 }, to: { node: 'n_no', input: 0 } },
        ],
        pinData: {},
      },
    }),
  );
  const started = await json<{ id: string }>(await api.post(`/api/v1/workflows/${created.id}/executions`, { data: {} }));
  await expect.poll(async () => (await detail(api, started.id)).summary.state, { timeout: 30_000 }).toBe('waiting');
  const link = String((await detail(api, started.id)).steps.find((s) => s.nodeId === 'n_link')!.output[0][0].json['link']);
  expect(link).toMatch(/\/tasks\/[A-Za-z0-9_-]+$/);

  // Задача — у «Моїх» відкритих.
  await page.goto('/tasks');
  await expect(page.getByTestId('task-link').filter({ hasText: title })).toBeVisible();

  // Картка за посиланням з листа: без коментаря не надсилається, з коментарем — погоджено.
  await page.goto(link.slice(link.indexOf('/tasks/')));
  await expect(page.getByTestId('task-title')).toHaveText(title);
  await page.getByTestId('decision-approve').click();
  await expect(page.getByTestId('task-submit-error')).toBeVisible();
  await page.getByTestId('form-field-comment').fill('Погоджую');
  await page.getByTestId('decision-approve').click();
  await expect(page.getByTestId('task-decided')).toContainText('Погодити');

  await expect.poll(async () => (await detail(api, started.id)).summary.state, { timeout: 30_000 }).toBe('success');
  const ok = (await detail(api, started.id)).steps.find((s) => s.nodeId === 'n_ok')!;
  expect(ok.output[0][0].json['result']).toBe('approve: Погоджую');
});
