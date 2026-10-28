import { createServer, IncomingMessage, Server } from 'node:http';
import { AddressInfo } from 'node:net';
import { APIRequestContext, expect, test } from '@playwright/test';
import { E2E_ADMIN } from './env';

/**
 * F3-114: загальний помічник проти справжніх Api + Worker і фейкового провайдера (OpenAI-сумісний сервер у тесті).
 * Модель просить створити таблицю → інструмент змін не виконується, а стає карткою → «Застосувати» створює таблицю.
 */
interface ChatMessage {
  role: string;
  content?: string | null;
}

function readBody(request: IncomingMessage): Promise<{ messages: ChatMessage[] }> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    request.on('data', (c: Buffer) => chunks.push(c));
    request.on('end', () => resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')) as { messages: ChatMessage[] }));
    request.on('error', reject);
  });
}

async function json<T>(response: Awaited<ReturnType<APIRequestContext['get']>>): Promise<T> {
  expect(response.ok(), `${response.url()} → ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

test('F3-114: помічник пропонує створити таблицю, людина підтверджує — таблиця з\'являється', async ({ page }) => {
  const tableName = `Заявки ${Date.now()}`;
  const requests: string[] = [];
  const provider: Server = createServer(async (request, response) => {
    const body = await readBody(request);
    requests.push(JSON.stringify(body));
    const last = body.messages[body.messages.length - 1];
    const message =
      last.role === 'tool'
        ? { role: 'assistant', content: `Пропоную створити таблицю «${tableName}». Підтвердьте картку.` }
        : {
            role: 'assistant',
            content: null,
            tool_calls: [
              {
                id: 'call_1',
                type: 'function',
                function: {
                  name: 'create_table',
                  arguments: JSON.stringify({ name: tableName, columns: [{ name: 'number', type: 'string', key: true }] }),
                },
              },
            ],
          };
    response.setHeader('content-type', 'application/json');
    response.end(JSON.stringify({ choices: [{ message }] }));
  });
  await new Promise<void>((resolve) => provider.listen(0, '127.0.0.1', resolve));
  const port = (provider.address() as AddressInfo).port;

  try {
    const api = page.request;
    expect((await api.post('/api/v1/auth/login', { data: E2E_ADMIN })).status()).toBe(204);
    const credential = await json<{ id: string }>(
      await api.post('/api/v1/credentials', {
        data: { name: `Фейкова модель ${Date.now()}`, type: 'openai-compatible', data: { baseUrl: `http://127.0.0.1:${port}/v1`, apiKey: 'sk-e2e' } },
      }),
    );
    await json(await api.put('/api/v1/settings/assistant', { data: { credentialId: credential.id, model: 'fake-model' } }));

    await page.goto('/workflows');
    await page.getByTestId('global-assistant-toggle').click();
    await page.getByTestId('global-assistant-input').fill(`Створи таблицю ${tableName}`);
    await page.getByTestId('global-assistant-send').click();

    // Зміна не виконана: є картка з планом, таблиці ще немає.
    const card = page.locator('app-proposal-card');
    await expect(card).toContainText(tableName, { timeout: 30_000 });
    await expect(page.getByTestId('global-assistant-log')).toContainText('Підтвердьте картку.');
    let tables = await json<{ items?: { name: string }[] } | { name: string }[]>(await api.get('/api/v1/tables'));
    expect(JSON.stringify(tables)).not.toContain(tableName.replace(/"/g, ''));

    await page.getByTestId('proposal-apply').click();
    await expect(page.getByTestId('proposal-status')).toContainText('Застосовано.');
    tables = await json(await api.get('/api/v1/tables'));
    expect(JSON.stringify(tables)).toContain(tableName);
    expect(requests.length).toBeGreaterThanOrEqual(2);
    // Секрет credential не потрапив у запити до моделі.
    expect(requests.join('\n')).not.toContain('sk-e2e');
  } finally {
    provider.close();
  }
});
