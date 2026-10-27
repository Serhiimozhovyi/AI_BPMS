import { Page, expect, test } from '@playwright/test';

/** F3-85: сторінки на телефоні (390 px) — списки картками, без горизонтальної прокрутки; погодження задачі з телефона. */
const USER = { id: 'u1', email: 'boss@firma.ua', role: 'admin' };

const TASK = {
  id: 't1',
  executionId: 'e1',
  workflowId: 'w1',
  workflowName: 'Знижки',
  nodeId: 'n_appr',
  title: 'Погодити знижку 15%',
  status: 'open',
  assignee: { email: 'boss@firma.ua' },
  escalated: false,
  canComplete: true,
  createdAt: '2026-10-26T09:00:00Z',
  dueAt: null,
};

async function mockApi(page: Page, completed: { decision?: string; comment?: string } = {}) {
  let done = false;
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/workflows') {
      return route.fulfill({
        json: {
          items: [
            { id: 'w1', name: 'Заявки з сайту — дуже довга назва сценарію для перевірки переносу', active: true, version: 3, updatedAt: '2026-10-23T10:00:00Z', groupId: null, triggerType: 'core.webhook', lastExecution: { id: 'e1', status: 'success', startedAt: '2026-10-26T09:00:00Z' } },
            { id: 'w2', name: 'Щоденний звіт', active: false, version: 1, updatedAt: '2026-10-22T10:00:00Z', groupId: null, triggerType: null, lastExecution: null },
          ],
          page: 1,
          pageSize: 200,
          total: 2,
        },
      });
    }
    if (path === '/api/v1/workflow-groups') return route.fulfill({ json: [] });
    if (path === '/api/v1/executions') {
      return route.fulfill({
        json: {
          items: [
            { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'trigger', state: 'success', startedAt: '2026-10-26T09:00:00Z', finishedAt: '2026-10-26T09:00:05Z', error: null, correlationKey: 'deal-1234567890-abcdefghij' },
          ],
          page: 1,
          pageSize: 50,
          total: 1,
        },
      });
    }
    if (path === '/api/v1/tasks' && method === 'GET') {
      return route.fulfill({ json: { items: [done ? { ...TASK, status: 'done', canComplete: false } : TASK], page: 1, pageSize: 50, total: 1 } });
    }
    if (path === '/api/v1/tasks/t1' && method === 'GET') {
      return route.fulfill({
        json: {
          task: done ? { ...TASK, status: 'done', decision: completed.decision, canComplete: false } : TASK,
          description: 'Клієнт ТОВ «Зерно», знижка 15% на партію пшениці',
          decisions: [
            { key: 'approve', label: 'Погодити' },
            { key: 'reject', label: 'Відхилити' },
          ],
          fields: { type: 'object', properties: { comment: { type: 'string', title: 'Коментар' } } },
          data: { client: 'ТОВ «Зерно»', discount: 15 },
        },
      });
    }
    if (path === '/api/v1/tasks/t1/complete' && method === 'POST') {
      const body = request.postDataJSON() as { decision: string; data: { comment?: string } };
      completed.decision = body.decision;
      completed.comment = body.data.comment;
      done = true;
      return route.fulfill({
        json: { task: { ...TASK, status: 'done', decision: body.decision, canComplete: false }, decisions: [{ key: 'approve', label: 'Погодити' }, { key: 'reject', label: 'Відхилити' }] },
      });
    }
    if (path === '/api/v1/forms/leave') {
      return route.fulfill({
        json: {
          kind: 'trigger',
          title: 'Заявка на відпустку',
          description: 'Заповніть форму',
          submitLabel: 'Надіслати',
          schema: { type: 'object', required: ['name'], properties: { name: { type: 'string', title: 'ПІБ' }, days: { type: 'number', title: 'Днів' } } },
        },
      });
    }
    return route.fulfill({ json: { items: [], page: 1, pageSize: 50, total: 0 } });
  });
}

async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

test.use({ viewport: { width: 390, height: 844 } });

test('сценарії, виконання й задачі — картки без горизонтальної прокрутки', async ({ page }) => {
  await mockApi(page);

  await page.goto('/workflows');
  await expect(page.getByTestId('open-link').first()).toBeVisible();
  await expect(page.locator('table[appCardTable] thead').first()).toBeHidden();
  await expect(page.getByTestId('last-run-cell').first()).toHaveAttribute('data-label', 'Останній запуск');
  await expectNoHorizontalScroll(page);

  await page.goto('/executions');
  await expect(page.getByTestId('state').first()).toBeVisible();
  await expectNoHorizontalScroll(page);

  await page.goto('/tasks');
  await expect(page.getByTestId('task-link')).toBeVisible();
  await expect(page.getByTestId('task-status')).toHaveAttribute('data-label', 'Статус');
  await expectNoHorizontalScroll(page);
});

test('погодження задачі з телефона: коментар, кнопка на всю ширину', async ({ page }) => {
  const completed: { decision?: string; comment?: string } = {};
  await mockApi(page, completed);

  await page.goto('/tasks/t1');
  await expect(page.getByTestId('task-title')).toContainText('Погодити знижку 15%');
  await expect(page.getByTestId('task-data')).toBeVisible();
  await expect(page.getByTestId('task-data').locator('pre')).toBeHidden();
  await expectNoHorizontalScroll(page);

  const approve = page.getByTestId('decision-approve');
  const box = (await approve.boundingBox())!;
  const bar = (await page.locator('.task-card__decisions').boundingBox())!;
  expect(box.width).toBeGreaterThan(bar.width - 40);
  expect(box.y + box.height).toBeLessThanOrEqual(844);

  await page.getByTestId('form-field-comment').fill('Погоджено, відвантажуйте');
  await approve.click();
  await expect(page.getByTestId('task-decided')).toContainText('Погодити');
  expect(completed).toEqual({ decision: 'approve', comment: 'Погоджено, відвантажуйте' });
});

test('публічна форма: поля й кнопка на всю ширину, без горизонтальної прокрутки', async ({ page }) => {
  await mockApi(page);
  await page.goto('/form/leave');
  await expect(page.getByTestId('form-title')).toContainText('Заявка на відпустку');
  const submit = page.getByTestId('form-submit');
  expect((await submit.boundingBox())!.width).toBeGreaterThan(390 - 64);
  await expectNoHorizontalScroll(page);
});
