import { Page, expect, test } from '@playwright/test';

/**
 * F3-110: загальний помічник на кожній сторінці — іконка, панель, розмова з контекстом сторінки, історія. Api підмінено
 * (`page.route`): розмови зберігаються в пам'яті, відповідь помічника — готовий потік SSE замість фейкового провайдера.
 */
const BASE = '/api/v1/assistant/conversations';

interface Conversation {
  id: string;
  title: string;
  userId: string;
  createdAt: string;
  updatedAt: string;
  messages: { role: string; text: string | null; tool: string | null; ok: boolean | null; at: string }[];
  proposals: unknown[];
}

class FakeApi {
  conversations: Conversation[] = [];
  /** Відповідь на повідомлення «створи таблицю»: пропозиція з планом (F3-109), яку людина застосовує чи скасовує. */
  proposal = {
    id: 'p1',
    tool: 'create_table',
    arguments: { name: 'Заявки' },
    plan: {
      planId: 'abc',
      action: 'create',
      definition: { name: 'Заявки' },
      addedColumns: [
        { name: 'number', type: 'string', key: true },
        { name: 'phone', type: 'string', sensitive: true },
      ],
      warnings: [],
    },
    status: 'pending',
    result: null as string | null,
    createdAt: '2026-10-28T10:00:00Z',
    decidedAt: null as string | null,
  };
  decisions: boolean[] = [];
  sent: { id: string; body: { message: string; context: Record<string, unknown> } }[] = [];

  async install(page: Page, role = 'admin'): Promise<void> {
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const method = request.method();
      if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role } });
      if (path === BASE && method === 'GET') return route.fulfill({ json: this.conversations });
      if (path === BASE && method === 'POST') {
        const created: Conversation = {
          id: `c${this.conversations.length + 1}`,
          title: 'Нова розмова',
          userId: 'u1',
          createdAt: '2026-10-28T10:00:00Z',
          updatedAt: '2026-10-28T10:00:00Z',
          messages: [],
          proposals: [],
        };
        this.conversations.unshift(created);
        return route.fulfill({ status: 201, json: created });
      }
      const messages = new RegExp(`^${BASE}/([^/]+)/messages$`).exec(path);
      if (messages && method === 'POST') {
        const body = request.postDataJSON() as { message: string; context: Record<string, unknown> };
        this.sent.push({ id: messages[1], body });
        const conversation = this.conversations.find((c) => c.id === messages[1])!;
        conversation.title = body.message;
        conversation.messages.push(
          { role: 'user', text: body.message, tool: null, ok: null, at: '' },
          { role: 'tool', text: 'виконано', tool: 'list_tables', ok: true, at: '' },
          { role: 'assistant', text: 'У вас дві таблиці.', tool: null, ok: null, at: '' },
        );
        const sse = (e: object) => `event: ${(e as { type: string }).type}\ndata: ${JSON.stringify(e)}\n\n`;
        if (body.message.includes('таблицю')) {
          return route.fulfill({
            status: 200,
            contentType: 'text/event-stream',
            body:
              sse({ type: 'step', tool: 'create_table', ok: true, text: 'план змін — чекає рішення' }) +
              sse({ type: 'proposal', tool: 'create_table', proposal: this.proposal }) +
              sse({ type: 'message', text: 'Пропоную створити таблицю «Заявки».' }) +
              sse({ type: 'done' }),
          });
        }
        return route.fulfill({
          status: 200,
          contentType: 'text/event-stream',
          body:
            sse({ type: 'step', tool: 'list_tables', ok: true, text: 'виконано' }) +
            sse({ type: 'message', text: 'У вас дві таблиці.' }) +
            sse({ type: 'done' }),
        });
      }
      const decide = new RegExp(`^${BASE}/([^/]+)/proposals/([^/]+)$`).exec(path);
      if (decide && method === 'POST') {
        const apply = (request.postDataJSON() as { apply: boolean }).apply;
        this.decisions.push(apply);
        this.proposal = {
          ...this.proposal,
          status: apply ? 'applied' : 'rejected',
          result: apply ? '{"applied":true,"table":{"id":"t1","name":"Заявки"}}' : null,
          decidedAt: '2026-10-28T10:01:00Z',
        };
        return route.fulfill({ json: this.proposal });
      }
      const one = new RegExp(`^${BASE}/([^/]+)$`).exec(path);
      if (one && method === 'GET') return route.fulfill({ json: this.conversations.find((c) => c.id === one[1]) });
      if (one && method === 'DELETE') {
        this.conversations = this.conversations.filter((c) => c.id !== one[1]);
        return route.fulfill({ status: 204 });
      }
      if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [], page: 1, pageSize: 200, total: 0 } });
      return route.fulfill({ json: method === 'GET' ? [] : {} });
    });
  }
}

test('F3-110: іконка → розмова з контекстом сторінки → історія; панель переживає перехід між сторінками', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto('/workflows');

  await expect(page.getByTestId('global-assistant')).toHaveCount(0);
  await page.getByTestId('global-assistant-toggle').click();
  await expect(page.getByTestId('global-assistant')).toBeVisible();
  await expect(page.getByTestId('global-assistant-context')).toContainText('/workflows');

  await page.getByTestId('global-assistant-input').fill('Які в мене таблиці?');
  await page.getByTestId('global-assistant-send').click();
  const log = page.getByTestId('global-assistant-log');
  await expect(log).toContainText('Які в мене таблиці?');
  await expect(log).toContainText('Переглянув таблиці: виконано');
  await expect(log).toContainText('У вас дві таблиці.');
  expect(api.sent).toEqual([{ id: 'c1', body: { message: 'Які в мене таблиці?', context: { page: '/workflows' } } }]);

  // Перехід на іншу сторінку: панель відкрита, розмова на місці, контекст — нової сторінки.
  await page.getByRole('link', { name: 'Таблиці' }).click();
  await expect(page).toHaveURL(/\/tables$/);
  await expect(page.getByTestId('global-assistant')).toBeVisible();
  await expect(page.getByTestId('global-assistant-context')).toContainText('/tables');
  await expect(log).toContainText('У вас дві таблиці.');

  // Історія: розмова в списку; «Нова розмова» очищає чат; вибір із історії повертає її.
  await page.getByTestId('global-assistant-new').click();
  await expect(page.locator('[data-kind]')).toHaveCount(0);
  await page.getByTestId('global-assistant-history').click();
  await expect(page.getByTestId('global-assistant-list')).toContainText('Які в мене таблиці?');
  await page.locator('[data-conversation="c1"]').click();
  await expect(page.getByTestId('global-assistant-log')).toContainText('У вас дві таблиці.');

  // Видалення поточної розмови: чат порожній, список теж.
  await page.getByTestId('global-assistant-history').click();
  await page.locator('[data-delete="c1"]').click();
  await expect(page.getByTestId('global-assistant-list-empty')).toBeVisible();

  await page.getByTestId('global-assistant-close').click();
  await expect(page.getByTestId('global-assistant')).toHaveCount(0);
  await expect(page.getByTestId('global-assistant-toggle')).toBeVisible();
});

test('F3-110: viewer іконки не бачить', async ({ page }) => {
  await new FakeApi().install(page, 'viewer');
  await page.goto('/workflows');
  await expect(page.getByTestId('user-menu')).toBeVisible();
  await expect(page.getByTestId('global-assistant-toggle')).toHaveCount(0);
});

test('F3-111: картка підтвердження — план таблиці, «Застосувати» і «Скасувати»', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto('/workflows');
  await page.getByTestId('global-assistant-toggle').click();

  const ask = async (text: string) => {
    await page.getByTestId('global-assistant-input').fill(text);
    await page.getByTestId('global-assistant-send').click();
  };

  // Пропозиція: план показано, нічого не застосовано, доки людина не натисне кнопку.
  await ask('Створи таблицю для заявок');
  const card = page.locator('app-proposal-card');
  await expect(card).toContainText('Створити таблицю «Заявки»');
  await expect(card).toContainText('number (string, ключ), phone (string, чутлива)');
  await expect(page.getByTestId('global-assistant-log')).toContainText('Пропоную створити таблицю «Заявки».');
  expect(api.decisions).toEqual([]);

  await page.getByTestId('proposal-apply').click();
  await expect(page.getByTestId('proposal-status')).toContainText('Застосовано.');
  await expect(page.getByTestId('proposal-apply')).toHaveCount(0);
  await expect(page.getByTestId('proposal-link')).toHaveAttribute('href', '/tables');
  expect(api.decisions).toEqual([true]);

  // Друга пропозиція: «Скасувати» нічого не застосовує.
  api.proposal = { ...api.proposal, status: 'pending', result: null, decidedAt: null };
  await page.getByTestId('global-assistant-new').click();
  await ask('Створи ще одну таблицю');
  await expect(page.getByTestId('proposal-apply')).toBeVisible();
  await page.getByTestId('proposal-cancel').click();
  await expect(page.getByTestId('proposal-status')).toContainText('Скасовано');
  expect(api.decisions).toEqual([true, false]);
});
