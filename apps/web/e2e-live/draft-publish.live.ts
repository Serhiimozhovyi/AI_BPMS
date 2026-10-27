import { APIRequestContext, Page, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F6-10: чернетка і публікація проти справжніх Api + Worker. Активний сценарій з webhook'ом змінюють на канвасі —
 * webhook і далі виконує опубліковану версію; після «Опублікувати» — нову.
 */

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

interface Detail {
  id: string;
  version: number;
  versionId: string;
  publishedVersion: number | null;
}

/** Викликає webhook і повертає `versionId` створеного виконання. */
async function webhookVersion(page: Page, path: string): Promise<string> {
  const { executionId } = await json<{ executionId: string }>(await page.request.post(`/webhook/${path}`, { data: { text: 'привіт' } }));
  const execution = await json<{ summary: { versionId: string } }>(await page.request.get(`/api/v1/executions/${executionId}`));
  return execution.summary.versionId;
}

test('F6-10: зміна активного сценарію — чернетка; webhook виконує стару версію, після «Опублікувати» — нову', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const path = `draft-${Date.now()}`;
  const created = await json<Detail>(
    await api.post('/api/v1/workflows', {
      data: {
        formatVersion: 1,
        name: `Чернетка ${Date.now()}`,
        settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 0, errorWorkflowId: null, saveSuccessfulData: false },
        nodes: [
          { id: 'n_hook', name: 'Webhook', type: 'core.webhook', typeVersion: 1, position: [0, 0], parameters: { method: 'POST', path } },
          { id: 'n_set', name: 'Задати поля', type: 'core.set', typeVersion: 1, position: [300, 0], parameters: { fields: [] } },
        ],
        connections: [{ from: { node: 'n_hook', output: 0 }, to: { node: 'n_set', input: 0 } }],
        pinData: {},
      },
    }),
  );
  const v1 = (await json<Detail>(await api.post(`/api/v1/workflows/${created.id}/activate`))).versionId;

  await page.goto(`/workflows/${created.id}`);
  await expect(page.getByTestId('publish-state')).toHaveAttribute('data-state', 'published');

  // Видаляємо «Задати поля» на канвасі: автозбереження пише чернетку (версія 2).
  await expect(page.locator('[data-node-id]')).toHaveCount(2);
  // Канвас після завантаження ще підганяє масштаб — клікаємо, доки вузол не виділиться.
  await expect(async () => {
    await page.locator('[data-node-id="n_set"] .node__name').click();
    await expect(page.getByTestId('node-panel-name')).toHaveText('Задати поля', { timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
  await page.getByTestId('delete').click();
  await expect(page.getByTestId('saved')).toHaveAttribute('title', /Версія 2/, { timeout: 15_000 });
  await expect(page.getByTestId('publish-state')).toHaveAttribute('data-state', 'changed');
  await expect(page.getByTestId('publish-diff')).toContainText('видалено: Задати поля');

  const draft = await json<Detail>(await api.get(`/api/v1/workflows/${created.id}`));
  expect(draft).toMatchObject({ version: 2, publishedVersion: 1 });
  expect(await webhookVersion(page, path)).toBe(v1);

  await page.getByTestId('publish').click();
  await expect(page.getByTestId('publish-state')).toHaveAttribute('data-state', 'published');
  await expect(page.getByTestId('publish-state')).toContainText('версія 2');
  expect(await webhookVersion(page, path)).toBe(draft.versionId);

  await json(await api.post(`/api/v1/workflows/${created.id}/deactivate`));
});
