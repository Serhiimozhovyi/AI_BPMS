import { readFileSync } from 'node:fs';
import path from 'node:path';
import { APIRequestContext, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F2-20, критерій фази 2: приклад `examples/telegram-1c.json` запускається з браузера кнопкою «Виконати» проти
 * справжніх Api + Worker і stdio-моків 1С/Telegram (FakeServer `--mock`), і всі вузли зеленіють.
 * Вебхук і `ai.llm` мають `pinData`, тож у ручному запуску віддають закріплені items; `mcp.tool` викликають моки.
 */

const root = path.resolve(__dirname, '../../..');
const fakeServerProject = path.join(root, 'tests/OpenBP.Mcp.Tests/FakeServer');

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

/** Реєструє stdio-мок як конектор і завантажує його tools; повертає id конектора (`serverId` вузлів). */
async function mockConnector(api: APIRequestContext, name: string, mock: '1c' | 'telegram'): Promise<string> {
  const created = await json<{ summary: { id: string } }>(
    await api.post('/api/v1/connectors', {
      data: {
        name,
        transport: 'stdio',
        config: { command: 'dotnet', arguments: ['run', '--no-build', '--project', fakeServerProject, '--', '--mock', mock] },
      },
    }),
  );
  const id = created.summary.id;
  const refreshed = await json<{ summary: { status: string; toolCount: number } }>(await api.post(`/api/v1/connectors/${id}/refresh-tools`));
  expect(refreshed.summary).toMatchObject({ status: 'ok' });
  expect(refreshed.summary.toolCount).toBeGreaterThan(0);
  return id;
}

test('telegram-1c: «Виконати» з канвасу — усі вузли зеленіють, у панелі output 1С', async ({ page }) => {
  // Cookie входу спільна для page.request і сторінки.
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);

  const oneC = await mockConnector(api, `1С (мок) ${Date.now()}`, '1c');
  const telegram = await mockConnector(api, `Telegram (мок) ${Date.now()}`, 'telegram');
  const definition = JSON.parse(
    readFileSync(path.join(root, 'examples/telegram-1c.json'), 'utf8').replaceAll('"srv_1c"', `"${oneC}"`).replaceAll('"srv_telegram"', `"${telegram}"`),
  ) as { nodes: { id: string; name: string }[] };
  const workflow = await json<{ id: string }>(await api.post('/api/v1/workflows', { data: definition }));

  await page.goto(`/workflows/${workflow.id}`);
  const cards = page.locator('[data-node-id]');
  await expect(cards).toHaveCount(definition.nodes.length);

  await page.getByTestId('run').click();
  await expect(page.getByTestId('run-status')).toHaveAttribute('data-state', 'success', { timeout: 90_000 });
  for (const node of definition.nodes) {
    await expect(page.locator(`[data-node-id="${node.id}"]`)).toHaveAttribute('data-run-status', 'success');
  }
  await expect(page.getByTestId('run')).toBeEnabled();

  // Input/output вузла 1С у панелі: на вході — розібрана заявка (pinData ai.llm), на виході — номер замовлення з моку.
  await page.locator('[data-node-id="n_1c"]').click();
  const run = page.getByTestId('node-run');
  await expect(run).toHaveAttribute('data-status', 'success');
  await expect(run.getByTestId('node-run-input')).toContainText('Петренко');
  await expect(run.getByTestId('node-run-output')).toContainText('"number"');
});
