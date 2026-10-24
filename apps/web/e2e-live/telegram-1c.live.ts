import { readFileSync } from 'node:fs';
import path from 'node:path';
import { APIRequestContext, Page, expect, test } from '@playwright/test';
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

/** Вхід, конектори-моки і сценарій прикладу; відкриває його редактор. */
async function openExample(page: Page): Promise<{ id: string; nodes: { id: string; name: string }[] }> {
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
  await expect(page.locator('[data-node-id]')).toHaveCount(definition.nodes.length);
  return { id: workflow.id, nodes: definition.nodes };
}

/**
 * Запускає кнопкою (`run` або `run-node`) і чекає успіху **саме цього** виконання: до відповіді на запуск (редактор
 * спершу дочікується автозбереження) на канвасі ще статус попереднього запуску, тож id беремо з відповіді Api.
 */
async function runAndWait(page: Page, testId: 'run' | 'run-node'): Promise<void> {
  const started = page.waitForResponse(
    (r) => r.request().method() === 'POST' && /^\/api\/v1\/workflows\/[^/]+\/executions$/.test(new URL(r.url()).pathname),
  );
  await page.getByTestId(testId).click();
  const { id } = (await (await started).json()) as { id: string };
  await expect
    .poll(async () => (await json<{ summary: { state: string } }>(await page.request.get(`/api/v1/executions/${id}`))).summary.state, {
      timeout: 90_000,
    })
    .toBe('success');
  await expect(page.getByTestId('run-status')).toHaveAttribute('data-state', 'success', { timeout: 90_000 });
  await expect(page.getByTestId('run')).toBeEnabled();
}

/** Номер замовлення з виходу вузла 1С у панелі (мок видає новий номер на кожен виклик). */
async function orderNumber(page: Page): Promise<number> {
  await page.locator('[data-node-id="n_1c"]').click();
  await expect(page.getByTestId('node-run-output')).toContainText('"number"');
  const text = (await page.getByTestId('node-run-output').textContent()) ?? '';
  const match = /"number":\s*(\d+)/.exec(text);
  expect(match, text).not.toBeNull();
  return Number(match![1]);
}

test('telegram-1c: «Виконати» з канвасу — усі вузли зеленіють, у панелі output 1С', async ({ page }) => {
  const definition = await openExample(page);

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

test('telegram-1c: закріплений output 1С не викликає 1С; «Виконати вузол» бере вхід з останнього запуску (F2-21)', async ({ page }) => {
  const workflow = await openExample(page);
  await runAndWait(page, 'run');
  const first = await orderNumber(page);

  // Закріпити: наступний повний запуск не викликає 1С — той самий номер, бейдж 📌 на вузлі, pinData збережено.
  await page.getByTestId('pin-output').click();
  await expect(page.locator('[data-node-id="n_1c"]')).toHaveAttribute('data-pinned', 'true');
  await runAndWait(page, 'run');
  expect(await orderNumber(page)).toBe(first);
  const saved = await json<{ definition: { pinData: Record<string, unknown> } }>(await page.request.get(`/api/v1/workflows/${workflow.id}`));
  expect(Object.keys(saved.definition.pinData)).toContain('n_1c');

  // Відкріпити: 1С викликається знову — новий номер.
  await page.getByTestId('unpin-output').click();
  await expect(page.locator('[data-node-id="n_1c"]')).not.toHaveAttribute('data-pinned', 'true');
  await runAndWait(page, 'run');
  const second = await orderNumber(page);
  expect(second).not.toBe(first);

  // «Виконати вузол» на відповіді Telegram: 1С не викликається, відповідь — з номером останнього запуску.
  // Панель вузла 1С перекриває правий край канвасу: спершу знімаємо виділення кліком по порожньому місцю.
  await page.locator('app-canvas').click({ position: { x: 5, y: 5 } });
  await expect(page.getByTestId('node-run')).toHaveCount(0);
  await page.locator('[data-node-id="n_reply"]').click();
  await runAndWait(page, 'run-node');
  await expect(page.getByTestId('node-run')).toHaveAttribute('data-status', 'success');
  await expect(page.getByTestId('node-run-output')).toContainText(String(second));
  expect(await orderNumber(page)).toBe(second);
});


test('telegram-1c: відредагований закріплений output 1С іде на вхід наступного вузла (F3-51)', async ({ page }) => {
  await openExample(page);
  await page.locator('[data-node-id="n_1c"]').click();

  // Вузол ще не виконувався: дані вводяться вручну з шаблону, далі — JSON у панелі.
  await page.getByTestId('pin-manual').click();
  await expect(page.locator('[data-node-id="n_1c"]')).toHaveAttribute('data-pinned', 'true');
  await page.getByTestId('pinned-json').fill('[{ "json": { "number": 777001, "status": "created" } }]');
  await page.getByTestId('pinned-apply').click();
  await expect(page.getByTestId('pinned-apply')).toBeDisabled();

  await runAndWait(page, 'run');
  await page.locator('app-canvas').click({ position: { x: 5, y: 5 } });
  await page.locator('[data-node-id="n_reply"]').click();
  await expect(page.getByTestId('node-run-input')).toContainText('777001');
});
