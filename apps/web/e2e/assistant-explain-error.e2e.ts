import { expect, test } from '@playwright/test';

/**
 * F3-112: «Пояснити помилку» на виконанні, що впало, відкриває загальний помічник і одразу надсилає йому запит
 * з контекстом виконання та вузла, що впав. Api підмінено (`page.route`), відповідь помічника — готовий потік SSE.
 */
const EXECUTION_ID = '0f8fad5b-d9cb-469f-a165-70867728950e';
const WORKFLOW_ID = '7c9e6679-7425-40de-944b-e07fc1f90ae7';
const NODE_ID = 'n_fail';

const summary = (state: string) => ({
  id: EXECUTION_ID,
  workflowId: WORKFLOW_ID,
  versionId: 'v1',
  mode: 'manual',
  state,
  startedAt: '2026-10-28T10:00:00Z',
  finishedAt: '2026-10-28T10:00:02Z',
  error: state === 'error' ? 'Крок упав' : null,
});
const step = (id: string, nodeId: string, status: string) => ({
  id,
  nodeId,
  runIndex: 0,
  status,
  input: [[{ json: { phone: '+380000000000' } }]],
  output: status === 'success' ? [[{ json: {} }]] : [],
  error: status === 'error' ? { message: 'Не вдалося відповісти клієнту' } : null,
  startedAt: '2026-10-28T10:00:00Z',
  finishedAt: '2026-10-28T10:00:01Z',
});
const workflow = {
  id: WORKFLOW_ID,
  name: 'Заявки',
  active: false,
  version: 1,
  versionId: 'v1',
  definition: { formatVersion: 1, name: 'Заявки', settings: {}, nodes: [], connections: [], pinData: {} },
  updatedAt: '2026-10-28T09:00:00Z',
};

test('F3-112: «Пояснити помилку» відкриває помічника й надсилає запит із контекстом виконання та вузла', async ({ page }) => {
  const sent: { message: string; context: Record<string, unknown> }[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
    if (path === '/api/v1/executions') return route.fulfill({ json: { items: [summary('error')], page: 1, pageSize: 50, total: 1 } });
    if (path === `/api/v1/executions/${EXECUTION_ID}`) {
      return route.fulfill({ json: { summary: summary('error'), steps: [step('s1', 'n_start', 'success'), step('s2', NODE_ID, 'error')] } });
    }
    if (path === '/api/v1/workflows') return route.fulfill({ json: { items: [workflow], page: 1, pageSize: 200, total: 1 } });
    if (path === '/api/v1/assistant/conversations' && method === 'GET') return route.fulfill({ json: [] });
    if (path === '/api/v1/assistant/conversations' && method === 'POST') {
      return route.fulfill({
        status: 201,
        json: { id: 'c1', title: 'Нова розмова', userId: 'u1', createdAt: '2026-10-28T10:00:00Z', updatedAt: '2026-10-28T10:00:00Z', messages: [], proposals: [] },
      });
    }
    if (path === '/api/v1/assistant/conversations/c1/messages' && method === 'POST') {
      sent.push(request.postDataJSON() as { message: string; context: Record<string, unknown> });
      const sse = (e: object) => `event: ${(e as { type: string }).type}\ndata: ${JSON.stringify(e)}\n\n`;
      return route.fulfill({
        status: 200,
        contentType: 'text/event-stream',
        body: sse({ type: 'message', text: 'Крок не зміг відповісти клієнту: перевірте підключення.' }) + sse({ type: 'done' }),
      });
    }
    return route.fulfill({ json: method === 'GET' ? [] : {} });
  });

  await page.goto(`/executions/${EXECUTION_ID}`);
  await expect(page.getByTestId('execution-detail')).toBeVisible();
  await expect(page.getByTestId('global-assistant')).toHaveCount(0);

  await page.getByTestId('explain-error').click();

  const log = page.getByTestId('global-assistant-log');
  await expect(page.getByTestId('global-assistant')).toBeVisible();
  await expect(log).toContainText('Не вдалося відповісти клієнту');
  await expect(log).toContainText('Крок не зміг відповісти клієнту: перевірте підключення.');
  expect(sent).toHaveLength(1);
  expect(sent[0].context).toMatchObject({ executionId: EXECUTION_ID, nodeId: NODE_ID });
  // У запит не потрапляють дані кроку.
  expect(sent[0].message).not.toContain('+380000000000');
});
