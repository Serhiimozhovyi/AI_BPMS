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

/** Вкладка «Вихід» панелі у вигляді JSON (вид запам'ятовується, тож перемикаємо лише раз). */
async function showOutputJson(page: Page): Promise<void> {
  await page.getByTestId('panel-tab-output').click();
  const json = page.getByTestId('output-view-json');
  if ((await json.getAttribute('aria-pressed')) !== 'true') await json.click();
}

/** Номер замовлення з виходу вузла 1С у панелі (мок видає новий номер на кожен виклик). */
async function orderNumber(page: Page): Promise<number> {
  await page.locator('[data-node-id="n_1c"]').click();
  await showOutputJson(page);
  await expect(page.getByTestId('node-run-output')).toContainText('"number"');
  const text = (await page.getByTestId('node-run-output').textContent()) ?? '';
  const match = /"number":\s*(\d+)/.exec(text);
  expect(match, text).not.toBeNull();
  // Дії над вузлом (pin, тест кроку) — у вкладці «Параметри».
  await page.getByTestId('panel-tab-params').click();
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
  await expect(page.getByTestId('node-run-input')).toContainText('Петренко');
  await showOutputJson(page);
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

test('telegram-1c: клік по полю схеми входу вставляє вираз у параметр, запуск вузла дає значення (F3-52)', async ({ page }) => {
  await openExample(page);
  await runAndWait(page, 'run');
  await page.locator('[data-node-id="n_reply"]').click();

  // Поле параметра з фокусом — ціль вставки; курсор у кінці введеного тексту.
  const text = page.getByTestId('field-text');
  await text.fill('Номер ');
  await page.getByTestId('panel-tab-input').click();
  await expect(page.getByTestId('input-port')).toContainText('1 item');
  await page.locator('[data-testid="schema-field"][data-path=".number"]').click();

  // Після вставки панель повертається на «Параметри».
  await expect(text).toBeVisible();
  await expect(text).toHaveValue('Номер {{ $json.number }}');

  await runAndWait(page, 'run-node');
  await page.getByTestId('panel-tab-output').click();
  await expect(page.getByTestId('node-run-output')).toContainText(/"Номер \d+"/);
  // Лічильник items на ребрі з виходу 1С.
  await expect(page.locator('[data-testid="edge-count"]').first()).toHaveText('1 item');
});

test('IF з двома умовами OR, зібраний у конструкторі умов: items ідуть у правильні гілки (F3-53)', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const workflow = await json<{ id: string }>(
    await api.post('/api/v1/workflows', {
      data: {
        schemaVersion: 1,
        name: `Умова OR ${Date.now()}`,
        nodes: [
          { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [100, 200], parameters: {} },
          { id: 'n_if', name: 'Умова', type: 'core.if', typeVersion: 1, position: [360, 200], parameters: { conditions: [] } },
        ],
        connections: [{ from: { node: 'n_start', output: 0 }, to: { node: 'n_if', input: 0 } }],
        pinData: { n_start: [{ json: { n: 5 } }, { json: { n: 50 } }, { json: { n: 7 } }] },
      },
    }),
  );
  await page.goto(`/workflows/${workflow.id}`);
  await page.locator('[data-node-id="n_if"]').click();

  // n > 10 АБО n = 5: 5 і 50 — у true, 7 — у false.
  const conditions = page.getByTestId('condition');
  for (const [i, operator, right] of [
    [0, 'більше', '10'],
    [1, 'дорівнює', '5'],
  ] as const) {
    await page.getByTestId('add-conditions').click();
    const row = conditions.nth(i);
    await row.getByTestId('field-left').fill('{{ $json.n }}');
    // Порожній mat-select перекриває його ж плаваючий підпис — клік по самому полю.
    await row.getByTestId('field-operator').click({ force: true });
    await page.getByRole('option', { name: operator, exact: true }).click();
    await row.getByTestId('field-right').fill(right);
  }
  await page.getByTestId('combine-or').click();
  await expect(page.getByTestId('combine-or')).toHaveAttribute('aria-checked', 'true');

  await runAndWait(page, 'run');
  await page.getByTestId('panel-tab-output').click();
  const ports = page.getByTestId('output-port');
  await expect(ports.nth(0)).toContainText('true · 2 items');
  await expect(ports.nth(1)).toContainText('false · 1 item');

  const saved = await json<{ definition: { nodes: { id: string; parameters: unknown }[] } }>(await api.get(`/api/v1/workflows/${workflow.id}`));
  expect(saved.definition.nodes.find((n) => n.id === 'n_if')?.parameters).toEqual({
    conditions: [
      { left: '{{ $json.n }}', operator: 'gt', right: 10 },
      { left: '{{ $json.n }}', operator: 'equals', right: 5 },
    ],
    combine: 'or',
  });
});

test('telegram-1c: chat_id обирається зі списку чатів get_updates — у параметр записується id (F3-54)', async ({ page }) => {
  const workflow = await openExample(page);
  await page.locator('[data-node-id="n_reply"]').click();

  // У прикладі chat_id — вираз: режим виводиться зі значення.
  await expect(page.getByTestId('mode-expression-chat_id')).toHaveAttribute('aria-checked', 'true');
  await page.getByTestId('mode-list-chat_id').click();
  await expect(page.getByTestId('mode-list-chat_id')).toHaveAttribute('aria-checked', 'true');

  const field = page.getByTestId('field-chat_id');
  await field.click();
  // Мок get_updates: три оновлення з двох чатів — два варіанти.
  await expect(page.getByTestId('option-chat_id')).toHaveCount(2);
  await page.getByTestId('option-chat_id').filter({ hasText: 'Петро' }).click();
  await expect(field).toHaveValue('Петро');

  await expect
    .poll(async () => {
      const saved = await json<{ definition: { nodes: { id: string; parameters: { arguments?: Record<string, unknown> } }[] } }>(
        await page.request.get(`/api/v1/workflows/${workflow.id}`),
      );
      return saved.definition.nodes.find((n) => n.id === 'n_reply')?.parameters.arguments?.['chat_id'];
    })
    .toBe(2002);

  // Повний запуск: мок send_message повертає chat_id, що прийшов у аргументах.
  await runAndWait(page, 'run');
  await page.locator('[data-node-id="n_reply"]').click();
  await showOutputJson(page);
  await expect(page.getByTestId('node-run-output')).toContainText('2002');
});

test('конектор одним вузлом: додати Telegram, обрати дію «Надіслати повідомлення», запустити (F3-55)', async ({ page }) => {
  const api = page.request;
  expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
  const connectorName = `Telegram (мок) ${Date.now()}`;
  await mockConnector(api, connectorName, 'telegram');
  const workflow = await json<{ id: string }>(
    await api.post('/api/v1/workflows', { data: { schemaVersion: 1, name: `Конектор-вузол ${Date.now()}`, nodes: [], connections: [] } }),
  );
  await page.goto(`/workflows/${workflow.id}`);

  // Тригер, далі «+» біля порту → конектор одним елементом.
  await expect(page.getByTestId('counts')).toContainText('вузлів: 0');
  await page.keyboard.press('Tab');
  await page.getByTestId('quick-add-search').fill('ручний');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('counts')).toContainText('вузлів: 1');
  await page.getByTestId('port-add').click();
  await page.getByTestId('quick-add-search').fill(connectorName);
  await expect(page.getByTestId('quick-add').locator('[data-item^="connector:"]')).toHaveCount(1);
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('counts')).toContainText('вузлів: 2 · ребер: 1');

  // Дія за замовчуванням — перша в групах (get_updates); обираємо «Надіслати повідомлення» зі списку з пошуком.
  const action = page.getByTestId('action-select');
  await expect(action).toHaveValue('Оновлення');
  await action.click();
  await action.fill('надіслати');
  await page.locator('[data-testid="action-option"][data-tool="send_message"]').click();
  await expect(action).toHaveValue('Надіслати повідомлення');
  await expect(page.getByTestId('node-panel-name')).toHaveText('Надіслати повідомлення');

  await page.getByTestId('field-text').fill('Вітаємо з OpenBP');
  await page.getByTestId('field-chat_id').click();
  await page.getByTestId('option-chat_id').filter({ hasText: 'Олена' }).click();

  await runAndWait(page, 'run');
  await showOutputJson(page);
  await expect(page.getByTestId('node-run-output')).toContainText('Вітаємо з OpenBP');
  await expect(page.getByTestId('node-run-output')).toContainText('1001');
  // Ребро тригер → вузол конектора збереглося після зміни дії.
  const saved = await json<{ definition: { connections: unknown[]; nodes: { name: string; parameters: Record<string, unknown> }[] } }>(
    await api.get(`/api/v1/workflows/${workflow.id}`),
  );
  expect(saved.definition.connections).toHaveLength(1);
  expect(saved.definition.nodes[1]).toMatchObject({ name: 'Надіслати повідомлення', parameters: { toolName: 'send_message', arguments: { text: 'Вітаємо з OpenBP', chat_id: 1001 } } });
});
